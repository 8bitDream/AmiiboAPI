#!/bin/sh
set -eu

DEFAULT_DOMAIN="amiiboapi.org"
CERTBOT_DOMAIN="${CERTBOT_DOMAIN:-$DEFAULT_DOMAIN}"
CERTBOT_EMAIL="${CERTBOT_EMAIL:-ssl-admin@${CERTBOT_DOMAIN}}"
CERTBOT_WEBROOT="${CERTBOT_WEBROOT:-/var/www/certbot}"
CERTBOT_STAGING="${CERTBOT_STAGING:-0}"

if [ -n "${HEROKU_APP_NAME:-}" ] || [ -n "${DYNO:-}" ]; then
  echo "Detected Heroku environment. SSL is managed by Heroku ACM; skipping Certbot."
  exit 0
fi

if ! command -v certbot >/dev/null 2>&1; then
  echo "certbot is not installed; skipping SSL bootstrap."
  exit 0
fi

HOSTING_LOCATION="unknown"
if [ -n "${ECS_CONTAINER_METADATA_URI:-}" ] || [ -n "${ECS_CONTAINER_METADATA_URI_V4:-}" ]; then
  HOSTING_LOCATION="aws-ecs"
elif [ -n "${AWS_EXECUTION_ENV:-}" ]; then
  HOSTING_LOCATION="aws-container"
elif [ -n "${KUBERNETES_SERVICE_HOST:-}" ]; then
  HOSTING_LOCATION="kubernetes"
else
  IMDS_TOKEN="$(curl -fsS --max-time 1 -X PUT -H "X-aws-ec2-metadata-token-ttl-seconds: 21600" http://169.254.169.254/latest/api/token || true)"
  if [ -n "${IMDS_TOKEN}" ] && curl -fsS --max-time 1 -H "X-aws-ec2-metadata-token: ${IMDS_TOKEN}" http://169.254.169.254/latest/meta-data/ >/dev/null 2>&1; then
    HOSTING_LOCATION="aws-ec2"
  fi
fi

echo "Detected hosting location: ${HOSTING_LOCATION}"
echo "Using certificate domain: ${CERTBOT_DOMAIN}"

mkdir -p "${CERTBOT_WEBROOT}/.well-known/acme-challenge"

set -- \
  --non-interactive \
  --agree-tos \
  --email "${CERTBOT_EMAIL}" \
  --webroot \
  -w "${CERTBOT_WEBROOT}" \
  -d "${CERTBOT_DOMAIN}" \
  --keep-until-expiring
if [ "${CERTBOT_STAGING}" = "1" ]; then
  set -- "$@" --staging
fi

certbot certonly "$@"

mkdir -p /etc/cron.d /var/log
cat <<EOF >/etc/cron.d/certbot-renew
0 3,15 * * * root /bin/sh /usr/src/app/deploy/certbot/renew.sh >> /var/log/certbot-renew.log 2>&1
EOF
chmod 0644 /etc/cron.d/certbot-renew
echo "Certbot bootstrap completed and renewal cron configured."
