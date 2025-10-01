using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HotkeyListener.Models;
using HotkeyListener.Services.SystemSpecificStuff.Logging;

namespace HotkeyListener.Services.Translation.Translators;

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

        ConsoleLog.Info($"{Name} sending HTTP request to port {_port}");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await HttpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        ConsoleLog.Info($"{Name} received response: {response.StatusCode}");

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Handle InternalServerError with timeout information
            if (response.StatusCode == HttpStatusCode.InternalServerError)
            {
                HandleDeepLServerError(errorContent);
            }
            else
            {
                ConsoleLog.Error($"{Name} API error: {response.StatusCode} - {errorContent}");
            }

            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var translationResponse = await JsonSerializer.DeserializeAsync<TranslationResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        ConsoleLog.Info($"{Name} deserialized response: {translationResponse?.TranslatedText ?? "null"}");

        return translationResponse?.TranslatedText;
    }

    private void HandleDeepLServerError(string errorContent)
    {
        // Extract timeout information from HTML error message
        // Example: "Time limit exceeded for line 2. (15000 ms)"
        var timeoutMatch = Regex.Match(errorContent, @"Time limit exceeded.*?\((\d+)\s*ms\)", RegexOptions.IgnoreCase);

        if (timeoutMatch.Success && int.TryParse(timeoutMatch.Groups[1].Value, out var timeoutMs))
        {
            var timeoutSeconds = timeoutMs / 1000.0;
            ConsoleLog.Error($"{Name} timeout error: Translation exceeded time limit of {timeoutSeconds:F1} seconds ({timeoutMs} ms)");

            // Extract line number if present
            var lineMatch = Regex.Match(errorContent, @"line (\d+)", RegexOptions.IgnoreCase);
            if (lineMatch.Success)
            {
                ConsoleLog.Warning($"{Name} timeout occurred at line {lineMatch.Groups[1].Value}");
            }
        }
        else
        {
            // Generic 500 error without timeout info
            ConsoleLog.Error($"{Name} server error (500): {ExtractErrorMessage(errorContent)}");
        }
    }

    private static string ExtractErrorMessage(string htmlError)
    {
        // Try to extract the message from HTML: <p>Message: ...</p>
        var messageMatch = Regex.Match(htmlError, @"<p>Message:\s*(.+?)</p>", RegexOptions.IgnoreCase);
        if (messageMatch.Success)
        {
            var message = messageMatch.Groups[1].Value.Trim();
            // Truncate if too long
            return message.Length > 200 ? message[..200] + "..." : message;
        }

        // Fallback: return truncated raw HTML
        return htmlError.Length > 200 ? htmlError[..200] + "..." : htmlError;
    }
}
