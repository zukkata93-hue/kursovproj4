using System.Text.Json;
namespace Shared;
// One JSON object per line; requests and responses are exchanged over TLS/TCP.
public class Request { public string Action { get; set; } = ""; public JsonElement Data { get; set; } }
public class Response { public bool Ok { get; set; } public string Message { get; set; } = ""; public object? Data { get; set; } }
public static class Json { public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true }; }
