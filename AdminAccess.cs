using System.Globalization;
using System.Security.Cryptography;
using System.Text;

public sealed class AdminAccess
{
    private const string CookieName = "__Host-canvas-admin";
    private static readonly TimeSpan Duration = TimeSpan.FromDays(30);
    private readonly byte[]? _secret;

    public AdminAccess()
    {
        var secret = Environment.GetEnvironmentVariable("CANVAS_ADMIN_SECRET");
        if (string.IsNullOrEmpty(secret)) return; // Admin mode is opt-in.
        if (secret.Length < 32)
            throw new InvalidOperationException("CANVAS_ADMIN_SECRET must be at least 32 characters.");
        _secret = Encoding.UTF8.GetBytes(secret);
    }

    public bool Enabled => _secret is not null;

    public bool TryUnlock(HttpContext context, string? candidate)
    {
        if (_secret is null || candidate is null || candidate.Length > 1024) return false;
        var actual = SHA256.HashData(_secret);
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        if (!CryptographicOperations.FixedTimeEquals(actual, supplied)) return false;

        var expires = DateTimeOffset.UtcNow.Add(Duration).ToUnixTimeSeconds();
        var payload = expires.ToString(CultureInfo.InvariantCulture);
        var sig = Signature(payload);
        context.Response.Cookies.Append(CookieName, $"{payload}.{sig}", new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = Duration,
            IsEssential = true
        });
        return true;
    }

    public bool IsAdmin(HttpContext context)
    {
        if (_secret is null || !context.Request.Cookies.TryGetValue(CookieName, out var cookie)) return false;
        int separator = cookie.IndexOf('.');
        if (separator < 1 || separator > 20 || cookie.Length != separator + 1 + 64) return false;
        var payload = cookie[..separator];
        if (!long.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out long expires)) return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expires <= now || expires > now + (long)Duration.TotalSeconds) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(cookie[(separator + 1)..]),
            Encoding.ASCII.GetBytes(Signature(payload)));
    }

    public void SignOut(HttpContext context) => context.Response.Cookies.Delete(CookieName, new CookieOptions
    {
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/"
    });

    private string Signature(string payload) => Convert.ToHexString(HMACSHA256.HashData(
        _secret!, Encoding.UTF8.GetBytes("canvas-admin-v1:" + payload)));
}

public record AdminUnlockRequest(string Secret);
