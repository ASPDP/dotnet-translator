using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using HotkeyListener.Models;

namespace HotkeyListener.Services;

internal sealed record TranslationApiSettings(string DefaultEngine, string FallbackEngine, int DefaultPort, int DeepLPort);

internal readonly record struct TranslationResult(string Text, string Provider);

internal sealed class TranslationApiClient
{
    private readonly HttpClient _httpClient;
    private readonly TranslationApiSettings _settings;

    public TranslationApiClient(HttpClient httpClient, TranslationApiSettings settings)
    {
        _httpClient = httpClient;
        _settings = settings;
    }

    public string DefaultEngine => _settings.DefaultEngine;
    public string FallbackEngine => _settings.FallbackEngine;

    public async Task<TranslationResult?> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken, bool allowFallback = true)
    {
        var requestedEngine = string.IsNullOrWhiteSpace(request.Engine)
            ? _settings.DefaultEngine
            : request.Engine!;
        request.Engine = requestedEngine;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var requestTasks = new List<(Task<TranslationResponse?> Task, string Engine)>
        {
            (SendRequestAsync(request, requestedEngine, linkedCts.Token), requestedEngine)
        };

        if (allowFallback && !string.Equals(requestedEngine, _settings.FallbackEngine, StringComparison.OrdinalIgnoreCase))
        {
            var fallbackRequest = new TranslationRequest
            {
                Text = request.Text,
                From = request.From,
                To = request.To,
                Engine = _settings.FallbackEngine
            };

            requestTasks.Add((SendRequestAsync(fallbackRequest, _settings.FallbackEngine, linkedCts.Token), _settings.FallbackEngine));
        }

        while (requestTasks.Count > 0)
        {
            var tasksSnapshot = new Task<TranslationResponse?>[requestTasks.Count];
            for (var i = 0; i < requestTasks.Count; i++)
            {
                tasksSnapshot[i] = requestTasks[i].Task;
            }

            var completedTask = await Task.WhenAny(tasksSnapshot).ConfigureAwait(false);

            var completedIndex = -1;
            for (var i = 0; i < requestTasks.Count; i++)
            {
                if (ReferenceEquals(requestTasks[i].Task, completedTask))
                {
                    completedIndex = i;
                    break;
                }
            }

            if (completedIndex < 0)
            {
                continue;
            }

            var (task, engineName) = requestTasks[completedIndex];
            requestTasks.RemoveAt(completedIndex);

            var response = await task.ConfigureAwait(false);
            var result = CreateResult(response, engineName);
            if (result is not null)
            {
                if (!string.Equals(engineName, requestedEngine, StringComparison.OrdinalIgnoreCase))
                {
                    ConsoleLog.Warning("Returning response from fallback engine.");
                }

                linkedCts.Cancel();
                return result;
            }
        }

        return null;
    }

    private static TranslationResult? CreateResult(TranslationResponse? response, string requestedEngine)
    {
        if (response?.TranslatedText is string text && !string.IsNullOrWhiteSpace(text))
        {
            var provider = string.IsNullOrWhiteSpace(response.Engine) ? requestedEngine : response.Engine!;
            return new TranslationResult(text, provider);
        }

        return null;
    }

    private async Task<TranslationResponse?> SendRequestAsync(TranslationRequest request, string engine, CancellationToken cancellationToken)
    {
        var (port, effectiveEngine) = ResolveEngine(engine);
        var engineValue = Uri.EscapeDataString(effectiveEngine ?? string.Empty);
        var fromValue = Uri.EscapeDataString(request.From ?? string.Empty);
        var toValue = Uri.EscapeDataString(request.To ?? string.Empty);
        var textValue = Uri.EscapeDataString(request.Text ?? string.Empty);

        string url = $"http://127.0.0.1:{port}/api/translate?engine={engineValue}&from={fromValue}&to={toValue}&text={textValue}";

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<TranslationResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ConsoleLog.Error($"Error calling translation API with engine {engine}: {response.StatusCode} - {errorContent}");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConsoleLog.Info($"Translation API request canceled for engine {engine}.");
        }
        catch (HttpRequestException ex)
        {
            ConsoleLog.Error($"Error calling translation API with engine {engine}: {ex.Message}");
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Unexpected error calling translation API with engine {engine}: {ex.Message}");
        }

        return null;
    }

    private (int Port, string Engine) ResolveEngine(string engine)
    {
        if (string.Equals(engine, "deepl", StringComparison.OrdinalIgnoreCase))
        {
            return (_settings.DeepLPort, "google");
        }

        return (_settings.DefaultPort, engine);
    }
}

