#!/bin/bash
# Signs the ELF files of an installed /opt/mailclient for the closed software environment (ЗПС) of
# Astra Linux SE. Run on an Astra machine with bsign and the organization's signing key (gpg) set up;
# the public key must be installed on the target computers (/etc/digsig/keys) by the administrator.
#   sudo packaging/linux/sign-zps.sh [/opt/mailclient]
# Astra Linux SE 1.8 also offers bsign-integrator for whole packages.
set -euo pipefail
DIR="${1:-/opt/mailclient}"
command -v bsign >/dev/null || { echo "bsign не найден (пакет bsign из репозитория Astra Linux)"; exit 1; }
find "$DIR" -type f | while read -r f; do
  if head -c 4 "$f" | grep -q $'\x7fELF'; then
    bsign --sign "$f" && echo "подписан: $f"
  fi
done
