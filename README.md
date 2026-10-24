# Moisty Canvas — RGB / zoom edition

A 128×128 shared canvas. Custom RGB colors, 10-second cooldown, anonymous cookie + IP safeguards, SQLite history, SignalR updates, mouse-wheel zoom, shift/right/middle-drag panning and optional grid.

## Upgrade from first version (important)

**Back up the SQLite database before the first run.** On first launch, the app converts old 0–15 palette indexes to full RGB integers, once, inside an SQLite transaction (`PRAGMA user_version = 1`). This preserves existing placed pixels and placement history. Only pixels within the new 128×128 area are visible; old pixels outside it remain in the database, not deleted.

From your Mac (ZIP in Downloads):

```bash
scp ~/Downloads/canvas-rgb.zip moisty:~/
```

On the VPS as `logeryx`:

```bash
sudo systemctl stop canvas
sudo sqlite3 /var/lib/canvas/canvas.db ".backup '/var/lib/canvas/canvas-pre-rgb.db'"
unzip -o ~/canvas-rgb.zip -d ~/src/canvas
cd ~/src/canvas
bash fetch-signalr.sh    # only needed if wwwroot/lib/signalr.min.js is absent
dotnet restore
dotnet publish -c Release -o /srv/canvas
sudo systemctl start canvas
sudo systemctl status canvas
```

If your `dotnet publish` fails, restart the old service only after checking the build error. Do not overwrite a running service's binaries. For logs: `sudo journalctl -u canvas -n 80 --no-pager`.

No nginx or systemd changes required for normal visitors.

## Optional: private cooldown bypass

Admin mode is **disabled by default**. Enable it only if you want unlimited pixel placement in your own authenticated browser. Do not use IP allowlisting or hardcode the secret in C# or JavaScript.

Create a root-readable systemd environment file:

```bash
sudo install -d -o root -g root -m 700 /etc/canvas
sudo sh -c 'umask 077; printf "CANVAS_ADMIN_SECRET=%s\\n" "$(openssl rand -hex 32)" > /etc/canvas/admin.env'
sudo cat /etc/canvas/admin.env    # copy/store the key privately
```

Edit `/etc/systemd/system/canvas.service` and add this in its `[Service]` section:

```ini
EnvironmentFile=/etc/canvas/admin.env
```

Then reload and restart:

```bash
sudo systemctl daemon-reload
sudo systemctl restart canvas
```

Visit **https://canvas.moisty.cz/admin**, paste the generated key, then return to the canvas. A 30-day, HTTPS-only, HttpOnly, signed admin cookie enables cooldown bypass for that browser. If you change the key and restart the service, previous admin cookies become invalid. The secret never appears in frontend source or URLs. Keep the environment file and key private. Admin login attempts are limited to 5/min/IP; it does not constitute a full user-account system. To sign out, clear site cookies or POST to `/api/admin/logout` from that browser.

A visitor in another browser remains limited to 10 seconds per cookie and IP. Visitors on the same public IP share the per-IP 10-second restriction. An anonymous rate limit is not impossible to bypass with multiple IP addresses.

## Controls

- Left click / tap: place pixel.
- Wheel or trackpad scrolling while over canvas: zoom around cursor (100–2400%).
- Shift + left-drag, middle-drag or right-drag: pan zoomed canvas.
- `Show grid`: faint 1-device-pixel grid overlay, without modifying stored pixels.
- Color: use saturation/brightness field, hue slider, native color picker, hex entry, or RGB number fields.

## Storage / database

The systemd unit's `StateDirectory=canvas` creates `/var/lib/canvas` owned by `canvas`. The SQLite file is `/var/lib/canvas/canvas.db`. The app initializes/migrates it automatically. **Never manually change its schema to RGB.**

- `Pixels(X,Y,Color)`: RGB value is a single integer, e.g. pure red = 16711680 (`0xFF0000`).
- `Placements`: historical pixel edits, also migrated to RGB.
- `Cooldowns`: hashed anonymous visitor keys and timestamps.
- `cooldown.key`: private server key for those hashes.

Useful checks:

```bash
sudo sqlite3 /var/lib/canvas/canvas.db 'PRAGMA user_version;'
sudo sqlite3 /var/lib/canvas/canvas.db 'SELECT count(*) FROM Pixels;'
sudo sqlite3 /var/lib/canvas/canvas.db 'SELECT X,Y,printf("#%06X",Color) FROM Pixels LIMIT 10;'
```

Back up consistently with `.backup` rather than copying a live SQLite WAL file:

```bash
sudo sqlite3 /var/lib/canvas/canvas.db ".backup '/var/lib/canvas/canvas-backup.db'"
```

A backup **off** the VPS is still important against VPS loss.

## Tech & security

ASP.NET Core 10 + `Microsoft.Data.Sqlite` + SignalR. nginx forwards to the loopback listener `127.0.0.1:5001` and passes `X-Forwarded-For` and `X-Forwarded-Proto`. Kestrel should not bind to public ports. The admin cookie is host-only, Secure, HttpOnly, SameSite=Strict, signed and time-limited; unlock is checked against a high-entropy server-only secret.

This is a hobby project, not an anti-abuse platform. Consider an nginx request-rate limit if spam becomes an issue.
