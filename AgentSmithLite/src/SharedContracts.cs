using System.Text.Json;

// The two small contracts needed by the linked authentication/process sources.
namespace AgentSmith.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public sealed class UserFacingException(string message) : Exception(message);
