"""Loopback-only Remote Doctor fixture. No external services, packages or credentials.

Modes, chosen by the first argument:

    json        a working Streamable HTTP server answering with plain JSON
    sse-open    answers with an event stream and then leaves it open
    redirect    answers every POST with a 302 (following it would falsely pass)
    oversized   declares a body far past the response limit
    slow        accepts the request and never answers
"""
import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

mode = sys.argv[1]


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def reply(self, status, body=b"", content_type="application/json", headers=None):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        for key, value in (headers or {}).items():
            self.send_header(key, value)
        self.end_headers()
        self.wfile.write(body)
        self.wfile.flush()

    def do_DELETE(self):
        self.reply(204)

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        if mode == "redirect":
            # Following this redirect would falsely pass the check.
            if self.path != "/destination":
                self.reply(302, headers={"Location": "/destination"})
                return
        if mode == "oversized":
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(8 * 1024 * 1024))
            self.end_headers()
            self.wfile.write(b"x")
            self.wfile.flush()
            return
        if mode == "slow":
            time.sleep(3)
            return
        request = json.loads(body)
        method = request["method"]
        if method == "notifications/initialized":
            self.reply(202)
            return
        if method == "initialize":
            result = {"protocolVersion": request["params"]["protocolVersion"],
                      "capabilities": {"tools": {}}, "serverInfo": {"name": "loopback", "version": "1"}}
        elif method == "tools/list":
            result = {"tools": [{"name": "fixture_read", "inputSchema": {"type": "object"}}]}
        else:
            self.reply(400)
            return
        message = json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}).encode()
        if mode == "sse-open":
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
            # Intentionally leave SSE open after a complete matching answer.
            self.wfile.write(b": heartbeat\n\ndata: " + message + b"\n\n")
            self.wfile.flush()
            time.sleep(3)
        else:
            self.reply(200, message)


server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
server.daemon_threads = True
print(server.server_port, flush=True)
server.serve_forever()
