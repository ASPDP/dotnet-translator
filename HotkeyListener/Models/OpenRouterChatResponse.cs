using System.Text.Json.Serialization;

namespace HotkeyListener.Models;

public sealed class OpenRouterChatResponse
{
    [JsonPropertyName("choices")]
    public OpenRouterChoice[]? Choices { get; set; }
}

public sealed class OpenRouterChoice
{
    [JsonPropertyName("message")]
    public OpenRouterMessage? Message { get; set; }
}

public sealed class OpenRouterMessage
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}
