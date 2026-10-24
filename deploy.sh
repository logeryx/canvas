#!/usr/bin/env bash
set -e

cd /home/logeryx/src/canvas

sudo systemctl stop canvas
dotnet publish -c Release -o /srv/canvas
sudo systemctl start canvas

sudo systemctl status canvas --no-pager
