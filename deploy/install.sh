#!/usr/bin/env bash
# Run as root ON THE VM after copying a published build to /tmp/aegis-publish.tar.gz.
# Idempotent. The passcode file /etc/aegis/aegis.env must already exist (see aegis.env.example).
set -euo pipefail

TARBALL="${1:-/tmp/aegis-publish.tar.gz}"
SRC_DIR="$(cd "$(dirname "$0")" && pwd)"

id aegis-app >/dev/null 2>&1 || useradd --system --home-dir /var/lib/aegis --shell /usr/sbin/nologin aegis-app
[ -f /etc/aegis/aegis.env ] || { echo "missing /etc/aegis/aegis.env" >&2; exit 1; }
chown root:aegis-app /etc/aegis/aegis.env && chmod 640 /etc/aegis/aegis.env

# Atomic-ish app swap
rm -rf /opt/aegis.new && mkdir -p /opt/aegis.new
tar -xzf "$TARBALL" -C /opt/aegis.new
chown -R root:root /opt/aegis.new
systemctl stop aegis 2>/dev/null || true
rm -rf /opt/aegis.old; [ -d /opt/aegis ] && mv /opt/aegis /opt/aegis.old
mv /opt/aegis.new /opt/aegis

install -m 644 "$SRC_DIR/aegis.service" /etc/systemd/system/aegis.service
install -m 644 "$SRC_DIR/aegis-ytdlp-update.service" /etc/systemd/system/aegis-ytdlp-update.service
install -m 644 "$SRC_DIR/aegis-ytdlp-update.timer" /etc/systemd/system/aegis-ytdlp-update.timer
install -m 644 "$SRC_DIR/Caddyfile" /etc/caddy/Caddyfile

systemctl daemon-reload
systemctl enable --now aegis-ytdlp-update.timer
systemctl enable aegis
systemctl restart aegis
systemctl reload caddy || systemctl restart caddy
sleep 3
systemctl is-active aegis caddy
