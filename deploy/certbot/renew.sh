#!/bin/sh
set -eu

if [ -n "${HEROKU_APP_NAME:-}" ] || [ -n "${DYNO:-}" ]; then
  echo "Detected Heroku environment. SSL is managed by Heroku ACM; skipping Certbot renewal."
  exit 0
fi

if ! command -v certbot >/dev/null 2>&1; then
  echo "certbot is not installed; skipping SSL renewal."
  exit 0
fi

CERTBOT_STAGING="${CERTBOT_STAGING:-0}"
RELOAD_COMMAND="${CERTBOT_RELOAD_COMMAND:-}"

set -- --quiet
if [ "${CERTBOT_STAGING}" = "1" ]; then
  set -- "$@" --staging
fi
if [ -n "${RELOAD_COMMAND}" ]; then
  set -- "$@" --deploy-hook "${RELOAD_COMMAND}"
fi

certbot renew "$@"
