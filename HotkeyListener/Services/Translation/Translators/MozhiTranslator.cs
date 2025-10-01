using System.Text.Json;
using HotkeyListener.Models;
using HotkeyListener.Services.SystemSpecificStuff.Logging;

namespace HotkeyListener.Services.Translation.Translators;

/// <summary>
/// Translator that uses Mozhi translation API (supports Google, Yandex, etc.).
/// </summary>
internal sealed class MozhiTranslator : HttpTranslatorBase
{
    private readonly string _engine;
    private readonly int _port;

    public MozhiTranslator(HttpClient httpClient, string engine, int port = 3000)
        : base(httpClient, engine)
    {
        _engine = engine;
        _port = port;
    }

    protected override async Task<string?> TranslateInternalAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        var engineValue = Uri.EscapeDataString(_engine);
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
