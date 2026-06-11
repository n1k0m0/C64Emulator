#!/usr/bin/env bash
set -euo pipefail

SERVICE_NAME="${SERVICE_NAME:-c64-relay-server}"
SERVICE_USER="${SERVICE_USER:-c64relay}"
SERVICE_GROUP="${SERVICE_GROUP:-c64relay}"
INSTALL_DIR="${INSTALL_DIR:-/opt/c64-relay-server}"
CONFIG_DIR="${CONFIG_DIR:-/etc/c64-relay-server}"
LOG_DIR="${LOG_DIR:-/var/log/c64-relay-server}"
ENV_FILE="${CONFIG_DIR}/${SERVICE_NAME}.env"
UNIT_FILE="/etc/systemd/system/${SERVICE_NAME}.service"

DEFAULT_HOST="${C64_RELAY_HOST:-0.0.0.0}"
DEFAULT_PORT="${C64_RELAY_PORT:-6465}"
DEFAULT_CN="${C64_RELAY_CN:-C64RelayServer}"

if [[ "${EUID}" -ne 0 ]]; then
    exec sudo \
        SERVICE_NAME="${SERVICE_NAME}" \
        SERVICE_USER="${SERVICE_USER}" \
        SERVICE_GROUP="${SERVICE_GROUP}" \
        INSTALL_DIR="${INSTALL_DIR}" \
        CONFIG_DIR="${CONFIG_DIR}" \
        LOG_DIR="${LOG_DIR}" \
        C64_RELAY_HOST="${DEFAULT_HOST}" \
        C64_RELAY_PORT="${DEFAULT_PORT}" \
        C64_RELAY_CN="${DEFAULT_CN}" \
        bash "$0" "$@"
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER_SCRIPT="${SCRIPT_DIR}/c64_relay_server.py"

if [[ ! -f "${SERVER_SCRIPT}" ]]; then
    echo "Cannot find ${SERVER_SCRIPT}" >&2
    exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 is required." >&2
    exit 1
fi

if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemd/systemctl is required." >&2
    exit 1
fi

if ! getent group "${SERVICE_GROUP}" >/dev/null; then
    groupadd --system "${SERVICE_GROUP}"
fi

if ! id -u "${SERVICE_USER}" >/dev/null 2>&1; then
    useradd \
        --system \
        --gid "${SERVICE_GROUP}" \
        --home-dir "${INSTALL_DIR}" \
        --shell /usr/sbin/nologin \
        "${SERVICE_USER}"
fi

install -d -o root -g root -m 0755 "${INSTALL_DIR}"
install -m 0755 "${SERVER_SCRIPT}" "${INSTALL_DIR}/c64_relay_server.py"

install -d -o "${SERVICE_USER}" -g "${SERVICE_GROUP}" -m 0750 "${CONFIG_DIR}"
install -d -o "${SERVICE_USER}" -g "${SERVICE_GROUP}" -m 0750 "${LOG_DIR}"
touch "${LOG_DIR}/relay.log"
chown "${SERVICE_USER}:${SERVICE_GROUP}" "${LOG_DIR}/relay.log"
chmod 0640 "${LOG_DIR}/relay.log"

if [[ ! -f "${ENV_FILE}" ]]; then
    cat > "${ENV_FILE}" <<EOF
C64_RELAY_HOST=${DEFAULT_HOST}
C64_RELAY_PORT=${DEFAULT_PORT}
C64_RELAY_CERT=${CONFIG_DIR}/relay.crt
C64_RELAY_KEY=${CONFIG_DIR}/relay.key
C64_RELAY_CN=${DEFAULT_CN}
C64_RELAY_LOG=${LOG_DIR}/relay.log
EOF
    chmod 0644 "${ENV_FILE}"
fi

if ! grep -q '^C64_RELAY_LOG=' "${ENV_FILE}"; then
    printf '\nC64_RELAY_LOG=%s/relay.log\n' "${LOG_DIR}" >> "${ENV_FILE}"
fi

if grep -q '^C64_RELAY_PORT=6464$' "${ENV_FILE}"; then
    sed -i 's/^C64_RELAY_PORT=6464$/C64_RELAY_PORT=6465/' "${ENV_FILE}"
fi

cat > "${UNIT_FILE}" <<EOF
[Unit]
Description=C64 Emulator Relay Server
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=${SERVICE_USER}
Group=${SERVICE_GROUP}
WorkingDirectory=${INSTALL_DIR}
EnvironmentFile=${ENV_FILE}
Environment=PYTHONUNBUFFERED=1
ExecStart=/usr/bin/python3 -u ${INSTALL_DIR}/c64_relay_server.py --host \${C64_RELAY_HOST} --port \${C64_RELAY_PORT} --cert \${C64_RELAY_CERT} --key \${C64_RELAY_KEY} --cn \${C64_RELAY_CN} --log-file \${C64_RELAY_LOG}
Restart=on-failure
RestartSec=3
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=full
ReadWritePaths=${CONFIG_DIR} ${LOG_DIR}
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "${SERVICE_NAME}.service" >/dev/null
systemctl restart "${SERVICE_NAME}.service"

echo "Installed and started ${SERVICE_NAME}."
echo "Config: ${ENV_FILE}"
echo "Logs:"
echo "  journalctl -u ${SERVICE_NAME} -f"
echo "  tail -f ${LOG_DIR}/relay.log"
echo "Service control:"
echo "  systemctl status ${SERVICE_NAME}"
echo "  systemctl restart ${SERVICE_NAME}"
echo "  systemctl stop ${SERVICE_NAME}"
