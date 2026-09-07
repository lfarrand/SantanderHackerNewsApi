import json
from collections import Counter
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Lock


ITEMS = {
    101: {"id": 101, "type": "story", "title": "First, not best", "url": "https://fixture.invalid/101", "by": "alice", "time": 1700000000, "score": 5, "descendants": 1},
    103: {"id": 103, "type": "story", "title": "Tie, larger ID", "url": "https://fixture.invalid/103", "by": "carol", "time": 1700000000, "score": 150, "descendants": 3},
    102: {"id": 102, "type": "story", "title": "Tie, smaller ID", "url": "https://fixture.invalid/102", "by": "bob", "time": 1700000000, "score": 150, "descendants": 2},
    104: {"id": 104, "type": "story", "title": "Highest score, last upstream", "url": "https://fixture.invalid/104", "by": "dora", "time": 1700000000, "score": 900, "descendants": 4},
}
REQUESTS = Counter()
LOCK = Lock()


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        status = 200
        if self.path == "/health":
            payload = {"status": "ready"}
        elif self.path == "/__requests":
            with LOCK:
                payload = dict(REQUESTS)
        else:
            with LOCK:
                REQUESTS[self.path] += 1
            if self.path == "/v0/beststories.json":
                payload = [101, 103, 102, 104]
            else:
                payload = next((item for key, item in ITEMS.items() if self.path == f"/v0/item/{key}.json"), None)
                if payload is None:
                    status = 404
                    payload = {"error": "Unexpected fixture request", "path": self.path}
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 8080), Handler).serve_forever()