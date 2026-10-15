"""Smith's fixed local inference worker. Protocol v1; never downloads models."""
import os
os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1",
                  TF_CPP_MIN_LOG_LEVEL="2", CUDA_VISIBLE_DEVICES="-1", OMP_NUM_THREADS="4")
import contextlib
import csv
import hashlib
import importlib.metadata
import json
import pathlib
import socket
import sys
import time

# Defense in depth: inference has no network use, including dependency callbacks.
def no_network(*args, **kwargs):
    raise RuntimeError("Network access is disabled in the local audio helper")
socket.socket.connect = no_network
socket.socket.connect_ex = no_network
socket.create_connection = no_network

PROTOCOL = 1
RATE = 16000
output = sys.stdout

def emit(kind, **values):
    output.write(json.dumps(dict(protocol=PROTOCOL, type=kind, **values), ensure_ascii=True, allow_nan=False) + "\n")
    output.flush()

def progress(stage):
    emit("progress", stage=stage)

def decode(path, start, duration):
    import av
    import numpy as np
    if type(start) is not int or type(duration) is not int or start < 0 or not 0 <= duration <= 120:
        raise ValueError("Start must be nonnegative and duration 0..120 seconds")
    limit = (duration or 120) * RATE
    pieces = []
    count = 0
    extension = pathlib.Path(path).suffix.lower()
    if extension not in (".wav", ".mp3"):
        raise ValueError("Only WAV and MP3 inputs are supported")
    # Force the expected demuxer; a disguised playlist must not open other files or URLs.
    with av.open(path, format=extension[1:], options={"protocol_whitelist": "file"}) as container:
        if len(container.streams.audio) != 1:
            raise ValueError("Expected exactly one audio stream")
        stream = container.streams.audio[0]
        if stream.codec_context.sample_rate <= 0 or stream.codec_context.sample_rate > 192000:
            raise ValueError("Unsupported sample rate (maximum 192 kHz)")
        if stream.codec_context.channels > 8:
            raise ValueError("Audio exceeds eight channels")
        source_duration = float(container.duration / av.time_base) if container.duration is not None else None
        # MP3 timestamps include encoder delay even after skip-samples decoding.
        # User ranges are relative to the first decoded sample, not that timestamp origin.
        origin_seconds = float(stream.start_time * stream.time_base) if stream.start_time is not None else 0
        origin_samples = round(origin_seconds * RATE)
        if source_duration is not None and start >= source_duration:
            raise ValueError("Selected range is empty or starts beyond the recording")
        if start:
            container.seek(int(max(0, start + origin_seconds - 1) * av.time_base), backward=True)
        resampler = av.AudioResampler(format="fltp", layout="mono", rate=RATE)
        frames = 0
        finished = False
        fallback = max(0, start - 1) * RATE
        def collect(frame):
            nonlocal count, fallback
            position = round(float(frame.time) * RATE) - origin_samples if frame.time is not None else fallback
            samples = frame.to_ndarray().reshape(-1)
            fallback = position + len(samples)
            left = max(0, start * RATE - position)
            right = min(len(samples), (start * RATE + limit) - position)
            if right > left:
                chunk = samples[left:right][:limit-count]
                pieces.append(chunk.copy())
                count += len(chunk)
            return count >= limit or position >= start * RATE + limit
        for frame in container.decode(stream):
            frames += 1
            if frames > 100000:
                raise ValueError("Audio decoding exceeded its frame budget")
            for normalized in resampler.resample(frame):
                if collect(normalized):
                    finished = True
                    break
            if finished:
                break
        if not finished:
            for normalized in resampler.resample(None):
                collect(normalized)
    if not count:
        raise ValueError("Selected audio range is empty")
    waveform = np.clip(np.concatenate(pieces), -1, 1).astype("<f4")
    if not np.isfinite(waveform).all():
        raise ValueError("Audio contains non-finite samples")
    return waveform, dict(startSeconds=start, durationSeconds=len(waveform)/RATE,
                          sourceDurationSeconds=source_duration, sampleRate=RATE, channels=1,
                          partial=start > 0 or source_duration is None or start + len(waveform)/RATE < source_duration - .01,
                          clipHash=hashlib.sha256(waveform.tobytes()).hexdigest(), clipEncoding="float32-le-mono-16000")

def transcribe(waveform, root, start, language):
    from faster_whisper import WhisperModel
    from faster_whisper.tokenizer import _LANGUAGE_CODES
    import numpy as np
    if language != "auto" and language not in _LANGUAGE_CODES:
        raise ValueError("Unsupported Whisper language code")
    progress("loading Whisper")
    model = WhisperModel(str(root / "whisper"), device="cpu", compute_type="int8", cpu_threads=4,
                         num_workers=1, local_files_only=True)
    progress("transcribing")
    result = dict(status="succeeded", model="Whisper small multilingual", computeType="int8",
                  engineVersion=importlib.metadata.version("faster-whisper"), language=None, segments=[], noSpeech=True)
    if float(np.max(np.abs(waveform))) < 0.0001:
        return result
    segments, info = model.transcribe(waveform, language=None if language == "auto" else language,
                                     task="transcribe", vad_filter=True, condition_on_previous_text=False,
                                     beam_size=5)
    for segment in segments:
        text = segment.text.strip()
        begin = max(0, min(segment.start, len(waveform)/RATE))
        end = max(begin, min(segment.end, len(waveform)/RATE))
        if text and end > begin:
            result["segments"].append(dict(startSeconds=start+begin, endSeconds=start+end, text=text))
    result.update(language=info.language if result["segments"] else None,
                  languageScore=info.language_probability, noSpeech=not result["segments"])
    return result

def classify(waveform, root, start):
    progress("loading YAMNet")
    import tensorflow as tf
    import numpy as np
    tf.config.threading.set_intra_op_parallelism_threads(4)
    tf.config.threading.set_inter_op_parallelism_threads(2)
    model = tf.saved_model.load(str(root / "yamnet"))
    with open(root / "yamnet" / "assets" / "yamnet_class_map.csv", encoding="utf-8", newline="") as file:
        classes = list(csv.DictReader(file))
    if len(classes) != 521:
        raise ValueError("YAMNet class map must contain 521 classes")
    progress("classifying")
    scores, _, _ = model(waveform)
    scores = scores.numpy()
    if scores.shape[1] != 521 or not np.isfinite(scores).all():
        raise ValueError("Invalid YAMNet model output")
    def labels(row, indices):
        return [dict(index=int(i), label=classes[i]["display_name"], score=float(row[i])) for i in indices]
    mean = scores.mean(axis=0)
    duration = len(waveform)/RATE
    return dict(status="succeeded", model="Google YAMNet 1", engineVersion=tf.__version__,
                classMap=classes, aggregateScores=labels(mean, np.argsort(mean)[::-1]),
                windows=[dict(startSeconds=start+i*.48, endSeconds=start+min(i*.48+.96,duration),
                              labels=labels(row, np.argsort(row)[-5:][::-1]), padded=i*.48+.96 > duration)
                         for i, row in enumerate(scores) if i*.48 < duration],
                windowSeconds=.96, hopSeconds=.48)

def run(request):
    import numpy as np
    if request.get("protocol") != PROTOCOL:
        raise ValueError("Unsupported local audio protocol")
    root = pathlib.Path(request["models"])
    operation = request["operation"]
    if operation not in ("audio_transcribe", "audio_classify", "audio_listen", "selftest"):
        raise ValueError("Unsupported local audio operation")
    progress("decoding")
    if operation == "selftest":
        waveform = np.zeros(RATE, dtype="<f4")
        details = dict(startSeconds=0, durationSeconds=1)
    else:
        waveform, details = decode(request["path"], request["startSeconds"], request["durationSeconds"])
    components = {}
    for name, enabled, work in [
        ("transcription", operation != "audio_classify", lambda: transcribe(waveform, root, details["startSeconds"], request.get("language", "auto"))),
        ("classification", operation != "audio_transcribe", lambda: classify(waveform, root, details["startSeconds"]))]:
        if enabled:
            started = time.monotonic()
            try:
                components[name] = work()
            except Exception as exc:
                components[name] = dict(status="failed", error=f"{type(exc).__name__}: {str(exc)[:500]}")
            components[name]["elapsedSeconds"] = round(time.monotonic()-started, 3)
    return dict(**details, **components)

def main():
    try:
        line = sys.stdin.buffer.readline(16385)
        if len(line) > 16384:
            raise ValueError("Audio request exceeds 16 KiB")
        with contextlib.redirect_stdout(sys.stderr):
            result = run(json.loads(line))
        emit("result", result=result)
    except Exception as exc:
        emit("error", error=f"{type(exc).__name__}: {str(exc)[:500]}")
        return 1
    return 0

if __name__ == "__main__":
    sys.exit(main())
