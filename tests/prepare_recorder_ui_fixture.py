"""Create explicitly synthetic, offline UI review data; no recorded speech or inference."""
import datetime
import hashlib
import json
import math
from pathlib import Path
import struct
import sys
import uuid
import wave

root = Path(sys.argv[1]).resolve()
capture_id = uuid.uuid4().hex
analysis_id = uuid.uuid4().hex
capture = root / "audio" / "recordings" / capture_id
analysis = root / "audio" / "analyses" / analysis_id
metadata = root / "desktop"
for directory in (capture, analysis, metadata):
    directory.mkdir(parents=True, exist_ok=True)

def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")

save(root / "recorder-ui-fixture.json", dict(synthetic=True))
now = datetime.datetime.now(datetime.timezone.utc).isoformat()
tracks = []
for name, rate, frequency in (("call", 16000, 440), ("mic", 48000, 660)):
    filename = name + "-chunk-000001.wav"
    samples = b"".join(struct.pack("<h", int(1800 * math.sin(2 * math.pi * frequency * i / rate))) for i in range(rate * 12))
    with wave.open(str(capture / filename), "wb") as output:
        output.setparams((1, 2, rate, 0, "NONE", "not compressed"))
        output.writeframes(samples)
    data = (capture / filename).read_bytes()
    tracks.append(dict(Version=1, Id=capture_id, OperationId=capture_id, TrackId=name, State="Completed", Started=now, Ended=now,
        Device=dict(Id="fixture-"+name, Name="Synthetic "+name, Default=False, Kind="microphone" if name == "mic" else "output"),
        SampleRate=rate, Channels=1, BitsPerSample=16, BlockAlign=2, Format=f"16 bit PCM: {rate}Hz 1 channels", Frames=rate*12,
        TimelineSeconds=12, Bytes=len(data), StartQpc=100000000, Anchors=[dict(Frame=0,Qpc=100000000),dict(Frame=rate*11,Qpc=210000000)], Coverage=[],
        Chunks=[dict(File=filename,StartFrame=0,Frames=rate*12,Bytes=len(data),Hash=hashlib.sha256(data).hexdigest())]))
save(capture / "manifest.json", dict(Version=2, Id=capture_id, OperationId=capture_id, State="Completed", Started=now, Ended=now,
    Device=tracks[0]["Device"], Tracks=tracks, TimelineSeconds=12, Bytes=sum(t["Bytes"] for t in tracks)))
save(metadata / (capture_id+".json"), dict(Title="Synthetic review fixture — not a real call", Language="en", AnalysisId=analysis_id))
save(analysis / "job.json", dict(Version=2, Id=analysis_id, SourceKind="capture", Source=capture_id, State="Completed", Stage="Done", Frames=192000, TrackCount=2,
    Created=now, Updated=now, Options=dict(NoSummary=True), Chunks=[]))
rows = [dict(id="callc000000s00000",startSeconds=1,endSeconds=4,speaker="Speaker 1",text="Synthetic example: review the transcript and its timestamps.",track="call"),
    dict(id="micc000001s00000",startSeconds=3,endSeconds=6,speaker="You",text="Primjer na hrvatskom: provjerimo zapis razgovora.",track="mic"),
    dict(id="callc000000s00001",startSeconds=7,endSeconds=10,speaker="Speaker 2",text="Primer v slovenščini: preverimo zapis pogovora.",track="call")]
(analysis / "transcript.jsonl").write_text("\n".join(json.dumps(row,ensure_ascii=False) for row in rows)+"\n",encoding="utf-8")
save(analysis / "coverage.json", dict(note="Synthetic UI fixture. No speech recognition was performed.",tracks=["call","mic"],durationSeconds=12))
(analysis / "summary.md").write_text("# Synthetic summary fixture\n\nThis text was authored for UI testing, not generated from a call.\n\n- Review the transcript. [callc000000s00000 00:01–00:04]\n",encoding="utf-8")
print(json.dumps(dict(data=str(root), captureId=capture_id, analysisId=analysis_id)))
