using System.CommandLine;
using System.Text;
using AgentSmith.Core;
using AgentSmith.Storage;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace AgentSmith.Cli;

// User defaults only. Conversations, account references and credentials retain their own stores.
internal sealed class SmithConfig
{
    private readonly TomlTable values;
    public string Path { get; }
    public bool Exists { get; }
    public ModelProvider? Provider => String("provider") is { } value ? Enum.Parse<ModelProvider>(value, true) : null;
    private SmithConfig(string path, string? text)
    {
        Path = path; Exists = text is not null;
        values = Parse(text ?? "", path);
        Validate(values, "");
    }
    public static SmithConfig Load(string directory)
    {
        var path = System.IO.Path.Combine(directory, "config.toml");
        try { return new(path, Read(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new UserFacingException($"Cannot read {path}. Check file access."); }
    }
    private static string? Read(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new UserFacingException($"{path} exceeds the 64 KiB config limit.");
        return File.ReadAllText(path);
    }
    private static TomlTable Parse(string text, string path)
    {
        try
        {
            // The DOM reader alone can replace duplicate keys; syntax validation rejects them.
            SyntaxParser.ParseStrict(text);
            return TomlSerializer.Deserialize<TomlTable>(text, new TomlSerializerOptions { DuplicateKeyHandling = TomlDuplicateKeyHandling.Error, SourceName = path })!;
        }
        catch (TomlException ex)
        { throw new UserFacingException($"Invalid TOML in {path} at line {ex.Line}, column {ex.Column}. Check syntax and duplicate keys."); }
    }
    private void Invalid(string key, string expected) => throw new UserFacingException($"Invalid setting '{key}' in {Path}: {expected}.");
    private void Validate(TomlTable table, string prefix)
    {
        foreach (var (name, value) in table)
        {
            var key = prefix + name;
            switch (key)
            {
                case "permissions": case "execution": case "workers": case "display":
                case "chatgpt": case "local": case "lmstudio":
                    if (value is not TomlTable nested) { Invalid(key, "expected a table"); break; }
                    Validate(nested, key + "."); break;
                case "provider": Choice(key, value, "chatgpt", "local", "lmstudio"); break;
                case "permissions.mode": Choice(key, value, "Inspect", "Edit", "Execute"); break;
                case "workers.delegation": Choice(key, value, "manual", "proactive"); break;
                case "display.verbosity": Choice(key, value, "compact", "verbose"); break;
                case "display.color": if (value is not bool) Invalid(key, "expected true or false"); break;
                case "model": case "chatgpt.model": case "local.model": case "lmstudio.model":
                    if (value is not string model || (key == "model" && string.IsNullOrWhiteSpace(model)) || model.Length > 2048 || model.Any(char.IsControl)) Invalid(key, "expected a model ID without control characters");
                    break;
                case "endpoint": case "local.endpoint": case "lmstudio.endpoint":
                    if (value is not string endpoint) { Invalid(key, "expected a loopback HTTP URL"); break; }
                    try
                    {
                        if (key == "lmstudio.endpoint") AgentSmith.LmStudio.LmStudioModelClient.Endpoint(endpoint);
                        else AgentSmith.LmStudio.LocalChatModelClient.Endpoint(endpoint);
                    }
                    catch (UserFacingException) { Invalid(key, "expected a loopback HTTP URL ending in / or /v1"); }
                    break;
                case "max_output_tokens": case "chatgpt.max_output_tokens": case "local.max_output_tokens": case "lmstudio.max_output_tokens":
                    Range(key, value, 256, 32768); break;
                case "execution.max_steps": case "workers.max_requests": Range(key, value, 1, 10000); break;
                case "execution.max_minutes": case "workers.max_minutes": Range(key, value, 1, 1440); break;
                case "workers.max_concurrent": case "workers.max_local_concurrent": Range(key, value, 1, 16); break;
                case "execution.context_budget":
                    if (value is not string budget) { Invalid(key, "expected balanced, max, or a quoted token count"); break; }
                    try { new ContextPolicy { Budget = budget }.Validate(); }
                    catch (UserFacingException) { Invalid(key, "expected balanced, max, or a quoted token count of at least 1024"); }
                    break;
                default: Invalid(key, "unknown setting"); break;
            }
        }
    }
    private void Choice(string key, object? value, params string[] allowed)
    {
        if (value is not string s || !allowed.Contains(s, StringComparer.OrdinalIgnoreCase)) Invalid(key, "expected " + string.Join(", ", allowed));
    }
    private void Range(string key, object? value, long min, long max)
    {
        if (value is not long n || n < min || n > max) Invalid(key, $"expected an integer from {min} to {max}");
    }
    private object? Find(string key)
    {
        object? current = values;
        foreach (var part in key.Split('.')) current = current is TomlTable table && table.TryGetValue(part, out var value) ? value : null;
        return current;
    }
    public string? String(string key) => Find(key) as string;
    public int? Integer(string key) => Find(key) is long value ? checked((int)value) : null;
    public bool? Boolean(string key) => Find(key) as bool?;
    public string? ProviderString(ModelProvider provider, string key, ModelProvider legacyProvider)
    {
        var value = String(provider.ToString().ToLowerInvariant() + "." + key)
            ?? (provider == legacyProvider ? String(key) : null);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    public int? ProviderInteger(ModelProvider provider, string key, ModelProvider legacyProvider) =>
        Integer(provider.ToString().ToLowerInvariant() + "." + key) ?? (provider == legacyProvider ? Integer(key) : null);
    public T Option<T>(ParseResult parse, Option<T> option, string key)
    {
        if (parse.GetResult(option) is { Implicit: false } || Find(key) is not { } value) return parse.GetValue(option)!;
        if (typeof(T).IsEnum) return (T)Enum.Parse(typeof(T), (string)value, true);
        if (typeof(T) == typeof(int)) return (T)(object)checked((int)(long)value);
        if (key == "workers.delegation") value = ((string)value).ToLowerInvariant();
        return (T)value;
    }
    public static void SaveModel(string directory, ChatModelSelection selection)
    {
        using var lease = SessionStore.AcquireLock(directory, "config");
        var config = Load(directory);
        var original = Read(config.Path) ?? "";
        if (original.Length == 0 || new[] { "chatgpt", "local", "lmstudio" }.Any(config.values.ContainsKey))
        {
            var sectioned = UpdateSection(original.Length == 0 ? Template : original, selection);
            _ = new SmithConfig(config.Path, sectioned);
            Write(config.Path, sectioned, overwrite: true);
            return;
        }
        var document = SyntaxParser.Parse(original);
        var changes = new List<(int Offset, int Length, string Text)>();
        var additions = new StringBuilder();
        var updates = new Dictionary<string, object>
        {
            ["provider"] = selection.Provider.ToString().ToLowerInvariant(), ["model"] = selection.Model,
            ["endpoint"] = selection.Endpoint, ["max_output_tokens"] = (long)selection.MaxOutputTokens
        };
        foreach (var (key, value) in updates)
        {
            var existing = document.KeyValues.FirstOrDefault(k => Parse(k.Key + " = true", config.Path).ContainsKey(key));
            if (key == "endpoint" && selection.Provider == ModelProvider.ChatGpt)
            {
                // Keep surrounding whitespace/comments, but remove the irrelevant local endpoint.
                if (existing is { Key: { } unusedKey, Value: { } unused })
                    changes.Add((unusedKey.Span.Offset, unused.Span.Offset + unused.Span.Length - unusedKey.Span.Offset, ""));
                continue;
            }
            var serialized = TomlSerializer.Serialize(new TomlTable { [key] = value });
            var replacement = SyntaxParser.Parse(serialized).KeyValues.Single().Value!;
            if (existing?.Value is { } old)
                changes.Add((old.Span.Offset, old.Span.Length, serialized.Substring(replacement.Span.Offset, replacement.Span.Length)));
            else additions.Append(serialized.TrimEnd()).AppendLine();
        }
        var updated = original;
        foreach (var change in changes.OrderByDescending(c => c.Offset)) updated = updated.Remove(change.Offset, change.Length).Insert(change.Offset, change.Text);
        updated = additions + updated;
        _ = new SmithConfig(config.Path, updated);
        Write(config.Path, updated, overwrite: true);
    }
    private static string UpdateSection(string text, ChatModelSelection selection)
    {
        var document = SyntaxParser.ParseStrict(text);
        var section = selection.Provider.ToString().ToLowerInvariant();
        var nodes = new Dictionary<string, KeyValueSyntax>(StringComparer.Ordinal);
        var tables = new Dictionary<string, TableSyntaxBase>(StringComparer.Ordinal);
        var edits = new List<(int Offset, int Length, string Text)>();
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string KeyName(KeySyntax key)
        {
            var parsed = Parse(key + " = true", "config.toml");
            var parts = new List<string>();
            object current = parsed;
            while (current is TomlTable table)
            {
                var entry = table.Single(); parts.Add(entry.Key); current = entry.Value;
            }
            return string.Join('.', parts);
        }
        void Collect(KeyValueSyntax node, string prefix)
        {
            var key = prefix + KeyName(node.Key!);
            nodes.Add(key, node);
            if (node.Value is InlineTableSyntax inline)
                foreach (var item in inline.Items) Collect(item.KeyValue!, key + ".");
        }
        foreach (var node in document.KeyValues) Collect(node, "");
        foreach (var table in document.Tables)
        {
            var name = KeyName(table.Name!); tables.Add(name, table);
            foreach (var node in table.Items) Collect(node, name + ".");
        }
        static string Literal(object value)
        {
            var serialized = TomlSerializer.Serialize(new TomlTable { ["value"] = value });
            var span = SyntaxParser.Parse(serialized).KeyValues.Single().Value!.Span;
            return serialized.Substring(span.Offset, span.Length);
        }
        bool Replace(string key, object value)
        {
            if (!nodes.TryGetValue(key, out var node)) return false;
            var span = node.Value!.Span;
            edits.Add((span.Offset, span.Length, Literal(value))); return true;
        }
        if (!Replace("provider", section)) edits.Add((0, 0, "provider = " + Literal(section) + newline));
        var desired = new Dictionary<string, object> { ["model"] = selection.Model, ["max_output_tokens"] = (long)selection.MaxOutputTokens };
        if (selection.Provider != ModelProvider.ChatGpt) desired["endpoint"] = selection.Endpoint;
        var missing = desired.Where(pair => !Replace(section + "." + pair.Key, pair.Value)).ToArray();
        if (missing.Length > 0)
        {
            var assignments = missing.Select(pair => pair.Key + " = " + Literal(pair.Value)).ToArray();
            if (tables.TryGetValue(section, out var table))
            {
                var eol = table.EndOfLineToken;
                var offset = eol is null ? table.CloseBracket!.Span.Offset + table.CloseBracket.Span.Length : eol.Span.Offset + eol.Span.Length;
                edits.Add((offset, 0, (eol is null ? newline : "") + string.Join(newline, assignments) + newline));
            }
            else if (nodes.TryGetValue(section, out var container) && container.Value is InlineTableSyntax inline)
            {
                if (inline.Items.LastOrDefault() is { Comma: null, KeyValue.Value: { } last })
                    edits.Add((last.Span.Offset + last.Span.Length, 0, ","));
                edits.Add((inline.CloseBrace!.Span.Offset, 0, newline + string.Join("," + newline, assignments) + newline));
            }
            else if (nodes.Keys.Any(key => key.StartsWith(section + ".", StringComparison.Ordinal)))
                edits.Add((0, 0, string.Join(newline, assignments.Select(line => section + "." + line)) + newline));
            else edits.Add((text.Length, 0, newline + "[" + section + "]" + newline + string.Join(newline, assignments) + newline));
        }
        foreach (var edit in edits.OrderByDescending(edit => edit.Offset))
            text = text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.Text);
        return text;
    }
    public static void Initialize(string directory, ChatModelSelection? selection)
    {
        using var lease = SessionStore.AcquireLock(directory, "config");
        var path = System.IO.Path.Combine(directory, "config.toml");
        if (File.Exists(path)) throw new UserFacingException($"{path} already exists. Edit it directly; it was not overwritten.");
        var text = selection is null ? Template : UpdateSection(Template, selection);
        _ = new SmithConfig(path, text);
        Write(path, text, overwrite: false);
    }
    private static void Write(string path, string text, bool overwrite)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new UserFacingException($"Cannot save {path}. Check file access and retry."); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal const string Template = """
        # Defaults for new conversations. Explicit CLI flags override these values.
        # Resumed conversations keep their saved model, permissions and budgets.
        # Select the active provider: "chatgpt", "local", or "lmstudio".
        provider = "chatgpt"

        # ChatGPT account connection. Sign in with: smith auth login
        # Uses the ChatGPT service; no local endpoint or API key belongs here.
        [chatgpt]
        # Model ID from smith models --provider chatgpt; empty means not selected.
        model = ""
        # Output token reserve per request (256..32768).
        max_output_tokens = 16384

        # OpenAI-compatible local server using the Chat Completions API.
        [local]
        # Model ID from smith models --provider local; empty means not selected.
        model = ""
        # Loopback HTTP origin or /v1 base URL. Match your server's port.
        endpoint = "http://127.0.0.1:8080/v1"
        max_output_tokens = 8192

        # LM Studio discovery and Responses APIs, distinct from the local adapter.
        [lmstudio]
        # Loaded model ID from smith models --provider lmstudio.
        model = ""
        # HTTP origin only, without /v1. LM Studio defaults to port 1234.
        endpoint = "http://127.0.0.1:1234"
        max_output_tokens = 8192

        # Permissions for new parent conversations; does not grant Windows admin.
        [permissions]
        # Inspect: read-only tools. Edit: file edits. Execute: also run processes.
        mode = "Execute"

        # Per-batch execution limits. Reaching a limit pauses for continuation.
        [execution]
        # Maximum model requests (1..10000) and elapsed minutes (1..1440).
        max_steps = 150
        max_minutes = 60
        # "balanced", "max", or a quoted input-token budget of at least "1024".
        context_budget = "balanced"

        # Delegation defaults; named worker profiles are configured separately.
        [workers]
        # "proactive" permits automatic delegation; "manual" requires direction.
        delegation = "proactive"
        # Parallel workers excluding the parent (1..16); local is bounded by total.
        max_concurrent = 6
        max_local_concurrent = 1
        # Per-worker budgets, also bounded by the parent's remaining deadline.
        max_requests = 100
        max_minutes = 30

        # Display preferences for this CLI process, including resumed chats.
        [display]
        # --no-color, NO_COLOR, --json and redirected output still disable color.
        color = true
        # "compact" or "verbose" tool output; /display changes it during a chat.
        verbosity = "compact"
        """;
}
