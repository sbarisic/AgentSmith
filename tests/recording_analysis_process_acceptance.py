"""Explicit local acceptance: kill only the analysis client created here, then resume."""
import argparse
import hashlib
import json
import pathlib
import subprocess
import time


def main():
    parser = argparse.ArgumentParser()
    for name in ("smith", "data-dir", "source", "output"):
        parser.add_argument("--" + name, type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    base = [str(args.smith), "audio"]
    suffix = ["--data-dir", str(args.data_dir), "--json"]
    log = args.output / "crash.ndjson"
    with log.open("w", encoding="utf-8") as output:
        child = subprocess.Popen(base + ["analyze", "file", str(args.source), "--no-summary"] + suffix,
                                 stdout=output, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            deadline = time.monotonic() + 240
            job_id = None
            while time.monotonic() < deadline:
                assert child.poll() is None, log.read_text(encoding="utf-8")
                for line in log.read_text(encoding="utf-8").splitlines():
                    try: event = json.loads(line)
                    except ValueError: continue
                    if event["type"] == "audio.analysis" and "id" in event["payload"]:
                        job_id = event["payload"]["id"]
                if job_id:
                    root = args.data_dir / "audio/analyses" / job_id
                    job = json.loads((root / "job.json").read_text(encoding="utf-8"))
                    if (root / "evidence-000000.json").exists() and "Submitted" in job["Chunks"][1]["Components"].values():
                        break
                time.sleep(.05)
            else: raise AssertionError("Never reached the second dispatch")
            first = hashlib.sha256((root / "evidence-000000.json").read_bytes()).hexdigest()
            child.kill(); child.wait(timeout=15)
        finally:
            if child.poll() is None: child.kill(); child.wait(timeout=15)
    def command(words, label):
        result = subprocess.run(base + words + suffix, capture_output=True, text=True, encoding="utf-8", timeout=300,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        (args.output / (label + ".ndjson")).write_text(result.stdout + result.stderr, encoding="utf-8")
        assert result.returncode in (0, 3), result.stdout + result.stderr
        return json.loads(result.stdout.splitlines()[-1])["payload"]["job"]
    status = command(["analysis", "status", job_id], "interrupted")
    assert status["state"] == "Interrupted", status
    held = command(["analysis", "resume", job_id], "resume-held")
    assert held["state"] == "CompletedWithGaps", held
    assert held["chunks"][1]["components"]["transcription"] in ("Submitted", "Uncertain")
    resumed = command(["analysis", "resume", job_id, "--retry-uncertain"], "resume-explicit")
    assert resumed["state"] == "Completed", resumed
    assert hashlib.sha256((root / "evidence-000000.json").read_bytes()).hexdigest() == first
    report = dict(analysisId=job_id, interruptedState=status["state"], ordinaryResumeState=held["state"],
                  explicitRetryState=resumed["state"], firstEvidenceUnchanged=True, processedSeconds=resumed["processedSeconds"])
    (args.output / "acceptance.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__": main()
