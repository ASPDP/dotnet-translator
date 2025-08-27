using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public class TranslationRequest
{
    public string? engine { get; set; }
    public string? from { get; set; }
    public string? to { get; set; }
    public string? text { get; set; }
}

public class TranslationResponse
{
    [JsonPropertyName("engine")]
    public string? Engine { get; set; }

    [JsonPropertyName("detected")]
    public string? Detected { get; set; }

    [JsonPropertyName("translated-text")]
    public string? TranslatedText { get; set; }

    [JsonPropertyName("source_language")]
    public string? SourceLanguage { get; set; }

    [JsonPropertyName("target_language")]
    public string? TargetLanguage { get; set; }
}

public class HealthResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class HotkeyListener
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private static LowLevelKeyboardProc _proc = HookCallback;
    private static IntPtr _hookID = IntPtr.Zero;
    private static bool _isSimulatingKeyPress = false;

    private static DateTime _lastCtrlPressTime = DateTime.MinValue;
    private static readonly TimeSpan _doublePressThreshold = TimeSpan.FromMilliseconds(300); // 300ms for a double press

    private const int KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_LCONTROL = 0xA2;
    private const byte VK_C = 0x43;

    // Imports the SetWindowsHookEx function from user32.dll to set a Windows hook.
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    // Imports the UnhookWindowsHookEx function from user32.dll to remove a Windows hook.
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    // Imports the CallNextHookEx function from user32.dll to pass the hook information to the next hook procedure.
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // Imports the GetModuleHandle function from kernel32.dll to retrieve a handle to the specified module.
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    public static async Task Main()
    {
        var tskMozhi = CheckAndRunMozhiServer();
        var taskDeepL = CheckAndRunDeepLServer();
        await tskMozhi;
        await taskDeepL;

        _hookID = SetHook(_proc);
        Console.WriteLine("Double-press Ctrl to trigger translation.");
        Application.Run();
        UnhookWindowsHookEx(_hookID);
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using (Process curProcess = Process.GetCurrentProcess())
        {
            ProcessModule? curModule = curProcess.MainModule;
            if (curModule != null)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
            return IntPtr.Zero;
        }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (_isSimulatingKeyPress)
        {
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            var key = (Keys)vkCode;

            if (key == Keys.LControlKey || key == Keys.RControlKey)
            {
                TimeSpan elapsed = DateTime.Now - _lastCtrlPressTime;
                if (elapsed < _doublePressThreshold)
                {
                    Console.WriteLine("Double Ctrl Pressed!");
                    HandleHotkeyPress();
                    _lastCtrlPressTime = DateTime.MinValue; // Reset after detection
                }
                else
                {
                    _lastCtrlPressTime = DateTime.Now;
                }
            }
            else
            {
                // If any other key is pressed, reset the timer.
                _lastCtrlPressTime = DateTime.MinValue;
            }
        }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    private static async void HandleHotkeyPress()
    {
        _isSimulatingKeyPress = true;
        try
        {
            // Simulate Ctrl+C to copy selected text
            keybd_event(VK_LCONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LCONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Task.Delay(100); // Wait for clipboard to update
        }
        finally
        {
            _isSimulatingKeyPress = false;
        }

        string textToTranslate = GetClipboardText();
        if (string.IsNullOrEmpty(textToTranslate))
        {
            return;
        }

        Debug.WriteLine($"Original text: {textToTranslate}");

        var request = new TranslationRequest
        {
            text = textToTranslate,
            engine = "google"
        };

        if (ContainsRussianSymbols(textToTranslate))
        {
            request.from = "ru";
            request.to = "en";
        }
        else
        {
            request.from = "en";
            request.to = "ru";
        }

        string translatedText = await TranslateText(request);

        if (!string.IsNullOrEmpty(translatedText))
        {
            Debug.WriteLine($"Translated text: {translatedText}");
            SendMessageToWindower(translatedText);
            SetClipboardText(translatedText);
        }
    }

    private static Task CheckAndRunMozhiServer()
    {
        const string mozhiProcessName = "mozhi";
        const string mozhiPath = @"C:\Users\Admin\source\education\mozhi\mozhi.exe";

        var processes = Process.GetProcessesByName(mozhiProcessName);
        if (processes.Length == 0)
        {
            try
            {
                Process.Start(mozhiPath);
                Console.WriteLine("Mozhi server started.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not start Mozhi server: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("Mozhi server is already running.");
        }
        return Task.CompletedTask;
    }

    private static async Task CheckAndRunDeepLServer()
    {
        const string deeplUrl = "http://127.0.0.1:3001/health";
        bool deeplRunning = false;

        using (var httpClient = new HttpClient())
        {
            try
            {
                var response = await httpClient.GetAsync(deeplUrl);
                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    var healthResponse = JsonSerializer.Deserialize<HealthResponse>(responseBody);
                    if (healthResponse?.Status == "alive")
                    {
                        deeplRunning = true;
                        Console.WriteLine("DeepL server is running.");
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Server is not running
            }
        }

        if (!deeplRunning)
        {
            Console.WriteLine("DeepL server not found, starting it...");
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "main.py --server --host 127.0.0.1 --port 3001",
                    WorkingDirectory = @"C:\Users\Admin\source\education\deepl-cli\deepl",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                Process.Start(processInfo);
                Console.WriteLine("DeepL server started.");
                await Task.Delay(3000); // Give it a moment to start up
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not start DeepL server: {ex.Message}");
            }
        }
    }

    private static string GetClipboardText()
    {
        string clipboardText = string.Empty;
        Thread staThread = new Thread(() =>
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    clipboardText = Clipboard.GetText();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting clipboard text: {ex.Message}");
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();
        return clipboardText;
    }

    private static void SetClipboardText(string text)
    {
        Thread staThread = new Thread(() =>
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting clipboard text: {ex.Message}");
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();
    }

    private static bool ContainsRussianSymbols(string text)
    {
        return Regex.IsMatch(text, @"[\u0400-\u04FF]");
    }

    private static async Task<string> TranslateText(TranslationRequest request, bool isRetry = false)
    {
        var port = 3000;
        var engineToUse = request.engine;

        if (isRetry)
        {
            engineToUse = "yandex";
        }

        if (engineToUse == "deepl")
        {
            port = 3001;
            engineToUse = "google"; // т.к. сервер deepl неправильно принимает google вместо нужного deepl :) надо исправить сервр python
        }

        var engine = Uri.EscapeDataString(engineToUse ?? string.Empty);
        var from = Uri.EscapeDataString(request.from ?? string.Empty);
        var to = Uri.EscapeDataString(request.to ?? string.Empty);
        var text = Uri.EscapeDataString(request.text ?? string.Empty);

        string url = $"http://127.0.0.1:{port}/api/translate?engine={engine}&from={from}&to={to}&text={text}";

        using (var httpClient = new HttpClient())
        {
            try
            {
                var response = await httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    var translationResponse = JsonSerializer.Deserialize<TranslationResponse>(responseBody);
                    return translationResponse?.TranslatedText ?? string.Empty;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Error calling translation API with engine {engineToUse}: {response.StatusCode} - {errorContent}");

                    if (!isRetry)
                    {
                        Console.WriteLine("Retrying with yandex engine.");
                        return await TranslateText(request, true);
                    }

                    return string.Empty;
                }
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"Error calling translation API with engine {engineToUse}: {e.Message}");

                if (!isRetry)
                {
                    Console.WriteLine("Retrying with yandex engine.");
                    return await TranslateText(request, true);
                }

                return string.Empty;
            }
        }
    }

    private static void SendMessageToWindower(string message)
    {
        try
        {
            using (var client = new NamedPipeClientStream(".", "DotNetTranslatorPipe", PipeDirection.Out))
            {
                client.Connect(5000); // 5-second timeout
                if (client.IsConnected)
                {
                    byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                    client.Write(messageBytes, 0, messageBytes.Length);
                    Console.WriteLine("Message sent to windower.");
                }
                else
                {
                    Console.WriteLine("Could not connect to the windower pipe.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending message to windower: {ex.Message}");
        }
    }
}

public struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public System.Drawing.Point pt;
}
