using System.Diagnostics;
using System.Text.Json;
using AgentSmith.Core;
using AgentSmith.OpenAI;
using AgentSmith.Storage;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class CliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AgentSmith-cli-tests-" + Guid.NewGuid().ToString("N"));
    public CliTests() => Directory.CreateDirectory(root);
    private async Task<(int Code, string Output, string Error)> Invoke(string[] args, string input = "")
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(typeof(AgentSmith.Cli.Program).Assembly.Location);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(root);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await output, await error);
    }
    [Fact]
    public async Task HelpAndJsonStatusWorkWithoutCredentials()
    {
        var help = await Invoke(["--help"]); Assert.Equal(0, help.Code); Assert.Contains("resume", help.Output);
        var status = await Invoke(["--json", "auth", "status"]); Assert.Equal(0, status.Code);
        foreach (var line in status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        { using var json = JsonDocument.Parse(line); Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32()); }
        Assert.Empty(status.Error);
    }
    [Fact]
    public async Task PendingQuestionSurvivesEofAndDoesNotLeakCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        var account = new Account { AccessToken = "sensitive-access-seed", RefreshToken = "sensitive-refresh-seed", ClientId = "client", Email = "person@example.test" };
        var vault = new CredentialVault(root, new WindowsSecretProtector());
        using (var lease = vault.Acquire()) vault.Write(new Credentials { ActiveId = account.Id, Accounts = [account] });
        var session = new Session { Workspace = root, AccountId = account.Id, Model = "test-model" };
        var question = new Question { CallId = "question", Text = "Pick a file", Choices = ["one", "two"] };
        session.Runs.Add(new Run { State = RunState.AwaitingUser, Question = question, Calls = [new ToolCall { Id = "question", Name = "user_ask", Arguments = "{}" }] });
        var store = new SessionStore(root); store.Save(session, new("question.pending", session.Id, session.Runs[0].Id, question));
        var result = await Invoke(["--json", "resume", session.Id], "/no-such-command\n/status\n/markdown off\n\n");
        Assert.Equal(3, result.Code); Assert.Contains("Pick a file", result.Output); Assert.DoesNotContain("sensitive-", result.Output + result.Error);
        Assert.Contains("Unknown command", result.Output);
        Assert.DoesNotContain("run.started", result.Output);
        Assert.Null(store.Get(session.Id)!.Runs[0].Question!.Answer);
        Assert.Equal(RunState.AwaitingUser, store.Get(session.Id)!.Runs[0].State);
        var status = await Invoke(["--json", "auth", "accounts"]); Assert.DoesNotContain("sensitive-", status.Output + status.Error);
    }
    [Fact]
    public async Task AnotherProcessCannotOwnConversation()
    {
        var store = new SessionStore(root); using var lease = store.Acquire("blocked");
        var result = await Invoke(["--json", "resume", "blocked"]);
        Assert.NotEqual(0, result.Code); Assert.Contains("another active", result.Output);
    }
    [Fact]
    public async Task InspectIsReadOnly()
    {
        var store = new SessionStore(root); var session = new Session { Workspace = root }; session.Runs.Add(new Run { State = RunState.Running });
        store.Save(session, new("run.started", session.Id, session.Runs[0].Id, new { }));
        var result = await Invoke(["--json", "inspect", session.Runs[0].Id]); Assert.Equal(0, result.Code);
        Assert.Equal(RunState.Running, store.Get(session.Id)!.Runs[0].State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlashCommandsNeverStartARunOrEnterHistory(bool json)
    {
        if (!OperatingSystem.IsWindows()) return;
        var account = new Account { AccessToken = "unused-token", ClientId = "unused-client" };
        var vault = new CredentialVault(root, new WindowsSecretProtector());
        using (var lease = vault.Acquire()) vault.Write(new Credentials { ActiveId = account.Id, Accounts = [account] });
        var store = new SessionStore(root);
        var session = new Session { Workspace = root, AccountId = account.Id, Model = "test-model" };
        store.Save(session, new("conversation.created", session.Id, null, new { }));
        var result = await Invoke(json ? ["--json", "resume", session.Id] : ["--no-color", "resume", session.Id],
            "  /missing argument\n/help unexpected\n/markdown sideways\n/markdown off\n/markdown on\n/status\n/help\n/tools\n/mode edit\n/mode inspect\n/mode nonsense\n/changes\n/processes\n/recover\n/exit\n");
        Assert.Equal(0, result.Code);
        Assert.Contains("Unknown command", result.Output);
        Assert.Contains("does not accept arguments", result.Output);
        Assert.Contains("Usage: /markdown", result.Output);
        Assert.Contains("Markdown display disabled", result.Output);
        Assert.Contains("Markdown display enabled", result.Output);
        Assert.DoesNotContain("run.started", result.Output);
        Assert.Empty(store.Get(session.Id)!.Runs);
        Assert.Empty(store.Get(session.Id)!.History);
        Assert.Equal(PermissionMode.Inspect, store.Get(session.Id)!.Mode);
        Assert.Contains("file_apply_patch", result.Output);
        Assert.Contains("process_start", result.Output);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanReviewSurvivesEofModeSelectionAndRestartWithoutApproval(bool json)
    {
        if (!OperatingSystem.IsWindows()) return;
        var account = new Account { AccessToken = "unused-secret-token", ClientId = "unused-client" };
        var vault = new CredentialVault(root, new WindowsSecretProtector());
        using (var lease = vault.Acquire()) vault.Write(new Credentials { ActiveId = account.Id, Accounts = [account] });
        var store = new SessionStore(root); var session = new Session { Workspace = root, AccountId = account.Id, Model = "test-model", Workflow = WorkflowMode.Plan };
        var run = new Run { State = RunState.AwaitingPlan, Calls = [new ToolCall { Id = "queued", Name = "file_create", Arguments = "{}" }] }; session.Runs.Add(run);
        var ctx = new ToolExecutionContext(session, run, store, default);
        Planning.Update(ctx, 0, new PlanRevision { Goal = "Test plan", Steps = [new PlanStep { Id = "one", Description = "Check", Verification = "A test" }] }); Planning.Submit(session);
        ctx.Save("plan.submitted", new { });
        var args = json ? new[] { "--json", "resume", session.Id } : ["--no-color", "resume", session.Id];
        var first = await Invoke(args, "\n/mode execute\nimplement\n");
        Assert.Equal(3, first.Code); Assert.DoesNotContain("run.started", first.Output); Assert.DoesNotContain("unused-secret", first.Output + first.Error);
        var saved = store.Get(session.Id)!; var plan = Planning.Active(saved)!;
        Assert.Empty(plan.Approvals); Assert.Equal("Mode", plan.PendingReview!.Stage); Assert.Equal(WorkflowMode.Plan, saved.Workflow);
        var second = await Invoke(args, "/unknown\n/exit\n"); Assert.Equal(3, second.Code); Assert.Contains("Unknown command", second.Output);
        Assert.Empty(Planning.Active(store.Get(session.Id)!)!.Approvals); Assert.Equal(CallState.Pending, store.Get(session.Id)!.Runs.Single().Calls.Single().State);
    }
}
