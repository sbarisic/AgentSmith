"""Explicit fixture preparation, requires managed Python. Downloads one pinned MIT-repository fixture.
Synthetic tone/silence/mixed fixtures are generated mathematically; no third-party non-speech recording.
Usage: python -I tests/prepare_local_audio_acceptance.py <output-directory>
The user-supplied tests/test01_20s.wav is intentionally not distributed by this script.
"""
import hashlib
import pathlib
import shutil
import sys
import urllib.request
import wave
import av
import numpy as np

root = pathlib.Path(__file__).resolve().parents[1]
output = pathlib.Path(sys.argv[1]).resolve()
output.mkdir(parents=True, exist_ok=True)
url = "https://raw.githubusercontent.com/openai/whisper/v20250625/tests/jfk.flac"
flac = output / "jfk.flac"
urllib.request.urlretrieve(url, flac)
assert hashlib.sha256(flac.read_bytes()).hexdigest() == "63a4b1e4c1dc655ac70961ffbf518acd249df237e5a0152faae9a4a836949715"

def wav(name, samples):
    with wave.open(str(output / name), "wb") as file:
        file.setnchannels(1)
        file.setsampwidth(2)
        file.setframerate(16000)
        file.writeframes((np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes())

with av.open(str(flac)) as source:
    resampler = av.AudioResampler(format="fltp", layout="mono", rate=16000)
    chunks = [normalized.to_ndarray().reshape(-1) for frame in source.decode(audio=0)
              for normalized in resampler.resample(frame)]
    chunks.extend(normalized.to_ndarray().reshape(-1) for normalized in resampler.resample(None))
    speech = np.concatenate(chunks)
wav("speech.wav", speech)
tone = .2 * np.sin(np.arange(16000 * 5) * 2 * np.pi * 440 / 16000)
wav("tone.wav", tone)
wav("silence.wav", np.zeros(16000 * 5))
wav("mixed.wav", np.concatenate([np.zeros(16000), speech, np.zeros(16000), tone]))
source_path = root / "tests/test01_20s.wav"
shutil.copyfile(source_path, output / source_path.name)
with av.open(str(source_path)) as source, av.open(str(output / "test01.mp3"), "w") as target:
    stream = target.add_stream("libmp3lame", rate=48000)
    stream.layout = "stereo"
    for frame in source.decode(audio=0):
        for packet in stream.encode(frame): target.mux(packet)
    for packet in stream.encode(None): target.mux(packet)
reference = "Dancing in the masquerade, idle truth in plain sight jaded, pop, roll, click, shot, who will i be today? Or not. But such a tide as moving seems asleep too full for sound and foam. When that which drew from out the boundless deep turns again home twilight and evening bell and after that"
(output / "reference.txt").write_text(reference, encoding="utf-8")
print("Prepared fixtures in", output)
