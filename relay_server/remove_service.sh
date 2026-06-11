#!/usr/bin/env bash
set -euo pipefail

SERVICE_NAME="${SERVICE_NAME:-c64-relay-server}"
SERVICE_USER="${SERVICE_USER:-c64relay}"
SERVICE_GROUP="${SERVICE_GROUP:-c64relay}"
INSTALL_DIR="${INSTALL_DIR:-/opt/c64-relay-server}"
CONFIG_DIR="${CONFIG_DIR:-/etc/c64-relay-server}"
LOG_DIR="${LOG_DIR:-/var/log/c64-relay-server}"
UNIT_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
PURGE=0

if [[ "${1:-}" == "--purge" ]]; then
    PURGE=1
fi

if [[ "${EUID}" -ne 0 ]]; then
    exec sudo \
        SERVICE_NAME="${SERVICE_NAME}" \
        SERVICE_USER="${SERVICE_USER}" \
        SERVICE_GROUP="${SERVICE_GROUP}" \
        INSTALL_DIR="${INSTALL_DIR}" \
        CONFIG_DIR="${CONFIG_DIR}" \
        LOG_DIR="${LOG_DIR}" \
        bash "$0" "$@"
fi

if command -v systemctl >/dev/null 2>&1; then
    if systemctl list-unit-files "${SERVICE_NAME}.service" >/dev/null 2>&1; then
        systemctl disable --now "${SERVICE_NAME}.service" >/dev/null 2>&1 || true
    fi
fi

rm -f "${UNIT_FILE}"
rm -rf "${INSTALL_DIR}"

if [[ "${PURGE}" -eq 1 ]]; then
    rm -rf "${CONFIG_DIR}" "${LOG_DIR}"
    if id -u "${SERVICE_USER}" >/dev/null 2>&1; then
        userdel "${SERVICE_USER}" >/dev/null 2>&1 || true
    fi
    if getent group "${SERVICE_GROUP}" >/dev/null; then
        groupdel "${SERVICE_GROUP}" >/dev/null 2>&1 || true
    fi
fi

if command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload
    systemctl reset-failed "${SERVICE_NAME}.service" >/dev/null 2>&1 || true
fi

echo "Removed ${SERVICE_NAME} service and ${INSTALL_DIR}."
if [[ "${PURGE}" -eq 0 ]]; then
    echo "Preserved config/certificates in ${CONFIG_DIR} and logs in ${LOG_DIR}."
    echo "Run with --purge to remove those as well."
else
    echo "Purged config/certificates, logs, and service user where possible."
fi
