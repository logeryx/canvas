using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using System.Net;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

// Trust forwarded client IP and scheme only from the local nginx reverse proxy.
// ASP.NET Core defaults already trust loopback; do NOT clear KnownProxies.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
});

builder.Services.AddSignalR();
builder.Services.AddSingleton<CanvasStore>();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();

// The SQLite database and tables are created on the first launch.
var store = app.Services.GetRequiredService<CanvasStore>();
store.Initialize();

app.MapGet("/api/state", (HttpContext context, CanvasStore db) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var session = AnonymousVisitor.GetSession(context);
    return Results.Ok(db.GetState(session, AnonymousVisitor.GetIp(context)));
});

app.MapPost("/api/pixel", async (
    PixelPlacement request,
    HttpContext context,
    CanvasStore db,
    IHubContext<CanvasHub> hub) =>
{
    context.Response.Headers.CacheControl = "no-store";

    // JSON requests from the same site only. This prevents ordinary cross-site form abuse;
    // it is not a substitute for server-side cooldown enforcement.
    var origin = context.Request.Headers.Origin.ToString();
    if (origin.Length != 0 &&
        !string.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var result = db.PlacePixel(
        request,
        AnonymousVisitor.GetSession(context),
        AnonymousVisitor.GetIp(context));

    if (result.Status == PlacementStatus.Invalid)
        return Results.BadRequest(new { error = "Invalid coordinate or color." });

    if (result.Status == PlacementStatus.Cooldown)
        return Results.Json(new
        {
            error = "Cooldown active.",
            nextAllowedAtUnixMs = result.NextAllowedAtUnixMs,
            serverNowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }, statusCode: StatusCodes.Status429TooManyRequests);

    await hub.Clients.All.SendAsync("PixelChanged", request.X, request.Y, request.Color);
    return Results.Ok(new
    {
        nextAllowedAtUnixMs = result.NextAllowedAtUnixMs,
        serverNowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    });
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

    public static string GetIp(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        return address is null ? "unknown" :
            (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }
}
