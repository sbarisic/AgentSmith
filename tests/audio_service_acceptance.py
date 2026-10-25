"""Explicit Windows process/real-model acceptance. Never installs models or calls a chat provider.

Build Release first. Usage:
  python tests/audio_service_acceptance.py --data-dir <installed Smith data> [--real]
Evidence stays in artifacts/audio-service-acceptance.
"""
import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts/audio-service-acceptance"
CLI = ROOT / "src/AgentSmith.Cli/bin/Release/net10.0/smith.dll"
FIXTURE = ROOT / "tests/AgentSmith.ProcessFixture/bin/Release/net10.0/AgentSmith.ProcessFixture.dll"
FLAGS = subprocess.CREATE_NO_WINDOW

def launch(args):
    return subprocess.Popen(["dotnet", *map(str, args)], cwd=ROOT, stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                            encoding="utf-8", creationflags=FLAGS)

def finish(process, timeout=180):
    stdout, stderr = process.communicate(timeout=timeout)
    if process.returncode:
        raise RuntimeError(f"Process {process.pid} failed ({process.returncode}): {stderr[-2000:]}\n{stdout[-1000:]}")
    return json.loads(stdout.strip().splitlines()[-1])

def control(data, action, *extra):
    p = launch([CLI, "service", action, "--data-dir", data, "--json", *extra])
    stdout, stderr = p.communicate(timeout=25)
    value = json.loads(stdout.strip().splitlines()[-1])
    if value["type"] != "service.status":
        raise RuntimeError(str(value) + stderr)
    return value["payload"]

def until(check, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = check()
        if result:
            return result
        time.sleep(.05)
    raise TimeoutError("Acceptance condition did not become true")

class Entry(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("usage", wintypes.DWORD), ("pid", wintypes.DWORD),
                ("heap", ctypes.c_size_t), ("module", wintypes.DWORD), ("threads", wintypes.DWORD),
                ("parent", wintypes.DWORD), ("priority", wintypes.LONG), ("flags", wintypes.DWORD),
                ("name", wintypes.WCHAR * 260)]

class Counters(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("faults", wintypes.DWORD),
                *[(name, ctypes.c_size_t) for name in
                  ("peakWorking", "working", "peakPaged", "paged", "peakNonpaged", "nonpaged", "pagefile", "peakPagefile", "private")]]

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
k32.OpenProcess.restype = wintypes.HANDLE
k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k32.CloseHandle.argtypes = [wintypes.HANDLE]
k32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
k32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
k32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]

def processes():
    snapshot = k32.CreateToolhelp32Snapshot(2, 0)
    entry = Entry(); entry.size = ctypes.sizeof(entry)
    result = {}
    try:
        more = k32.Process32FirstW(snapshot, ctypes.byref(entry))
        while more:
            result[entry.pid] = {"parent": entry.parent, "name": entry.name}
            more = k32.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        k32.CloseHandle(snapshot)
    return result

def tree_memory(roots):
    inventory = processes()
    selected = set(roots)
    while True:
        children = {pid for pid, item in inventory.items() if item["parent"] in selected}
        if children <= selected:
            break
        selected |= children
    details = []
    for pid in selected:
        handle = k32.OpenProcess(0x410, False, pid)
        if not handle:
            continue
        try:
            counters = Counters(); counters.size = ctypes.sizeof(counters)
            if psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.size):
                details.append(dict(pid=pid, name=inventory.get(pid, {}).get("name"), working=counters.working, private=counters.private))
        finally:
            k32.CloseHandle(handle)
    return dict(working=sum(p["working"] for p in details), private=sum(p["private"] for p in details), processes=details)

def terminate(pid):
    handle = k32.OpenProcess(1, False, pid)
    if not handle:
        raise OSError("Cannot open owned acceptance process")
    try:
        if not k32.TerminateProcess(handle, 1):
            raise OSError("Cannot terminate owned acceptance process")
    finally:
        k32.CloseHandle(handle)

def fake_acceptance(report):
    data = OUT / "fake-service"
    host = launch([FIXTURE, "--audio-host", data])
    owned = []
    try:
        assert host.stdout.readline().strip() == "ready"
        until(lambda: control(data, "status")["available"])
        inputs = ROOT / "tests/test01_20s.wav"
        jobs = [launch([FIXTURE, "--audio-client", data, inputs, 1]) for _ in range(3)]
        owned.extend(jobs)
        results = [finish(p) for p in jobs]
        assert all(r["success"] for r in results)
        assert sorted(r["result"]["call"] for r in results) == [1, 2, 3]
        report["three_process_fifo"] = results
        # Reservations consume the global limit before any audio is uploaded.
        reservations = []
        for _ in range(8):
            p = launch([FIXTURE, "--audio-reserve", data, 4]); owned.append(p); reservations.append(p)
            replies = json.loads(p.stdout.readline())
            assert replies["connected"] and all(r["Type"] == "accepted" for r in replies["replies"]), replies
        extra = launch([FIXTURE, "--audio-reserve", data, 1]); owned.append(extra)
        reply = json.loads(extra.stdout.readline())
        assert reply["replies"][0]["Type"] == "error" and "queue is full" in reply["replies"][0]["Data"]["message"]
        report["global_32_reservations"] = True
        for p in reservations + [extra]:
            p.stdin.write("\n"); p.stdin.flush(); p.wait(timeout=10)
        until(lambda: control(data, "status")["waitingJobs"] == 0)
        until(lambda: control(data, "status")["clients"] == 0)
        connections = []
        for _ in range(32):
            p = launch([FIXTURE, "--audio-reserve", data, 0]); owned.append(p); connections.append(p)
            assert json.loads(p.stdout.readline())["connected"]
        extra_connection = launch([FIXTURE, "--audio-reserve", data, 0]); owned.append(extra_connection)
        assert not json.loads(extra_connection.stdout.readline())["connected"]
        report["global_32_clients"] = True
        for p in connections:
            p.stdin.write("\n"); p.stdin.flush(); p.wait(timeout=10)
        assert not control(data, "stop")["available"]
        host.wait(timeout=10)
    finally:
        for p in owned + [host]:
            if p.poll() is None:
                p.kill(); p.wait()
    # Real production start commands must agree on one detached instance.
    detached = OUT / "detached-service"
    launches = [launch([CLI, "service", "start", "--data-dir", detached, "--json"]) for _ in range(4)]
    statuses = [finish(p)["payload"] for p in launches]
    assert len({s["processId"] for s in statuses}) == 1
    assert control(detached, "status")["processId"] == statuses[0]["processId"]
    assert not (detached / "sessions.db").exists()
    report["concurrent_start_and_launcher_exit"] = statuses[0]
    other = OUT / "separate-service"
    other_status = control(other, "start")
    assert other_status["identity"] != statuses[0]["identity"]
    assert other_status["processId"] != statuses[0]["processId"]
    control(other, "stop"); control(detached, "stop")
    report["separate_data_directories"] = True

def batch(data, mode, service_pid=None):
    sample = ROOT / "tests/test01_20s.wav"
    clients = [launch([FIXTURE, mode, data, sample, 15000]) for _ in range(3)]
    try:
        # Each client emits a result then stays alive for measurement; stdout can contain large results.
        import concurrent.futures
        with concurrent.futures.ThreadPoolExecutor(3) as readers:
            futures = [readers.submit(p.stdout.readline) for p in clients]
            peaks = dict(working=0, private=0)
            while not all(f.done() for f in futures):
                memory = tree_memory([p.pid for p in clients] + ([service_pid] if service_pid else []))
                peaks = {k: max(peaks[k], memory[k]) for k in peaks}
                time.sleep(.1)
            values = []
            for p, f in zip(clients, futures):
                line = f.result()
                if not line:
                    raise RuntimeError(p.stderr.read()[-2000:])
                values.append(json.loads(line))
            resident = tree_memory([p.pid for p in clients] + ([service_pid] if service_pid else []))
            return dict(results=values, sampledPeak=peaks, afterResults=resident)
    finally:
        for p in clients:
            if p.poll() is None:
                p.kill(); p.wait()

def real_acceptance(data, report):
    if control(data, "status")["available"]:
        control(data, "stop")
    baseline = batch(data, "--audio-baseline")
    report["independent_helpers"] = baseline
    status = control(data, "start")
    shared = batch(data, "--audio-real", status["processId"])
    helpers = {r["result"]["service"]["helperProcessId"] for r in shared["results"]}
    assert len(helpers) == 1 and None not in helpers
    report["shared_helper"] = shared
    # A service crash must terminate its whole warm Python job, and the next explicit start must recover.
    old = control(data, "status")
    child_ids = [p["pid"] for p in tree_memory([old["processId"]])["processes"] if p["pid"] != old["processId"]]
    terminate(old["processId"])
    until(lambda: not any(pid in processes() for pid in child_ids))
    restarted = control(data, "start")
    assert restarted["processId"] != old["processId"]
    result = finish(launch([FIXTURE, "--audio-real", data, ROOT / "tests/test01_20s.wav"]))
    assert result["success"]
    report["crash_cleanup_and_restart"] = dict(oldService=old["processId"], removedChildren=child_ids, newService=restarted["processId"])

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-dir", required=True)
    parser.add_argument("--real", action="store_true")
    args = parser.parse_args()
    os.environ.pop("OPENAI_API_KEY", None)
    OUT.mkdir(parents=True, exist_ok=True)
    existing = OUT / "process-acceptance.json"
    report = json.loads(existing.read_text(encoding="utf-8")) if existing.exists() else {}
    fake_acceptance(report)
    print("Process lifecycle and queue acceptance passed.", flush=True)
    if args.real:
        real_acceptance(args.data_dir, report)
    (OUT / "process-acceptance.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print("Acceptance complete; evidence:", OUT / "process-acceptance.json", flush=True)
