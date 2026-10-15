"""Explicit preparation of CC BY 4.0 LibriSpeech-derived acceptance fixtures.

Usage: managed-python -I tests/prepare_audio_speaker_fixtures.py <output-folder>
Sources/attribution: https://librosa.org/data/audio/{5703-47212-0000,3436-172162-0000}.txt
Dataset: https://www.openslr.org/12 (Vassil Panayotov, Daniel Povey).
Modified here: mono 16 kHz conversion, crops, concatenation and overlapping mix.
Reference activity is a deterministic energy annotation, NOT human word alignment.
"""
import hashlib
import json
import pathlib
import sys
import urllib.request
import wave
import av
import numpy as np

root = pathlib.Path(sys.argv[1]).resolve()
root.mkdir(parents=True, exist_ok=True)
sources = [("5703-47212-0000", "a284612b46af0535f7e1873758c4387bb8369f6dbbe192ffdec1f171108f98dd"),
           ("3436-172162-0000", "780e482b52f2c8a500356babf60178ce005449cc171a8537eb3ef2961eb3e855")]
audio = []
for name, digest in sources:
    path = root / (name + ".ogg")
    if not path.exists(): urllib.request.urlretrieve("https://librosa.org/data/audio/" + path.name, path)
    if hashlib.sha256(path.read_bytes()).hexdigest() != digest: raise ValueError("Fixture checksum mismatch")
    urllib.request.urlretrieve("https://librosa.org/data/audio/" + name + ".txt", root / (name + "-attribution.txt"))
    with av.open(str(path)) as source:
        resampler = av.AudioResampler(format="fltp", layout="mono", rate=16000)
        chunks = [out.to_ndarray().reshape(-1) for frame in source.decode(audio=0) for out in resampler.resample(frame)]
        chunks += [out.to_ndarray().reshape(-1) for out in resampler.resample(None)]
    clip = np.concatenate(chunks)[16000:9*16000]
    audio.append(clip / max(1, np.max(np.abs(clip)) / .8))


def write(name, tracks):
    size = max(offset + len(clip) for clip, offset in tracks)
    mixed = np.zeros(size, dtype=np.float32)
    annotations = []
    for speaker, (clip, offset) in enumerate(tracks, 1):
        mixed[offset:offset+len(clip)] += clip * .5
        # 10 ms RMS activity, with a fixed -45 dBFS threshold. Independent of tested models.
        for frame in range(0, len(clip)-159, 160):
            if np.sqrt(np.mean(clip[frame:frame+160] ** 2)) >= 10 ** (-45/20):
                annotations.append([round((offset+frame)/16000, 3), round((offset+frame+160)/16000, 3), speaker])
    with wave.open(str(root / name), "wb") as file:
        file.setnchannels(1); file.setsampwidth(2); file.setframerate(16000)
        file.writeframes((np.clip(mixed, -1, 1)*32767).astype("<i2").tobytes())
    return {"path": name, "expectedSpeakers": len(tracks), "referenceMethod": "10 ms RMS >= -45 dBFS on separate source tracks; approximate activity, not human annotations", "turns": annotations}


fixtures = [write("one-speaker.wav", [(audio[0], 0)]),
            write("two-speakers.wav", [(audio[0], 0), (audio[1], 9*16000)]),
            write("overlapping-speakers.wav", [(audio[0], 0), (audio[1], 4*16000)])]
(root / "speaker-references.json").write_text(json.dumps(fixtures, indent=2), encoding="utf-8")
print("Prepared licensed speaker fixtures in", root)
