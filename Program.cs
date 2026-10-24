using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Trust forwarding only from nginx on the same host; do not clear KnownProxies.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
});

builder.Services.AddSignalR();
builder.Services.AddSingleton<CanvasStore>();
builder.Services.AddSingleton<AdminAccess>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("admin-unlock", context => RateLimitPartition.GetFixedWindowLimiter(
        AnonymousVisitor.GetIp(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseRouting();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

// SQLite tables are created/migrated at startup.
var store = app.Services.GetRequiredService<CanvasStore>();
store.Initialize();

app.MapGet("/api/state", (HttpContext context, CanvasStore db, AdminAccess admin) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var session = AnonymousVisitor.GetSession(context);
    return Results.Ok(db.GetState(session, AnonymousVisitor.GetIp(context), admin.IsAdmin(context)));
});

app.MapPost("/api/pixel", async (
    PixelPlacement request,
    HttpContext context,
    CanvasStore db,
    AdminAccess admin,
    IHubContext<CanvasHub> hub) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!AnonymousVisitor.SameOrigin(context))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    var result = db.PlacePixel(request,
        AnonymousVisitor.GetSession(context),
        AnonymousVisitor.GetIp(context),
        admin.IsAdmin(context));

    if (result.Status == PlacementStatus.Invalid)
        return Results.BadRequest(new { error = "Invalid coordinate or color (use #RRGGBB)." });

    if (result.Status == PlacementStatus.Cooldown)
        return Results.Json(new
        {
            error = "Cooldown active.",
            nextAllowedAtUnixMs = result.NextAllowedAtUnixMs,
            serverNowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }, statusCode: StatusCodes.Status429TooManyRequests);

    await hub.Clients.All.SendAsync("PixelChanged", request.X, request.Y, result.Color);
    return Results.Ok(new
    {
        nextAllowedAtUnixMs = result.NextAllowedAtUnixMs,
        serverNowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    });
});

app.MapGet("/admin", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "admin.html"), "text/html"));

app.MapPost("/api/admin/unlock", (AdminUnlockRequest request, HttpContext context, AdminAccess admin) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!AnonymousVisitor.SameOrigin(context)) return Results.StatusCode(403);
    if (!admin.Enabled) return Results.NotFound();
    return admin.TryUnlock(context, request.Secret)
        ? Results.Ok(new { ok = true })
        : Results.Unauthorized();
}).RequireRateLimiting("admin-unlock");

app.MapPost("/api/admin/logout", (HttpContext context, AdminAccess admin) =>
{
    if (!AnonymousVisitor.SameOrigin(context)) return Results.StatusCode(403);
    admin.SignOut(context);
    return Results.Ok(new { ok = true });
});

app.MapHub<CanvasHub>("/hub");
app.Run();

public sealed class CanvasHub : Hub { }

public static class AnonymousVisitor
{
    private const string CookieName = "__Host-canvas-visitor";

    public static string GetSession(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var value) &&
            value is { Length: 64 } && value.All(Uri.IsHexDigit))
            return value;

        var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        context.Response.Cookies.Append(CookieName, session, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = TimeSpan.FromDays(180),
            IsEssential = true
        });
        return session;
    }

    public static bool SameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin,
            $"{context.Request.Scheme}://{context.Request.Host}",
            StringComparison.OrdinalIgnoreCase);
    }

    public static string GetIp(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        return address is null ? "unknown" :
            (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }
}
