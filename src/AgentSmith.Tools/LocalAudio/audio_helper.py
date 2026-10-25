"""Smith's fixed local inference worker. One-shot protocol v1 and streaming protocol v2; never downloads models."""
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

REQUEST_ID = None
MODELS = {}

def emit(kind, **values):
    if REQUEST_ID is not None:
        values["requestId"] = REQUEST_ID
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
    loading = time.monotonic()
    key = (str(root), "whisper")
    if key not in MODELS:
        progress("loading Whisper")
        MODELS[key] = WhisperModel(str(root / "whisper"), device="cpu", compute_type="int8", cpu_threads=4,
                                  num_workers=1, local_files_only=True)
    model = MODELS[key]
    load_seconds = time.monotonic() - loading
    progress("transcribing")
    result = dict(status="succeeded", model="Whisper small multilingual", computeType="int8",
                  engineVersion=importlib.metadata.version("faster-whisper"), loadSeconds=load_seconds, language=None, segments=[], noSpeech=True)
    if float(np.max(np.abs(waveform))) < 0.0001:
        return result
    segments, info = model.transcribe(waveform, language=None if language == "auto" else language,
                                     task="transcribe", vad_filter=True, condition_on_previous_text=False,
                                     beam_size=5, word_timestamps=True)
    for segment in segments:
        text = segment.text.strip()
        begin = max(0, min(segment.start, len(waveform)/RATE))
        end = max(begin, min(segment.end, len(waveform)/RATE))
        if text and end > begin:
            words = [dict(startSeconds=start+max(begin, min(w.start, end)),
                          endSeconds=start+max(begin, min(w.end, end)), text=w.word)
                     for w in (segment.words or []) if w.end > w.start]
            result["segments"].append(dict(startSeconds=start+begin, endSeconds=start+end, text=text, words=words))
    result.update(language=info.language if result["segments"] else None,
                  languageScore=info.language_probability, noSpeech=not result["segments"])
    return result

def classify(waveform, root, start):
    import tensorflow as tf
    import numpy as np
    loading = time.monotonic()
    key = (str(root), "yamnet")
    if key not in MODELS:
        progress("loading YAMNet")
        if not any(k[1] == "yamnet" for k in MODELS):
            tf.config.threading.set_intra_op_parallelism_threads(4)
            tf.config.threading.set_inter_op_parallelism_threads(2)
        MODELS[key] = tf.saved_model.load(str(root / "yamnet"))
    model = MODELS[key]
    load_seconds = time.monotonic() - loading
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
    return dict(status="succeeded", model="Google YAMNet 1", engineVersion=tf.__version__, loadSeconds=load_seconds,
                classMap=classes, aggregateScores=labels(mean, np.argsort(mean)[::-1]),
                windows=[dict(startSeconds=start+i*.48, endSeconds=start+min(i*.48+.96,duration),
                              labels=labels(row, np.argsort(row)[-5:][::-1]), padded=i*.48+.96 > duration)
                         for i, row in enumerate(scores) if i*.48 < duration],
                windowSeconds=.96, hopSeconds=.48)

def diarize(waveform, root, start):
    import numpy as np
    import sherpa_onnx
    loading = time.monotonic()
    key = (str(root), "diarization")
    if key not in MODELS:
        progress("loading speaker models")
        config = sherpa_onnx.OfflineSpeakerDiarizationConfig(
            segmentation=sherpa_onnx.OfflineSpeakerSegmentationModelConfig(
                pyannote=sherpa_onnx.OfflineSpeakerSegmentationPyannoteModelConfig(
                    model=str(root / "diarization" / "model.onnx")), num_threads=4, provider="cpu"),
            embedding=sherpa_onnx.SpeakerEmbeddingExtractorConfig(
                model=str(root / "diarization" / "nemo_en_titanet_small.onnx"), num_threads=4, provider="cpu"),
            clustering=sherpa_onnx.FastClusteringConfig(num_clusters=-1, threshold=0.5),
            min_duration_on=0.3, min_duration_off=0.5)
        if not config.validate():
            raise ValueError("Invalid speaker model configuration")
        MODELS[key] = sherpa_onnx.OfflineSpeakerDiarization(config)
    model = MODELS[key]
    load_seconds = time.monotonic() - loading
    progress("separating speakers")
    turns, names = [], {}
    if float(np.max(np.abs(waveform))) >= 0.0001:
        for segment in model.process(waveform).sort_by_start_time():
            name = names.setdefault(segment.speaker, "Speaker " + str(len(names) + 1))
            begin, end = max(0, segment.start), min(len(waveform)/RATE, segment.end)
            if end > begin:
                turns.append(dict(startSeconds=start+begin, endSeconds=start+end, speaker=name))
    # Local-only, recording-scoped reconciliation evidence. Never a real-person identity.
    embedding_key = (str(root), "speaker-embedding")
    if embedding_key not in MODELS:
        MODELS[embedding_key] = sherpa_onnx.SpeakerEmbeddingExtractor(
            sherpa_onnx.SpeakerEmbeddingExtractorConfig(
                model=str(root / "diarization" / "nemo_en_titanet_small.onnx"), num_threads=4, provider="cpu"))
    extractor = MODELS[embedding_key]
    embeddings = []
    for name in sorted(set(t["speaker"] for t in turns)):
        spans = [t for t in turns if t["speaker"] == name and t["endSeconds"]-t["startSeconds"] >= 1.5
                 and not any(other["speaker"] != name and other["startSeconds"] < t["endSeconds"]
                             and other["endSeconds"] > t["startSeconds"] for other in turns)]
        vectors = []
        seconds = 0.0
        for turn in sorted(spans, key=lambda t: t["endSeconds"]-t["startSeconds"], reverse=True)[:5]:
            begin = round((turn["startSeconds"]-start)*RATE)
            end = min(round((turn["endSeconds"]-start)*RATE), begin+10*RATE)
            stream = extractor.create_stream()
            stream.accept_waveform(sample_rate=RATE, waveform=np.ascontiguousarray(waveform[begin:end]))
            stream.input_finished()
            if extractor.is_ready(stream):
                vector = np.asarray(extractor.compute(stream), dtype=np.float32)
                norm = np.linalg.norm(vector)
                if np.isfinite(vector).all() and norm > 0:
                    vectors.append(vector / norm)
                    seconds += (end-begin)/RATE
        if vectors:
            vector = np.mean(vectors, axis=0)
            vector /= max(float(np.linalg.norm(vector)), 1e-12)
            embeddings.append(dict(speaker=name, seconds=seconds, vector=vector.tolist()))
    return dict(status="succeeded", model="pyannote segmentation 3.0 FP32 + NeMo TitaNet small",
                engineVersion=importlib.metadata.version("sherpa-onnx"), loadSeconds=load_seconds,
                speakerCount=len({t["speaker"] for t in turns}), turns=turns, embeddings=embeddings,
                note="Estimated anonymous speakers local to this analysis. Short and overlapping speech may be misassigned.")


def run(request):
    import numpy as np
    if request.get("protocol") != PROTOCOL:
        raise ValueError("Unsupported local audio protocol")
    root = pathlib.Path(request["models"])
    operation = request["operation"]
    operations = {"audio_transcribe": ["transcription", "diarization"],
                  "audio_classify": ["classification"], "audio_diarize": ["diarization"],
                  "audio_listen": ["transcription", "classification", "diarization"],
                  "selftest": ["transcription", "classification", "diarization"]}
    if operation not in operations:
        raise ValueError("Unsupported local audio operation")
    selected = request.get("components", operations[operation])
    if not isinstance(selected, list) or not selected or len(selected) != len(set(selected)) or any(c not in operations[operation] for c in selected):
        raise ValueError("Invalid audio component selection")
    progress("decoding")
    started = time.monotonic()
    if operation == "selftest":
        waveform = np.zeros(RATE, dtype="<f4")
        details = dict(startSeconds=0, durationSeconds=1)
    else:
        waveform, details = decode(request["path"], request["startSeconds"], request["durationSeconds"])
    details["decodeSeconds"] = time.monotonic() - started
    components = {}
    for name, work in [
        ("transcription", lambda: transcribe(waveform, root, details["startSeconds"], request.get("language", "auto"))),
        ("classification", lambda: classify(waveform, root, details["startSeconds"])),
        ("diarization", lambda: diarize(waveform, root, details["startSeconds"]))]:
        if name in selected:
            started = time.monotonic()
            try:
                components[name] = work()
            except Exception as exc:
                components[name] = dict(status="failed", error=f"{type(exc).__name__}: {str(exc)[:500]}")
            components[name]["elapsedSeconds"] = round(time.monotonic()-started, 3)
            if PROTOCOL == 2:
                emit("component", component=name, status=components[name]["status"])
    return dict(**details, **components)


def install_segmentation(archive, target):
    import tarfile
    target = pathlib.Path(target)
    target.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive, "r:bz2") as tar:
        for name in ("model.onnx", "LICENSE", "README.md"):
            matches = [m for m in tar.getmembers() if pathlib.PurePosixPath(m.name).name == name]
            if len(matches) != 1 or not matches[0].isfile() or matches[0].size > 16*1024*1024:
                raise ValueError("Invalid segmentation archive member")
            with tar.extractfile(matches[0]) as source:
                (target / name).write_bytes(source.read())


def main():
    global PROTOCOL, REQUEST_ID
    if len(sys.argv) == 4 and sys.argv[1] == "--install-segmentation":
        install_segmentation(sys.argv[2], sys.argv[3])
        return 0
    server = sys.argv[1:] == ["--server"]
    PROTOCOL = 2 if server else 1
    while True:
        try:
            line = sys.stdin.buffer.readline(16385)
            if not line and server:
                return 0
            if len(line) > 16384:
                raise ValueError("Audio request exceeds 16 KiB")
            request = json.loads(line)
            REQUEST_ID = request.get("requestId") if server else None
            if server and (not isinstance(REQUEST_ID, str) or not 1 <= len(REQUEST_ID) <= 64):
                raise ValueError("Invalid request identity")
            with contextlib.redirect_stdout(sys.stderr):
                result = run(request)
            emit("result", result=result)
            if server:
                emit("complete")
            del result, request, line
            if not server:
                return 0
        except Exception as exc:
            emit("error", error=f"{type(exc).__name__}: {str(exc)[:500]}")
            return 1


if __name__ == "__main__":
    sys.exit(main())
