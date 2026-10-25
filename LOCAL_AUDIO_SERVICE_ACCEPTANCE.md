# Shared local audio service acceptance

Verified locally on 15 October 2026: Windows x64, AMD Ryzen 5 3600XT (6 cores / 12 threads), approximately 48 GiB RAM. Recording, long-recording analysis and explicit chat exit remain proposed.

## Delivery

The self-contained Release executable was published to artifacts/publish/win-x64/smith.exe, the existing smith PATH destination. The published service was started and left available; models unload after five idle minutes. No automatic startup registration, Git commit or push was made.

Explicit audio setup installed the pinned private runtime, Whisper small CPU INT8, YAMNet and sherpa-onnx speaker models. All three offline self-tests passed. The installed generation is 7f58672db522462b9100eda4fb670e79. System Python and persistent PATH were unchanged.

Windows package redirection applies on this host. Service status resolves actual audio storage beneath the Codex package's LocalCache directory. Use that reported data directory explicitly when another terminal otherwise selects different storage.

## Verification

| Check | Result |
| --- | --- |
| Full Release unit/runtime/real-model suite | 542 passed, no failures or skips |
| Full Release integration suite | 324 passed, no failures or skips |
| Python audio helper tests | 8 passed |
| Published CLI smoke | Real audio_listen, retained audio_read speaker view, normal completion in three scripted chat requests |
| Actual idle expiry | Helper unloaded after 299.984 seconds from the post-request check; service remained available |

After the final text-status display adjustment, both audio CLI integration tests and the published CLI smoke were rerun successfully.

The suite covers multiplexed parent/worker submissions, FIFO serialization, active/queued cancellation, disconnect/reconnect, upload and queued deadlines, partial results, malformed/oversized frames and results, incompatible identities/builds/protocols, duplicate/unknown request IDs, upload length/hash checks, graceful/forced stop, generation pinning and fingerprint rejection, legacy helper compatibility, artifact permissions, receipts, cache invalidation and concurrent temporary-file cleanup.

The explicit process driver verified three independent clients with isolated results; four concurrent production start commands agreeing on one service; survival after launcher exit; separate services for different data directories; rejection of a thirty-third upload reservation and a thirty-third inference client; and service-crash cleanup of the warm Python process tree followed by successful restart/inference.

Kernel security inspection found a protected pipe DACL containing only the current user's full-access ACE. Machine-name UNC pipe access was rejected with Windows error 5 (access denied). Separate-account interactive acceptance was not performed.

An actual spawned Smith worker and its parent transcribed through the same service/helper while retaining distinct artifacts. A reload/cache check stopped the service after inference: a fresh matching request reused the same artifact; changed source bytes produced startup guidance without launching a private helper.

Real generation switching used a temporary hardlinked copy of the same immutable installed engines, without changing the current pointer. Requests pinned original / temporary / original generations and returned three distinct helper process IDs. The temporary generation was removed after the service returned to the original. This verifies real switching and request pinning, not compatibility or accuracy between different model releases.

## Measurements

Three independent processes analyzed the same approximately 24-second sample. The baseline used one private helper per process; the shared run started a fresh service. Memory samples, approximately 100 ms apart, include clients and their process trees.

| Measurement | Independent helpers | Shared service |
| --- | ---: | ---: |
| Sampled peak aggregate private bytes | 3.465 GiB | 1.512 GiB |
| Sampled peak aggregate working set | 3.033 GiB | 1.419 GiB |
| Last client's completion | 36.84 s | 41.01 s |

Sampled peak private memory decreased by 56.4%. Shared clients finished after 18.50, 29.38 and 41.01 seconds; service execution took 18.27, 10.88 and 11.61 seconds, with approximately 0, 18.29 and 29.17 seconds queued. Only the first loaded models. Serialization saves memory without promising higher throughput.

A repeated matching request reused durable evidence in 0.0085 seconds. The entry named "cold" in performance.json means the first request of that test and can use a helper warmed by earlier tests; the fresh-service process comparison above supplies the cold measurement. All measurements are observations on this machine.

The supplied checked transcript retained 0% normalized word error rate and two estimated speakers. Existing speech, silence, tone, mixed sound, MP3-range and one/two/overlapping-speaker assertions passed. These fixtures do not establish general recognition or speaker-attribution accuracy.

## Evidence and reproduction

Evidence is retained outside Git under artifacts/audio-service-acceptance: process-acceptance.json, parent-worker.json, real-tool-results.json, performance.json, pipe-security.json, offline-cache.json, generation-switch.json, published-smoke.ndjson, published-smoke-evidence.json and final-service-status.json. TRX reports are under each test project's TestResults directory.

~~~powershell
dotnet build AgentSmith.slnx -c Release
smith audio setup
smith service start
# Prepare fixtures with the installation's env/Scripts/python.exe:
# -I tests/prepare_local_audio_acceptance.py artifacts/audio-service-acceptance/fixtures
# -I tests/prepare_audio_speaker_fixtures.py artifacts/audio-service-acceptance/fixtures
$env:SMITH_AUDIO_ACCEPTANCE_DATA = Join-Path $env:LOCALAPPDATA 'AgentSmith'
$env:SMITH_AUDIO_ACCEPTANCE_FIXTURES = (Resolve-Path artifacts/audio-service-acceptance/fixtures).Path
$env:SMITH_AUDIO_IDLE_ACCEPTANCE = '1'
$env:OPENAI_API_KEY = ''
dotnet test AgentSmith.slnx -c Release --no-build -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
python tests/audio_service_acceptance.py --data-dir "$env:LOCALAPPDATA\AgentSmith" --real
~~~

The process driver stops/restarts the selected service and tests crash cleanup; use a service without unrelated work. The published smoke driver accepts --smith and --data-dir, uses a scripted loopback provider with real audio inference, and saves an identifiable acceptance conversation.

Inference ran without an audio API key, with offline hub settings, helper socket blocking and file-only decoding. OS-enforced firewall isolation was not tested. Native dependencies remain trusted code. Returned text and labels may enter the independently configured chat provider.
