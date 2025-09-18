using HotkeyListener.Interop;
using HotkeyListener.Services;

namespace HotkeyListener;

internal sealed class HotkeyApplication : IDisposable
{
    private readonly ExternalProcessManager _externalProcesses;
    private readonly DoublePressHotkeyListener _hotkeyListener;
    private readonly TranslationWorkflow _workflow;
    private readonly HttpClient _translationHttpClient;
    private readonly HttpClient _openRouterHttpClient;
    private readonly CancellationTokenSource _cts = new();

    private HotkeyApplication(
        ExternalProcessManager externalProcesses,
        DoublePressHotkeyListener hotkeyListener,
        TranslationWorkflow workflow,
        HttpClient translationHttpClient,
        HttpClient openRouterHttpClient)
    {
        _externalProcesses = externalProcesses;
        _hotkeyListener = hotkeyListener;
        _workflow = workflow;
        _translationHttpClient = translationHttpClient;
        _openRouterHttpClient = openRouterHttpClient;
    }

    public static HotkeyApplication CreateDefault()
    {
        var keyboardHook = new KeyboardHook();
        var hotkeyListener = new DoublePressHotkeyListener(
            keyboardHook,
            TimeSpan.FromMilliseconds(500),
            Keys.LControlKey,
            Keys.RControlKey);

        var clipboard = new ClipboardService();
        var inputSimulator = new KeyboardInputSimulator();
        var selectionCapture = new SelectionCaptureService(inputSimulator, TimeSpan.FromMilliseconds(100));
        var windowerClient = new WindowerClient("DotNetTranslatorPipe");
        var languageResolver = new LanguageDirectionResolver();

        var translationHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var translationSettings = new TranslationApiSettings("google", "yandex", 3000, 3001);
        var translationClient = new TranslationApiClient(translationHttpClient, translationSettings);

        var openRouterHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var openRouterApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ??
                               "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";
        var openRouterClient = new OpenRouterClient(openRouterHttpClient, openRouterApiKey,
            "deepseek/deepseek-r1-0528-qwen3-8b:free");

        var workflow = new TranslationWorkflow(
            selectionCapture,
            clipboard,
            translationClient,
            windowerClient,
            openRouterClient,
            hotkeyListener,
            languageResolver);

        var externalProcesses = new ExternalProcessManager();

        return new HotkeyApplication(externalProcesses, hotkeyListener, workflow, translationHttpClient,
            openRouterHttpClient);
    }

    public async Task InitializeAsync()
    {
        await _externalProcesses.EnsureAsync();
        _hotkeyListener.HotkeyTriggered += OnHotkeyTriggered;
        _hotkeyListener.Start();
    }

    public void Run()
    {
        Application.Run();
    }

    private void OnHotkeyTriggered(object? sender, EventArgs e)
    {
        _ = Task.Run(() => _workflow.HandleHotkeyAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _hotkeyListener.HotkeyTriggered -= OnHotkeyTriggered;
        _workflow.Dispose();
        _hotkeyListener.Dispose();
        _externalProcesses.Dispose();
        _translationHttpClient.Dispose();
        _openRouterHttpClient.Dispose();
        _cts.Dispose();
    }
}
