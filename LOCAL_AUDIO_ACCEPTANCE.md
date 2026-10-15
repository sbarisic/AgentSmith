# Local audio acceptance

Validated on 15 October 2026 on Windows x64, AMD Ryzen AI 7 PRO 350, 24 GB RAM. This record covers the local Whisper and YAMNet implementation. Earlier cloud-audio evidence remains historical.

## Installed configuration

- Private Python 3.11.14, managed environment created with private uv 0.9.5.
- faster-whisper 1.2.1, multilingual small, CPU INT8, four threads, VAD enabled.
- Whisper revision `536b0662742c02347bc0e980a01041f333bce120`.
- TensorFlow CPU 2.20.0, Google YAMNet 1, 521 categories, matching class map.
- PyAV 18.1.0; no system FFmpeg installation is required by the helper.
- Current installation occupies approximately 2.26 GiB, excluding reusable download/package caches and previous generations.

Runtime, package and model pins/checksums are in `src/AgentSmith.Tools/LocalAudio/downloads.json` and `requirements.lock`. The published executable is `artifacts/publish/win-x64/smith.exe`, the existing user PATH destination. Managed data is under `%LOCALAPPDATA%/AgentSmith/audio`; system Python and persistent PATH were not changed.

## Real model observations

All six scenarios passed through Smith's actual tool dispatcher, artifacts, receipts and scripted chat responses. No paid chat inference was used. Measurements include process startup and model loading and are observations on this machine, not performance guarantees.

| Fixture | Operation | Wall time | Observation |
| --- | --- | ---: | --- |
| User `test01_20s.wav`, approximately 24 seconds | Combined | 13.64 s | English transcription; Speech mean score 0.871; 0% word error rate against the user's checked reference |
| JFK speech fixture | Combined | 11.42 s | Speech ranked first, mean score 0.939 |
| Five seconds of digital silence | Combined | 7.88 s | No speech detected; Silence ranked first, score approximately 1.000 |
| Five seconds of generated 440 Hz tone | Classification | 6.99 s | Telephone 0.984, Dial tone 0.975, Busy signal 0.968 |
| Speech, silence and tone concatenated | Combined | 11.26 s | Speech 0.586; telephone/tone categories next; transcript retained |
| MP3 of user sample, seconds 10 through 20 | Transcription | 4.03 s | Speech transcribed with timestamps relative to the original recording |

For the user sample, Whisper took 4.906 seconds and YAMNet 7.438 seconds including their model loads. The reference was supplied by the user in this conversation. Word error rate uses word-level edit distance after lowercasing and removing punctuation. `[Woman]` and `[Man]` are reference annotations and are excluded because this release does not identify speakers. The sample's SHA-256 is `a1deea3c0c2c1335361531ccb4dc5d46bf5182605237c5200e63eaf1c2863a7f`. One matching sample does not establish general transcription accuracy.

The pure tone result illustrates a classification limitation: high scores for telephone categories do not establish that a physical telephone was recorded. These are model associations, not calibrated certainty. This acceptance includes synthetic non-speech and mixed sound; it is not an environmental-sound benchmark.

Detailed output is retained locally in `artifacts/audio-acceptance/real-tool-results.json`; compact measurements are in `measurements.json`. Generated audio and downloaded model files remain outside Git.

## Verification coverage

The automated checks cover source containment, exclusions, file/range limits, supported languages, private snapshot stability, per-engine availability, artifact previews, partial component failure, cancellation and deadlines, helper crashes, malformed/oversized output, temporary cleanup, parent/worker serialization, receipt reuse and interruption recovery. Installation tests cover fresh/repeated setup, interrupted downloads, checksum mismatch, prerequisite/self-test failure, retry and preservation of the previous working generation. Scripted chat verifies the tool path without a cloud provider.

Six managed-Python helper tests cover decoding, downmixing/resampling, WAV/MP3 range alignment, maximum range, invalid/empty ranges, missing models and blocked socket connections. The MP3 test caught encoder-delay misalignment and now compares a selected range against the matching samples from a complete decode.

The final full Release suite passed with test collections serialized and real-model acceptance enabled: 827 passed, zero failed and zero skipped (504 unit/acceptance tests and 323 integration tests). The explicit real-model test also passed separately; it is skipped by default on machines without prepared models/fixtures. Normal parallel runs exposed intermittent existing SQLite pool-cleanup and retry-delay test races; serialization avoids relying on their scheduling. No production retry or storage behavior was changed for those races.

Published executable checks covered command help, `audio setup`, repeated setup without downloads, `audio status`, PATH resolution, and successful Whisper/YAMNet self-tests using the published helper. The installed generation remains stable across setup retries.

## Offline validation and its boundary

Real inference ran with `OPENAI_API_KEY` and `SMITH_AUDIO_MODEL` absent. Hub offline flags were enabled, local model paths were used, socket connections were blocked inside the helper, and PyAV was restricted to file protocols and explicit WAV/MP3 demuxers. A test confirms attempted socket connections fail. Setup alone uses network downloads.

An operating-system firewall deny rule was not tested: this process does not have administrator privileges. The verified boundary is the helper's offline configuration and socket blocking, not a Windows network sandbox. Native dependency code is trusted; the helper is not an arbitrary-code execution sandbox. Raw audio remains local, while returned text and labels may enter the independently configured chat provider's context.

## Reproduction

1. Build Release and run `smith audio setup`, using `--data-dir` if needed. Inspect `smith audio status` for the installation location.
2. Run `<installation>/env/Scripts/python.exe -I tests/local_audio_test.py`.
3. With the supplied user WAV present, run the explicit fixture preparer: `<installation>/env/Scripts/python.exe -I tests/prepare_local_audio_acceptance.py artifacts/audio-acceptance/fixtures`. This command downloads the pinned JFK test fixture; it is separate from inference.
4. Set `SMITH_AUDIO_ACCEPTANCE_FIXTURES` to that absolute fixture directory, `SMITH_AUDIO_ACCEPTANCE_DATA` to the Smith data directory, and remove `OPENAI_API_KEY` for the acceptance process.
5. Run `dotnet test AgentSmith.slnx -c Release -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1`. With the acceptance variables set, the full suite includes real-model acceptance; otherwise that one test is skipped.

The JFK excerpt comes from the [Whisper v20250625 test fixtures](https://github.com/openai/whisper/tree/v20250625/tests), distributed in the [MIT-licensed repository](https://github.com/openai/whisper/blob/v20250625/LICENSE). Its FLAC SHA-256 is `63a4b1e4c1dc655ac70961ffbf518acd249df237e5a0152faae9a4a836949715`. Non-speech fixtures are mathematical signals generated by the checked-in preparation script; no third-party sound recording is redistributed. The user sample is local user-supplied input, not bundled test audio.
