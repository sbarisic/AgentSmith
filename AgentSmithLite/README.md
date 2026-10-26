# AgentSmithLite

A separate, basic Windows console assistant targeting .NET 6 and x86. It chats through your ChatGPT account and can read/write files, run console commands, and launch desktop apps. The self-contained package includes .NET 6.0.36; no .NET SDK is needed on the destination machine.

Windows 8.1 x86 is the intended legacy target. On 26 October 2026, the user confirmed that the latest build works on their Windows 8.1 machine, following manual sign-in and the streaming fix. Automated checks were run as a 32-bit process on Windows 11; the Windows 8.1 result is user-reported acceptance. Windows 8.1 and .NET 6 are out of support. See [Microsoft's Windows runtime compatibility information](https://learn.microsoft.com/en-us/dotnet/core/install/windows). HTTPS connectivity and browser sign-in still depend on the destination machine's TLS, certificates and browser.

## Run the portable build

Extract the whole ZIP into a writable folder. Keep its DLLs and runtime files beside the executable. Open Command Prompt in that folder:

```bat
AgentSmithLite.exe auth login
AgentSmithLite.exe chat --workspace "C:\Work"
```

Sign-in opens your default browser. Chat lists the models available to the signed-in account and waits for you to choose one. For an explicit model or a single prompt:

```bat
AgentSmithLite.exe models
AgentSmithLite.exe chat --model MODEL_ID --workspace "C:\Work"
AgentSmithLite.exe chat --model MODEL_ID --workspace "C:\Work" --prompt "List the files here"
```

Replace `MODEL_ID` with an ID from `models`. Account/model access is required. Lite uses the existing AgentSmith ChatGPT OAuth flow; it does not require a separate API key and does not automatically use the full application's account registrations.

### Sign in using another computer's browser

On the machine that will run Lite:

```bat
AgentSmithLite.exe auth login offline
```

1. Leave the command running. Copy its printed sign-in link to a browser on the computer where you can authenticate.
2. Complete sign-in, including any security-key/passkey verification, and approve access there.
3. The browser redirects to `http://127.0.0.1:PORT/auth/callback?...`. It may show a connection error or keep loading because Lite is running on the other computer.
4. Copy the **entire callback URL** from the browser address bar. Paste it at `Callback URL>` on the original machine and press Enter.
5. Wait for `Signed in`, then start chat normally.

This mode does not open a browser automatically. Even if you use a browser on the same computer, paste the callback URL at the prompt. It waits for explicit input; Ctrl+C or input EOF cancels. A malformed/mismatched callback or expired code requires starting sign-in again. Keep the links private and do not paste them into support chats.

Here, `offline` means manual browser handoff, not disconnected operation: the machine running Lite still needs Internet access to exchange the code, validate identity and chat. It retains the original PKCE verifier and validates the callback address, state, signed identity and nonce before storing credentials in its own DPAPI vault. No token migration or 2FA bypass is involved. The flow follows the [OpenAI sign-in contract](https://developers.openai.com/siwc/token-sharing-open-source/sign-in).

Try asking: `Read README.md`, `Run dir in this folder`, or `Open notepad.exe`.

## Tools and controls

| Tool | Behavior |
| --- | --- |
| `list_files` | Lists up to 200 entries in one directory. |
| `read_file` | Reads up to 16,000 characters of a text file. |
| `write_file` | Creates/replaces a UTF-8 text file, up to 32,000 characters. No undo. |
| `run_process` | Captures up to 16,000 characters per output stream. Waits up to 1–120 seconds, then terminates the owned process tree. Standard input is closed. |
| `start_app` | Launches an `.exe` independently. The app stays open when chat closes; launch success does not prove readiness. |

File tools use workspace-relative paths and reject directory traversal and links. This is a convenience boundary, **not an OS sandbox**: approved processes/apps run with your Windows account's access. Use a trusted workspace; path checks do not lock files against concurrent replacement.

Writes, commands and app launches display their arguments and wait for `y` before running. Reads/listing do not prompt. `--yes` enables actions without confirmations. Redirected input denies actions unless `--yes` is supplied. User/model text and tool results may be sent to ChatGPT.

The shell is explicit: use `cmd.exe` with `arguments: ["/c", "COMMAND"]`, or `powershell.exe` with `arguments: ["-NoProfile", "-Command", "SCRIPT"]`. Programs that require an interactive terminal are not supported by `run_process`; use `start_app` to open them separately. Console output is decoded as UTF-8 (or its detected byte-order mark); legacy tools emitting an OEM code page may need UTF-8 output configured.

- `/clear`: discard this conversation and begin a new one.
- `/quit` or `/exit`: exit; running owned commands are terminated, independently launched apps remain open.
- `Ctrl+C` during a turn: cancel it. Completed side effects remain and are not automatically retried. At the idle prompt, Ctrl+C exits.
- `--help`, `--version`: show usage or the actual runtime/architecture.
- `auth status`, `auth logout`: inspect sign-in or clear the selected account's tokens and attempt remote revocation.

Chats exist only in memory. There is no SQLite, saved conversation recovery, undo, audio, local inference, background service, worker agent, semantic code analysis, or terminal graphics. The conversation is bounded to approximately 512,000 serialized characters, with 16 model rounds per message. Use `/clear` when full. No automatic compaction or model-request retry is performed.

Credentials use current-user Windows DPAPI under `%LOCALAPPDATA%\AgentSmithLite`. `--data-dir DIRECTORY` selects another credential directory. Do not copy credentials between Windows users/machines; sign in on the destination machine instead.

## Build from source on modern Windows

From the repository's `AgentSmithLite` directory, using a .NET 10 SDK (10.0.400 or a later stable .NET 10 feature band):

```powershell
.\build.ps1
```

The script builds and runs the x86 acceptance suite, publishes the app, checks startup, and creates:

```text
artifacts/publish/AgentSmithLite/win-x86/AgentSmithLite.exe
artifacts/AgentSmithLite-win-x86.zip
```

These paths are relative to the repository root. Outputs are ignored by Git. The modern SDK compiles C# 12 syntax against .NET 6; build on the modern machine, then copy the package to the legacy machine. Lite's own `global.json`, build properties and dependency versions leave the full application's .NET 10 target intact. Do not invoke the repository's `.slnx` to build Lite; it has its own project and script.

Lite compiles the existing OAuth/DPAPI and Windows Job Object sources directly, without referencing the full app's projects. IdentityModel is pinned to 8.14.0 because newer versions pull in cryptography packages that do not support .NET 6. Package warnings and NuGet audit remain enabled. The intentional .NET 6 end-of-life build notice is suppressed.

## Verification

Version 1.0.1 fixes a streaming parser error that produced `Invalid response completion` when completed tool/reasoning items arrived in `response.output_item.done` events and the final completion snapshot had empty or omitted output. Lite now retains these completed items in output-index order and uses them after a successful `response.completed`. It does not run tools on partial, failed or disconnected responses, and does not execute a tool twice when both streamed items and a final snapshot are present. All 29 x86 acceptance checks passed, including regression fixtures that reproduced the original failure. The reported live stream was not captured, so another response shape could still require diagnosis; malformed completion errors now distinguish missing status, invalid output and genuinely empty output without logging response content.

The dependency-free acceptance runner is `tests/AgentSmithLite.Tests.csproj`. It tests actual x86 file/process operations, child-process termination, separate app launch, DPAPI, authentication protocol checks, and a scripted HTTP streaming conversation that runs real file/process tools. It also checks denial, malformed arguments, interrupted streams, duplicate tool calls, and bounded history/tool loops.

The scripted provider tests do not establish live ChatGPT access. Separately, the user reported successful manual sign-in and confirmed that the latest build works on their Windows 8.1 machine. No live response trace or comprehensive Windows 8.1 compatibility test was captured by the developer.
