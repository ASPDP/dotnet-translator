using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
            ConsoleLog.Warning(
                "OpenRouter API key is missing. Place it in openrouter_api_key.txt or set the OPENROUTER_API_KEY environment variable.");
            return null;
        }

        var source = string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage;
        var target = string.IsNullOrWhiteSpace(targetLanguage) ? "auto" : targetLanguage;
        // add error expalanation only for eng source language
        var userPrompt =
            $"You are a translation assistant. Respond with only the translated text while preserving line breaks.\n\nSource language: {source}\nTarget language: {target}\nIf english sentence contain errors add ONE --- after translation, then and add english error explanation in short compact manner on russian\n\n{text}";
        if (sourceLanguage == "ru")
            userPrompt =
                $"You are a translation assistant. Respond with only the translated text while preserving line breaks.\n\nSource language: {source}\nTarget language: {target}\n\n{text}";

        var modelToUse = string.IsNullOrWhiteSpace(modelOverride) ? _defaultModel : modelOverride;
        var payload = new
        {
            model = modelToUse,
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

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            LogHttpResponse(response, responseBody);

            if (!response.IsSuccessStatusCode)
            {
                ConsoleLog.Error(
                    $"OpenRouter request failed ({(int)response.StatusCode}): {response.ReasonPhrase}. Body: {responseBody}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(responseBody))
            {
                ConsoleLog.Warning("OpenRouter response body was empty.");
                return null;
            }

            using var document = JsonDocument.Parse(responseBody);

            if (!document.RootElement.TryGetProperty("choices", out var choicesElement) ||
                choicesElement.ValueKind != JsonValueKind.Array ||
                choicesElement.GetArrayLength() == 0)
            {
                ConsoleLog.Warning("OpenRouter response did not contain any choices.");
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

            ConsoleLog.Warning("OpenRouter response did not contain any usable content.");
            return null;
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"OpenRouter request error: {ex}");
            return null;
        }
    }

    private static void LogHttpRequest(HttpRequestMessage request, string? body)
    {
        var builder = new StringBuilder();
        builder.AppendLine("--- HTTP Request ---");
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

    private static void LogHttpResponse(HttpResponseMessage response, string? body)
    {
        var builder = new StringBuilder();
        builder.AppendLine("--- HTTP Response ---");
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
