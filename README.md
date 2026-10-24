# Moisty Canvas — first version

A 256×256 shared pixel board for ASP.NET Core 10, SQLite and SignalR.

## What it does
- White canvas, 16-color palette. Click to place one pixel.
- Broadcasts pixel changes live using SignalR.
- Stores pixels and historical placements in SQLite.
- 30-second cooldown per persistent browser cookie and an additional 10-second per-IP cooldown. Both are enforced by the server; refreshing does not reset the timer.
- Anonymous: a determined visitor can bypass the limits with a different IP (or, after the shorter IP timeout, a new cookie). No-login rate limiting is not perfect.

## Copy into the existing source project

Extract all these files to `/home/logeryx/src/canvas/`, replacing the original `Program.cs` and `Canvas.csproj` created by `dotnet new web`. Existing `bin`/`obj` folders can stay. The app builds with .NET 10.

Run as `logeryx` (NOT as root):

```bash
cd ~/src/canvas
bash fetch-signalr.sh        # downloads the browser client into wwwroot/lib/
dotnet restore
sudo systemctl stop canvas
dotnet publish -c Release -o /srv/canvas
sudo systemctl start canvas
sudo systemctl status canvas
```

If you prefer deploying from macOS, copy the ZIP using `scp canvas-starter.zip moisty:~/` and then extract it with `unzip -o ~/canvas-starter.zip -d ~/src/canvas` on the VPS. No changes to nginx or the existing service unit are needed.

Verify at https://canvas.moisty.cz. Open two browsers and test live updates. Different browsers on the same IP share the 10-second IP safeguard.

## Database details

systemd `StateDirectory=canvas` makes `/var/lib/canvas` writable for the unprivileged `canvas` service. The app automatically creates:

```text
/var/lib/canvas/canvas.db       database
/var/lib/canvas/canvas.db-wal   optional SQLite WAL journal
/var/lib/canvas/canvas.db-shm   optional SQLite shared memory
/var/lib/canvas/cooldown.key    key for hashing cooldown identifiers
```

In SQLite, `Pixels` stores the current board, `Placements` stores the append-only history, and `Cooldowns` stores hashed visitor identifiers and last-placement timestamps.

Inspect safely while the server is running:

```bash
sudo sqlite3 /var/lib/canvas/canvas.db 'SELECT count(*) FROM Placements;'
sudo sqlite3 /var/lib/canvas/canvas.db 'SELECT X,Y,Color FROM Pixels LIMIT 10;'
```

Create a consistent backup (prefer this over copying `.db` alone while SQLite is active in WAL mode):

```bash
sudo sqlite3 /var/lib/canvas/canvas.db ".backup '/var/lib/canvas/canvas-backup.db'"
```

To download the backup to another machine you can copy it to a temporary directory that `logeryx` can access. Remember: a backup on the same VPS doesn't protect against VPS loss.

## Changing defaults

Edit `CanvasSettings` in `CanvasStore.cs`:

- `Width`, `Height` (changing dimensions will not delete the database but may hide out-of-bounds pixels)
- `SessionCooldownSeconds`, `IpCooldownSeconds`
- `Palette` (changing existing color order changes the appearance of saved pixels)

After changes: `dotnet publish -c Release -o /srv/canvas` (while stopped) and `sudo systemctl start canvas`.

## Assumptions / security

- nginx proxies requests to Kestrel on **127.0.0.1:5001** and sets `X-Forwarded-For`/`X-Forwarded-Proto`, as in our existing nginx config.
- `UseForwardedHeaders` trusts loopback proxy addresses (ASP.NET Core defaults). Do not expose the Kestrel port publicly.
- Cookie is `Secure`, so browser play requires HTTPS. `dotnet run` over HTTP is not a full test of per-browser cooldowns.
- The app intentionally stores a hashed IP identifier, not raw IPs, for the rate limit. Keep `cooldown.key` private.
- `Cooldowns` and `Placements` will grow over time; adding cleanup/retention is a sensible future improvement.
- This is a small hobby-project MVP, not a full anti-bot system. Add reverse-proxy request rate limits if the service becomes public/popular.
- Client-side JS is hosted locally after the `fetch-signalr.sh` step; no CDN needed at runtime.
