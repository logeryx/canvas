#!/usr/bin/env bash
set -euo pipefail
mkdir -p wwwroot/lib
curl --fail --location --show-error --silent \
  'https://cdn.jsdelivr.net/npm/@microsoft/signalr@10.0.11/dist/browser/signalr.min.js' \
  --output wwwroot/lib/signalr.min.js
printf 'Installed local SignalR JavaScript client (%s bytes).\n' "$(wc -c < wwwroot/lib/signalr.min.js)"
