using AgentSmith.Core;
using AgentSmith.Runtime;

namespace AgentSmith.Cli;

internal sealed partial class CliApp
{
    private bool PolicyCommand(string name, string args, Session session)
    {
        if (name is not ("/web-search" or "/limits" or "/worker-limits" or "/worker-budget" or "/delegation" or "/context")) return false;
        if (name == "/context" && args.Length == 0) return false;
        if (args.Length > 0 && activeRun is not null) throw new UserFacingException("Settings can only change while idle.");
        if (name == "/web-search")
        {
            if (args.Length > 0)
            {
                if (args is not ("native" or "basic" or "off")) throw new UserFacingException("Usage: /web-search [native|basic|off]");
                session.WebSearch = args;
            }
            var effective = session.WebSearch == "native" && session.Connection.Provider != ModelProvider.ChatGpt ? "basic (local provider)" : session.WebSearch;
            Write("web.search.policy", new { message = "Web search: " + effective + ". Native uses ChatGPT plan access, subject to model/account policy. Basic uses DuckDuckGo. Off disables search, not web_read or HTTP tools. New workers inherit this setting." });
        }
        else if (name == "/worker-limits")
        {
            if (args.Length > 0)
            {
                var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out var total) || !int.TryParse(parts[1], out var local))
                    throw new UserFacingException("Usage: /worker-limits [total local]");
                var policy = new WorkerPolicy { MaxConcurrent = total, MaxLocalConcurrent = local, MaxRequests = session.WorkerPolicy.MaxRequests, MaxMinutes = session.WorkerPolicy.MaxMinutes }; policy.Validate();
                var active = Store.Workers(session.Id).Where(w => w.Active).ToArray();
                if (active.Length > total || active.Count(w => w.Profile.Connection.Provider != ModelProvider.ChatGpt) > local)
                    throw new UserFacingException("Collect or cancel existing workers before lowering limits below active counts.");
                session.WorkerPolicy = policy;
            }
            Write("worker.limits", new { session.WorkerPolicy, message = $"Workers: {session.WorkerPolicy.MaxConcurrent} total · at most {Math.Min(session.WorkerPolicy.MaxConcurrent, session.WorkerPolicy.MaxLocalConcurrent)} local · parent excluded · 16 launches per run. ChatGPT workers share account usage limits." });
        }
        else if (name == "/worker-budget")
        {
            if (args.Length > 0)
            {
                var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out var requests) || !int.TryParse(parts[1], out var minutes))
                    throw new UserFacingException("Usage: /worker-budget [requests minutes]");
                var policy = new WorkerPolicy { MaxConcurrent = session.WorkerPolicy.MaxConcurrent, MaxLocalConcurrent = session.WorkerPolicy.MaxLocalConcurrent, MaxRequests = requests, MaxMinutes = minutes };
                policy.Validate(); session.WorkerPolicy = policy;
            }
            Write("worker.limits", new { session.WorkerPolicy, message = $"Worker budget: {session.WorkerPolicy.MaxRequests} requests · {session.WorkerPolicy.MaxMinutes} minutes per new worker, bounded by the parent's remaining deadline. Includes compaction requests. Existing workers retain their assigned budget." });
        }
        else if (name == "/limits")
        {
            if (args.Length > 0)
            {
                var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out var steps) || !int.TryParse(parts[1], out var minutes)) throw new UserFacingException("Usage: /limits [requests minutes]");
                var policy = new ExecutionPolicy { MaxRequests = steps, MaxMinutes = minutes }; policy.Validate(); session.Execution = policy;
            }
            Write("limits", new { session.Execution, batchRequests = session.Runs.LastOrDefault()?.BatchRequests, message = $"Limits: {session.Execution.MaxRequests} requests · {session.Execution.MaxMinutes} minutes per explicit batch. Current batch: {session.Runs.LastOrDefault()?.BatchRequests ?? 0} requests." });
        }
        else if (name == "/delegation")
        {
            if (args.Length > 0)
            {
                if (args is not ("proactive" or "manual")) throw new UserFacingException("Usage: /delegation [proactive|manual]"); session.Delegation = args;
            }
            Write("delegation", new { message = "Delegation: " + session.Delegation + ". Enabled profiles: " + string.Join(", ", session.EnabledWorkers.Select(w => w.Name)) });
        }
        else
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0] != "budget") throw new UserFacingException("Usage: /context budget balanced|max|<tokens>");
            var next = new ContextPolicy { Budget = parts[1], TokensPerByte = session.ContextPolicy.TokensPerByte }; next.Validate(); next.WorkingTokens(session.Connection); session.ContextPolicy = next;
            Write("context.policy", new { message = "Context budget: " + next.Budget + ". Continue paused work explicitly." });
        }
        if (args.Length > 0) Store.Save(session, new("policy.changed", session.Id, null, new { session.Execution, session.ContextPolicy, session.Delegation, session.WorkerPolicy, session.WebSearch }));
        return true;
    }
}
