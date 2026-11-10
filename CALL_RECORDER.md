# AgentSmith Call Recorder

Build a Windows x64 folder containing the desktop app, matching CLI/service, and .NET runtime:

```powershell
./publish-recorder.ps1
./artifacts/recorder-win-x64/AgentSmith.Recorder.exe
```

The app runs as the current user; administrator rights are not requested. Startup automatically checks the local audio installation and downloads/installs missing or outdated components using the existing managed setup. Existing installations and verified download caches are reused; an up-to-date installation does not reinstall. A visible banner shows setup progress, cancellation, and a Retry action if setup fails. Saved recordings remain usable and recording is available after a failed/cancelled setup; Transcribe and Resume transcription retry preparation automatically. If a shared capture is already active at launch, setup waits until transcription is requested after recording. Custom `--data-dir` is respected. Configure the summary provider under **Settings > Summary agent**.

The GUI accepts `--data-dir <directory>` and, for development, `--smith <matching-executable>`. Keep the published GUI and CLI together. Stop an older idle service explicitly before upgrading; the app does not replace incompatible running services.

## Recording a call

The desktop uses WPF Fluent controls, outline icons, and a native Windows title bar. Orange accents, peach selections, and light/dark palettes are defined in `src/AgentSmith.Desktop/RecorderTheme.xaml`; change that file to experiment with colors. The app has no color settings. Windows high-contrast colors take precedence. The grouped toolbar provides recording, playback, transcription, Copy summary, and separate result/audio exports.

Top navigation offers **Home**, **Recordings**, and **Configuration**. Home shows a searchable recordings sidebar and three tabs: **Recording** for status and summary, **Transcript** for search/playback, speaker names and test Keep/Delete, and **Details** for coverage, exports and deletion. Recordings expands the library; double-click a call or press Enter to open it. Configuration contains the summary agent, audio devices, languages, ten-second test, and advanced processing limits. Use **Save changes** to apply the agent selection. Global settings remain drafts until Save changes. Starting or transcribing a recording does not apply unsaved configuration edits.

## Choosing a summary agent

Settings opens **Summary agent**. Choose ChatGPT, LM Studio, or Local. For ChatGPT, select a saved account or sign in through your browser, then refresh models and choose one. Sign-in can be cancelled. Accounts reuse the protected credential vault; recorder sign-ins do not change the CLI's active account. For LM Studio or Local, enter the loopback server address and refresh models. Unloaded LM Studio models cannot be selected; load them in LM Studio first. A text-generation model is sufficient for summaries; tool-use support is not required.

Refresh models checks the connection without sending transcript evidence. You can enter a model ID and save while the server is offline. Saving validates the configuration format; generation checks actual model/account availability. Connection errors expose **Open agent settings**. Agent settings remain a draft until **Save changes**. The recorder initially uses existing CLI defaults, then stores its own selection under `<data directory>/desktop/preferences.json`. Saving it does not change CLI defaults.

Summary attempts retain their original agent identity. If you choose another agent, **Restart summary with selected agent** explicitly starts summary processing with that selection. Audio and transcription remain intact. A previous completed summary remains displayed/exportable until the replacement succeeds; its generating agent is shown separately from the current selection. Existing model-specific caches and uncertain-request retry checks still apply. Changing settings or restarting never silently retries an uncertain request.

## Recording controls

The window starts at 1060 by 740 logical pixels. Recording uses a fixed layout; long summaries scroll inside their own panel. With a saved recording selected, **New** opens a draft. Edit its title, choose output and microphone from the inline dropdowns, then click **Record**. These device choices apply only to that capture; global defaults remain unchanged.

New recordings default to `New call — yyyy-MM-dd HH:mm:ss` using local time. The timestamp refreshes when recording starts; custom titles are preserved.

Save default output and microphone devices in Configuration. A new recording starts with those defaults and can override either device in its draft. A headset is the supported baseline. Use the ten-second test to check both sides, then play it back in Transcript and choose Keep test or Delete test.

Device lists refresh automatically when Windows reports an added, removed, renamed, enabled/disabled, or default audio device change. Current selections are preserved; a missing selected device is restored automatically when the same endpoint reconnects. Updates wait until recording or processing finishes, so an active recording never switches endpoints. Refresh devices remains available as a manual fallback.

Use Windows defaults loads the current Windows communications output and microphone, falling back to multimedia defaults. Save changes remembers the selected device IDs across launches. If saved devices are disconnected at startup, the app waits and selects them when they become available. It does not substitute other devices or follow later changes to Windows defaults unless you choose Use Windows defaults again.

Home shows a compact output/microphone display beside the controls. While recording, it reflects the active capture's actual devices, including captures started by another client. Unavailable selections show the last known device name instead of a blank selector; reconnecting restores the selection without another click.

During recording, Cancel recording stops and keeps the audio without starting transcription. During transcription or summary generation, Pause processing saves completed work for explicit resume. These controls appear only when relevant; capture itself does not pause. The Recording tab shows the summary directly. Transcript contains playback and speaker names; Details contains coverage and Manage selected recording. Summary citations open Transcript for timestamp playback.

Record and Stop control the shared audio service. Stop finalizes both tracks and starts local transcription for normal recordings. Test recordings require explicit transcription. A changed default device never redirects an active recording. Device failures finalize the call as interrupted; analyzing retained parts requires explicit acknowledgment.

Call audio includes every application playing on the selected output. For VM calls, route playback through a host output and keep the microphone accessible to the host. USB devices passed exclusively into a guest are not made host-accessible by administrator elevation.

**Recorder microphone mute is independent of Teams/VM mute.** The recorder captures a separate microphone stream. Its mute replaces incoming microphone samples with silence before storage. Muting Teams alone may still leave your microphone audible in the recording. “You” means the microphone track, not verified speaker identity.

## Reviewing results

Search the transcript; double-click a row or press Enter to play from its timestamp. The playback button shows Play, Pause playback, or Replay: pausing preserves your position, and Replay at the end starts the recording from the beginning. The playback bar shows elapsed and total time, an orange seek track, play/pause, and ten-second backward/forward controls. Click the track to seek; dragging previews the time and seeks once on release. Cancelling a drag restores the current playback position. Times display milliseconds. Arrow keys move 0.1 seconds and Page Up/Page Down move ten seconds. The playback selector offers both tracks, call only, or microphone only. Transcript range markers sit below the seek track (call above, microphone below); click one to select its passage and seek. Playback highlights all active passages, including overlaps, and leaves gaps unhighlighted. Follow playback scrolls the list when the active passage changes. Select a row to expand its exact time range and track. **Translate** sends only that passage to the saved summary agent, using the saved summary language. Results are cached separately by passage, language and agent; the original transcript stays intact. Translation can be cancelled through Pause processing.

Speaker names can be renamed locally without modifying recognition evidence. Transcript text editing and automatic echo cancellation are not included.

Generate summary is a separate action. It sends retained transcript evidence to the displayed configured provider; the audio stays local. Summaries retain citations to source segments. Changing speaker names or summary language marks a previous summary outdated. Summary failure does not remove the transcript. English, Croatian, and Slovenian have dedicated language choices; additional Whisper language codes are accepted in advanced settings.

Export results creates a new directory with timestamped TXT/SRT, original structured evidence, speaker-name mapping, coverage, and any saved summary. Export audio separately to retain the native tracks and timing manifest. Track playlists play chunks sequentially within each track; they are not a synchronized mixed call.

## Closing and recovery

Closing your recording asks to stop and exit; transcription remains pending. Closing processing asks to pause and exit. No tray mode is used. An existing recording started by another client is shown, but merely closing the GUI does not stop it. A crashed GUI can leave service-owned recording active; reopen and use Stop.

The app starts the matching hidden audio service when needed and tracks the exact process it launched. Normal exit waits for that service and its helper to stop after recording/processing cleanup, provided no other client or work needs them. A service that was already running before the app needed it is left alone. The conditional shutdown never forces another client's recording or inference to stop. If shutdown fails, the window stays open with an error so closing can be retried.

Completed work is retained. Resume is explicit, and failed/uncertain retries have separate checkboxes. Uncertain retries may repeat a request whose response was lost. Storage limits never delete older calls automatically. Advanced controls retain the defaults of 10 GiB per recording/analysis store, a 1 GiB free-disk reserve, and 30 minutes/100 summary requests per processing attempt. Increase limits or resume when necessary.

Version 1 recordings and analyses remain readable. New paired recordings and analysis jobs use version 2. The named-pipe service protocol is version 4; GUI, CLI, and service must use the matching build. Live transcription, speakerphone echo cancellation, per-application capture, transcript editing, and embedded chat are deferred.

## Acceptance boundaries

Capture diagnostics distinguish Windows-reported glitches from inconsistent device position counters. Shared-mode Bluetooth positions may advance at a different rate than delivered samples. Forward positions, no glitch flag, and packet timestamps agreeing within 20 ms allow continuous sample placement; periodic timestamp anchors retain drift correction. Backwards/stalled counters require agreement within one frame. Delivered packets are preserved instead of trimming them against jittery timestamps. Capture threads use Windows multimedia scheduling priority.

A normal Stop completes recording and proceeds to transcription even when nonfatal glitches were reported. Such recordings show Audio warnings; coverage is retained and analysis reports CompletedWithGaps. Device loss, invalid timestamps, storage failures, forced stops, and crashes still require interrupted-recording confirmation. Existing recordings retain their original state and coverage; previously discarded samples cannot be recovered by upgrading.

Automated fixtures cover capture, mute, paired failure recovery, storage, timing, analysis overlap/reuse, summaries, and compatibility. Published-package checks use disposable data and synthetic audio. They do not establish headset/Teams/VM compatibility on every machine, transcription quality, or a successful real two-hour call.

Before treating those scenarios as accepted, run a two-hour Teams call and a VM call with a headset, verify both tracks at beginning/middle/end, test device unplugging and the two mute controls, and review English/Croatian/Slovenian speech against human transcripts. Record hardware, routing, recognition errors, and timing results. Mixed-language accuracy remains exploratory.

Transcription actions have separate buttons: Transcribe creates the first analysis, Resume transcription continues pending work, Retry failed steps retries only failed components, and Retry uncertain steps explicitly repeats only requests with unknown outcomes. Completed analysis with audio gaps alone offers no redundant transcription action. Summary actions likewise show Generate summary, Resume summary, or Update summary only when applicable. The Recording tab renders compact summary headings, bullets, emphasis, and timestamp citations; analysis IDs and export metadata remain in Details and exported files.

The selected summary model and Agent settings button sit at the top-right of the Call summary card. Hidden summary actions leave no empty action row. After generation, the card leads with the result in readable body text. A single summary point is displayed as a paragraph; longer summaries retain bullets and sections. Provider information, the accuracy note, and coverage warnings are grouped under the collapsed Summary details section. Provider disclosure remains expanded before the first summary request.
