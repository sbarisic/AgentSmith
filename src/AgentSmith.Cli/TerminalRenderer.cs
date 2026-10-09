using System.Globalization;
using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.Cli;

/// <summary>Human-facing presentation only. Machine events and persisted state bypass this renderer.</summary>
public sealed class TerminalRenderer(TextWriter output, bool color, int width = 90, TimeProvider? timeProvider = null)
{
    private readonly int width = Math.Clamp(width, 32, 100);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long? startedAt;
    private bool streaming;
    private MarkdownWriter? markdownStream;
    public bool MarkdownEnabled { get; set; } = true;
    private long inputTokens;
    private long outputTokens;
    private int tools;
    private string? lastQuestion;

    public void Render(string type, object payload)
    {
        var data = JsonSerializer.SerializeToElement(payload, Json.Options);
        if (type == "text.delta") { Stream(Text(data, "text")); return; }
        if (type is "model.requested" or "question.answered") return;
        if (type == "model.completed")
        {
            if (TryJson(Text(data, "usage"), out var usage))
            {
                inputTokens += Number(usage, "input_tokens");
                outputTokens += Number(usage, "output_tokens");
            }
            return;
        }
        EndStream();
        switch (type)
        {
            case "plan.display":
                Plan(data.GetProperty("plan")); break;
            case "plan.review": Plan(data); break;
            case "plan.review.pending": MessageText(Text(data, "text"), "accent"); MessageText(Text(data, "responses"), "dim"); break;
            case "plan.decision": MessageText("Plan decision: " + Text(data, "decision") + " " + Text(data, "mode"), "accent"); break;
            case "plan.updated":
                MessageText("Plan revision " + Text(data, "revision"), "accent");
                if (data.TryGetProperty("steps", out var progress)) foreach (var step in progress.EnumerateArray()) MessageText("[" + Text(step, "status") + "] " + Text(step, "id"), "dim");
                break;
            case "artifact.result": if (TryJson(Text(data, "content"), out var artifact)) Artifact(artifact); break;
            case "tools":
                Title("Registered tools · serial execution");
                foreach (var tool in data.EnumerateArray())
                { Wrapped("  " + Text(tool, "name") + " · " + Text(tool, "capability") + (Flag(tool, "enabled") ? "" : " · disabled"), "accent"); Wrapped("    " + Text(tool, "description")); }
                break;
            case "changes":
                Title("File-tool checkpoints · process side effects are not checkpointed");
                if (data.GetArrayLength() == 0) Line("  No file-tool changes.", "dim");
                foreach (var change in data.EnumerateArray())
                { Wrapped("  " + Text(change, "id") + " · " + Text(change, "kind") + " · " + Text(change, "state"), "accent"); MessageText(Text(change, "diff")); }
                break;
            case "processes":
                Title("Processes owned by this conversation");
                foreach (var process in data.EnumerateArray())
                { Wrapped("  " + Text(process, "id") + " · " + Text(process, "state") + " · exit " + Text(process, "exitCode"), "accent"); Wrapped("    " + Text(process, "executable")); Wrapped("    " + Text(process, "logPath"), "dim"); }
                break;
            case "operation":
                if (TryJson(Text(data, "content"), out var operation)) Operation(operation);
                break;
            case "conversation":
            case "conversation.resumed":
                Header(data);
                if (type == "conversation.resumed" && data.TryGetProperty("history", out var history)) History(history);
                break;
            case "run.started":
                startedAt = clock.GetTimestamp(); inputTokens = outputTokens = 0; tools = 0; lastQuestion = null;
                MessageTitle("AgentSmith");
                Line("  │ Thinking…", "dim");
                break;
            case "tool.started":
                tools++;
                Line("  │ › " + ToolLabel(Text(data, "name")), "dim");
                break;
            case "tool.completed":
                if (TryJson(Text(data, "content"), out var result))
                {
                    if (result.ValueKind != JsonValueKind.Object) break;
                    if (result.TryGetProperty("current", out _)) Plan(result);
                    else if (result.TryGetProperty("verificationId", out _) && result.TryGetProperty("kind", out _)) Verification(result);
                    else if (result.TryGetProperty("artifactId", out _) || result.TryGetProperty("artifacts", out _)) Artifact(result);
                    else if (result.TryGetProperty("checkpointId", out _) || result.TryGetProperty("processId", out _) || result.TryGetProperty("exitCode", out _)) Operation(result);
                    else if (result.TryGetProperty("error", out var error)) MessageText("  " + error.GetString(), "warning");
                    else if (result.TryGetProperty("path", out var path))
                        MessageText("  " + path.GetString() + (result.TryGetProperty("totalLines", out var lines) ? $"  ·  {lines} lines" : ""), "dim");
                    else if (result.TryGetProperty("files", out var files)) MessageText($"  {files.GetArrayLength()} files" + (Flag(result, "truncated") ? " · more available" : ""), "dim");
                    else if (result.TryGetProperty("matches", out var matches)) MessageText($"  {matches.GetArrayLength()} matches" + (Flag(result, "limitReached") ? " · limit reached" : ""), "dim");
                }
                break;
            case "tool.rejected":
                if (TryJson(Text(data, "result"), out var rejection)) MessageText(Text(rejection, "error"), "warning");
                break;
            case "run.outcome":
                var elapsed = startedAt is { } start ? clock.GetElapsedTime(start) : (TimeSpan?)null;
                startedAt = null;
                var state = Text(data, "state");
                var detail = new List<string>();
                if (elapsed is { } duration) detail.Add($"Executed in {duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s");
                if (tools > 0) detail.Add($"{tools} tool call{(tools == 1 ? "" : "s")}");
                if (inputTokens + outputTokens > 0) detail.Add($"{inputTokens.ToString("N0", CultureInfo.InvariantCulture)} in / {outputTokens.ToString("N0", CultureInfo.InvariantCulture)} out tokens");
                Line("  │", "border");
                Wrapped("  ╰─ " + (state switch { "Completed" => "✓ Done", "AwaitingUser" => "Question ready", _ => state }) + (detail.Count > 0 ? "  ·  " + string.Join("  ·  ", detail) : ""), state is "Completed" or "AwaitingUser" ? "dim" : "warning");
                if (!string.IsNullOrEmpty(Text(data, "reason"))) Wrapped("  " + Text(data, "reason"), "warning");
                output.WriteLine();
                break;
            case "models":
                Title("Available models");
                if (width >= 72)
                {
                    Line("  " + "MODEL".PadRight(28) + "ID", "dim");
                    foreach (var model in data.EnumerateArray())
                    {
                        var name = Safe(Text(model, "name"));
                        if (name.Length > 26) { Wrapped("  " + name, "bright"); Line("  " + new string(' ', 28) + Safe(Text(model, "id")), "dim"); }
                        else { Paint("  " + name.PadRight(28), "bright"); Line(Text(model, "id"), "accent"); }
                    }
                }
                else foreach (var model in data.EnumerateArray()) { Wrapped("  " + Text(model, "name"), "bright"); Wrapped("    " + Text(model, "id"), "dim"); output.WriteLine(); }
                output.WriteLine(); Wrapped("  Start a chat: smith chat --model <id>", "dim"); output.WriteLine();
                break;
            case "question.heading":
                Message("Your input", Text(data, "text"));
                break;
            case "answer.submitted":
                Message("You", Text(data, "text"));
                break;
            case "question.pending":
                var questionId = Text(data, "id");
                if (lastQuestion == questionId) break;
                lastQuestion = questionId;
                Line("  ├─ Your input", "accent"); MessageText(Text(data, "text"));
                if (data.TryGetProperty("choices", out var choices)) foreach (var choice in choices.EnumerateArray()) MessageText("  • " + choice.GetString());
                MessageText("Type your answer. Nothing is selected automatically.", "dim");
                break;
            case "auth.accounts":
                Title("ChatGPT accounts");
                var accounts = data.GetProperty("accounts");
                if (accounts.GetArrayLength() == 0) Line("  Not signed in. Run smith auth login.", "dim");
                foreach (var account in accounts.EnumerateArray())
                {
                    Wrapped("  " + (Text(account, "id") == Text(data, "activeId") ? "● " : "  ") + Text(account, "email"), "bright");
                    Wrapped("    " + Text(account, "id"), "dim");
                    Line("    " + (Flag(account, "planUsage") ? "ChatGPT plan enabled" : "Sign-in or plan permission required"), "dim");
                }
                output.WriteLine(); break;
            case "auth.signed_in":
                Title("Signed in", "success"); Wrapped("  " + Text(data, "email"));
                Line(Flag(data, "planUsage") ? "  ChatGPT plan enabled · Run smith models to choose a model." : "  Plan permission is required before chatting.", "dim"); break;
            case "auth.selected": Line("  ✓ Account selected: " + Text(data, "id"), "success"); break;
            case "sessions":
                Title("Conversations");
                if (data.GetArrayLength() == 0) Line("  No saved conversations yet.", "dim");
                foreach (var session in data.EnumerateArray())
                {
                    Wrapped("  " + Text(session, "model") + "  ·  " + Text(session, "state"), "bright");
                    Wrapped("    " + Text(session, "workspace"), "dim");
                    Wrapped("    smith resume " + Text(session, "id"), "accent"); output.WriteLine();
                }
                break;
            case "status":
                Field("Workflow", Text(data, "workflow") + " / " + Text(data, "mode"));
                Title("Conversation details"); Field("Session", Text(data, "id")); Field("State", Text(data, "state"));
                if (TryJson(Text(data, "context"), out var context)) Context(context);
                break;
            case "run.inspection":
                Title("Run details"); Field("Run", Text(data, "id"));
                var stateValue = data.GetProperty("state");
                Field("State", stateValue.ValueKind == JsonValueKind.Number ? ((RunState)stateValue.GetInt32()).ToString() : stateValue.ToString());
                Field("Steps", Text(data, "steps")); Field("Reason", Text(data, "reason"));
                if (TryJson(Text(data, "contextManifest"), out var manifest)) Context(manifest);
                if (data.TryGetProperty("calls", out var calls)) foreach (var call in calls.EnumerateArray())
                { Wrapped("  › " + Text(call, "name")); Wrapped("    " + Text(call, "result"), "dim"); }
                break;
            case "resume.confirmation":
                Title("Continue saved work", "warning"); Field("State", Text(data, "state")); Field("Reason", Text(data, "reason")); Wrapped("  " + Text(data, "message")); break;
            case "help":
                Wrapped("  /plan new <task> · /plan show · /plan revise <note> · /plan implement · /plan leave", "accent");
                Wrapped("  /tasks · /artifacts · /artifact <id> [search <text>]", "accent");
                Title("Chat commands");
                foreach (var (command, description) in new[] { ("/help", "Show these commands"), ("/status", "Session and context details"), ("/context", "Inspect the context budget"), ("/markdown on|off", "Format Markdown or show its source"), ("/paste", "Multiline input; finish with /end"), ("/exit", "Leave this conversation"), ("Ctrl+C", "Stop the current run") })
                    Line("  " + command.PadRight(20) + description);
                foreach (var (command, description) in new[] { ("/tools", "List registry tools and enabled capabilities"), ("/mode <mode>", "Inspect, Edit, or Execute"), ("/changes", "Review file-tool checkpoints"), ("/undo <id>", "Undo a checkpoint if files still match"), ("/processes", "List process states and logs"), ("/stop <id>", "Stop an owned process tree"), ("/recover [abandon]", "Inspect or acknowledge uncertain operations") })
                    Wrapped("  " + command.PadRight(20) + description);
                output.WriteLine(); break;
            case "error": Title("Error", "warning"); Wrapped("  " + Text(data, "message")); output.WriteLine(); break;
            default:
                if (!string.IsNullOrEmpty(Text(data, "message"))) Wrapped("  " + Text(data, "message"));
                break;
        }
    }

    public void Prompt()
    {
        EndStream();
        MessageTitle("You");
        Paint("  │ you › ", "accent");
        output.Flush();
    }

    public void EndPrompt() => Line("  ╰─", "border");

    private void MessageTitle(string title)
    { output.WriteLine(); Line("  ╭─ " + title, "accent"); Line("  │", "border"); }

    private void MessageText(string text, string style = "normal") => Wrapped(text, style, "  │ ");

    private void Message(string title, string text)
    { MessageTitle(title); MessageText(text); EndPrompt(); output.WriteLine(); }

    private void Header(JsonElement data)
    {
        var inner = width - 4;
        output.WriteLine();
        Line("  ╭" + new string('─', inner) + "╮", "border");
        Box("AgentSmith", "bright", inner);
        var mode = Text(data, "mode");
        Box(Text(data, "model") + "  ·  ChatGPT plan  ·  " + (Text(data, "workflow").Length == 0 ? "Work" : Text(data, "workflow")) + " / " + (mode.Length == 0 ? "Inspect" : mode), "accent", inner);
        Box(Text(data, "workspace"), "dim", inner);
        Line("  ╰" + new string('─', inner) + "╯", "border");
        if (mode == "Execute") Wrapped("  Processes run as your Windows account; the workspace is not a sandbox.", "dim");
        Wrapped("  Remaining usage unavailable", "dim");
        Wrapped("  Manage usage: https://chatgpt.com/settings/usage", "dim");
        Line("  /help commands   /exit leave   Ctrl+C stop", "dim");
        output.WriteLine();
    }
    private void Box(string value, string style, int inner)
    {
        foreach (var part in Chunks(Safe(value).Replace('\n', ' ').Replace('\t', ' '), inner - 4))
        {
            Paint("  │  ", "border"); Paint(part.PadRight(inner - 4), style); Line("  │", "border");
        }
    }
    private void History(JsonElement history)
    {
        var messages = history.EnumerateArray().Where(i => Text(i, "role") is "user" or "assistant").TakeLast(6).ToArray();
        if (messages.Length == 0) return;
        Line("  Recent conversation", "dim");
        foreach (var message in messages)
        {
            if (!message.TryGetProperty("content", out var content)) continue;
            MessageTitle(Text(message, "role") == "user" ? "You" : "AgentSmith");
            var historyWriter = Text(message, "role") == "assistant" ? new MarkdownWriter(output, color, width, MarkdownEnabled) : null;
            void HistoryText(string value) { if (historyWriter is null) MessageText(value); else historyWriter.Append(value); }
            if (content.ValueKind == JsonValueKind.String) HistoryText(content.GetString() ?? "");
            else if (content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray()) if (!string.IsNullOrEmpty(Text(block, "text"))) HistoryText(Text(block, "text"));
            historyWriter?.Finish();
            EndPrompt();
        }
        output.WriteLine(); Line("  ── Resumed ──", "dim"); output.WriteLine();
    }
    private void Context(JsonElement data)
    {
        Field("Context", $"{Number(data, "characters"):N0} / {Number(data, "limit"):N0} characters");
        Field("History", $"{Number(data, "historyItems")} items");
        Field("Guidance", Text(data, "instructionSource"));
    }
    private void Operation(JsonElement data)
    {
        if (data.TryGetProperty("checkpointId", out _))
        { MessageText("Checkpoint " + Text(data, "checkpointId") + " · " + Text(data, "state"), "accent"); MessageText(Text(data, "diff")); }
        if (data.TryGetProperty("processId", out _))
        {
            MessageText("Process " + Text(data, "processId") + " · " + Text(data, "state") + (Text(data, "exitCode").Length > 0 ? " · exit " + Text(data, "exitCode") : ""), "dim");
            if (data.TryGetProperty("durationMilliseconds", out var elapsed)) MessageText($"Elapsed {elapsed.GetDouble() / 1000:0.0}s", "dim");
            if (data.TryGetProperty("output", out var chunks)) foreach (var chunk in chunks.EnumerateArray()) MessageText("[" + Text(chunk, "stream") + "] " + Text(chunk, "text"));
            if (Flag(data, "truncated")) MessageText("Output truncated; inspect the retained process log.", "warning");
            if (Flag(data, "hasMoreOutput")) MessageText("More retained output is available from the next cursor.", "dim");
        }
        else if (data.TryGetProperty("content", out _)) MessageText(Text(data, "content"));
    }
    private void Plan(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) { MessageText("No saved plan.", "dim"); return; }
        var current = data.GetProperty("current");
        MessageText("Plan " + Text(data, "id") + " · revision " + Text(current, "revision") + " · " + Text(data, "state"), "accent");
        MessageText(Text(current, "goal"));
        foreach (var key in new[] { "deliverables", "constraints", "questions" })
            if (current.TryGetProperty(key, out var list) && list.GetArrayLength() > 0) { MessageText(key + ":", "accent"); foreach (var item in list.EnumerateArray()) MessageText("  - " + item.GetString()); }
        foreach (var step in current.GetProperty("steps").EnumerateArray())
        {
            MessageText("[" + Text(step, "status") + "] " + Text(step, "id") + " · " + Text(step, "description"), "accent");
            MessageText("  Verify: " + Text(step, "verification"), "dim");
            if (Text(step, "note").Length > 0) MessageText("  " + Text(step, "note"));
        }
    }
    private void Artifact(JsonElement data)
    {
        if (data.TryGetProperty("artifacts", out var artifacts)) foreach (var item in artifacts.EnumerateArray()) MessageText(Text(item, "id") + " · " + Text(item, "kind"), "accent");
        else
        {
            MessageText("Artifact " + Text(data, "artifactId") + " · " + Text(data, "state"), "accent");
            if (data.TryGetProperty("text", out var text)) MessageText(text.GetString() ?? "");
            if (data.TryGetProperty("matches", out var matches)) foreach (var match in matches.EnumerateArray()) MessageText("[" + Text(match, "stream") + "] " + Text(match, "excerpt"));
            if (Flag(data, "hasMore")) MessageText("More content at cursor " + Text(data, "cursor") + ". Ask the agent to read the next section.", "dim");
        }
    }
    private void Verification(JsonElement data)
    {
        MessageText(Text(data, "kind") + " · " + Text(data, "state") + " · exit " + Text(data, "exitCode"), "accent");
        if (Text(data, "kind") == "test") MessageText($"Tests: {Number(data, "passed")} passed, {Number(data, "failed")} failed, {Number(data, "skipped")} skipped; {Number(data, "total")} total.");
        if (data.TryGetProperty("items", out var items)) foreach (var item in items.EnumerateArray()) MessageText(Text(item, "code") + " " + Text(item, "file") + " " + Text(item, "name") + " " + Text(item, "outcome") + " " + Text(item, "message"));
        MessageText("Report artifact: " + Text(data, "summaryArtifactId"), "dim");
        if (Text(data, "reason").Length > 0) MessageText(Text(data, "reason"), "warning");
    }
    private void Stream(string text)
    {
        if (!streaming)
        {
            if (startedAt is null) MessageTitle("AgentSmith"); else Line("  │", "border");
            streaming = true;
            markdownStream = new(output, color, width, MarkdownEnabled);
        }
        markdownStream!.Append(text);
    }
    private void EndStream()
    {
        if (!streaming) return;
        markdownStream!.Finish(); markdownStream = null;
        streaming = false;
    }
    private void Field(string label, string value)
    { if (value.Length > 0) Wrapped("  " + label.PadRight(10) + value); }
    private void Title(string text, string style = "accent") { output.WriteLine(); Line("  " + text, style); output.WriteLine(); }
    private void Wrapped(string text, string style = "normal", string prefix = "")
    {
        foreach (var line in Safe(text).Split('\n'))
        {
            var indent = new string(' ', Math.Min(line.TakeWhile(char.IsWhiteSpace).Count(), width / 2));
            var remaining = line.TrimStart();
            var available = width - 2 - indent.Length - prefix.Length;
            while (remaining.Length > available)
            {
                var split = remaining.LastIndexOf(' ', available, available + 1);
                if (split <= 0) split = available;
                if (char.IsHighSurrogate(remaining[split - 1])) split--;
                Line(prefix + indent + remaining[..split], style);
                remaining = remaining[split..].TrimStart();
            }
            Line(prefix + indent + remaining, style);
        }
    }
    private static IEnumerable<string> Chunks(string value, int length)
    {
        if (value.Length == 0) { yield return ""; yield break; }
        for (var i = 0; i < value.Length;)
        {
            var count = Math.Min(length, value.Length - i);
            if (i + count < value.Length && char.IsHighSurrogate(value[i + count - 1])) count--;
            yield return value.Substring(i, count); i += count;
        }
    }
    private void Line(string value, string style = "normal") { Paint(value, style); output.WriteLine(); }
    private void Paint(string value, string style)
    {
        var escape = style switch { "accent" => "\u001b[96m", "bright" => "\u001b[1m", "dim" => "\u001b[90m", "border" => "\u001b[36m", "warning" => "\u001b[33m", "success" => "\u001b[32m", _ => "" };
        if (color && escape.Length > 0) output.Write(escape);
        output.Write(Safe(value));
        if (color && escape.Length > 0) output.Write("\u001b[0m");
    }
    public static string Safe(string value) => new(value.Where(c => (!char.IsControl(c) || c is '\n' or '\t') && c is not (>= '\u202A' and <= '\u202E') and not (>= '\u2066' and <= '\u2069')).ToArray());
    private static string Text(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()) : "";
    private static long Number(JsonElement data, string name) => data.TryGetProperty(name, out var v) && v.TryGetInt64(out var result) ? result : 0;
    private static bool Flag(JsonElement data, string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static bool TryJson(string text, out JsonElement result)
    {
        try { using var doc = JsonDocument.Parse(text); result = doc.RootElement.Clone(); return result.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { result = default; return false; }
    }
    private static string ToolLabel(string name) => name switch { "workspace_list" => "Listing files", "file_read" => "Reading file", "text_search" => "Searching workspace", "user_ask" => "Waiting for your input", _ => name };
}
