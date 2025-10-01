using System.IO;
using HotkeyListener.Services.SystemSpecificStuff.ClipboardManagement;
using HotkeyListener.Services.SystemSpecificStuff.Interop;
using HotkeyListener.Services.SystemSpecificStuff.InterProcessCommunication;
using HotkeyListener.Services.SystemSpecificStuff.Keyboard;
using HotkeyListener.Services.SystemSpecificStuff.ProcessManagement;
using HotkeyListener.Services.Translation;
using HotkeyListener.Services.Translation.Translators;

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

        // HTTP clients
        var translationHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var openRouterHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var openRouterApiKey = TranslatorsConfigLoader.LoadOpenRouterApiKey();

        // Create translators
        // Traditional translators (fast, reliable) - all treated as primary candidates
        var primaryTranslators = new List<ITranslator>
        {
            new MozhiTranslator(translationHttpClient, "google", port: 3000),
            new MozhiTranslator(translationHttpClient, "yandex", port: 3000),
            new DeepLTranslator(translationHttpClient, port: 3001)
        };

        // AI translators (slower, but provide additional context) - always run as variants
        // Load from config file (translators_config.json)
        var openRouterConfigs = TranslatorsConfigLoader.LoadOpenRouterConfigs();
        var aiTranslators = openRouterConfigs
            .Select(config => new OpenRouterTranslator(openRouterHttpClient, openRouterApiKey, config))
            .Cast<ITranslator>()
            .ToList();

        var workflow = new TranslationWorkflow(
            selectionCapture,
            clipboard,
            primaryTranslators,
            aiTranslators,
            windowerClient,
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
