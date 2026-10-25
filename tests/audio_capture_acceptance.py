"""Explicit controlled Windows endpoint recording test. Plays the checked speech fixture audibly."""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import threading
import time
import wave
import numpy as np

ROOT = Path(__file__).resolve().parents[1]

class MemoryCounters(ctypes.Structure):
    _fields_ = [('cb', wintypes.DWORD), ('faults', wintypes.DWORD)] + [(name, ctypes.c_size_t) for name in
        ('peakWorking', 'working', 'peakPaged', 'paged', 'peakNonPaged', 'nonPaged', 'pagefile', 'peakPagefile', 'private')]

def memory(pid):
    kernel = ctypes.windll.kernel32
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]; kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    psapi = ctypes.windll.psapi
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(MemoryCounters), wintypes.DWORD]
    handle = kernel.OpenProcess(0x410, False, pid)
    try:
        counters = MemoryCounters(); counters.cb = ctypes.sizeof(counters)
        assert psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb)
        return dict(privateBytes=counters.private, workingSet=counters.working, peakPrivateBytes=counters.peakPagefile, peakWorkingSet=counters.peakWorking)
    finally: kernel.CloseHandle(handle)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--smith', required=True)
    parser.add_argument('--data-dir', required=True)
    parser.add_argument('--output', default=str(ROOT / 'artifacts/capture-acceptance'))
    args = parser.parse_args()
    out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
    def command(*words):
        p = subprocess.run([args.smith, *words, '--data-dir', args.data_dir, '--json'], capture_output=True, text=True, encoding='utf-8', timeout=40, creationflags=subprocess.CREATE_NO_WINDOW)
        if p.returncode: raise RuntimeError(p.stdout + p.stderr)
        return json.loads(p.stdout.strip().splitlines()[-1])['payload']
    with wave.open(str(ROOT / 'artifacts/audio-service-acceptance/fixtures/speech.wav')) as f:
        rate = f.getframerate(); speech = np.frombuffer(f.readframes(f.getnframes()), dtype='<i2').astype(np.float32) / 32768
    speech = np.interp(np.arange(int(len(speech) * 48000 / rate)) / 48000, np.arange(len(speech)) / rate, speech)
    tone = np.sin(np.arange(48000 * 3) * (2 * np.pi * 440 / 48000)) * .08
    block = np.concatenate([speech * .5, np.zeros(48000 * 3), tone, np.zeros(48000 * 2)])
    sound = np.concatenate([np.zeros(48000 * 3), np.tile(block, 10)])[:48000 * 137]
    sound = np.concatenate([sound, np.zeros(48000 * 3)])
    path = out / 'controlled-input.wav'
    with wave.open(str(path), 'wb') as f:
        f.setnchannels(2); f.setsampwidth(2); f.setframerate(48000)
        f.writeframes(np.repeat((sound * 32767).astype('<i2')[:, None], 2, axis=1).tobytes())
    status = command('service', 'status'); service_pid = status['processId']
    capture = command('audio', 'capture', 'start'); capture_id = capture['Id']
    evidence = dict(captureId=capture_id, servicePid=service_pid, device=capture['Device'], inputSeconds=len(sound)/48000,
                    inputBytes=path.stat().st_size, samples=[], start=capture, baselineMemory=memory(service_pid))
    print(json.dumps({'started': capture_id, 'duration': len(sound)/48000}), flush=True)
    player = threading.Thread(target=lambda: ctypes.windll.winmm.PlaySoundW(str(path), None, 0x20000), daemon=True)
    started = time.monotonic(); player.start()
    try:
        while player.is_alive():
            current = command('audio', 'capture', 'status', capture_id)
            service = command('service', 'status')
            evidence['samples'].append(dict(seconds=time.monotonic()-started, frames=current['Frames'], state=current['State'], helper=service['helper'], memory=memory(service_pid)))
            if current['State'] != 'Recording': raise AssertionError(current)
            player.join(5)
        final = command('audio', 'capture', 'stop', capture_id)
        while final['State'] == 'Finalizing':
            time.sleep(.2); final = command('audio', 'capture', 'status', capture_id)
        evidence['final'] = final
        assert final['State'] in ('Completed', 'Interrupted'), final
        assert final['DurationSeconds'] >= 140 and final['Bytes'] > 25 * 1024 * 1024, final
        export = out / ('export-' + capture_id)
        command('audio', 'capture', 'export', capture_id, str(export))
        manifest = json.loads((export/'manifest.json').read_text())
        frames = 0; arrays = []
        for chunk in manifest['Chunks']:
            data = (export/chunk['File']).read_bytes()
            assert hashlib.sha256(data).hexdigest() == chunk['Hash']
            assert chunk['StartFrame'] == frames; frames += chunk['Frames']
            pos = 12
            while data[pos:pos+4] != b'data': pos += 8 + struct.unpack_from('<I', data, pos+4)[0]
            length = struct.unpack_from('<I', data, pos+4)[0]
            assert length == chunk['Frames'] * manifest['BlockAlign']
            arrays.append(np.frombuffer(data[pos+8:pos+8+length], dtype='<f4').reshape(-1, 2))
        assert frames == manifest['Frames']
        recorded = np.concatenate(arrays)[:, 0]
        evidence['rms'] = float(np.sqrt(np.mean(recorded**2)))
        evidence['peak'] = float(np.max(np.abs(recorded)))
        evidence['chunks'] = len(arrays); evidence['coverage'] = manifest['Coverage']
        assert evidence['peak'] > .01, 'No test audio reached selected endpoint'
        # Read and play a short exported sample through a conventional PCM WAV.
        preview = out/'export-preview.wav'
        with wave.open(str(preview), 'wb') as f:
            f.setnchannels(1); f.setsampwidth(2); f.setframerate(48000)
            f.writeframes((np.clip(recorded[4*48000:7*48000], -1, 1)*32767).astype('<i2').tobytes())
        assert ctypes.windll.winmm.PlaySoundW(str(preview), None, 0x20000)
        evidence['exportPlayback'] = True
        evidence['exportDirectory'] = str(export)
        (out/'live-capture.json').write_text(json.dumps(evidence, indent=2))
        print(json.dumps({'completed':capture_id,'seconds':final['DurationSeconds'],'bytes':final['Bytes'],'chunks':len(arrays),'peak':evidence['peak']}), flush=True)
    finally:
        ctypes.windll.winmm.PlaySoundW(None, None, 0)
        command('audio', 'capture', 'stop', capture_id)

if __name__ == '__main__': main()
