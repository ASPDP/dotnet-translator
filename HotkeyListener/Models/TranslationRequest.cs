namespace HotkeyListener.Models;

public sealed class TranslationRequest
{
    public string? Engine { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Text { get; set; }
}
