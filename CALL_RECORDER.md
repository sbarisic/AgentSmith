# AgentSmith Call Recorder

Build a Windows x64 folder containing the desktop app, matching CLI/service, and .NET runtime:

```powershell
./publish-recorder.ps1
./artifacts/recorder-win-x64/AgentSmith.Recorder.exe
```

The app runs as the current user; administrator rights are not requested. Existing AgentSmith audio engines, accounts, and saved model defaults are reused. Install engines with `smith audio setup`; configure a summary provider with the existing CLI commands. Setup guidance includes `--data-dir` when applicable. Recordings can be made without installed transcription engines.

The GUI accepts `--data-dir <directory>` and, for development, `--smith <matching-executable>`. Keep the published GUI and CLI together. Stop an older idle service explicitly before upgrading; the app does not replace incompatible running services.

## Recording a call

The main panel has two tabs. **Recording** contains Record, Stop and transcribe, and Generate summary. **Configuration** contains call output and microphone selection, transcription and summary languages, the ten-second audio test, and advanced processing limits/retry options. Use Save configuration to remember changes; settings are also saved when recording or processing starts.

Select the output carrying the call and your microphone in Configuration. A headset is the supported baseline. Use the ten-second test to check both sides, then play it back in Recording and choose Keep test or Delete test.

During recording, Cancel recording stops and keeps the audio without starting transcription. During transcription or summary generation, Pause processing saves completed work for explicit resume. These controls appear only when relevant; capture itself does not pause. Summary is the default result view, with Transcript and Details & export beside it. Recording and analysis deletion are under Manage selected recording in the library.

Record and Stop control the shared audio service. Stop finalizes both tracks and starts local transcription for normal recordings. Test recordings require explicit transcription. A changed default device never redirects an active recording. Device failures finalize the call as interrupted; analyzing retained parts requires explicit acknowledgment.

Call audio includes every application playing on the selected output. For VM calls, route playback through a host output and keep the microphone accessible to the host. USB devices passed exclusively into a guest are not made host-accessible by administrator elevation.

**Recorder microphone mute is independent of Teams/VM mute.** The recorder captures a separate microphone stream. Its mute replaces incoming microphone samples with silence before storage. Muting Teams alone may still leave your microphone audible in the recording. “You” means the microphone track, not verified speaker identity.

## Reviewing results

Search the transcript; double-click a row or press Enter to play from its timestamp. The playback selector offers both tracks, call only, or microphone only. Speaker names can be renamed locally without modifying recognition evidence. Transcript text editing and automatic echo cancellation are not included.

Generate summary is a separate action. It sends retained transcript evidence to the displayed configured provider; the audio stays local. Summaries retain citations to source segments. Changing speaker names or summary language marks a previous summary outdated. Summary failure does not remove the transcript. English, Croatian, and Slovenian have dedicated language choices; additional Whisper language codes are accepted in advanced settings.

Export results creates a new directory with timestamped TXT/SRT, original structured evidence, speaker-name mapping, coverage, and any saved summary. Export audio separately to retain the native tracks and timing manifest. Track playlists play chunks sequentially within each track; they are not a synchronized mixed call.

## Closing and recovery

Closing your recording asks to stop and exit; transcription remains pending. Closing processing asks to pause and exit. No tray mode is used. An existing recording started by another client is shown, but merely closing the GUI does not stop it. A crashed GUI can leave service-owned recording active; reopen and use Stop.

Completed work is retained. Resume is explicit, and failed/uncertain retries have separate checkboxes. Uncertain retries may repeat a request whose response was lost. Storage limits never delete older calls automatically. Advanced controls retain the defaults of 10 GiB per recording/analysis store, a 1 GiB free-disk reserve, and 30 minutes/100 summary requests per processing attempt. Increase limits or resume when necessary.

Version 1 recordings and analyses remain readable. New paired recordings and analysis jobs use version 2. The named-pipe service protocol is version 4; GUI, CLI, and service must use the matching build. Live transcription, speakerphone echo cancellation, per-application capture, account onboarding, transcript editing, and embedded chat are deferred.

## Acceptance boundaries

Automated fixtures cover capture, mute, paired failure recovery, storage, timing, analysis overlap/reuse, summaries, and compatibility. Published-package checks use disposable data and synthetic audio. They do not establish headset/Teams/VM compatibility on every machine, transcription quality, or a successful real two-hour call.

Before treating those scenarios as accepted, run a two-hour Teams call and a VM call with a headset, verify both tracks at beginning/middle/end, test device unplugging and the two mute controls, and review English/Croatian/Slovenian speech against human transcripts. Record hardware, routing, recognition errors, and timing results. Mixed-language accuracy remains exploratory.
