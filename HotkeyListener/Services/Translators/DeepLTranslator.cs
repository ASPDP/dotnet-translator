using System.Text.Json;
using HotkeyListener.Models;

namespace HotkeyListener.Services.Translators;

/// <summary>
/// Translator that uses a custom DeepL API server.
/// </summary>
internal sealed class DeepLTranslator : HttpTranslatorBase
{
    private readonly int _port;

    public DeepLTranslator(HttpClient httpClient, int port = 3001)
        : base(httpClient, "DeepL")
    {
        _port = port;
    }

    protected override async Task<string?> TranslateInternalAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        // DeepL server uses Google engine internally but on a different port
        var engineValue = Uri.EscapeDataString("google");
        var fromValue = Uri.EscapeDataString(sourceLanguage ?? string.Empty);
        var toValue = Uri.EscapeDataString(targetLanguage ?? string.Empty);
        var textValue = Uri.EscapeDataString(text);

        var url = $"http://127.0.0.1:{_port}/api/translate?engine={engineValue}&from={fromValue}&to={toValue}&text={textValue}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await HttpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ConsoleLog.Error($"{Name} API error: {response.StatusCode} - {errorContent}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var translationResponse = await JsonSerializer.DeserializeAsync<TranslationResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return translationResponse?.TranslatedText;
    }
}
