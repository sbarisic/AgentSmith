using AgentSmith.Core;

namespace AgentSmith.Cli;

internal sealed partial class CliApp
{
    private void SubmitSteering(Session session, string targetId, string text)
    {
        var number = targetId == session.Id ? (int?)null : Store.Workers(session.Id).FirstOrDefault(w => w.ChildId == targetId)?.Number;
        var item = Store.SendSteering(session.Id, targetId, text);
        Write("steering.saved", SteeringNotice.Create(item, "Steering saved", number));
    }
    private bool SteeringCommand(string name, string args, Session session)
    {
        if (name == "/steer")
        {
            try { SubmitSteering(session, session.Id, args); }
            catch (UserFacingException) { input.Restore("/steer " + args); throw; }
            return true;
        }
        if (name != "/steering") return false;
        if (args.StartsWith("remove ", StringComparison.Ordinal))
        {
            Store.RemoveSteering(session.Id, args[7..].Trim());
            Write("steering.removed", new { message = "Pending steering removed." }); return true;
        }
        if (args.Length > 0) throw new UserFacingException("Usage: /steering [remove <message-id>]");
        var items = Store.Steering(session.Id).Where(i => i.State != "Removed").ToArray();
        var visible = items.Where(i => i.State is "Pending" or "Incorporated").Concat(items.Where(i => i.State == "Sent").TakeLast(20)).OrderBy(i => i.Sequence).ToArray();
        var workers = Store.Workers(session.Id);
        Write("steering.status", new { items = visible.Select(i => SteeringNotice.Create(i, i.State, workers.FirstOrDefault(w => w.ChildId == i.ConversationId)?.Number)),
            message = visible.Length == 0 ? "No steering messages." : "Pending messages stay with their original run. Paused/restarted work needs explicit continuation; cancelled/finished workers are never relaunched." });
        return true;
    }
}
