#!/bin/sh
set -eu

ENABLE_CERTBOT_AUTO_SSL="${ENABLE_CERTBOT_AUTO_SSL:-1}"

if [ "${ENABLE_CERTBOT_AUTO_SSL}" = "1" ]; then
  /bin/sh /usr/src/app/deploy/certbot/bootstrap.sh || echo "Certbot bootstrap failed; continuing startup."
  if command -v cron >/dev/null 2>&1; then
    service cron start >/dev/null 2>&1 || cron
  fi
fi

exec python ./app.py
