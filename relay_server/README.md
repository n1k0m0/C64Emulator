# C64 Emulator Relay Server

This directory contains the optional public relay for C64 Emulator Relay Mode.
It is not part of the Windows installer.

## Run

```bash
python3 c64_relay_server.py --host 0.0.0.0 --port 6465
```

To restrict the relay to clients that know a shared relay password:

```bash
python3 c64_relay_server.py --host 0.0.0.0 --port 6465 --password "change-me"
```

The script uses TLS. If `relay.crt` and `relay.key` do not exist, it creates a
self-signed certificate with Python `cryptography` when available, or falls back
to `openssl`.

## Ubuntu systemd service

Install and start the relay as a system service:

```bash
cd relay_server
bash install_service.sh
```

The installer creates:

```text
/opt/c64-relay-server/c64_relay_server.py
/etc/c64-relay-server/c64-relay-server.env
/etc/systemd/system/c64-relay-server.service
/var/log/c64-relay-server/relay.log
```

Change host, port, certificate path, key path, certificate common name, or relay
password in:

```bash
sudo nano /etc/c64-relay-server/c64-relay-server.env
sudo systemctl restart c64-relay-server
```

Set `C64_RELAY_PASSWORD=` to an empty value to leave the relay open. Set a value
to require the same `RELAY PASSWORD` in the emulator network menu before a host
or client can register with the relay.

Useful service commands:

```bash
sudo systemctl status c64-relay-server
sudo systemctl restart c64-relay-server
sudo systemctl stop c64-relay-server
sudo systemctl start c64-relay-server
sudo journalctl -u c64-relay-server -f
sudo tail -f /var/log/c64-relay-server/relay.log
```

Remove the service while keeping certificates/config/logs:

```bash
bash remove_service.sh
```

Remove everything, including certificates/config/logs and the service user:

```bash
bash remove_service.sh --purge
```

For a domain install, point the emulator's Relay Host to your domain and Relay
Port to the exposed TLS port. The emulator uses trust-on-first-use certificate
pinning for the relay certificate and still encrypts each C64 session end to end
between the host emulator and the client emulator.

## Routing

Each C64 host registers one `Connection ID`. Clients that use the same
`Connection ID` are bridged to that host. A second host trying to register the
same id is rejected until the first host disconnects.
