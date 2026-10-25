using AgentSmith.Core;

namespace AgentSmith.Cli;

internal sealed partial class CliApp
{
    private async Task RequestExit(Session session)
    {
        if (exitRequested) return;
        exitRequested = true;
        CancelActive();
        Store.HoldQueue(session.Id, "CLI exit");
        await RecordingExitNotice();
    }

    private async Task<string?> ReadDecision(Question question, Session session, CancellationToken ct)
    {
        var state = new QuestionInput(question.Choices);
        while (true)
        {
            var result = await QuestionPrompt.ReadAsync(question, color, ct, input.ReadKeyAsync, PasteNote, state);
            if (result.Outcome == QuestionOutcome.Answer) return result.Text;
            if (result.Outcome is QuestionOutcome.Exit or QuestionOutcome.Interrupted) return null;
            using var draft = input.SuspendDraft();
            while (true)
            {
                var command = await ReadInput(ct, "question-commands:" + question.Id);
                if (input.DraftCleared) continue;
                if (command is null) return null;
                if (command.Length == 0 && QuestionPrompt.IsInteractive) break;
                if (!command.TrimStart().StartsWith('/'))
                { Write("error", new { message = "Enter a command, or empty Enter to return to the question. No answer was submitted." }); continue; }
                if (await HandleCommand(command, session, answering: true) == CommandAction.Exit) return null;
            }
        }
    }
}
