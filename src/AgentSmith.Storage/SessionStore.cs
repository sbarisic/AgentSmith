using System.Text.Json;
using AgentSmith.Core;
using Microsoft.Data.Sqlite;

namespace AgentSmith.Storage;

public sealed partial class SessionStore : ISessionStore, IWorkerStore, IMemoryStore, ISteeringStore
{
    private readonly string root;
    public string DataDirectory => root;
    private readonly string connectionString;
    public SessionStore(string directory)
    {
        root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "locks"));
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "sessions.db"), DefaultTimeout = 5 }.ToString();
        using var schemaLease = AcquireLock(root, "schema");
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);
            INSERT INTO schema_version SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);
            CREATE TABLE IF NOT EXISTS conversations(id TEXT PRIMARY KEY, snapshot TEXT NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversations(id), state TEXT NOT NULL, snapshot TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS protocol_items(conversation_id TEXT NOT NULL REFERENCES conversations(id), ordinal INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(conversation_id,ordinal));
            CREATE TABLE IF NOT EXISTS invocations(run_id TEXT NOT NULL REFERENCES runs(id), call_id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(run_id,call_id));
            CREATE TABLE IF NOT EXISTS questions(id TEXT PRIMARY KEY, run_id TEXT NOT NULL REFERENCES runs(id), payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(sequence INTEGER PRIMARY KEY AUTOINCREMENT, conversation_id TEXT NOT NULL REFERENCES conversations(id), run_id TEXT, timestamp TEXT NOT NULL, type TEXT NOT NULL, payload TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT version FROM schema_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version == 1)
        {
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "sessions-v1-" + Guid.NewGuid().ToString("N") + ".backup.db") }.ToString()))
            { backup.Open(); db.BackupDatabase(backup); }
            using var migration = db.BeginTransaction();
            command.Transaction = migration;
            command.CommandText = """
                UPDATE conversations SET snapshot=json_set(snapshot,'$.mode',0) WHERE json_type(snapshot,'$.mode') IS NULL;
                CREATE TABLE mutations(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE processes(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, payload TEXT NOT NULL);
                UPDATE schema_version SET version=2;
                """;
            command.ExecuteNonQuery(); migration.Commit(); version = 2; command.Transaction = null;
        }
        if (version == 2)
        {
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "sessions-v2-" + Guid.NewGuid().ToString("N") + ".backup.db") }.ToString()))
            { backup.Open(); db.BackupDatabase(backup); }
            using var migration = db.BeginTransaction(); command.Transaction = migration;
            command.CommandText = """
                UPDATE conversations SET snapshot=json_set(snapshot,'$.workflow',0) WHERE json_type(snapshot,'$.workflow') IS NULL;
                CREATE TABLE plans(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE artifacts(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE verifications(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE receipts(conversation_id TEXT NOT NULL, call_id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(conversation_id,call_id));
                UPDATE schema_version SET version=3;
                """;
            command.ExecuteNonQuery(); migration.Commit(); version = 3; command.Transaction = null;
        }
        if (version == 3)
        {
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "sessions-v3-" + Guid.NewGuid().ToString("N") + ".backup.db") }.ToString()))
            { backup.Open(); db.BackupDatabase(backup); }
            using var migration = db.BeginTransaction(); command.Transaction = migration;
            command.CommandText = "CREATE TABLE context_snapshots(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, payload TEXT NOT NULL); CREATE TABLE prompt_queue(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, sequence INTEGER NOT NULL, state TEXT NOT NULL, payload TEXT NOT NULL, UNIQUE(conversation_id,sequence)); CREATE TABLE queue_holds(conversation_id TEXT PRIMARY KEY, reason TEXT); UPDATE schema_version SET version=4;";
            command.ExecuteNonQuery(); migration.Commit(); version = 4; command.Transaction = null;
        }
        if (version == 4)
        {
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "sessions-v4-" + Guid.NewGuid().ToString("N") + ".backup.db") }.ToString()))
            { backup.Open(); db.BackupDatabase(backup); }
            using var migration = db.BeginTransaction(); command.Transaction = migration;
            command.CommandText = "CREATE TABLE worker_profiles(name TEXT PRIMARY KEY COLLATE NOCASE, payload TEXT NOT NULL); CREATE TABLE workers(id TEXT PRIMARY KEY, parent_id TEXT NOT NULL, run_id TEXT NOT NULL, call_id TEXT NOT NULL, child_id TEXT NOT NULL UNIQUE, payload TEXT NOT NULL, UNIQUE(parent_id,run_id,call_id)); UPDATE schema_version SET version=5;";
            command.ExecuteNonQuery(); migration.Commit(); command.Transaction = null; version = 5;
        }
        if (version == 5)
        {
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "sessions-v5-" + Guid.NewGuid().ToString("N") + ".backup.db") }.ToString()))
            { backup.Open(); db.BackupDatabase(backup); }
            using var migration = db.BeginTransaction(); command.Transaction = migration;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS memories(id TEXT PRIMARY KEY, title_key TEXT NOT NULL, origin TEXT NOT NULL, updated TEXT NOT NULL, payload TEXT NOT NULL, UNIQUE(title_key,origin));
                CREATE VIRTUAL TABLE IF NOT EXISTS memory_search USING fts5(id UNINDEXED,title,content,tags);
                CREATE TABLE IF NOT EXISTS memory_revisions(id TEXT NOT NULL,revision INTEGER NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(id,revision));
                CREATE TABLE IF NOT EXISTS memory_receipts(operation_id TEXT PRIMARY KEY,payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS memory_settings(id INTEGER PRIMARY KEY CHECK(id=1),enabled INTEGER NOT NULL);
                INSERT OR IGNORE INTO memory_settings VALUES(1,1);
                CREATE TABLE IF NOT EXISTS memory_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,type TEXT NOT NULL,timestamp TEXT NOT NULL,payload TEXT NOT NULL);
                UPDATE schema_version SET version=6;
                """;
            command.ExecuteNonQuery(); migration.Commit(); command.Transaction = null; version = 6;
        }
        if (version == 6)
        {
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "sessions-v6-" + Guid.NewGuid().ToString("N") + ".backup.db") }.ToString()))
            { backup.Open(); db.BackupDatabase(backup); }
            using var migration = db.BeginTransaction(); command.Transaction = migration;
            command.CommandText = "CREATE TABLE IF NOT EXISTS attachments(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, payload TEXT NOT NULL); UPDATE schema_version SET version=7;";
            command.ExecuteNonQuery(); migration.Commit(); command.Transaction = null; version = 7;
        }
        if (version == 7)
        {
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "sessions-v7-" + Guid.NewGuid().ToString("N") + ".backup.db") }.ToString()))
            { backup.Open(); db.BackupDatabase(backup); }
            using var migration = db.BeginTransaction(); command.Transaction = migration;
            command.CommandText = "CREATE TABLE workflow_reports(id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, payload TEXT NOT NULL); CREATE TABLE workflow_revisions(report_id TEXT NOT NULL, revision INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(report_id,revision)); UPDATE schema_version SET version=8;";
            command.ExecuteNonQuery(); migration.Commit(); command.Transaction = null; version = 8;
        }
        if (version == 8)
        {
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "sessions-v8-" + Guid.NewGuid().ToString("N") + ".backup.db") }.ToString()))
            { backup.Open(); db.BackupDatabase(backup); }
            using var migration = db.BeginTransaction(); command.Transaction = migration;
            command.CommandText = "CREATE TABLE steering(id TEXT PRIMARY KEY, root_id TEXT NOT NULL, conversation_id TEXT NOT NULL, run_id TEXT NOT NULL, sequence INTEGER NOT NULL, state TEXT NOT NULL, operation_key TEXT UNIQUE, payload TEXT NOT NULL); CREATE INDEX steering_target ON steering(conversation_id,run_id,state); UPDATE schema_version SET version=9;";
            command.ExecuteNonQuery(); migration.Commit(); command.Transaction = null; version = 9;
        }
        if (version != 9) throw new UserFacingException("Unsupported session database version.");
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON";
        command.ExecuteNonQuery();
        return db;
    }
    public IDisposable Acquire(string conversationId) => AcquireLock(root, "conversation-" + conversationId);
    public static FileStream AcquireLock(string directory, string name)
    {
        if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new UserFacingException("Invalid lock identifier.");
        Directory.CreateDirectory(Path.Combine(directory, "locks"));
        try { return new FileStream(Path.Combine(directory, "locks", name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new UserFacingException("This conversation or account is owned by another active AgentSmith process."); }
    }
    public Session? Get(string conversationId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT snapshot FROM conversations WHERE id=$id";
        command.Parameters.AddWithValue("$id", conversationId);
        var session = command.ExecuteScalar() is string value ? JsonSerializer.Deserialize<Session>(value, Json.Options) : null;
        if (session is not null)
        {
            command.CommandText = "SELECT payload FROM processes WHERE conversation_id=$id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var process = JsonSerializer.Deserialize<ProcessRecord>(reader.GetString(0), Json.Options)!;
                session.Processes.RemoveAll(p => p.Id == process.Id); session.Processes.Add(process);
            }
        }
        if (session is not null)
        {
            var number = session.Processes.Select(p => p.DisplayNumber).DefaultIfEmpty().Max();
            foreach (var process in session.Processes.Where(p => p.DisplayNumber == 0).OrderBy(p => p.Id, StringComparer.Ordinal)) process.DisplayNumber = ++number;
        }
        return session;
    }
    public IReadOnlyList<Session> List()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT snapshot FROM conversations ORDER BY updated DESC";
        using var reader = command.ExecuteReader();
        var result = new List<Session>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<Session>(reader.GetString(0), Json.Options)!);
        return result;
    }
    public void Save(Session session, AgentEvent agentEvent)
    { lock (session) SaveLocked(session, agentEvent); }
    private void SaveLocked(Session session, AgentEvent agentEvent)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        SaveInTransaction(session, agentEvent, db, tx);
        tx.Commit();
    }
    private static void SaveInTransaction(Session session, AgentEvent agentEvent, SqliteConnection db, SqliteTransaction tx)
    {
        void Execute(string sql, params (string, object?)[] args)
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql;
            foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        session.UpdatedAt = DateTimeOffset.UtcNow;
        Execute("INSERT INTO conversations VALUES($id,$snapshot,$updated) ON CONFLICT(id) DO UPDATE SET snapshot=$snapshot,updated=$updated", ("$id", session.Id), ("$snapshot", Json.Serialize(session)), ("$updated", session.UpdatedAt.ToString("O")));
        for (var i = 0; i < session.History.Count; i++)
            Execute("INSERT OR REPLACE INTO protocol_items VALUES($id,$ordinal,$payload)", ("$id", session.Id), ("$ordinal", i), ("$payload", session.History[i].ToJsonString()));
        foreach (var run in session.Runs)
        {
            Execute("INSERT OR REPLACE INTO runs VALUES($id,$conversation,$state,$payload)", ("$id", run.Id), ("$conversation", session.Id), ("$state", run.State.ToString()), ("$payload", Json.Serialize(run)));
            foreach (var call in run.Calls)
                Execute("INSERT OR REPLACE INTO invocations VALUES($run,$call,$payload)", ("$run", run.Id), ("$call", call.Id), ("$payload", Json.Serialize(call)));
            if (run.Question is { } question)
                Execute("INSERT OR REPLACE INTO questions VALUES($id,$run,$payload)", ("$id", question.Id), ("$run", run.Id), ("$payload", Json.Serialize(question)));
        }
        foreach (var mutation in session.Mutations)
            Execute("INSERT OR REPLACE INTO mutations VALUES($id,$conversation,$payload)", ("$id", mutation.Id), ("$conversation", session.Id), ("$payload", Json.Serialize(mutation)));
        foreach (var process in session.Processes)
            Execute("INSERT OR REPLACE INTO processes VALUES($id,$conversation,$payload)", ("$id", process.Id), ("$conversation", session.Id), ("$payload", Json.Serialize(process)));
        foreach (var report in session.Reports)
        {
            Execute("INSERT OR REPLACE INTO workflow_reports VALUES($id,$conversation,$payload)", ("$id", report.Id), ("$conversation", session.Id), ("$payload", Json.Serialize(report)));
            foreach (var revision in report.Revisions)
                Execute("INSERT OR IGNORE INTO workflow_revisions VALUES($id,$revision,$payload)", ("$id", report.Id), ("$revision", revision.Revision), ("$payload", Json.Serialize(revision)));
        }
        foreach (var plan in session.Plans)
            Execute("INSERT OR REPLACE INTO plans VALUES($id,$conversation,$payload)", ("$id", plan.Id), ("$conversation", session.Id), ("$payload", Json.Serialize(plan)));
        foreach (var artifact in session.Artifacts)
            Execute("INSERT OR REPLACE INTO artifacts VALUES($id,$conversation,$payload)", ("$id", artifact.Id), ("$conversation", session.Id), ("$payload", Json.Serialize(artifact)));
        foreach (var verification in session.Verifications)
            Execute("INSERT OR REPLACE INTO verifications VALUES($id,$conversation,$payload)", ("$id", verification.Id), ("$conversation", session.Id), ("$payload", Json.Serialize(verification)));
        foreach (var receipt in session.Receipts)
            Execute("INSERT OR REPLACE INTO receipts VALUES($conversation,$call,$payload)", ("$conversation", session.Id), ("$call", receipt.Key), ("$payload", Json.Serialize(receipt.Value)));
        Execute("INSERT INTO events(conversation_id,run_id,timestamp,type,payload) VALUES($conversation,$run,$timestamp,$type,$payload)", ("$conversation", session.Id), ("$run", agentEvent.RunId), ("$timestamp", session.UpdatedAt.ToString("O")), ("$type", agentEvent.Type), ("$payload", Json.Serialize(agentEvent.Payload)));
        foreach (var snapshot in session.ContextSnapshots)
            Execute("INSERT OR REPLACE INTO context_snapshots VALUES($id,$conversation,$payload)", ("$id", snapshot.Id), ("$conversation", session.Id), ("$payload", Json.Serialize(snapshot)));
    }
    public void SaveProcess(string conversationId, ProcessRecord process)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT OR REPLACE INTO processes VALUES($id,$conversation,$payload); INSERT INTO events(conversation_id,type,timestamp,payload) VALUES($conversation,'process.exited',$time,$payload);";
        command.Parameters.AddWithValue("$id", process.Id); command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$payload", Json.Serialize(process)); command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery(); tx.Commit();
    }
}
