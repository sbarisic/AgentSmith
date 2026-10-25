"""Explicit local full-recording acceptance. Uses checked fixtures; never records live audio."""
import argparse
import ctypes
import json
import pathlib
import subprocess
import time
import wave
import hashlib
import urllib.request
import numpy as np
import av

ROOT = pathlib.Path(__file__).resolve().parents[1]
FLAGS = subprocess.CREATE_NO_WINDOW

def source(path):
    with av.open(str(path)) as container:
        resampler = av.AudioResampler(format="fltp", layout="mono", rate=16000)
        parts = [n.to_ndarray().reshape(-1) for f in container.decode(audio=0) for n in resampler.resample(f)]
        parts += [n.to_ndarray().reshape(-1) for n in resampler.resample(None)]
    return np.concatenate(parts)

def fixture(path, seconds, rate, speakers, events):
    with wave.open(str(path), "wb") as output:
        channels = 2 if rate == 48000 else 1
        output.setparams((channels, 2, rate, 0, "NONE", "not compressed"))
        for second in range(seconds):
            samples = np.zeros(rate, dtype=np.float32)
            for start, speaker in events:
                clip = speakers[speaker]
                a, b = max(0, second-start), min(len(clip)/16000, second+1-start)
                if b > a:
                    portion = clip[round(a*16000):round(b*16000)]
                    up = np.repeat(portion, rate//16000)
                    offset = round((start+a-second)*rate)
                    samples[offset:offset+len(up)] += up*.7
            output.writeframes(np.repeat((np.clip(samples,-1,1)*32767).astype("<i2")[:,None],channels,axis=1).tobytes())

class Memory(ctypes.Structure):
    _fields_ = [("cb",ctypes.c_ulong),("PageFaultCount",ctypes.c_ulong)] + [(n,ctypes.c_size_t) for n in ["PeakWorkingSetSize","WorkingSetSize","QuotaPeakPagedPoolUsage","QuotaPagedPoolUsage","QuotaPeakNonPagedPoolUsage","QuotaNonPagedPoolUsage","PagefileUsage","PeakPagefileUsage","PrivateUsage"]]

def memory(pid):
    kernel=ctypes.WinDLL("kernel32",use_last_error=True)
    kernel.OpenProcess.restype=ctypes.c_void_p
    kernel.CloseHandle.argtypes=[ctypes.c_void_p]
    psapi=ctypes.WinDLL("psapi"); psapi.GetProcessMemoryInfo.argtypes=[ctypes.c_void_p,ctypes.POINTER(Memory),ctypes.c_ulong]
    handle=kernel.OpenProcess(0x410,False,pid)
    if not handle:return {}
    try:
        value=Memory();value.cb=ctypes.sizeof(value)
        if not psapi.GetProcessMemoryInfo(handle,ctypes.byref(value),value.cb):return {}
        return dict(private=value.PrivateUsage,peakPrivate=value.PeakPagefileUsage,working=value.WorkingSetSize)
    finally:kernel.CloseHandle(handle)

def run(args, commands, label):
    log=args.output/(label+".ndjson")
    started=time.monotonic(); peak=0; working=0
    with log.open("w",encoding="utf-8") as out:
        p=subprocess.Popen([str(args.smith),"audio",*commands,"--data-dir",str(args.data_dir),"--json"],stdout=out,stderr=subprocess.STDOUT,creationflags=FLAGS)
        while p.poll() is None:
            m=memory(p.pid);peak=max(peak,m.get("peakPrivate",0));working=max(working,m.get("working",0));time.sleep(.25)
    events=[json.loads(line) for line in log.read_text(encoding="utf-8").splitlines() if line.startswith("{")]
    if p.returncode not in (0,3):raise RuntimeError(f"{label} failed: {log.read_text(encoding='utf-8')[-4000:]}")
    job=events[-1]["payload"].get("job")
    return dict(label=label,seconds=time.monotonic()-started,peakClientPrivate=peak,peakClientWorking=working,job=job)

def main():
    parser=argparse.ArgumentParser();parser.add_argument("--smith",type=pathlib.Path,required=True);parser.add_argument("--data-dir",type=pathlib.Path,required=True);parser.add_argument("--output",type=pathlib.Path,required=True);parser.add_argument("--multi-hour",action="store_true");parser.add_argument("--heldout",action="store_true");args=parser.parse_args()
    args.output.mkdir(parents=True,exist_ok=True)
    fixtures=ROOT/"artifacts/audio-service-acceptance/fixtures"
    speakers=[source(fixtures/"5703-47212-0000.ogg"),source(fixtures/"3436-172162-0000.ogg")]
    if args.heldout:
        # New voice reserved until matching settings were frozen. CC BY 4.0 LibriSpeech,
        # official checksum: librosa/util/example_data/registry.txt.
        third=args.output/"198-209-0000.ogg"
        if not third.exists():urllib.request.urlretrieve("https://librosa.org/data/audio/198-209-0000.ogg",third)
        assert hashlib.sha256(third.read_bytes()).hexdigest()=="0473ba9613befa1026db5e254546f105aeffba8366b5468fc5e8d474d844ca1d"
        urllib.request.urlretrieve("https://librosa.org/data/audio/198-209-0000.txt",args.output/"198-209-0000-attribution.txt")
        speakers.append(source(third))
        path=args.output/"heldout-three-voices.wav";events=[(5,2),(40,0),(106,2),(150,1),(214,2),(260,0)]
        fixture(path,300,48000,speakers,events)
        result=run(args,["analyze","file",str(path),"--no-summary"],"heldout")
        rows=[json.loads(line) for line in (args.data_dir/"audio/analyses"/result["job"]["id"]/"speakers.jsonl").read_text(encoding="utf-8").splitlines()]
        votes={};total=unknown=0
        for start,speaker in events:
            clip=speakers[speaker]
            for frame in range(0,len(clip)-160,160):
                if np.sqrt(np.mean(clip[frame:frame+160]**2))<10**(-45/20):continue
                total+=1;position=start+(frame+80)/16000
                labels={r["speaker"] for r in rows if r["startSeconds"]<=position<r["endSeconds"]}
                if len(labels)!=1 or not next(iter(labels)).startswith("Speaker "):unknown+=1;continue
                label=next(iter(labels));votes.setdefault(label,{})[str(speaker)]=votes.get(label,{}).get(str(speaker),0)+1
        correct=sum(max(counts.values()) for counts in votes.values())
        result["speakerMetrics"]=dict(reference="10ms energy activity; approximate, not manual diarization",referenceFrames=total,unknownFrames=unknown,
            attributionAccuracy=correct/total,unknownFraction=unknown/total,falseMergeFrames=sum(sum(c.values())-max(c.values()) for c in votes.values()),
            falseSplitLabels=sum(max(0,sum(max(c,key=c.get)==str(s) for c in votes.values())-1) for s in range(3)),votes=votes,
            limitation="One new held-out voice and two development voices; not a broad unseen-speaker benchmark")
        (args.output/"heldout-acceptance.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
        print(json.dumps(result["speakerMetrics"],indent=2));return
    wav=args.output/"long-two-speakers.wav"
    events=[(2,0),(40,1),(104,0),(155,1),(214,0),(290,1),(340,0)]
    if not wav.exists():fixture(wav,380,48000,speakers,events)
    mp3=args.output/"long-two-speakers.mp3"
    if not mp3.exists():
        with av.open(str(wav)) as inp,av.open(str(mp3),"w") as out:
            stream=out.add_stream("libmp3lame",rate=48000);stream.layout="stereo"
            stream.codec_context.flags |= av.codec.context.Flags.qscale
            stream.codec_context.global_quality = 4 * 118
            for frame in inp.decode(audio=0):
                for packet in stream.encode(frame):out.mux(packet)
            for packet in stream.encode(None):out.mux(packet)
    report=[]
    for label,path in [("wav",wav),("mp3",mp3)]:
        result=run(args,["analyze","file",str(path),"--no-summary"],label)
        job=result["job"];assert abs(job["processedSeconds"]-380)<.01 and job["state"]=="Completed",job
        rows=[json.loads(line) for line in (args.data_dir/"audio/analyses"/job["id"]/"transcript.jsonl").read_text(encoding="utf-8").splitlines()]
        assert any(r["startSeconds"]>340 for r in rows),"Final speech not covered"
        result["segments"]=len(rows);result["labels"]=sorted(set(r["speaker"] for r in rows));report.append(result)
        report.append(run(args,["analysis","resume",job["id"]],label+"-resume"))
        run(args,["analysis","export",job["id"],str(args.output/(label+"-export"))],label+"-export")
        (args.output/"acceptance.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
    if args.multi_hour:
        long=args.output/"two-hours.wav"
        if not long.exists():fixture(long,7203,16000,speakers,[(2,0),(109,1),(3599,0),(7180,1)])
        result=run(args,["analyze","file",str(long),"--no-summary"],"two-hours")
        assert result["job"]["processedSeconds"]==7203,result
        report.append(result)
        (args.output/"acceptance.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
    print(json.dumps(report,indent=2))

if __name__=="__main__":main()
