using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

public record PixelPlacement(int X, int Y, string Color);
public enum PlacementStatus { Success, Cooldown, Invalid }
public record PlacementResult(PlacementStatus Status, long NextAllowedAtUnixMs, string Color);

public static class CanvasSettings
{
    public const int Width = 128;
    public const int Height = 128;
    public const int SessionCooldownSeconds = 10;
    public const int IpCooldownSeconds = 10;

    // Original version's palette, used only to upgrade an existing database.
    private static readonly string[] OldPalette =
    [
        "#FFFFFF", "#191919", "#6B7280", "#E74C3C",
        "#F28C28", "#F5D547", "#59B85E", "#1C9F99",
        "#3681DC", "#654CC4", "#B04CBF", "#E47FB1",
        "#8B5E3C", "#E8BFA4", "#B7DCEC", "#B2B2B2"
    ];

    public static string LegacyColorMigrationSql => string.Join(" ",
        OldPalette.Select((hex, index) => $"WHEN {index} THEN {Convert.ToInt32(hex[1..], 16)}"));

    public static bool TryParseColor(string? hex, out int rgb)
    {
        rgb = 0;
        return hex is { Length: 7 } && hex[0] == '#' &&
            int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)
            && rgb is >= 0 and <= 0xFFFFFF;
    }
}

public sealed class CanvasStore
{
    private readonly string _directory;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private byte[] _key = [];

    public CanvasStore(IHostEnvironment host)
    {
        _directory = Environment.GetEnvironmentVariable("STATE_DIRECTORY")
            ?? Path.Combine(host.ContentRootPath, "data");
        var databasePath = Path.Combine(_directory, "canvas.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 5,
            Pooling = true
        }.ToString();
    }

    public void Initialize()
    {
        Directory.CreateDirectory(_directory);

        var keyPath = Path.Combine(_directory, "cooldown.key");
        if (!File.Exists(keyPath))
            File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
        _key = File.ReadAllBytes(keyPath);
        if (_key.Length != 32)
            throw new InvalidOperationException("Invalid cooldown.key file (expected 32 bytes).");

        using var db = Open();
        using (var command = db.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS Pixels (
                    X INTEGER NOT NULL,
                    Y INTEGER NOT NULL,
                    Color INTEGER NOT NULL,
                    PRIMARY KEY (X, Y)
                );
                CREATE TABLE IF NOT EXISTS Cooldowns (
                    VisitorKey TEXT PRIMARY KEY,
                    LastPlacedAtUnixMs INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS Placements (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    X INTEGER NOT NULL,
                    Y INTEGER NOT NULL,
                    Color INTEGER NOT NULL,
                    PlacedAtUnixMs INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        using var versionCommand = db.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version == 0)
        {
            // Old installations stored a 0..15 palette index in Color; convert both
            // live pixels and history to 24-bit 0xRRGGBB integers, only once.
            using var tx = db.BeginTransaction();
            using var migrate = db.CreateCommand();
            migrate.Transaction = tx;
            var cases = CanvasSettings.LegacyColorMigrationSql;
            migrate.CommandText = $"""
                UPDATE Pixels SET Color = CASE Color {cases} ELSE 16777215 END;
                UPDATE Placements SET Color = CASE Color {cases} ELSE 16777215 END;
                PRAGMA user_version = 1;
                """;
            migrate.ExecuteNonQuery();
            tx.Commit();
        }
        else if (version != 1)
        {
            throw new InvalidOperationException($"Unsupported canvas database version {version}.");
        }
    }

    public object GetState(string session, string ip, bool isAdmin)
    {
        _mutex.Wait();
        try
        {
            using var db = Open();
            // Three bytes per pixel, RGB; unplaced pixels are pure white.
            var pixels = new byte[CanvasSettings.Width * CanvasSettings.Height * 3];
            Array.Fill(pixels, (byte)255);
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT X, Y, Color FROM Pixels WHERE X >= 0 AND X < $w AND Y >= 0 AND Y < $h";
                command.Parameters.AddWithValue("$w", CanvasSettings.Width);
                command.Parameters.AddWithValue("$h", CanvasSettings.Height);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int x = reader.GetInt32(0), y = reader.GetInt32(1), rgb = reader.GetInt32(2);
                    if (rgb is < 0 or > 0xFFFFFF) continue;
                    int i = (y * CanvasSettings.Width + x) * 3;
                    pixels[i] = (byte)(rgb >> 16);
                    pixels[i + 1] = (byte)(rgb >> 8);
                    pixels[i + 2] = (byte)rgb;
                }
            }

            return new
            {
                width = CanvasSettings.Width,
                height = CanvasSettings.Height,
                pixels = Convert.ToBase64String(pixels),
                nextAllowedAtUnixMs = isAdmin ? 0 : GetNextAllowed(db, null, session, ip),
                serverNowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        finally { _mutex.Release(); }
    }

    public PlacementResult PlacePixel(PixelPlacement pixel, string session, string ip, bool isAdmin)
    {
        if (pixel.X < 0 || pixel.X >= CanvasSettings.Width ||
            pixel.Y < 0 || pixel.Y >= CanvasSettings.Height ||
            !CanvasSettings.TryParseColor(pixel.Color, out int rgb))
            return new(PlacementStatus.Invalid, 0, "");

        var normalized = $"#{rgb:X6}";
        _mutex.Wait();
        try
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var next = isAdmin ? 0 : GetNextAllowed(db, tx, session, ip);
            if (next > now)
                return new(PlacementStatus.Cooldown, next, normalized);

            using (var command = db.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO Pixels (X, Y, Color) VALUES ($x, $y, $color)
                    ON CONFLICT (X, Y) DO UPDATE SET Color = excluded.Color;
                    INSERT INTO Placements (X, Y, Color, PlacedAtUnixMs)
                    VALUES ($x, $y, $color, $now);
                    """;
                command.Parameters.AddWithValue("$x", pixel.X);
                command.Parameters.AddWithValue("$y", pixel.Y);
                command.Parameters.AddWithValue("$color", rgb);
                command.Parameters.AddWithValue("$now", now);
                command.ExecuteNonQuery();
            }

            // Admin placements must not impose IP limits on other visitors.
            if (!isAdmin)
            {
                SetCooldown(db, tx, Hash("session", session), now);
                SetCooldown(db, tx, Hash("ip", ip), now);
            }
            tx.Commit();

            return new(PlacementStatus.Success,
                isAdmin ? 0 : now + CanvasSettings.SessionCooldownSeconds * 1000L,
                normalized);
        }
        finally { _mutex.Release(); }
    }

    private long GetNextAllowed(SqliteConnection db, SqliteTransaction? tx, string session, string ip)
    {
        long sessionLast = GetCooldown(db, tx, Hash("session", session));
        long ipLast = GetCooldown(db, tx, Hash("ip", ip));
        return Math.Max(
            sessionLast + CanvasSettings.SessionCooldownSeconds * 1000L,
            ipLast + CanvasSettings.IpCooldownSeconds * 1000L);
    }

    private string Hash(string kind, string identifier)
    {
        var bytes = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(kind + ":" + identifier));
        return Convert.ToHexString(bytes);
    }

    private static long GetCooldown(SqliteConnection db, SqliteTransaction? tx, string key)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT LastPlacedAtUnixMs FROM Cooldowns WHERE VisitorKey = $key";
        command.Parameters.AddWithValue("$key", key);
        var last = command.ExecuteScalar();
        return last is null or DBNull ? 0L : Convert.ToInt64(last);
    }

    private static void SetCooldown(SqliteConnection db, SqliteTransaction tx, string key, long now)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO Cooldowns (VisitorKey, LastPlacedAtUnixMs) VALUES ($key, $now)
            ON CONFLICT (VisitorKey) DO UPDATE SET LastPlacedAtUnixMs = excluded.LastPlacedAtUnixMs;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
