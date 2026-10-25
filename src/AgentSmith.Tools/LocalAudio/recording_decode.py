"""Fixed streaming preparation helper. No models, network, user code or inference."""
import hashlib
import json
import os
import pathlib
import shutil
import socket
import sys

def no_network(*args, **kwargs):
    raise RuntimeError("Network is disabled in recording preparation")
socket.socket.connect = no_network
socket.socket.connect_ex = no_network
socket.create_connection = no_network

RATE = 16000

def prepare(request):
    import av
    import numpy as np
    destination = pathlib.Path(request["destination"])
    partial = destination.with_suffix(".partial")
    maximum = request["maxBytes"]
    cursor = 0
    mappings = []
    coverage = []
    with partial.open("wb") as output:
        def write(samples):
            nonlocal cursor
            data = np.clip(samples, -1, 1).astype("<f4")
            if not np.isfinite(data).all():
                raise ValueError("Audio contains non-finite samples")
            if (cursor + len(data)) * 4 > maximum:
                raise ValueError("Analysis storage budget reached during decoding")
            if shutil.disk_usage(destination.parent).free < 1073741824 + data.nbytes:
                raise ValueError("Analysis paused at 1 GiB free-disk reserve")
            output.write(data.tobytes())
            cursor += len(data)

        for item in request["inputs"]:
            start = item.get("start", cursor)
            if start < cursor:
                raise ValueError("Overlapping source chunks")
            while cursor < start:
                write(np.zeros(min(RATE, start-cursor), dtype="<f4"))
            source_start = cursor
            path = pathlib.Path(item["path"])
            if path.suffix.lower() not in (".wav", ".mp3"):
                raise ValueError("Only WAV and MP3 sources are supported")
            with av.open(str(path), format=path.suffix[1:].lower(), options={"protocol_whitelist": "file"}) as container:
                if len(container.streams.audio) != 1:
                    raise ValueError("Expected exactly one audio stream")
                stream = container.streams.audio[0]
                if ("sampleRate" in item and stream.codec_context.sample_rate != item["sampleRate"] or
                    "channels" in item and stream.codec_context.channels != item["channels"]):
                    raise ValueError("Capture format disagrees with manifest")
                if "bitsPerSample" in item:
                    bits = item["bitsPerSample"]
                    kind = "f" if item.get("floatingPoint") else "u" if bits == 8 else "s"
                    expected_codec = "pcm_" + kind + str(bits) + ("le" if bits > 8 else "")
                    if stream.codec_context.name != expected_codec:
                        raise ValueError("Capture sample encoding disagrees with manifest")
                if not 0 < stream.codec_context.sample_rate <= 192000 or not 0 < stream.codec_context.channels <= 8:
                    raise ValueError("Unsupported source format")
                resampler = av.AudioResampler(format="fltp", layout="mono", rate=RATE)
                first_position = None
                def collect(frame):
                    nonlocal first_position
                    if frame.time is not None:
                        position = round(float(frame.time)*RATE)
                        if first_position is None:
                            first_position = position
                        expected_position = source_start + position-first_position
                        difference = expected_position-cursor
                        if difference < -2:
                            raise ValueError("Source timestamps moved backwards; coverage cannot be established")
                        if difference > 2:
                            coverage.append(dict(start=cursor, frames=difference, kind="TimestampGap"))
                            while cursor < expected_position:
                                write(np.zeros(min(RATE, expected_position-cursor), dtype="<f4"))
                    write(frame.to_ndarray().reshape(-1))
                # Decoded samples include FFmpeg's MP3 skip-samples correction. Never use container duration as a cutoff.
                for frame in container.decode(stream):
                    for normalized in resampler.resample(frame):
                        collect(normalized)
                for normalized in resampler.resample(None):
                    collect(normalized)
            expected = item.get("frames")
            if expected is not None and abs(cursor-source_start-expected) > 1:
                raise ValueError("Decoded capture length disagrees with manifest")
            if expected is not None:
                # Native chunk boundaries can land between 16 kHz samples. Preserve the
                # absolute rounded frame grid rather than accumulating a sample per chunk.
                difference = cursor-source_start-expected
                if difference == 1:
                    output.seek(-4, os.SEEK_CUR)
                    output.truncate()
                    cursor -= 1
                elif difference == -1:
                    write(np.zeros(1, dtype="<f4"))
            mappings.append(dict(file=path.name, start=source_start, end=cursor))
        if not cursor:
            raise ValueError("Audio is empty")
        output.flush()
        os.fsync(output.fileno())
    partial.replace(destination)
    digest = hashlib.sha256()
    with destination.open("rb") as source:
        for block in iter(lambda: source.read(1024*1024), b""):
            digest.update(block)
    return dict(version=1, frames=cursor, hash=digest.hexdigest(), mappings=mappings, coverage=coverage)

if __name__ == "__main__":
    print(json.dumps(prepare(json.loads(sys.stdin.readline())), allow_nan=False))
