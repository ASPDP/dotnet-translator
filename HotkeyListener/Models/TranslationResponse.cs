using System.Text.Json.Serialization;

namespace HotkeyListener.Models;

public sealed class TranslationResponse
{
    [JsonPropertyName("engine")]
    public string? Engine { get; set; }

    [JsonPropertyName("detected")]
    public string? Detected { get; set; }

    [JsonPropertyName("translated-text")]
    public string? TranslatedText { get; set; }

    [JsonPropertyName("source_language")]
    public string? SourceLanguage { get; set; }

    [JsonPropertyName("target_language")]
    public string? TargetLanguage { get; set; }
}
