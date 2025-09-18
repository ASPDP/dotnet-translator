using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HotkeyListener.Models;

namespace HotkeyListener.Services;

internal sealed class TranslationWorkflow : IDisposable
{
    private readonly SelectionCaptureService _selectionCapture;
    private readonly ClipboardService _clipboard;
    private readonly TranslationApiClient _translationClient;
    private readonly WindowerClient _windowerClient;
    private readonly OpenRouterClient _openRouterClient;
    private readonly IHotkeySimulationGuard _simulationGuard;
    private readonly LanguageDirectionResolver _languageResolver;
    private readonly SemaphoreSlim _processingLock = new(1, 1);

    private const string DeepseekChatModel = "deepseek/deepseek-chat-v3.1:free";

    private string? _lastSourceText;
    private string? _lastTranslatedText;
    private string? _lastTranslatedProvider;

    public TranslationWorkflow(
        SelectionCaptureService selectionCapture,
        ClipboardService clipboard,
        TranslationApiClient translationClient,
        WindowerClient windowerClient,
        OpenRouterClient openRouterClient,
        IHotkeySimulationGuard simulationGuard,
        LanguageDirectionResolver languageResolver)
    {
        _selectionCapture = selectionCapture;
        _clipboard = clipboard;
        _translationClient = translationClient;
        _windowerClient = windowerClient;
        _openRouterClient = openRouterClient;
        _simulationGuard = simulationGuard;
        _languageResolver = languageResolver;
    }

    public async Task HandleHotkeyAsync(CancellationToken cancellationToken)
    {
        if (!await _processingLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            _windowerClient.ShowRhombus();

            using (_simulationGuard.BeginSimulationScope())
            {
                await _selectionCapture.CaptureSelectionAsync(cancellationToken).ConfigureAwait(false);
            }

            var originalText = await _clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(originalText))
            {
                return;
            }

            var sessionId = Guid.NewGuid().ToString("N");

            var (from, to) = _languageResolver.Resolve(originalText);
            var reuseCached = string.Equals(originalText, _lastSourceText, StringComparison.Ordinal) &&
                              !string.IsNullOrWhiteSpace(_lastTranslatedText);

            TranslationResult? translationResult = null;

            var primaryProvider = reuseCached && !string.IsNullOrWhiteSpace(_lastTranslatedProvider)
                ? _lastTranslatedProvider!
                : _translationClient.DefaultEngine;

            LogTranslationStart(sessionId, primaryProvider, originalText);

            if (reuseCached && !string.IsNullOrWhiteSpace(_lastTranslatedText))
            {
                translationResult = new TranslationResult(_lastTranslatedText!, primaryProvider);
            }

            if (translationResult is null)
            {
                var request = new TranslationRequest
                {
                    Text = originalText,
                    From = from,
                    To = to,
                    Engine = _translationClient.DefaultEngine
                };

                translationResult = await _translationClient.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
                if (translationResult is null)
                {
                    return;
                }

                _lastSourceText = originalText;
                _lastTranslatedText = translationResult.Value.Text;
                _lastTranslatedProvider = translationResult.Value.Provider;
            }

            var result = translationResult.Value;

            LogTranslationEnd(sessionId, result.Provider, result.Text);
            Debug.WriteLine($"Translated text ({result.Provider}): {result.Text}");
            _windowerClient.SendTranslation(sessionId, result.Text, result.Provider);
            await _clipboard.SetTextAsync(result.Text, cancellationToken).ConfigureAwait(false);

            var deeplTask = GetDeeplVariantAsync(sessionId, originalText, from, to);
            var openRouterTask = GetOpenRouterVariantAsync(sessionId, originalText, from, to);
            var deepseekChatTask = GetDeepseekChatVariantAsync(sessionId, originalText, from, to);

            _ = Task.Run(async () =>
            {
                var results = await Task.WhenAll(deeplTask, openRouterTask, deepseekChatTask).ConfigureAwait(false);
                foreach (var variant in results)
                {
                    if (variant is null)
                    {
                        continue;
                    }

                    _windowerClient.ShowVariant(sessionId, variant.Value.Name, variant.Value.Text);
                }
            }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation triggered during shutdown.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Hotkey workflow error: {ex.Message}");
        }
        finally
        {
            _processingLock.Release();
        }
    }

    private async Task<VariantResult?> GetDeeplVariantAsync(string sessionId, string text, string from, string to)
    {
        const string providerName = "DeepL";
        LogTranslationStart(sessionId, providerName, text);

        try
        {
            var request = new TranslationRequest
            {
                Text = text,
                From = from,
                To = to,
                Engine = "deepl"
            };

            var aiResult = await _translationClient.TranslateAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (aiResult is null)
            {
                return null;
            }

            LogTranslationEnd(sessionId, providerName, aiResult.Value.Text);
            return new VariantResult(providerName, aiResult.Value.Text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Variant \"{providerName}\" error: {ex.Message}");
            return null;
        }
    }

    private async Task<VariantResult?> GetOpenRouterVariantAsync(string sessionId, string text, string from, string to)
    {
        const string providerName = "OpenRouter";
        LogTranslationStart(sessionId, providerName, text);

        try
        {
            var variantText = await _openRouterClient.RequestVariantAsync(text, from, to, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(variantText))
            {
                return null;
            }

            LogTranslationEnd(sessionId, providerName, variantText);
            return new VariantResult(providerName, variantText);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Variant \"{providerName}\" error: {ex.Message}");
            return null;
        }
    }

    private async Task<VariantResult?> GetDeepseekChatVariantAsync(string sessionId, string text, string from, string to)
    {
        const string providerName = "DeepSeek Chat";
        LogTranslationStart(sessionId, providerName, text);

        try
        {
            var variantText = await _openRouterClient.RequestVariantAsync(text, from, to, CancellationToken.None, modelOverride: DeepseekChatModel).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(variantText))
            {
                return null;
            }

            LogTranslationEnd(sessionId, providerName, variantText);
            return new VariantResult(providerName, variantText);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Variant \"{providerName}\" error: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _processingLock.Dispose();
    }

    private static void LogTranslationStart(string sessionId, string provider, string text)
    {
        Console.WriteLine($"TranslationStart session={sessionId} provider={provider} original: {text}");
    }

    private static void LogTranslationEnd(string sessionId, string provider, string text)
    {
        Console.WriteLine($"TranslationEnd session={sessionId} provider={provider} translated: {text}");
    }

    private readonly record struct VariantResult(string Name, string Text);
}

