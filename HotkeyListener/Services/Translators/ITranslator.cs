namespace HotkeyListener.Services.Translators;

/// <summary>
/// Represents a translation service that can translate text between languages.
/// </summary>
internal interface ITranslator
{
    /// <summary>
    /// Gets the display name of this translator (e.g., "Google", "DeepL", "Grok").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Translates text from one language to another.
    /// </summary>
    /// <param name="text">The text to translate.</param>
    /// <param name="sourceLanguage">Source language code (e.g., "en", "ru").</param>
    /// <param name="targetLanguage">Target language code (e.g., "en", "ru").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The translated text, or null if translation failed.</returns>
    Task<string?> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken);
}
