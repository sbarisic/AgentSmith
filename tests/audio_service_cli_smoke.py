"""Explicit published CLI smoke using real installed audio and a scripted loopback chat provider."""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import threading

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts/audio-service-acceptance"

class Script(BaseHTTPRequestHandler):
    requests = 0
    def log_message(self, *_):
        pass
    def reply(self, body, content_type="application/json", status=200):
        data = body.encode() if isinstance(body, str) else json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    def do_GET(self):
        if self.path == "/v1/models":
            self.reply({"data": [{"id": "scripted-audio-acceptance", "meta": {"n_ctx": 65536}}]})
        elif self.path == "/props":
            self.reply({"total_slots": 4})
        else:
            self.reply({}, status=404)
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        if self.path != "/v1/chat/completions":
            self.reply({}, status=404)
            return
        Script.requests += 1
        results = [json.loads(m["content"]) for m in body["messages"] if m.get("role") == "tool"]
        if not results:
            name = "audio_listen"
            args = dict(path="test01_20s.wav", question="Acceptance fixture: transcribe speech and identify sounds.",
                        startSeconds=0, durationSeconds=0)
        elif len(results) == 1:
            assert results[0].get("artifactId"), results[0]
            name = "audio_read"
            args = dict(artifactId=results[0]["artifactId"], view="speakers", cursor=0, maxCharacters=4000)
        else:
            name = None
        delta = ({"tool_calls": [{"index": 0, "id": "acceptance-" + str(len(results)), "type": "function",
                                  "function": {"name": name, "arguments": json.dumps(args)}}]}
                 if name else {"content": "Published service acceptance completed using retained speaker evidence."})
        chunk = {"choices": [{"index": 0, "delta": delta, "finish_reason": "tool_calls" if name else "stop"}]}
        self.reply("data: " + json.dumps(chunk) + "\n\ndata: [DONE]\n\n", "text/event-stream")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--smith", required=True)
    parser.add_argument("--data-dir", required=True)
    args = parser.parse_args()
    os.environ.pop("OPENAI_API_KEY", None)
    server = ThreadingHTTPServer(("127.0.0.1", 0), Script)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        result = subprocess.run([args.smith, "run", "--data-dir", args.data_dir, "--json", "--provider", "local",
            "--endpoint", f"http://127.0.0.1:{server.server_port}/v1", "--model", "scripted-audio-acceptance",
            "--workspace", str(ROOT / "tests"), "--mode", "Inspect", "--delegation", "manual",
            "--prompt", "Acceptance smoke: analyze test01_20s.wav, then read retained speaker evidence.",
            "--max-steps", "8"], capture_output=True, text=True, encoding="utf-8", timeout=180,
            creationflags=subprocess.CREATE_NO_WINDOW)
        OUT.mkdir(parents=True, exist_ok=True)
        (OUT / "published-smoke.ndjson").write_text(result.stdout, encoding="utf-8")
        assert result.returncode == 0, result.stdout[-4000:] + result.stderr[-1000:]
        events = [json.loads(line) for line in result.stdout.splitlines() if line.startswith("{")]
        completed = [e["payload"] for e in events if e["type"] == "tool.completed"]
        assert [t["name"] for t in completed] == ["audio_listen", "audio_read"], completed
        audio = json.loads(completed[0]["content"])
        assert audio["outcomes"]["transcription"] == "succeeded", audio["outcomes"]
        assert audio["service"]["helperProcessId"]
        assert Script.requests == 3
        record = dict(requests=Script.requests, audio=audio, conversationIds=list({e["conversationId"] for e in events if e.get("conversationId")}))
        (OUT / "published-smoke-evidence.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
        print("Published CLI audio and retained-speaker smoke passed.")
    finally:
        server.shutdown()
