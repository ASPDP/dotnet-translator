using System.IO;
using HotkeyListener.Interop;
using HotkeyListener.Services;
using HotkeyListener.Services.Translators;

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
        var translationHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var openRouterHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var openRouterApiKey = LoadOpenRouterApiKey();

        // Create translators
        // Traditional translators (fast, reliable) - all treated as primary candidates
        var primaryTranslators = new List<ITranslator>
        {
            new MozhiTranslator(translationHttpClient, "google", port: 3000),
            new MozhiTranslator(translationHttpClient, "yandex", port: 3000),
            new DeepLTranslator(translationHttpClient, port: 3001)
        };

        // AI translators (slower, but provide additional context) - always run as variants
        var aiTranslators = new List<ITranslator>
        {
            new OpenRouterTranslator(
                openRouterHttpClient,
                openRouterApiKey,
                new OpenRouterConfig(
                    ModelId: "x-ai/grok-4-fast:free",
                    DisplayName: "Grok",
                    IncludeErrorExplanation: true,
                    StripReasoningTags: false)),
            new OpenRouterTranslator(
                openRouterHttpClient,
                openRouterApiKey,
                new OpenRouterConfig(
                    ModelId: "deepseek/deepseek-chat-v3.1:free",
                    DisplayName: "DeepSeek",
                    IncludeErrorExplanation: false,
                    StripReasoningTags: true))
        };

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

    private static string? LoadOpenRouterApiKey()
    {
        const string keyFileName = "openrouter_api_key.txt";

        var apiKeyFromFile = TryReadApiKeyFromFile(keyFileName);
        if (!string.IsNullOrWhiteSpace(apiKeyFromFile))
        {
            return apiKeyFromFile;
        }

        return Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
    }

    private static string? TryReadApiKeyFromFile(string keyFileName)
    {
        var directory = AppContext.BaseDirectory;

        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(directory, keyFileName);
            if (File.Exists(candidate))
            {
                try
                {
                    foreach (var line in File.ReadLines(candidate))
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            return trimmed;
                        }
                    }
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }

                return null;
            }

            var parent = Directory.GetParent(directory);
            if (parent is null)
            {
                break;
            }

            directory = parent.FullName;
        }

        return null;
    }

}
