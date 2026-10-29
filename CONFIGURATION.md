# Smith configuration

Full AgentSmith reads `config.toml` from `%LOCALAPPDATA%\AgentSmith` on startup.
With `--data-dir <directory>`, it reads `<directory>\config.toml` instead.
AgentSmithLite does not read this file.

```powershell
smith config          # Show the file location and whether it exists
smith config init     # Create it using existing model defaults; never overwrite
smith model --json    # Show the effective default model connection
```

`config init` includes commented `[chatgpt]`, `[local]`, `[lmstudio]`,
`[permissions]`, `[execution]`, `[workers]` and `[display]` sections. It copies the
saved model into its provider section; other model IDs are empty until selected.
It does not sign in, contact a provider, start inference, or change conversations.

For the local server on port 8081, the file can contain:

```toml
provider = "local"

[chatgpt]
model = "gpt-6-astra"
max_output_tokens = 16384

[local]
model = "qwen3.8-flash-next-q2_0"
endpoint = "http://127.0.0.1:8081/v1"
max_output_tokens = 8192

[lmstudio]
model = "" # Set a loaded model ID before using this provider.
endpoint = "http://127.0.0.1:1234"
max_output_tokens = 8192

[permissions]
mode = "Execute"

[execution]
max_steps = 150
max_minutes = 60
context_budget = "balanced"

[workers]
delegation = "manual"
max_concurrent = 6
max_local_concurrent = 1
max_requests = 100
max_minutes = 30

[display]
color = true
verbosity = "compact"
```

All settings are optional. Keys are case-sensitive and use underscores.
Provider, permission, delegation and verbosity values are case-insensitive.
Comments, quoted keys, literal strings, inline tables and dotted keys follow TOML
syntax. Files must be at most 64 KiB. Syntax errors, duplicate keys, unknown keys,
incorrect types and out-of-range values stop startup with an error identifying
the file and setting or syntax location. Values are not echoed in these errors.

## Settings

| Setting | Accepted values | Default when not otherwise saved |
| --- | --- | --- |
| `provider` | `chatgpt`, `local`, `lmstudio` | `chatgpt` |
| `<provider>.model` | Model ID from that provider's catalog | Empty/unset until selected |
| `local.endpoint`, `lmstudio.endpoint` | Loopback HTTP URL | Local: `http://127.0.0.1:8080/v1`; LM Studio: `http://127.0.0.1:1234` |
| `<provider>.max_output_tokens` | Integer, 256–32768 | ChatGPT: 16384; local/LM Studio: 8192 |
| `permissions.mode` | `Inspect`, `Edit`, `Execute` | `Execute` |
| `execution.max_steps` | Integer, 1–10000 model requests | 150 |
| `execution.max_minutes` | Integer, 1–1440 | 60 |
| `execution.context_budget` | `"balanced"`, `"max"`, or a quoted integer of at least 1024 | `"balanced"` |
| `workers.delegation` | `manual`, `proactive` | `proactive` |
| `workers.max_concurrent` | Integer, 1–16 | 6 |
| `workers.max_local_concurrent` | Integer, 1–16, also bounded by the total | 1 |
| `workers.max_requests` | Integer, 1–10000 | 100 |
| `workers.max_minutes` | Integer, 1–1440 | 30 |
| `display.color` | Boolean | `true` |
| `display.verbosity` | `compact`, `verbose` | `compact` |

The `local` provider uses Chat Completions and accepts an origin or `/v1` URL.
The `lmstudio` provider uses LM Studio's discovery API and Responses API and
requires an origin without `/v1`. ChatGPT uses its fixed authenticated service;
there is no `chatgpt.endpoint` setting. Model discovery validates the
selected model and its available context before a new chat starts.

## Precedence and persistence

The top-level `provider` selects the active section. `--provider local` selects
`[local]` for that command, including its model, endpoint and output budget.
For `chat` and `run`, explicit CLI options override TOML. Missing model settings
fall back to the existing SQLite defaults, then the most recent parent
conversation, then built-in defaults. Settings from a different provider are
not carried across an explicit provider change. The earlier flat model settings
remain readable for the active provider; a provider section takes precedence.
Missing non-model settings use
the built-in defaults. The workspace remains the current directory unless
`--workspace` is supplied.

`smith models` uses the configured provider and endpoint unless overridden.
`smith model <id>`, `/model <id>`, and successful `smith chat` setup save the
selected model connection to its provider section, set the active `provider`,
and keep the SQLite defaults in sync. Other provider sections remain unchanged.
Existing legacy flat files retain their flat format when saved. Existing
comments and unrelated settings are preserved; saves use a temporary file and
atomic replacement. Model saves create the file if it does not exist. `smith run`
does not save its model overrides. Changes to other CLI flags do not rewrite TOML.

Resumed conversations retain their saved model, permissions, budgets and worker
settings. Display settings apply to the current CLI process, including resume;
`/display` changes verbosity for that process. `--no-color`, `NO_COLOR`, redirected
output and `--json` still disable colors.

Named worker profiles remain in SQLite and are enabled with `--workers`.
The TOML worker settings control delegation and limits for new parent chats;
they do not change individual worker profiles. History, task state, plans,
memories, account references and tool results remain in `sessions.db`.
Credentials remain protected in `credentials.dpapi` and are never written to TOML.

The implementation uses [Tomlyn](https://github.com/xoofx/Tomlyn) for TOML parsing
and syntax spans so model updates retain comments and formatting.
