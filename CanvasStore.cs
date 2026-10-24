using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

public record PixelPlacement(int X, int Y, int Color);
public enum PlacementStatus { Success, Cooldown, Invalid }
public record PlacementResult(PlacementStatus Status, long NextAllowedAtUnixMs);

public static class CanvasSettings
{
    public const int Width = 256;
    public const int Height = 256;
    public const int SessionCooldownSeconds = 30;
    public const int IpCooldownSeconds = 10;

    // Index 0 is the initial white background and can also erase a pixel.
    public static readonly string[] Palette =
    [
        "#FFFFFF", "#191919", "#6B7280", "#E74C3C",
        "#F28C28", "#F5D547", "#59B85E", "#1C9F99",
        "#3681DC", "#654CC4", "#B04CBF", "#E47FB1",
        "#8B5E3C", "#E8BFA4", "#B7DCEC", "#B2B2B2"
    ];
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

        // This private key hashes cooldown identifiers so raw IPs aren't stored.
        var keyPath = Path.Combine(_directory, "cooldown.key");
        if (!File.Exists(keyPath))
            File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
        _key = File.ReadAllBytes(keyPath);
        if (_key.Length != 32)
            throw new InvalidOperationException("Invalid cooldown.key file (expected 32 bytes).");

        using var db = Open();
        using var command = db.CreateCommand();
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

    public object GetState(string session, string ip)
    {
        _mutex.Wait();
        try
        {
            using var db = Open();
            var pixels = new byte[CanvasSettings.Width * CanvasSettings.Height];
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT X, Y, Color FROM Pixels";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int x = reader.GetInt32(0), y = reader.GetInt32(1), color = reader.GetInt32(2);
                    if (x >= 0 && x < CanvasSettings.Width &&
                        y >= 0 && y < CanvasSettings.Height &&
                        color >= 0 && color < CanvasSettings.Palette.Length)
                        pixels[y * CanvasSettings.Width + x] = (byte)color;
                }
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new
            {
                width = CanvasSettings.Width,
                height = CanvasSettings.Height,
                palette = CanvasSettings.Palette,
                sessionCooldownSeconds = CanvasSettings.SessionCooldownSeconds,
                pixels = Convert.ToBase64String(pixels),
                nextAllowedAtUnixMs = GetNextAllowed(db, null, session, ip),
                serverNowUnixMs = now
            };
        }
        finally { _mutex.Release(); }
    }

    public PlacementResult PlacePixel(PixelPlacement pixel, string session, string ip)
    {
        if (pixel.X < 0 || pixel.X >= CanvasSettings.Width ||
            pixel.Y < 0 || pixel.Y >= CanvasSettings.Height ||
            pixel.Color < 0 || pixel.Color >= CanvasSettings.Palette.Length)
            return new(PlacementStatus.Invalid, 0);

        _mutex.Wait();
        try
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var next = GetNextAllowed(db, tx, session, ip);
            if (next > now)
                return new(PlacementStatus.Cooldown, next);

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
                command.Parameters.AddWithValue("$color", pixel.Color);
                command.Parameters.AddWithValue("$now", now);
                command.ExecuteNonQuery();
            }

            SetCooldown(db, tx, Hash("session", session), now);
            SetCooldown(db, tx, Hash("ip", ip), now);
            tx.Commit();

            return new(PlacementStatus.Success, now + CanvasSettings.SessionCooldownSeconds * 1000L);
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
