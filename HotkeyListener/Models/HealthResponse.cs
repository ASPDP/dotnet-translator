using System.Text.Json.Serialization;

namespace HotkeyListener.Models;

public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
