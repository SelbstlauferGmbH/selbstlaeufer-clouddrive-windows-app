"""
CloudDrive WebDAV test server entrypoint.

Loads the WsgiDAV config, wraps the app with JSON request logging,
and starts a Cheroot WSGI server on 0.0.0.0:8080.

Startup lines written to stdout (one per line):
  {"ts":"...","source":"webdav-server","event":"startup","msg":"..."}
"""

import json
import signal
import sys
from datetime import datetime, timezone

import yaml
from cheroot import wsgi
from wsgidav.wsgidav_app import WsgiDAVApp

from log_middleware import JsonLogMiddleware

CONFIG_PATH = "/etc/wsgidav/wsgidav.yaml"
HOST = "0.0.0.0"
PORT = 8080


def _info(msg, **extra):
    ts = datetime.now(timezone.utc)
    entry = {
        "ts": ts.strftime("%Y-%m-%dT%H:%M:%S.") + f"{ts.microsecond // 1000:03d}Z",
        "source": "webdav-server",
        "event": "startup",
        "msg": msg,
        **extra,
    }
    print(json.dumps(entry), flush=True)


def main():
    with open(CONFIG_PATH) as f:
        config = yaml.safe_load(f)

    users = list(
        (config.get("simple_dc", {}).get("user_mapping", {}).get("*") or {}).keys()
    )
    mounts = list(config.get("provider_mapping", {}).keys())

    _info("Starting WebDAV test server", host=HOST, port=PORT,
          mounts=mounts, auth_users=users)

    inner = WsgiDAVApp(config)
    app = JsonLogMiddleware(inner, config)

    server = wsgi.Server((HOST, PORT), app, numthreads=10)

    def _shutdown(sig, frame):
        _info("Shutdown signal received, stopping server")
        server.stop()

    signal.signal(signal.SIGTERM, _shutdown)
    signal.signal(signal.SIGINT, _shutdown)

    _info("WebDAV server ready", url=f"http://localhost:{PORT}/")

    try:
        server.start()
    except KeyboardInterrupt:
        server.stop()

    _info("WebDAV server stopped")


if __name__ == "__main__":
    main()
