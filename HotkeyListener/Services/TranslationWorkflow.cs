using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
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
    private readonly object _sessionSync = new();

    private const string DeepseekChatModel = "deepseek/deepseek-chat-v3.1:free";

    private string? _lastSourceText;
    private string? _lastTranslatedText;
    private string? _lastTranslatedProvider;
    private CancellationTokenSource? _activeSessionCts;

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
        var lockHeld = false;
        CancellationTokenSource? sessionCts = null;
        var variantsStarted = false;

        try
        {
            if (!await _processingLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                CancelActiveSession();
                await _processingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lockHeld = true;

            sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            RegisterActiveSession(sessionCts);
            var sessionToken = sessionCts.Token;

            sessionToken.ThrowIfCancellationRequested();

            _windowerClient.ShowRhombus();

            using (_simulationGuard.BeginSimulationScope())
            {
                await _selectionCapture.CaptureSelectionAsync(sessionToken).ConfigureAwait(false);
            }

            var originalText = await _clipboard.GetTextAsync(sessionToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(originalText))
            {
                return;
            }

            var sessionId = Guid.NewGuid().ToString("N");

            var (from, to) = _languageResolver.Resolve(originalText);
            var reuseCached = string.Equals(originalText, _lastSourceText, StringComparison.Ordinal) &&
                              !string.IsNullOrWhiteSpace(_lastTranslatedText);

            if (reuseCached && !string.IsNullOrWhiteSpace(_lastTranslatedText))
            {
                // Use cached result immediately
                var cachedProvider = _lastTranslatedProvider ?? _translationClient.DefaultEngine;
                ConsoleLog.Info($"Using cached translation ({cachedProvider}): {_lastTranslatedText}");
                _windowerClient.ShowVariant(sessionId, cachedProvider, _lastTranslatedText!);
                await _clipboard.SetTextAsync(_lastTranslatedText!, sessionToken).ConfigureAwait(false);
                
                // Still start variants for additional options
                var (started, _) = StartVariantRequests(sessionId, originalText, from, to, sessionCts, displayDefaultVariant: false);
                variantsStarted = started;
            }
            else
            {
                // Start variants and wait for the first completed response
                var (started, variantTasks) = StartVariantRequests(sessionId, originalText, from, to, sessionCts, displayDefaultVariant: true);
                variantsStarted = started;
                
                if (started)
                {
                    var firstResult = await WaitForFirstVariantAsync(variantTasks, sessionToken).ConfigureAwait(false);
                    if (firstResult is not null)
                    {
                        _lastSourceText = originalText;
                        _lastTranslatedText = firstResult.Value.Text;
                        _lastTranslatedProvider = firstResult.Value.Name;
                        
                        await _clipboard.SetTextAsync(firstResult.Value.Text, sessionToken).ConfigureAwait(false);
                    }
                }
            }

        }
        catch (OperationCanceledException) when (sessionCts is not null && sessionCts.IsCancellationRequested)
        {
            ConsoleLog.Info("Translation session canceled (superseded by a new request).");
        }
        catch (OperationCanceledException)
        {
            ConsoleLog.Info("Translation session canceled during shutdown.");
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Hotkey workflow error: {ex}");
        }
        finally
        {
            if (!variantsStarted && sessionCts is not null)
            {
                TryResetActiveSession(sessionCts);
                sessionCts.Dispose();
            }

            if (lockHeld)
            {
                _processingLock.Release();
            }
        }
    }

    private (bool Started, Task<VariantResult?>[] VariantTasks) StartVariantRequests(
        string sessionId,
        string originalText,
        string from,
        string to,
        CancellationTokenSource sessionCts,
        bool displayDefaultVariant)
    {
        var sessionToken = sessionCts.Token;
        if (sessionToken.IsCancellationRequested)
        {
            return (false, Array.Empty<Task<VariantResult?>>());
        }

        var defaultTask = GetDefaultVariantAsync(sessionId, originalText, from, to, sessionToken);
        var deeplTask = GetDeeplVariantAsync(sessionId, originalText, from, to, sessionToken);
        var openRouterTask = GetOpenRouterVariantAsync(sessionId, originalText, from, to, sessionToken);
        var deepseekChatTask = GetDeepseekChatVariantAsync(sessionId, originalText, from, to, sessionToken);

        var variantTasks = new[] { defaultTask, deeplTask, openRouterTask, deepseekChatTask };

        void AttachVariantNotifier(Task<VariantResult?> variantTask)
        {
            variantTask.ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully || t.Result is null || sessionToken.IsCancellationRequested)
                {
                    return;
                }

                _windowerClient.ShowVariant(sessionId, t.Result.Value.Name, t.Result.Value.Text);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        if (displayDefaultVariant)
        {
            AttachVariantNotifier(defaultTask);
        }

        AttachVariantNotifier(deeplTask);
        AttachVariantNotifier(openRouterTask);
        AttachVariantNotifier(deepseekChatTask);

        var pipelineTask = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(variantTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ConsoleLog.Info("Translation session canceled before all variants completed.");
            }
            catch (Exception ex)
            {
                ConsoleLog.Error($"Variant pipeline error: {ex}");
            }
        }, CancellationToken.None);

        pipelineTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                var baseException = t.Exception?.GetBaseException();
                ConsoleLog.Error($"Variant pipeline faulted: {baseException}");
            }

            TryResetActiveSession(sessionCts);
            sessionCts.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return (true, variantTasks);
    }

    private static async Task<VariantResult?> WaitForFirstVariantAsync(Task<VariantResult?>[] variantTasks, CancellationToken sessionToken)
    {
        if (variantTasks.Length == 0)
        {
            return null;
        }

        var remaining = new List<Task<VariantResult?>>(variantTasks);
        while (remaining.Count > 0)
        {
            var completed = await Task.WhenAny(remaining).ConfigureAwait(false);
            remaining.Remove(completed);

            if (sessionToken.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                var result = await completed.ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellations propagated by individual variant tasks.
            }
        }

        return null;
    }

    private async Task<VariantResult?> GetDeeplVariantAsync(string sessionId, string text, string from, string to, CancellationToken cancellationToken)
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

            var aiResult = await _translationClient.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            if (aiResult is null)
            {
                return null;
            }

            LogTranslationEnd(sessionId, providerName, aiResult.Value.Text);
            return new VariantResult(providerName, aiResult.Value.Text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Variant \"{providerName}\" error: {ex}");
            return null;
        }
    }

    private async Task<VariantResult?> GetOpenRouterVariantAsync(string sessionId, string text, string from, string to, CancellationToken cancellationToken)
    {
        const string providerName = "x-ai/grok-4-fast:free";
        LogTranslationStart(sessionId, providerName, text);

        try
        {
            var variantText = await _openRouterClient.RequestVariantAsync(text, from, to, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(variantText))
            {
                return null;
            }

            variantText = StripDeepseekReasoning(variantText);
            if (string.IsNullOrWhiteSpace(variantText))
            {
                return null;
            }

            LogTranslationEnd(sessionId, providerName, variantText);
            return new VariantResult(providerName, variantText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Variant \"{providerName}\" error: {ex}");
            return null;
        }
    }

    private async Task<VariantResult?> GetDefaultVariantAsync(string sessionId, string text, string from, string to, CancellationToken cancellationToken)
    {
        var providerName = _translationClient.DefaultEngine;
        LogTranslationStart(sessionId, providerName, text);

        try
        {
            var request = new TranslationRequest
            {
                Text = text,
                From = from,
                To = to,
                Engine = _translationClient.DefaultEngine
            };

            var translationResult = await _translationClient.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            if (translationResult is null)
            {
                return null;
            }

            var result = translationResult.Value;
            LogTranslationEnd(sessionId, result.Provider, result.Text);
            return new VariantResult(result.Provider, result.Text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Variant \"{providerName}\" error: {ex}");
            return null;
        }
    }

    private async Task<VariantResult?> GetDeepseekChatVariantAsync(string sessionId, string text, string from, string to, CancellationToken cancellationToken)
    {
        const string providerName = "DeepSeek V3.1";
        LogTranslationStart(sessionId, providerName, text);

        try
        {
            var variantText = await _openRouterClient.RequestVariantAsync(text, from, to, cancellationToken, modelOverride: DeepseekChatModel).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(variantText))
            {
                return null;
            }

            variantText = StripDeepseekReasoning(variantText);
            if (string.IsNullOrWhiteSpace(variantText))
            {
                return null;
            }

            LogTranslationEnd(sessionId, providerName, variantText);
            return new VariantResult(providerName, variantText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Variant \"{providerName}\" error: {ex}");
            return null;
        }
    }

    private static string StripDeepseekReasoning(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(text, "<think>.*?</think>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    public void Dispose()
    {
        CancelActiveSession();

        CancellationTokenSource? cts;
        lock (_sessionSync)
        {
            cts = _activeSessionCts;
            _activeSessionCts = null;
        }

        cts?.Dispose();
        _processingLock.Dispose();
    }

    private void RegisterActiveSession(CancellationTokenSource sessionCts)
    {
        lock (_sessionSync)
        {
            _activeSessionCts = sessionCts;
        }
    }

    private bool TryResetActiveSession(CancellationTokenSource sessionCts)
    {
        lock (_sessionSync)
        {
            if (ReferenceEquals(_activeSessionCts, sessionCts))
            {
                _activeSessionCts = null;
                return true;
            }
        }

        return false;
    }

    private void CancelActiveSession()
    {
        CancellationTokenSource? sessionCts;
        lock (_sessionSync)
        {
            sessionCts = _activeSessionCts;
        }

        if (sessionCts is null)
        {
            return;
        }

        try
        {
            sessionCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void LogTranslationStart(string sessionId, string provider, string text)
    {
        ConsoleLog.Highlight($"TranslationStart session={sessionId} provider={provider} original: {text}");
    }

    private static void LogTranslationEnd(string sessionId, string provider, string text)
    {
        ConsoleLog.Success($"TranslationEnd session={sessionId} provider={provider} translated: {text}");
    }

    private readonly record struct VariantResult(string Name, string Text);
}


