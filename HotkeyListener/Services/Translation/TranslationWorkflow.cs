using HotkeyListener.Services.SystemSpecificStuff.ClipboardManagement;
using HotkeyListener.Services.SystemSpecificStuff.InterProcessCommunication;
using HotkeyListener.Services.SystemSpecificStuff.Keyboard;
using HotkeyListener.Services.SystemSpecificStuff.Logging;
using HotkeyListener.Services.Translation.Translators;

namespace HotkeyListener.Services.Translation;

internal sealed class TranslationWorkflow : IDisposable
{
    private readonly SelectionCaptureService _selectionCapture;
    private readonly ClipboardService _clipboard;
    private readonly IReadOnlyList<ITranslator> _primaryTranslators;
    private readonly IReadOnlyList<ITranslator> _variantTranslators;
    private readonly WindowerClient _windowerClient;
    private readonly IHotkeySimulationGuard _simulationGuard;
    private readonly LanguageDirectionResolver _languageResolver;
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private readonly object _sessionSync = new();

    private string? _lastSourceText;
    private string? _lastTranslatedText;
    private string? _lastTranslatedProvider;
    private CancellationTokenSource? _activeSessionCts;

    public TranslationWorkflow(
        SelectionCaptureService selectionCapture,
        ClipboardService clipboard,
        IReadOnlyList<ITranslator> primaryTranslators,
        IReadOnlyList<ITranslator> variantTranslators,
        WindowerClient windowerClient,
        IHotkeySimulationGuard simulationGuard,
        LanguageDirectionResolver languageResolver)
    {
        _selectionCapture = selectionCapture;
        _clipboard = clipboard;
        _primaryTranslators = primaryTranslators;
        _variantTranslators = variantTranslators;
        _windowerClient = windowerClient;
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
                var cachedProvider = _lastTranslatedProvider ?? (_primaryTranslators.Count > 0 ? _primaryTranslators[0].Name : "cached");
                ConsoleLog.Info($"Using cached translation ({cachedProvider}): {_lastTranslatedText}");
                _windowerClient.ShowVariant(sessionId, cachedProvider, _lastTranslatedText!);
                await _clipboard.SetTextAsync(_lastTranslatedText!, sessionToken).ConfigureAwait(false);

                // Still start all translators for additional options (but don't wait for primary)
                var (started, _, _) = StartVariantRequests(sessionId, originalText, from, to, sessionCts, displayPrimaryTranslators: false);
                variantsStarted = started;
            }
            else
            {
                // Start all translators and wait for the first primary translator to complete
                var (started, primaryTasks, variantTasks) = StartVariantRequests(sessionId, originalText, from, to, sessionCts, displayPrimaryTranslators: true);
                variantsStarted = started;

                if (started && primaryTasks.Length > 0)
                {
                    // Wait for the first PRIMARY translator (Google/Yandex/DeepL) to complete
                    var firstResult = await WaitForFirstVariantAsync(primaryTasks, sessionToken).ConfigureAwait(false);
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

    private (bool Started, Task<VariantResult?>[] PrimaryTasks, Task<VariantResult?>[] VariantTasks) StartVariantRequests(
        string sessionId,
        string originalText,
        string from,
        string to,
        CancellationTokenSource sessionCts,
        bool displayPrimaryTranslators)
    {
        var sessionToken = sessionCts.Token;
        if (sessionToken.IsCancellationRequested)
        {
            return (false, Array.Empty<Task<VariantResult?>>(), Array.Empty<Task<VariantResult?>>());
        }

        // Create tasks for all primary translators (Google, Yandex, DeepL)
        var primaryTasks = new List<Task<VariantResult?>>();
        foreach (var translator in _primaryTranslators)
        {
            var task = TranslateAsync(translator, sessionId, originalText, from, to, sessionToken);
            primaryTasks.Add(task);
        }

        // Create tasks for variant translators (AI models)
        var variantTasks = new List<Task<VariantResult?>>();
        foreach (var translator in _variantTranslators)
        {
            var task = TranslateAsync(translator, sessionId, originalText, from, to, sessionToken);
            variantTasks.Add(task);
        }

        var primaryTasksArray = primaryTasks.ToArray();
        var variantTasksArray = variantTasks.ToArray();
        var allTasks = primaryTasks.Concat(variantTasks).ToArray();

        // Attach notifiers to display results as they complete
        void AttachVariantNotifier(Task<VariantResult?> variantTask, bool shouldDisplay)
        {
            if (!shouldDisplay)
            {
                return;
            }

            variantTask.ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully || t.Result is null || sessionToken.IsCancellationRequested)
                {
                    return;
                }

                _windowerClient.ShowVariant(sessionId, t.Result.Value.Name, t.Result.Value.Text);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // Display primary translators if requested
        foreach (var task in primaryTasks)
        {
            AttachVariantNotifier(task, displayPrimaryTranslators);
        }

        // Always display all variant translators
        foreach (var task in variantTasks)
        {
            AttachVariantNotifier(task, true);
        }

        // Wait for all translators (or cancellation) - they all run independently
        var pipelineTask = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(allTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ConsoleLog.Info("Translation session canceled before all translators completed.");
            }
            catch (Exception ex)
            {
                ConsoleLog.Error($"Translation pipeline error: {ex}");
            }
        }, CancellationToken.None);

        pipelineTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                var baseException = t.Exception?.GetBaseException();
                ConsoleLog.Error($"Translation pipeline faulted: {baseException}");
            }

            TryResetActiveSession(sessionCts);
            sessionCts.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return (true, primaryTasksArray, variantTasksArray);
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

    private async Task<VariantResult?> TranslateAsync(
        ITranslator translator,
        string sessionId,
        string text,
        string from,
        string to,
        CancellationToken cancellationToken)
    {
        LogTranslationStart(sessionId, translator.Name, text);

        try
        {
            var translatedText = await translator.TranslateAsync(text, from, to, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(translatedText))
            {
                return null;
            }

            LogTranslationEnd(sessionId, translator.Name, translatedText);
            return new VariantResult(translator.Name, translatedText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Variant \"{translator.Name}\" error: {ex.Message}");
            return null;
        }
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
