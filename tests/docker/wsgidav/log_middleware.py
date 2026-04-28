"""
JSON request/response logger for the CloudDrive E2E WebDAV test server.

Wraps the WsgiDAV WSGI app and emits one JSON line per request to stdout:

  {"ts":"2024-01-01T12:00:00.123Z","source":"webdav-server","method":"PUT",
   "path":"/e2e-test.txt","status":"201","duration_ms":12,"auth_user":"testuser"}

Fields:
  ts            ISO-8601 UTC timestamp (millisecond precision)
  source        always "webdav-server"
  method        HTTP method (PROPFIND / GET / PUT / DELETE / MOVE / MKCOL / OPTIONS)
  path          URL path
  status        HTTP status code string ("200", "207", "404", …)
  duration_ms   round-trip time in milliseconds
  content_length request Content-Length header (omitted if absent)
  depth         WebDAV Depth header (omitted if absent)
  auth_user     authenticated user name (omitted if absent / not yet set)
  destination   WebDAV Destination header for MOVE/COPY (omitted if absent)
  error         exception message if the inner app raised (omitted normally)
"""

import json
import os
import time
from datetime import datetime, timezone


class JsonLogMiddleware:
    """Standard WSGI middleware. Compatible with WsgiDAV 4.x."""

    def __init__(self, next_app, config=None):
        self.next_app = next_app
        self.config = config or {}
        self.locking_disabled = os.environ.get(
            "CLOUDDRIVE_WEBDAV_DISABLE_LOCKS", ""
        ).lower() in ("1", "true", "yes", "on")

    def __call__(self, environ, start_response):
        t0 = time.monotonic()
        status_holder = [None]

        if self.locking_disabled and environ.get("REQUEST_METHOD") in ("LOCK", "UNLOCK"):
            body = b"WebDAV locking is disabled for this test server.\n"
            status = "405 Method Not Allowed"
            headers = [
                ("Content-Type", "text/plain; charset=utf-8"),
                ("Content-Length", str(len(body))),
                ("Allow", "OPTIONS, PROPFIND, GET, HEAD, PUT, DELETE, MKCOL, MOVE, COPY"),
            ]
            start_response(status, headers)
            self._emit(environ, status, t0, "locking disabled by test server")
            return [body]

        def capturing_start_response(status, headers, exc_info=None):
            status_holder[0] = status
            return start_response(status, headers, exc_info)

        error_msg = None
        try:
            result = self.next_app(environ, capturing_start_response)
        except Exception as exc:
            error_msg = str(exc)
            self._emit(environ, "500 Internal Server Error", t0, error_msg)
            raise

        self._emit(environ, status_holder[0], t0, error_msg)
        return result

    # ── internal ──────────────────────────────────────────────────────────────

    def _emit(self, environ, status, t0, error=None):
        ts = datetime.now(timezone.utc)
        status_code = status.split(" ", 1)[0] if status else "?"

        entry = {
            "ts": ts.strftime("%Y-%m-%dT%H:%M:%S.")
                  + f"{ts.microsecond // 1000:03d}Z",
            "source": "webdav-server",
            "method": environ.get("REQUEST_METHOD", "?"),
            "path":   environ.get("PATH_INFO", "/"),
            "status": status_code,
            "duration_ms": round((time.monotonic() - t0) * 1000, 1),
        }

        # Optional fields — omit when absent to keep lines tidy
        for key, env_key in (
            ("content_length", "CONTENT_LENGTH"),
            ("depth",          "HTTP_DEPTH"),
            ("destination",    "HTTP_DESTINATION"),
        ):
            val = environ.get(env_key)
            if val:
                entry[key] = val

        auth_user = environ.get("wsgidav.auth.user_name")
        if auth_user:
            entry["auth_user"] = auth_user

        if error:
            entry["error"] = error

        print(json.dumps(entry), flush=True)
