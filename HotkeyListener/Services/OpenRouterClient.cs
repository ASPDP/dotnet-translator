using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HotkeyListener.Models;

namespace HotkeyListener.Services;

internal sealed class OpenRouterClient
{
    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly string _defaultModel;

    public OpenRouterClient(HttpClient httpClient, string? apiKey, string defaultModel)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _defaultModel = defaultModel;
    }

    public async Task<string?> RequestVariantAsync(
        string text,
        string? sourceLanguage,
        string? targetLanguage,
        CancellationToken cancellationToken,
        string? modelOverride = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            Debug.WriteLine("OpenRouter API key is missing. Place it in openrouter_api_key.txt or set the OPENROUTER_API_KEY environment variable.");
            return null;
        }

        var source = string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage;
        var target = string.IsNullOrWhiteSpace(targetLanguage) ? "auto" : targetLanguage;
        var userPrompt = $"Source language: {source}\nTarget language: {target}\n\n{text}";

        var modelToUse = string.IsNullOrWhiteSpace(modelOverride) ? _defaultModel : modelOverride;
        var payload = new
        {
            model = modelToUse,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are a translation assistant. Respond with only the translated text while preserving line breaks."
                },
                new { role = "user", content = userPrompt }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://dotnet-translator.local");
        request.Headers.TryAddWithoutValidation("X-Title", "DotNet Translator");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Debug.WriteLine($"OpenRouter request failed ({(int)response.StatusCode}): {response.ReasonPhrase}. Body: {errorBody}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var chatResponse = await JsonSerializer.DeserializeAsync<OpenRouterChatResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var choices = chatResponse?.Choices;
            if (choices == null || choices.Length == 0)
            {
                Debug.WriteLine("OpenRouter response did not contain any choices.");
                return null;
            }

            var content = choices[0].Message?.Content;
            return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenRouter request error: {ex.Message}");
            return null;
        }
    }
}
