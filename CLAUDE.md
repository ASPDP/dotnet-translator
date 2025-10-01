# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A Windows desktop application that provides system-wide text translation via hotkeys. When the user double-presses Control keys, the application captures selected text, translates it, and displays translation variants in a WPF overlay window. The system uses multiple translation providers in parallel and shows results as they arrive.

## Architecture

### Two-Process Design

The solution consists of two executables that communicate via Named Pipes:

1. **HotkeyListener** (Console application, .NET 9.0)
   - Main entry point that runs in system tray
   - Listens for double-press Control key events via low-level keyboard hooks
   - Orchestrates the translation workflow
   - Manages external process lifecycle (Mozhi, DeepL server, WpfWindower)
   - Communicates with WpfWindower via Named Pipe "DotNetTranslatorPipe"

2. **WpfWindower** (WPF application, .NET 9.0)
   - Runs as a separate process
   - Displays overlay UI with translation results
   - Receives commands via Named Pipe server
   - Shows rhombus indicator and translation variants with progress bar

### Translation Workflow

When hotkey is triggered:
1. `DoublePressHotkeyListener` detects double Control key press (500ms window)
2. `SelectionCaptureService` simulates Ctrl+C to capture selected text
3. `ClipboardService` retrieves text from clipboard
4. `LanguageDirectionResolver` detects language direction (Cyrillic → en-ru, otherwise → ru-en)
5. `TranslationWorkflow` starts **all** translation requests in parallel:
   - **Primary translators** (fast, traditional): Google, Yandex (both via Mozhi on port 3000), DeepL (port 3001)
   - **Variant translators** (slower, AI-based): Grok, DeepSeek (both via OpenRouter API)
6. **All translators run independently** - none are cancelled when others complete
7. First completed **primary translator** (Google/Yandex/DeepL) result goes to clipboard
8. All translation results are sent to WpfWindower as they complete (including all primary translators)
9. WpfWindower displays results in overlay with auto-hide timer

### Translator Architecture

All translators implement a common `ITranslator` interface with inheritance hierarchy:

```
ITranslator (interface)
  └─ HttpTranslatorBase (abstract base with error handling)
       ├─ MozhiTranslator (Google, Yandex via Mozhi API)
       ├─ DeepLTranslator (DeepL via custom Python server)
       └─ OpenRouterTranslator (Grok, DeepSeek via OpenRouter API)
```

**ITranslator**: Common interface with `TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken)` method and `Name` property

**HttpTranslatorBase**: Abstract base class providing:
- Common error handling (HTTP errors, cancellation, timeouts)
- Template method pattern with `TranslateInternalAsync` (abstract) and `PostProcessTranslation` (virtual)
- Logging infrastructure

**Specific Translators**:
- **MozhiTranslator**: Configured with engine name (google/yandex) and port
- **DeepLTranslator**: Uses port 3001, internally calls Google engine
- **OpenRouterTranslator**: Unified implementation for all OpenRouter models (Grok, DeepSeek)
  - Configured via `OpenRouterConfig` record with `ModelId`, `DisplayName`, `IncludeErrorExplanation`, `StripReasoningTags`
  - Handles DeepSeek reasoning tags `<think>...</think>` stripping when configured
  - Can add error explanations in Russian for English source text (Grok only)

### Key Services

- **TranslationWorkflow**: Main orchestrator for translation sessions. Uses a list of primary translators (Google/Yandex/DeepL) and variant translators (AI models). All translators run concurrently without cancellation. Waits for first primary translator to complete for clipboard, but continues running all others in background. Handles caching (reuses last translation if same text).
- **WindowerClient**: Named Pipe client that sends structured JSON messages to WpfWindower
- **ExternalProcessManager**: Ensures external dependencies are running (Mozhi, DeepL server, WpfWindower)
- **LanguageDirectionResolver**: Uses regex to detect Cyrillic characters and determine translation direction

### External Dependencies

The application requires these external services running:
- **Mozhi** (port 3000): Translation API server at `C:\Users\Admin\source\education\mozhi\mozhi.exe`
- **DeepL Python server** (port 3001): Custom server at `C:\Users\Admin\source\education\deepl-cli\deepl\main.py`
- **WpfWindower**: Located at `WpfWindower\bin\Debug\net9.0-windows\WpfWindower.exe`

These are auto-started by `ExternalProcessManager` if not already running.

### API Key Configuration

OpenRouter API key is loaded in this order:
1. Search for `openrouter_api_key.txt` file starting from executable directory and walking up parent directories
2. Fall back to `OPENROUTER_API_KEY` environment variable
3. If missing, OpenRouter variants are skipped with a warning

### Translation Variant Display

The WPF overlay window shows:
- **Primary translations**: Google, Yandex, and DeepL results as they complete (all run independently)
- **AI variants**: Grok and DeepSeek translations as they complete
- **Explanation feature**: Grok translations can include `---` separator followed by error explanations in Russian (for English source text only)
- **Progress bar**: Auto-hide timer based on word count (130 words per minute reading speed + 2 seconds)
- **Hover pause**: Timer pauses when hovering over explanations

**Important**: Even though Google responds fastest, Yandex and DeepL translations continue running in the background and display when ready.

## Building and Running

### Build Solution
```bash
dotnet build DotNetTranslator.sln
```

### Build Individual Projects
```bash
dotnet build HotkeyListener/HotkeyListener.csproj
dotnet build WpfWindower/WpfWindower.csproj
```

### Run HotkeyListener (main application)
```bash
dotnet run --project HotkeyListener/HotkeyListener.csproj
```

Or run the compiled executable:
```bash
./HotkeyListener/bin/Debug/net9.0-windows/HotkeyListener.exe
```

### Adding New Translators

To add a new translation provider:

1. Create a new class in `HotkeyListener/Services/Translators/` that inherits from `HttpTranslatorBase`
2. Implement `TranslateInternalAsync` to perform the HTTP translation request
3. Optionally override `PostProcessTranslation` for custom post-processing
4. Add the translator instance to `variantTranslators` list in `HotkeyApplication.CreateDefault()`

Example:
```csharp
internal sealed class MyTranslator : HttpTranslatorBase
{
    public MyTranslator(HttpClient httpClient)
        : base(httpClient, "MyTranslator") { }

    protected override async Task<string?> TranslateInternalAsync(
        string text, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken)
    {
        // Implement HTTP request logic
        return translatedText;
    }
}
```

### Configuration Note

The hardcoded paths in `ExternalProcessManager` (lines 21, 74, 92) may need adjustment for different development environments. These paths are specific to the original developer's machine.

## Key Implementation Details

### Hotkey Detection
- Uses low-level keyboard hook (`KeyboardHook.cs`) via Windows API (`user32.dll` SetWindowsHookEx)
- Detects double-press within 500ms window for either Control key
- `IHotkeySimulationGuard` prevents recursive triggering during Ctrl+C simulation

### Session Management
- Each translation session gets a unique GUID
- New hotkey press cancels active session and starts fresh
- `_processingLock` semaphore ensures only one session processes at a time
- Session cancellation propagates to all variant tasks via `CancellationTokenSource`

### Parallel Translation Strategy
- All translation variants start simultaneously
- `WaitForFirstVariantAsync` waits for any successful result for initial display
- Background pipeline continues fetching remaining variants
- Each variant task handles its own errors and returns null on failure

### WPF Overlay Window
- Uses `WS_EX_TRANSPARENT` window style to allow click-through except on controls
- Popup positioned at screen center, max width 20% of primary screen width
- Named Pipe server runs in background thread, dispatches to UI thread via `Dispatcher.Invoke`
- Message protocol: `SHOW_RHOMBUS` or `SHOW_VARIANT:{json}`

### Error Handling
- Translation failures are logged but don't block other variants
- Missing API keys log warnings and skip respective providers
- External process startup failures are logged but don't crash the application
- Pipe communication errors are caught and logged

## Development Notes

- The project uses .NET 9.0 (preview at time of development)
- WPF application targets `net9.0-windows` framework
- Both projects use `<ImplicitUsings>enable</ImplicitUsings>` and `<Nullable>enable</Nullable>`
- No unit tests are present in the solution
- Console logging uses `ConsoleLog` helper with colored output (Info/Success/Warning/Error/Highlight)
