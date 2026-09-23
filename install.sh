#!/bin/bash
# Installs the built plugin into Jellyfin and restarts the server. Run with sudo.
set -euxo pipefail
SRC="$(dirname "$(readlink -f "$0")")/bin/Release/net10.0"
DEST=/var/lib/jellyfin/plugins/XmltvCleaner_1.0.0.0
mkdir -p "$DEST"
cp "$SRC/Jellyfin.Plugin.XmltvCleaner.dll" "$SRC/meta.json" "$SRC/icon.png" "$DEST/"
chown -R jellyfin:jellyfin "$DEST"
ls -la "$DEST"
systemctl restart jellyfin
