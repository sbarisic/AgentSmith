# Local audio follow-up acceptance

Validated on 15 October 2026 on Windows x64, AMD Ryzen AI 7 PRO 350, 24 GB RAM. This records the audio views, presentation, persistent helper, component reuse and anonymous speaker separation update. `LOCAL_AUDIO_ACCEPTANCE.md` remains the historical record for the earlier two-engine release.

## Configuration and delivery

- Whisper multilingual small, faster-whisper 1.2.1, CPU INT8, VAD and word timestamps.
- YAMNet 1, TensorFlow CPU 2.20.0, complete 521-category evidence retained in artifacts.
- sherpa-onnx 1.13.8, public FP32 pyannote segmentation 3.0 and NeMo TitaNet small ONNX. CPU clustering threshold 0.5, minimum speech 0.3 s and minimum silence 0.5 s. Speaker numbers follow first occurrence and apply only within that analysis.
- Private Python 3.11.14, private uv 0.9.5 and PyAV 18.1.0. Exact package/model checksums, URLs and license provenance are committed in the local-audio lockfile and download manifest.
- Artifact schema version 2 is independent of streaming helper protocol version 2. Historical artifacts and protocol-1 helpers remain supported.

The verified self-contained Windows release is published to `artifacts/publish/win-x64`, already present in the user's persistent PATH. Explicit `smith audio setup` completed and repeat setup/status reported all three engines ready. The installed generation is `f6bc2b9a57914f6ea1b5f2e9b5b8f577` under `%LOCALAPPDATA%/AgentSmith/audio/installations`. Previous generations are retained. System Python and persistent PATH were not changed by this update.

## Automated verification

The full Release solution suite passed: **520 unit/runtime/real-model tests and 324 integration tests**, with no failures or skips. The helper's **8 Python tests** also passed. Real-model tests were enabled with the installed data directory and prepared fixtures; test collections were serialized to avoid competing model jobs.

Coverage includes focused view pagination and ownership/grants; threshold boundaries, brief/overlapping sound intervals; quiet rendering; word-to-speaker alignment and ambiguous attribution; durable result reuse and invalidation; parent/worker serialization; queued cancellation; helper crash, malformed/oversized output and restart; idle expiry; receipts and interruption recovery; compatibility with historical artifacts and helpers; setup failure/rollback behavior. A scripted transcript regression requests the exact file directly, then reads retained speaker and timeline evidence without another inference or class-map dump.

Commands used for the final suite:

```powershell
$env:SMITH_AUDIO_ACCEPTANCE_FIXTURES = 'C:\Projects\AgentSmith\artifacts\audio-followup-acceptance\fixtures'
$env:SMITH_AUDIO_ACCEPTANCE_DATA = Join-Path $env:LOCALAPPDATA 'AgentSmith'
$env:OPENAI_API_KEY = ''
dotnet test AgentSmith.slnx -c Release --no-restore --logger 'trx;LogFilePrefix=audio-followup-verified' -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
```

The separate actual-idle run set `SMITH_AUDIO_IDLE_ACCEPTANCE=1` and ran `LocalAudioAcceptanceTests`; both tests passed, including waiting through the production five-minute expiry. TRX records are retained locally under each test project's `TestResults` directory. Generated/downloaded fixtures and full evidence remain outside Git under `artifacts/audio-followup-acceptance`.

After publication, a fresh `smith.exe run` process used a loopback scripted Chat Completions server and the real installed models. It executed one `audio_listen` call on the user clip, then `audio_read` for `speakers` and `timeline`, and completed normally in three scripted model requests. All three inference components succeeded, Speaker 2 was present in retained evidence, and the focused views contained no class dictionary. API credentials were absent. Logs and the smoke driver are retained as `published-smoke.ndjson`, `published-smoke-evidence.json` and `published_smoke.py` in the acceptance artifact directory.

## Accuracy observations

The supplied approximately 24-second `tests/test01_20s.wav` produced **0% normalized word error rate** against the user's checked transcript, within the existing 10% acceptance threshold. Normalization excludes punctuation, case and the supplied speaker/gender annotations. The model estimated **two anonymous speakers**.

Speaker 1 occupied approximately 1.95–10.51 seconds and Speaker 2 approximately 10.53–22.02 seconds. Inspection of the retained word assignments placed “Who will I be today? Or not.” with Speaker 1 and “But such a tide…” with Speaker 2, consistent with the user's reference transition. This was a structured-evidence review, not a new independent human annotation of acoustic boundaries. The system does not infer gender or identity.

Additional observations: the JFK speech fixture produced one speaker; digital silence produced zero speakers and no detected speech; mixed speech/silence/tone retained speech and sound evidence; MP3 selection preserved source-relative timestamps. Generated tone classifications are model associations, not proof of a physical sound source.

Licensed speaker fixtures were prepared using `tests/prepare_audio_speaker_fixtures.py` from LibriSpeech excerpts distributed by librosa: `5703-47212-0000` and `3436-172162-0000`, with source attribution retained alongside the downloads. LibriSpeech is CC BY 4.0; the script verifies source hashes and records cropping, concatenation and mixing. See [LibriSpeech](https://www.openslr.org/12) and the [first](https://librosa.org/data/audio/5703-47212-0000.txt) and [second](https://librosa.org/data/audio/3436-172162-0000.txt) attribution records.

| Constructed fixture | Expected / estimated speakers | Approximate diarization error |
| --- | --- | --- |
| One speaker | 1 / 1 | 12.8% |
| Two alternating speakers | 2 / 2 | 15.0% |
| Two overlapping speakers | 2 / 2 | 19.0% |

These error rates compare model turns with independent 10 ms RMS activity labels from the separate source tracks at a fixed −45 dBFS threshold. They are **diagnostic estimates, not benchmark-grade human-annotated DER**. Known-source speaker counts matched in all three cases. Overlapping speech, short turns and low-volume speech remain limitations; a human-annotated diarization benchmark remains outstanding.

## Performance and memory

The final suite measured the user clip through the actual tool dispatcher:

| Request | Wall time | Behavior |
| --- | ---: | --- |
| Cold combined analysis | 14.255 s | Starts helper and loads models |
| Warm combined analysis | 6.718 s | Reuses loaded models; performs inference |
| Repeated matching request | 0.0195 s | Reuses durable component evidence |

The Windows helper job's peak committed memory was approximately **1.15 GiB cold and 1.33 GiB after the warm request**, including child processes. This is peak committed memory, not resident working set or a post-idle measurement. The separate production-timeout test confirmed the helper was fully unloaded after **300.272 seconds** of inactivity. Models are retained between requests; input arrays and temporary audio are released after each job. Measurements are observations on this machine, not latency guarantees.

The final performance record is `artifacts/audio-followup-acceptance/performance.json`. The separate idle-expiry record is `performance-idle-expiry.json`; its older launcher-only memory field must not be interpreted as total helper memory. Full model evidence is in `real-tool-results.json`; diagnostic accuracy calculations are retained in `metrics.json` from the earlier real-model acceptance pass.

## Offline scope and remaining limits

Inference ran with `OPENAI_API_KEY` absent, Hugging Face offline settings and helper-level socket connection blocking. Scripted chat responses exercised Smith integration without paid model inference. This verifies application-level blocking; **OS-enforced firewall isolation was not tested**. Returned transcripts, labels and speaker evidence can still enter the separately configured chat provider context.

YAMNet display thresholds (aggregate mean 0.05, timeline window score 0.20) are presentation heuristics. Omitted labels do not establish absence; intervals are approximate analysis windows, not exact acoustic boundaries. Anonymous speaker assignments can be wrong, especially during overlap. One matching transcription sample does not establish general recognition accuracy.
