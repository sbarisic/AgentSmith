using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
namespace AgentSmith.BuildLogger;
public sealed class DiagnosticLogger : ILogger
{
    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Minimal;
    public string? Parameters { get; set; }
    private StreamWriter? writer;
    private readonly object gate = new();
    private long bytes;
    private bool truncated;
    public void Initialize(IEventSource source)
    {
        writer = new(new FileStream(Parameters ?? throw new LoggerException("Diagnostic path missing."), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        source.BuildStarted += (_, _) => Write(new { type = "started" });
        source.ErrorRaised += (_, e) => Write(new { type = "error", code = e.Code, message = e.Message, project = e.ProjectFile, file = e.File, line = e.LineNumber, column = e.ColumnNumber });
        source.WarningRaised += (_, e) => Write(new { type = "warning", code = e.Code, message = e.Message, project = e.ProjectFile, file = e.File, line = e.LineNumber, column = e.ColumnNumber });
        source.BuildFinished += (_, e) => Write(new { type = "finished", succeeded = e.Succeeded, truncated });
    }
    private void Write(object value)
    {
        lock (gate)
        {
            var line = JsonSerializer.Serialize(value); var size = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + size > 20 * 1024 * 1024 - 128) { if (!truncated) writer!.WriteLine("{\"type\":\"truncated\"}"); truncated = true; return; }
            writer!.WriteLine(line); bytes += size;
        }
    }
    public void Shutdown() { lock (gate) { writer?.Dispose(); writer = null; } }
}
