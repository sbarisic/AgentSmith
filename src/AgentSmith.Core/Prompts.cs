using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentSmith.Core;

public sealed record PromptPart(string Kind, string Value);
public sealed class UserPrompt
{
    public List<PromptPart> Parts { get; set; } = [];
    public static UserPrompt Text(string text) => new() { Parts = [new("text", text)] };
    public string PlainText => string.Concat(Parts.Where(p => p.Kind == "text").Select(p => p.Value));
    public void Validate()
    {
        if (Parts.Any(p => p.Kind is not ("text" or "image")) || Encoding.UTF8.GetByteCount(PlainText) > 32768 || Parts.Count(p => p.Kind == "image") > 4)
            throw new UserFacingException("A prompt supports 32 KiB of text and four images.");
        if (string.IsNullOrWhiteSpace(PlainText) && Parts.All(p => p.Kind != "image")) throw new UserFacingException("A nonempty prompt is required.");
    }
    public JsonObject HistoryItem()
    {
        Validate();
        return new() { ["role"] = "user", ["content"] = new JsonArray(Parts.Select(p => (JsonNode)(p.Kind == "text"
            ? new JsonObject { ["type"] = "input_text", ["text"] = p.Value }
            : new JsonObject { ["type"] = "input_image", ["attachment_id"] = p.Value })).ToArray()) };
    }
}
public sealed class AttachmentRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConversationId { get; set; } = "";
    public string? SourceConversationId { get; set; }
    public string? SourceAttachmentId { get; set; }
    public string? SourcePath { get; set; }
    public string? RequestedSourceUrl { get; set; }
    public string? SourceUrl { get; set; }
    public DateTimeOffset? RetrievedAt { get; set; }
    public string? SourceHash { get; set; }
    public string? SourceRunId { get; set; }
    public string? SourceToolCallId { get; set; }
    public string Path { get; set; } = "";
    public string Hash { get; set; } = "";
    public string MimeType { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Bytes { get; set; }
}
public sealed record ArtifactGrant(string SourceConversationId, string ArtifactId);
public static class AttachmentFiles
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public static AttachmentRecord Import(ISessionStore store, string conversationId, byte[] png, int width, int height, string? sourceConversationId = null, string? sourceAttachmentId = null, string? sourcePath = null, string? sourceHash = null, string? sourceRunId = null, string? sourceToolCallId = null, string? requestedSourceUrl = null, string? sourceUrl = null, DateTimeOffset? retrievedAt = null)
    {
        if (png.Length > MaxBytes || png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) != width || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) != height || width <= 0 || height <= 0 || (long)width * height > 32000000)
            throw new UserFacingException("Image must be valid PNG, at most 10 MiB and 32 megapixels.");
        var item = new AttachmentRecord { ConversationId = conversationId, SourcePath = sourcePath, RequestedSourceUrl = requestedSourceUrl, SourceUrl = sourceUrl, RetrievedAt = retrievedAt, SourceHash = sourceHash, SourceRunId = sourceRunId, SourceToolCallId = sourceToolCallId, SourceConversationId = sourceConversationId, SourceAttachmentId = sourceAttachmentId, Width = width, Height = height, Bytes = png.Length, Hash = Convert.ToHexString(SHA256.HashData(png)) };
        item.Path = Path.Combine(store.DataDirectory, "attachments", conversationId, item.Id + ".png");
        ValidatePath(store, item); Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
        using (var file = new FileStream(item.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(png); file.Flush(true); }
        store.SaveAttachment(item); return item;
    }
    public static void ValidatePath(ISessionStore store, AttachmentRecord item)
    {
        if (!Guid.TryParseExact(item.ConversationId, "N", out _) || !Guid.TryParseExact(item.Id, "N", out _) || item.MimeType != "image/png") throw new UserFacingException("Invalid attachment record.");
        var expected = Path.GetFullPath(Path.Combine(store.DataDirectory, "attachments", item.ConversationId, item.Id + ".png"));
        if (!Path.GetFullPath(item.Path).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new UserFacingException("Attachment path is outside its registered location.");
        for (FileSystemInfo? entry = new FileInfo(expected); entry is not null; entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent)
        {
            try { if ((File.GetAttributes(entry.FullName) & FileAttributes.ReparsePoint) != 0) throw new UserFacingException("Attachment links are unsupported."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    public static byte[] Read(ISessionStore store, AttachmentRecord item)
    {
        ValidatePath(store, item);
        using var file = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != item.Bytes || file.Length > MaxBytes) throw new UserFacingException("Attachment is missing or changed.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != item.Hash) throw new UserFacingException("Attachment hash does not match.");
        return bytes;
    }
    public static HashSet<string> SubmittedIds(Session session) => session.History
        .Where(i => i["role"]?.ToString() == "user" && i["content"] is JsonArray)
        .SelectMany(i => i["content"]!.AsArray().OfType<JsonObject>())
        .Where(p => p["type"]?.ToString() == "input_image" && p["attachment_id"] is not null)
        .Select(p => p["attachment_id"]!.ToString()).Concat(session.Runs.SelectMany(r => r.Calls).Where(c => c.State == CallState.Completed).SelectMany(c => c.ImageAttachmentIds)).ToHashSet(StringComparer.Ordinal);
    public static ModelRequest Resolve(ISessionStore store, Session session, ModelRequest request)
    {
        var input = request.Input.Select(i => (JsonObject)i.DeepClone()).ToArray();
        foreach (var item in input)
            if (item["content"] is JsonArray parts)
                foreach (var part in parts.OfType<JsonObject>().Where(p => p["type"]?.ToString() == "input_image"))
                {
                    if (!session.Connection.Capabilities.Vision) throw new UserFacingException("This model has no verified image-input capability. The image was not sent.");
                    var id = part["attachment_id"]?.ToString() ?? throw new UserFacingException("Unregistered image input.");
                    var attachment = store.Attachments(session.Id).SingleOrDefault(a => a.Id == id) ?? session.Attachments.SingleOrDefault(a => a.Id == id)
                        ?? throw new UserFacingException("Attachment is not owned by this conversation.");
                    part.Remove("attachment_id"); part["image_url"] = "data:image/png;base64," + Convert.ToBase64String(Read(store, attachment));
                }
        return request with { Input = input };
    }
}
