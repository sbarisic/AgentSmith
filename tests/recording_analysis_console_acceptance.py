"""Real Windows Ctrl+C: cancel our standalone analysis and retain a resumable job."""
import argparse
import ctypes
import json
import pathlib
import subprocess
import time


def main():
    parser = argparse.ArgumentParser()
    for name in ("smith", "data-dir", "source", "output"):
        parser.add_argument("--" + name, type=pathlib.Path, required=True)
    args = parser.parse_args(); args.output.mkdir(parents=True, exist_ok=True)
    startup = subprocess.STARTUPINFO(); startup.dwFlags = subprocess.STARTF_USESHOWWINDOW; startup.wShowWindow = 0
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    log = args.output / "cancel.ndjson"
    with log.open("w", encoding="utf-8") as output:
        child = subprocess.Popen([str(args.smith), "audio", "analyze", "file", str(args.source), "--no-summary", "--data-dir", str(args.data_dir), "--json"],
                                 stdin=subprocess.DEVNULL, stdout=output, stderr=subprocess.STDOUT,
                                 creationflags=subprocess.CREATE_NEW_CONSOLE, startupinfo=startup)
        try:
            kernel.FreeConsole()
            for _ in range(100):
                if kernel.AttachConsole(child.pid): break
                time.sleep(.05)
            else: raise AssertionError("Cannot attach to owned console")
            kernel.SetConsoleCtrlHandler(None, True)
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                assert child.poll() is None
                events = []
                for line in log.read_text(encoding="utf-8").splitlines():
                    try: events.append(json.loads(line))
                    except ValueError: pass
                ids = [e["payload"]["id"] for e in events if e["type"] == "audio.analysis" and "id" in e["payload"]]
                if ids:
                    job_path = args.data_dir / "audio/analyses" / ids[0] / "job.json"
                    job = json.loads(job_path.read_text(encoding="utf-8"))
                    if any("Submitted" in c["Components"].values() for c in job["Chunks"]): break
                time.sleep(.05)
            else: raise AssertionError("No submitted request")
            assert kernel.GenerateConsoleCtrlEvent(0, 0)
            code = child.wait(timeout=20)
            job = json.loads(job_path.read_text(encoding="utf-8"))
            assert job["State"] == "Paused", job
            assert "Cancelled" in job["Error"], job
            report = dict(analysisId=job["Id"], state=job["State"], exitCode=code, error=job["Error"], consoleCtrlC=True)
            (args.output / "acceptance.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        finally:
            if child.poll() is None: child.kill(); child.wait(timeout=15)
            kernel.FreeConsole()
    print(json.dumps(report, indent=2))


if __name__ == "__main__": main()
