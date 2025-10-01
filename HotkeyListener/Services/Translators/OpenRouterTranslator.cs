using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HotkeyListener.Services.Translators;

/// <summary>
/// Configuration for OpenRouter-based translation.
/// </summary>
internal sealed record OpenRouterConfig(
    string ModelId,
    string DisplayName,
    bool IncludeErrorExplanation = false,
    bool StripReasoningTags = false
);

/// <summary>
/// Translator that uses OpenRouter API to access various AI models for translation.
/// Supports models like Grok, DeepSeek, etc.
/// </summary>
internal sealed class OpenRouterTranslator : HttpTranslatorBase
{
    private readonly string? _apiKey;
    private readonly OpenRouterConfig _config;

    public OpenRouterTranslator(HttpClient httpClient, string? apiKey, OpenRouterConfig config)
        : base(httpClient, config.DisplayName)
    {
        _apiKey = apiKey;
        _config = config;
    }

    protected override async Task<string?> TranslateInternalAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            ConsoleLog.Warning($"{Name}: OpenRouter API key is missing. Place it in openrouter_api_key.txt or set the OPENROUTER_API_KEY environment variable.");
            return null;
        }

        var userPrompt = BuildPrompt(text, sourceLanguage, targetLanguage);

        var payload = new
        {
            model = _config.ModelId,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = userPrompt
                }
            }
        };

        var requestBody = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://dotnet-translator.local");
        request.Headers.TryAddWithoutValidation("X-Title", "DotNet Translator");

        LogHttpRequest(request, requestBody);

        using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        LogHttpResponse(response, responseBody);

        if (!response.IsSuccessStatusCode)
        {
            ConsoleLog.Error($"{Name} request failed ({(int)response.StatusCode}): {response.ReasonPhrase}. Body: {responseBody}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            ConsoleLog.Warning($"{Name} response body was empty.");
            return null;
        }

        return ExtractContentFromResponse(responseBody);
    }

    protected override string? PostProcessTranslation(string? translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
        {
            return translation;
        }

        var result = translation;

        // Strip reasoning tags if configured (e.g., DeepSeek's <think>...</think>)
        if (_config.StripReasoningTags)
        {
            result = Regex.Replace(result, "<think>.*?</think>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }

        return result?.Trim();
    }

    private string BuildPrompt(string text, string sourceLanguage, string targetLanguage)
    {
        var source = string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage;
        var target = string.IsNullOrWhiteSpace(targetLanguage) ? "auto" : targetLanguage;

        var basePrompt = $"You are a translation assistant. Respond with only the translated text while preserving line breaks.\n\nSource language: {source}\nTarget language: {target}";

        // Add error explanation only for English source language if configured
        if (_config.IncludeErrorExplanation && sourceLanguage == "en")
        {
            basePrompt += "\nIf english sentence contain errors add ONE --- after translation, then and add english error explanation in short compact manner on russian";
        }

        return $"{basePrompt}\n\n{text}";
    }

    private string? ExtractContentFromResponse(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);

        if (!document.RootElement.TryGetProperty("choices", out var choicesElement) ||
            choicesElement.ValueKind != JsonValueKind.Array ||
            choicesElement.GetArrayLength() == 0)
        {
            ConsoleLog.Warning($"{Name} response did not contain any choices.");
            return null;
        }

        foreach (var choiceElement in choicesElement.EnumerateArray())
        {
            var content = ExtractChoiceContent(choiceElement);
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content.Trim();
            }
        }

        ConsoleLog.Warning($"{Name} response did not contain any usable content.");
        return null;
    }

    private void LogHttpRequest(HttpRequestMessage request, string? body)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"--- {Name} HTTP Request ---");
        builder.AppendLine($"{request.Method} {request.RequestUri}");
        AppendHeaders(builder, request.Headers);

        if (request.Content is not null)
        {
            AppendHeaders(builder, request.Content.Headers);
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            builder.AppendLine("Body:");
            builder.AppendLine(body);
        }
        else
        {
            builder.AppendLine("Body: <empty>");
        }

        ConsoleLog.Info(builder.ToString().TrimEnd());
    }

    private void LogHttpResponse(HttpResponseMessage response, string? body)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"--- {Name} HTTP Response ---");
        builder.AppendLine($"{(int)response.StatusCode} {response.StatusCode}");
        AppendHeaders(builder, response.Headers);

        if (response.Content is not null)
        {
            AppendHeaders(builder, response.Content.Headers);
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            builder.AppendLine("Body:");
            builder.AppendLine(body);
        }
        else
        {
            builder.AppendLine("Body: <empty>");
        }

        ConsoleLog.Info(builder.ToString().TrimEnd());
    }

    private static void AppendHeaders(StringBuilder builder, HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            var value = string.Join(", ", header.Value);
            builder.AppendLine($"{header.Key}: {value}");
        }
    }

    private static string? ExtractChoiceContent(JsonElement choiceElement)
    {
        if (choiceElement.ValueKind == JsonValueKind.Object)
        {
            if (choiceElement.TryGetProperty("message", out var messageElement))
            {
                var messageContent = ExtractContent(messageElement);
                if (!string.IsNullOrWhiteSpace(messageContent))
                {
                    return messageContent;
                }
            }

            if (choiceElement.TryGetProperty("content", out var choiceContentElement))
            {
                var choiceContent = ExtractContent(choiceContentElement);
                if (!string.IsNullOrWhiteSpace(choiceContent))
                {
                    return choiceContent;
                }
            }
        }
        else if (choiceElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var nested in choiceElement.EnumerateArray())
            {
                var nestedContent = ExtractChoiceContent(nested);
                if (!string.IsNullOrWhiteSpace(nestedContent))
                {
                    return nestedContent;
                }
            }
        }

        return null;
    }

    private static string? ExtractContent(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object => ExtractObjectContent(element),
            JsonValueKind.Array => ExtractArrayContent(element),
            _ => null
        };
    }

    private static string? ExtractObjectContent(JsonElement element)
    {
        if (element.TryGetProperty("content", out var nestedContent))
        {
            var nested = ExtractContent(nestedContent);
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        if (element.TryGetProperty("text", out var textElement))
        {
            var text = textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString()
                : ExtractContent(textElement);

            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        if (element.TryGetProperty("message", out var messageElement))
        {
            var messageContent = ExtractContent(messageElement);
            if (!string.IsNullOrWhiteSpace(messageContent))
            {
                return messageContent;
            }
        }

        return null;
    }

    private static string? ExtractArrayContent(JsonElement arrayElement)
    {
        var outputSegments = new List<string>();
        var fallbackSegments = new List<string>();

        foreach (var item in arrayElement.EnumerateArray())
        {
            var (segmentText, isOutput) = ExtractSegment(item);
            if (string.IsNullOrWhiteSpace(segmentText))
            {
                continue;
            }

            if (isOutput)
            {
                outputSegments.Add(segmentText);
            }
            else
            {
                fallbackSegments.Add(segmentText);
            }
        }

        var selected = outputSegments.Count > 0 ? outputSegments : fallbackSegments;
        return selected.Count > 0 ? string.Join(Environment.NewLine, selected) : null;
    }

    private static (string? Text, bool IsOutputText) ExtractSegment(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return (element.GetString(), false);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var type = element.TryGetProperty("type", out var typeProp)
                ? typeProp.GetString()
                : null;

            string? text = null;

            if (element.TryGetProperty("text", out var textProp))
            {
                text = textProp.ValueKind == JsonValueKind.String
                    ? textProp.GetString()
                    : ExtractContent(textProp);
            }
            else if (element.TryGetProperty("content", out var nestedContent))
            {
                text = ExtractContent(nestedContent);
            }

            var isReasoning = string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(type, "thought", StringComparison.OrdinalIgnoreCase);
            if (isReasoning)
            {
                return (null, false);
            }

            var isOutputText = string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(type, "message", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase);

            return (text, isOutputText);
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return (ExtractArrayContent(element), false);
        }

        return (null, false);
    }
}
