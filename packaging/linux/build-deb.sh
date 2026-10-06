#!/bin/bash
# Builds mailclient_<version>_amd64.deb from a self-contained linux-x64 publish folder.
#   packaging/linux/build-deb.sh <version> <publish dir> <output dir>
# Install: sudo apt install ./mailclient_<version>_amd64.deb   (apt resolves the dependencies)
set -euo pipefail
VERSION="$1"; SRC="$2"; OUT="$3"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(mktemp -d)"
PKG="$ROOT/mailclient_${VERSION}_amd64"

install -d "$PKG/DEBIAN" "$PKG/opt/mailclient" "$PKG/usr/bin" "$PKG/usr/share/applications" \
           "$PKG/usr/share/icons/hicolor/256x256/apps" "$PKG/usr/share/icons/hicolor/48x48/apps" \
           "$PKG/usr/share/doc/mailclient"
cp -a "$SRC/." "$PKG/opt/mailclient/"
chmod 755 "$PKG/opt/mailclient/mailclient"
ln -s /opt/mailclient/mailclient "$PKG/usr/bin/mailclient"
install -m 644 "$HERE/mailclient.desktop" "$PKG/usr/share/applications/mailclient.desktop"
install -m 644 "$HERE/../../src/MailClient.Linux/Assets/mailclient.png" "$PKG/usr/share/icons/hicolor/256x256/apps/mailclient.png"
install -m 644 "$HERE/../../src/MailClient.Linux/Assets/mailclient-48.png" "$PKG/usr/share/icons/hicolor/48x48/apps/mailclient.png"
install -m 644 "$HERE/policy.example.json" "$PKG/usr/share/doc/mailclient/policy.example.json"
install -m 644 "$HERE/../../THIRD-PARTY-NOTICES.txt" "$PKG/usr/share/doc/mailclient/copyright"

SIZE=$(du -sk "$PKG/opt" | cut -f1)
cat > "$PKG/DEBIAN/control" <<CONTROL
Package: mailclient
Version: ${VERSION}
Architecture: amd64
Maintainer: MailClient <mailclient@localhost>
Installed-Size: ${SIZE}
Section: mail
Priority: optional
Depends: libc6 (>= 2.27), libgcc-s1 | libgcc1, libstdc++6, zlib1g, libssl3 | libssl1.1,
 libicu76 | libicu74 | libicu72 | libicu67 | libicu63, libfontconfig1, libx11-6, libice6, libsm6,
 libxrandr2, libxi6, libxcursor1
Recommends: libwebkit2gtk-4.1-0 | libwebkit2gtk-4.0-37, libgtk-3-0, libsecret-tools, libnotify-bin, xdg-utils,
 libgssapi-krb5-2
Description: Корпоративная почта — клиент Microsoft Exchange и IMAP/SMTP
 Почтовый клиент для Microsoft Exchange Server (EWS) и серверов IMAP/SMTP:
 папки, синхронизация, работа без связи, вложения, приглашения на встречи.
 Версия для Astra Linux Special Edition 1.7/1.8, Debian и Ubuntu.
 .
 Без WebKitGTK письма показываются в текстовом виде.
CONTROL

cat > "$PKG/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q /usr/share/applications || true; fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true; fi
exit 0
POSTINST
cp "$PKG/DEBIAN/postinst" "$PKG/DEBIAN/postrm"
chmod 755 "$PKG/DEBIAN/postinst" "$PKG/DEBIAN/postrm"

mkdir -p "$OUT"
dpkg-deb --root-owner-group -Zxz --build "$PKG" "$OUT/mailclient_${VERSION}_amd64.deb"
rm -rf "$ROOT"
echo "$OUT/mailclient_${VERSION}_amd64.deb"
