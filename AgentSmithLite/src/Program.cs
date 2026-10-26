using System.Text;
using System.Text.Json;
using AgentSmith.Core;
using AgentSmith.OpenAI;

namespace AgentSmithLite;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var stop = new CancellationTokenSource();
        CancellationTokenSource? turn = null;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; if (turn is not null) turn.Cancel(); else stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var options = Options.Parse(args);
            if (options.Command == "help") { Console.WriteLine(Help); return 0; }
            if (options.Command == "version")
            {
                Console.WriteLine($"AgentSmithLite {typeof(Program).Assembly.GetName().Version!.ToString(3)} | .NET {Environment.Version} | {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
                return 0;
            }
            if (!OperatingSystem.IsWindows()) throw new UserFacingException("AgentSmithLite requires Windows.");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var vault = new CredentialVault(options.DataDirectory, new WindowsSecretProtector());
            var auth = new ChatGptAuth(http, vault, new IdentityValidator(), "AgentSmithLite");
            switch (options.Command)
            {
                case "auth login":
                    var signedIn = await auth.LoginAsync(null, Console.WriteLine, stop.Token);
                    Console.WriteLine("Signed in: " + Clean(signedIn.Email)); return 0;
                case "auth login offline":
                    var offlineInput = new LineInput();
                    var offlineAccount = await auth.LoginAsync(null, Console.WriteLine, stop.Token, async (url, ct) =>
                    {
                        Console.WriteLine("Open this link in a browser on the computer where you can sign in:");
                        Console.WriteLine(url);
                        Console.WriteLine();
                        Console.WriteLine("Leave this command running. Complete sign-in and approval in that browser.");
                        Console.WriteLine("It will redirect to http://127.0.0.1:PORT/auth/callback?... and may show a connection error or keep loading.");
                        Console.WriteLine("Copy the COMPLETE callback URL from its address bar and paste it here, then press Enter.");
                        Console.WriteLine("Keep the link and callback private. This computer still needs Internet access to finish sign-in. Ctrl+C cancels.");
                        Console.Write("Callback URL> ");
                        return await offlineInput.ReadAsync(ct);
                    });
                    Console.WriteLine("Signed in: " + Clean(offlineAccount.Email)); return 0;
                case "auth status":
                    using (vault.Acquire())
                    {
                        var credentials = vault.Read();
                        var selected = credentials.Accounts.SingleOrDefault(a => a.Id == credentials.ActiveId);
                        Console.WriteLine(selected is null || !selected.CanInfer ? "Not signed in. Run AgentSmithLite auth login." : "Selected account: " + Clean(selected.Email));
                    }
                    return 0;
                case "auth logout":
                    var revoked = await auth.LogoutAsync(vault.Active().Id, stop.Token);
                    Console.WriteLine(revoked ? "Signed out and revocation confirmed." : "Local tokens cleared. Remote revocation was not confirmed."); return 0;
                case "models":
                    foreach (var choice in await auth.ModelsAsync(vault.Active().Id, stop.Token)) Console.WriteLine(Clean(choice.Id + "  " + choice.Name));
                    return 0;
            }
            if (!Directory.Exists(options.Workspace)) throw new UserFacingException("Workspace directory does not exist.");
            var account = vault.Active();
            var models = await auth.ModelsAsync(account.Id, stop.Token);
            var input = new LineInput();
            var model = options.Model;
            if (model is null)
            {
                if (Console.IsInputRedirected || options.Prompt is not null)
                    throw new UserFacingException("Specify --model. Run AgentSmithLite models to see available models.");
                for (var i = 0; i < models.Count; i++) Console.WriteLine($"{i + 1}. {Clean(models[i].Id)}");
                if (models.Count == 0) throw new UserFacingException("No models are available for this account.");
                while (model is null)
                {
                    Console.Write("Choose a model number: ");
                    var choice = await input.ReadAsync(stop.Token);
                    if (choice is null) return 0;
                    if (int.TryParse(choice, out var number) && number >= 1 && number <= models.Count) model = models[number - 1].Id;
                }
            }
            if (!models.Any(m => m.Id == model)) throw new UserFacingException("Model is not in this account's catalog. Run AgentSmithLite models.");
            var tools = new BasicTools(options.Workspace, async (name, fields, ct) =>
            {
                Console.WriteLine(); Console.WriteLine(Clean(name + " " + fields.ToJsonString()));
                if (options.Yes) return true;
                if (Console.IsInputRedirected) { Console.WriteLine("Declined: redirected input requires --yes for actions."); return false; }
                Console.Write("Allow this action? [y/N] ");
                return string.Equals((await input.ReadAsync(ct))?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
            });
            var client = new ModelClient(http, ct => auth.AccessTokenAsync(account.Id, ct));
            var chat = new Chat(client, tools, model, options.Workspace, s => Console.Write(Clean(s)), s => Console.WriteLine("\n" + Clean(s)));
            Console.WriteLine($"AgentSmithLite | {Clean(model)} | {Clean(options.Workspace)}");
            Console.WriteLine("/clear starts a new conversation; /quit exits. Ctrl+C cancels a turn. Chat history is kept only in memory.");
            if (options.Yes) Console.WriteLine("Actions are enabled without confirmation (--yes). Commands run as your Windows account.");
            while (!stop.IsCancellationRequested)
            {
                string? prompt;
                if (options.Prompt is not null) prompt = options.Prompt;
                else { Console.Write("\nYou> "); prompt = await input.ReadAsync(stop.Token); }
                if (prompt is null || prompt.Trim() is "/quit" or "/exit") break;
                if (prompt.Trim() == "/clear") { chat.Clear(); Console.WriteLine("Conversation cleared."); if (options.Prompt is not null) return 0; continue; }
                if (prompt.Trim() == "/help") { Console.WriteLine(Help); if (options.Prompt is not null) return 0; continue; }
                using var current = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                turn = current;
                try { Console.Write("\nAssistant> "); await chat.SendAsync(prompt, current.Token); Console.WriteLine(); }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("\nTurn cancelled. Completed actions remain; no action is automatically retried.");
                    if (options.Prompt is not null) return 130;
                }
                catch (Exception ex) when (Expected(ex))
                {
                    Console.Error.WriteLine("\n" + Message(ex));
                    if (options.Prompt is not null) return 1;
                }
                finally { turn = null; }
                if (options.Prompt is not null) break;
            }
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
        catch (Exception ex) when (Expected(ex)) { Console.Error.WriteLine(Message(ex)); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static bool Expected(Exception ex) => ex is UserFacingException or IOException or UnauthorizedAccessException or HttpRequestException
        or JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException or NotSupportedException or System.Net.HttpListenerException;
    private static string Message(Exception ex) => Clean(ex.Message.Replace("smith auth login", "AgentSmithLite auth login", StringComparison.Ordinal));
    internal static string Clean(string value) => string.Concat(value.Where(c => (!char.IsControl(c) || c is '\n' or '\r' or '\t') && c is not '\u202e' and not '\u202d'));

    private sealed class LineInput
    {
        private Task<string?>? pending;
        public async Task<string?> ReadAsync(CancellationToken ct)
        {
            pending ??= Task.Run(Console.ReadLine);
            var value = await pending.WaitAsync(ct);
            pending = null;
            return value;
        }
    }

    internal sealed record Options(string Command, string? Model, string Workspace, string DataDirectory, string? Prompt, bool Yes)
    {
        public static Options Parse(string[] args)
        {
            string? model = null, prompt = null;
            var workspace = Environment.CurrentDirectory;
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSmithLite");
            var yes = false; var words = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg is "--help" or "-h") return new("help", null, workspace, data, null, false);
                if (arg == "--version") return new("version", null, workspace, data, null, false);
                if (!arg.StartsWith('-')) { words.Add(arg); continue; }
                if (!seen.Add(arg)) throw new UserFacingException("Duplicate option: " + arg);
                if (arg == "--yes") { yes = true; continue; }
                if (arg is not ("--model" or "--workspace" or "--data-dir" or "--prompt")) throw new UserFacingException("Unknown option: " + arg);
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i])) throw new UserFacingException("Missing value for " + arg);
                switch (arg)
                {
                    case "--model": model = args[i]; break;
                    case "--workspace": workspace = args[i]; break;
                    case "--data-dir": data = args[i]; break;
                    case "--prompt": prompt = args[i]; break;
                }
            }
            var command = words.Count == 0 ? "chat" : string.Join(' ', words);
            if (command is not ("chat" or "models" or "auth login" or "auth login offline" or "auth logout" or "auth status")) throw new UserFacingException("Unknown command. Use --help.");
            if (command != "chat" && (model is not null || prompt is not null || yes)) throw new UserFacingException("--model, --prompt and --yes apply only to chat.");
            return new(command, model, Path.GetFullPath(workspace), Path.GetFullPath(data), prompt, yes);
        }
    }

    private const string Help = """
        AgentSmithLite - basic Windows chat and tools (.NET 6, x86)

        AgentSmithLite auth login
        AgentSmithLite auth login offline   Print a sign-in link; paste the callback URL from another browser
        AgentSmithLite auth status
        AgentSmithLite auth logout
        AgentSmithLite models
        AgentSmithLite chat [--model MODEL] [--workspace DIRECTORY]
        AgentSmithLite chat --model MODEL --prompt "MESSAGE"

        --data-dir DIRECTORY   Credential directory; default %LOCALAPPDATA%\AgentSmithLite
        --yes                  Allow file writes, commands and app launches without confirmation
        --help                 Show this help
        --version              Show runtime and process architecture

        Tools: list_files, read_file, write_file, run_process, start_app.
        File tools stay within the workspace. Commands/apps run as your Windows account,
        not in a sandbox. start_app leaves the launched app open. No undo or saved chats.
        Chat commands: /clear, /help, /quit. Ctrl+C cancels the current turn.
        """;
}
