using HotkeyListener.Services.SystemSpecificStuff.Logging;

namespace HotkeyListener.Services.Translation.Translators;

/// <summary>
/// Base class for translators that use HTTP to communicate with translation services.
/// </summary>
internal abstract class HttpTranslatorBase : ITranslator
{
    protected readonly HttpClient HttpClient;

    protected HttpTranslatorBase(HttpClient httpClient, string name)
    {
        HttpClient = httpClient;
        Name = name;
    }

    public string Name { get; }

    public async Task<string?> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var result = await TranslateInternalAsync(text, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);
            return PostProcessTranslation(result);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConsoleLog.Info($"{Name} translation request canceled.");
            return null;
        }
        catch (HttpRequestException ex)
        {
            ConsoleLog.Error($"{Name} HTTP error: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"{Name} translation error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Performs the actual translation using HTTP.
    /// </summary>
    protected abstract Task<string?> TranslateInternalAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken);

    /// <summary>
    /// Post-processes the translation result (e.g., trimming, removing special tags).
    /// Default implementation just trims whitespace.
    /// </summary>
    protected virtual string? PostProcessTranslation(string? translation)
    {
        return translation?.Trim();
    }
}
