#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT_PATH="$PROJECT_ROOT/scripts/certbot_certificate.sh"

DOMAINS="${CERTBOT_DOMAINS:-amiiboapi.org,www.amiiboapi.org}"
PRIMARY_DOMAIN="${CERTBOT_PRIMARY_DOMAIN:-amiiboapi.org}"
CERTBOT_EMAIL="${CERTBOT_EMAIL:-}"
CERTBOT_LIVE_DIR="/etc/letsencrypt/live/$PRIMARY_DOMAIN"

DEST_FULLCHAIN="$PROJECT_ROOT/fullchain.pem"
DEST_PRIVKEY="$PROJECT_ROOT/privkey.pem"

CRON_FILE="/etc/cron.d/amiiboapi-certbot"
LOG_FILE="/var/log/amiiboapi-certbot.log"
CRON_CMD="/bin/bash $SCRIPT_PATH renew >> $LOG_FILE 2>&1"

run_as_root() {
  if [ "${EUID:-$(id -u)}" -eq 0 ]; then
    "$@"
  else
    sudo "$@"
  fi
}

sync_certificates_to_project_root() {
  local owner_group

  run_as_root test -f "$CERTBOT_LIVE_DIR/fullchain.pem"
  run_as_root test -f "$CERTBOT_LIVE_DIR/privkey.pem"

  if run_as_root test -f "$DEST_FULLCHAIN"; then
    owner_group="$(run_as_root stat -c '%U:%G' "$DEST_FULLCHAIN")"
  elif [ -n "${SUDO_USER:-}" ]; then
    owner_group="${SUDO_USER}:${SUDO_USER}"
  else
    owner_group="$(id -un):$(id -gn)"
  fi

  run_as_root cp "$CERTBOT_LIVE_DIR/fullchain.pem" "$DEST_FULLCHAIN"
  run_as_root cp "$CERTBOT_LIVE_DIR/privkey.pem" "$DEST_PRIVKEY"

  run_as_root chown "$owner_group" "$DEST_FULLCHAIN" "$DEST_PRIVKEY"
  run_as_root chmod 660 "$DEST_FULLCHAIN" "$DEST_PRIVKEY"
}

issue_certificate() {
  local certbot_args

  certbot_args=(certonly --standalone --non-interactive --agree-tos --domains "$DOMAINS")
  if [ -n "$CERTBOT_EMAIL" ]; then
    certbot_args+=(--email "$CERTBOT_EMAIL")
  else
    certbot_args+=(--register-unsafely-without-email)
  fi

  # --standalone uses an internal webserver and requires port 80 to be available.
  run_as_root certbot "${certbot_args[@]}"
  sync_certificates_to_project_root
}

renew_certificate() {
  run_as_root certbot renew --quiet
  sync_certificates_to_project_root
}

install_renewal_schedule() {
  local cron_line
  cron_line="0 3 * * * root $CRON_CMD"

  run_as_root touch "$LOG_FILE"
  run_as_root chmod 644 "$LOG_FILE"
  run_as_root sh -c "printf '%s\n' '$cron_line' > '$CRON_FILE'"
  run_as_root chmod 644 "$CRON_FILE"
}

usage() {
  cat <<EOF
Usage: $(basename "$0") [issue|renew|install-cron|all]

Commands:
  issue         Request/refresh the certificate for domains: $DOMAINS
  renew         Run certbot renew and re-copy certificates to project root
  install-cron  Install /etc/cron.d schedule for daily renewal checks
  all           issue + install-cron (default)

Environment variables:
  CERTBOT_DOMAINS         Comma-separated domain list (default: $DOMAINS)
  CERTBOT_PRIMARY_DOMAIN  Domain used under /etc/letsencrypt/live (default: $PRIMARY_DOMAIN)
  CERTBOT_EMAIL           Contact email for Let's Encrypt registration
EOF
}

main() {
  local command="${1:-all}"

  case "$command" in
    issue)
      issue_certificate
      ;;
    renew)
      renew_certificate
      ;;
    install-cron)
      install_renewal_schedule
      ;;
    all)
      issue_certificate
      install_renewal_schedule
      ;;
    -h|--help|help)
      usage
      ;;
    *)
      echo "Unknown command: $command" >&2
      usage
      exit 1
      ;;
  esac
}

main "$@"
