"""Same-origin runtime proof; requires Docker, no Flutter build or cloud resources."""

import http.server
import json
import pathlib
import subprocess
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request


class Kernel(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        body = json.dumps({"path": self.path, "host": self.headers.get("Host"),
                           "forwardedHost": self.headers.get("X-Forwarded-Host"),
                           "scheme": self.headers.get("X-Forwarded-Proto"),
                           "cookie": self.headers.get("Cookie")}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Set-Cookie", "session=proof; HttpOnly; Secure; SameSite=Lax; Path=/")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass


class NginxProxyFacts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.kernel = http.server.ThreadingHTTPServer(("0.0.0.0", 0), Kernel)
        threading.Thread(target=cls.kernel.serve_forever, daemon=True).start()
        cls.files = tempfile.TemporaryDirectory(prefix="intochat-nginx-proof-")
        pathlib.Path(cls.files.name, "index.html").write_text("SHELL", encoding="utf-8")
        shell = pathlib.Path(__file__).resolve().parents[1]
        command = ["docker", "run", "--rm", "-d", "-p", "127.0.0.1::80",
                   "-e", f"INTOCHATOS_KERNEL_UPSTREAM=http://host.docker.internal:{cls.kernel.server_port}",
                   "-e", "NGINX_ENVSUBST_FILTER=^INTOCHATOS_KERNEL_UPSTREAM$",
                   "--mount", f"type=bind,source={cls.files.name},target=/usr/share/nginx/html,readonly",
                   "--mount", f"type=bind,source={shell / 'nginx' / 'default.conf.template'},target=/etc/nginx/templates/default.conf.template,readonly",
                   "nginx:1.27-alpine"]
        cls.container = subprocess.check_output(command, text=True, stderr=subprocess.PIPE).strip()
        address = subprocess.check_output(["docker", "port", cls.container, "80/tcp"], text=True).strip()
        cls.base = "http://" + address
        for _ in range(60):
            try:
                urllib.request.urlopen(cls.base, timeout=1).close()
                return
            except (OSError, urllib.error.URLError):
                time.sleep(0.1)
        raise RuntimeError("nginx did not start")

    @classmethod
    def tearDownClass(cls):
        subprocess.run(["docker", "stop", cls.container], check=True, capture_output=True)
        cls.kernel.shutdown()
        cls.kernel.server_close()
        cls.files.cleanup()

    def test_root_stays_static(self):
        with urllib.request.urlopen(self.base) as response:
            self.assertEqual(response.read(), b"SHELL")

    def test_auth_identity_portal_and_transport_share_host_and_cookie(self):
        for path in ["/auth/handoff?t=QUERY-CANARY", "/identity/session", "/portal/assets/auth.js", "/neurons.Neurons/Call"]:
            with self.subTest(path=path):
                request = urllib.request.Request(self.base + path, headers={"Host": "os.example.test", "X-Forwarded-Proto": "https", "Cookie": "session=proof"})
                with urllib.request.urlopen(request) as response:
                    body = json.load(response)
                    self.assertEqual(body, {"path": path, "host": f"host.docker.internal:{self.kernel.server_port}", "forwardedHost": "os.example.test", "scheme": "https", "cookie": "session=proof"})
                    self.assertIn("HttpOnly", response.headers["Set-Cookie"])

    def test_proxy_logs_never_contain_query_tickets(self):
        urllib.request.urlopen(self.base + "/auth/handoff?t=QUERY-CANARY").close()
        logs = subprocess.check_output(["docker", "logs", self.container], text=True, stderr=subprocess.STDOUT)
        self.assertNotIn("QUERY-CANARY", logs)


if __name__ == "__main__":
    unittest.main()
