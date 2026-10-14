using System.Text.Json;
using AgentSmith.Core;

namespace AgentSmith.Storage;

public sealed partial class SessionStore
{
    public DirectoryPlans ListPlans(string workspace, int page = 1)
    {
        PlanDirectory.ValidatePage(page);
        workspace = PlanDirectory.Normalize(workspace);
        using var db = Open(); using var command = db.CreateCommand();
        // Read only catalog metadata, not all conversation histories or plan revisions.
        // Legacy payloads get their origin from the persisted owner, without rewriting it.
        command.CommandText = """
            SELECT p.id, p.conversation_id,
              COALESCE(NULLIF(json_extract(p.payload,'$.originWorkspace'),''), json_extract(c.snapshot,'$.workspace')),
              CASE WHEN COALESCE(json_extract(p.payload,'$.originWorkspace'),'') = '' THEN 1
                   ELSE COALESCE(json_extract(p.payload,'$.originInferred'),0) END,
              json_extract(p.payload,'$.createdAt'), json_extract(p.payload,'$.state'),
              json_extract(p.payload,'$.revisions[#-1].revision'),
              substr(json_extract(p.payload,'$.revisions[#-1].goal'),1,200)
            FROM plans p JOIN conversations c ON c.id=p.conversation_id
            WHERE json_array_length(p.payload,'$.revisions') > 0
            ORDER BY c.updated DESC, p.id
            """;
        using var reader = command.ExecuteReader();
        var entries = new List<PlanListing>(); var skip = (page - 1) * 10;
        while (reader.Read())
        {
            if (reader.IsDBNull(2) || !PlanDirectory.Matches(workspace, reader.GetString(2))) continue;
            if (skip-- > 0) continue;
            entries.Add(new(reader.GetString(0), reader.GetString(1), PlanDirectory.Normalize(reader.GetString(2)),
                reader.GetInt32(3) != 0, reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(5), reader.GetInt32(6), reader.GetString(7)));
            if (entries.Count == 11) break;
        }
        return new(workspace, page, entries.Count > 10, entries.Take(10).ToArray());
    }

    public DirectoryPlan ReadPlan(string workspace, string planId)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = """
            SELECT p.payload, p.conversation_id, json_extract(c.snapshot,'$.workspace')
            FROM plans p JOIN conversations c ON c.id=p.conversation_id WHERE p.id=$id
            """;
        command.Parameters.AddWithValue("$id", planId);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            var plan = JsonSerializer.Deserialize<WorkPlan>(reader.GetString(0), Json.Options)!;
            var legacy = string.IsNullOrEmpty(plan.OriginWorkspace);
            var origin = legacy ? reader.GetString(2) : plan.OriginWorkspace;
            if (plan.Revisions.Count > 0 && PlanDirectory.Matches(workspace, origin))
                return new(plan.Id, reader.GetString(1), PlanDirectory.Normalize(origin), legacy || plan.OriginInferred,
                    plan.CreatedAt, plan.State, plan.Current);
        }
        throw new UserFacingException("No saved plan with that ID in this workspace. Use /plans or plan_list to find plans for the current directory.");
    }
}
