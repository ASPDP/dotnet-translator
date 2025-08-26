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
    public const int HOTKEY_ID = 9000;
    public const int WM_HOTKEY = 0x0312;

    // Imports the RegisterHotKey function from user32.dll to register a system-wide hotkey.
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    // Imports the UnregisterHotKey function from user32.dll to unregister a system-wide hotkey.
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Imports the GetMessage function from user32.dll to retrieve a message from the calling thread's message queue.
    [DllImport("user32.dll")]
    public static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    public static async Task Main()
    {
        var tskMozhi = CheckAndRunMozhiServer();
        var taskDeepL = CheckAndRunDeepLServer();
        await tskMozhi;
        await taskDeepL;

        // Register F3 hotkey
        if (!RegisterHotKey(IntPtr.Zero, HOTKEY_ID, 0, (uint)Keys.F3))
        {
            Console.WriteLine("Failed to register hotkey.");
            return;
        }

        Console.WriteLine("F3 hotkey registered. Press F3 to trigger or Esc to exit.");

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == WM_HOTKEY)
            {
                if (msg.wParam.ToInt32() == HOTKEY_ID)
                {
                    Console.WriteLine("F3 Pressed!");

                    string textToTranslate = GetClipboardText();
                    if (string.IsNullOrEmpty(textToTranslate))
                    {
                        continue;
                    }

                    var request = new TranslationRequest
                    {
                        text = textToTranslate
                    };

                    if (ContainsRussianSymbols(textToTranslate))
                    {
                        request.from = "ru";
                        request.to = "en";
                        request.engine = "google";
                    }
                    else
                    {
                        request.from = "en";
                        request.to = "ru";
                        request.engine = "deepl";
                    }

                    string translatedText = await TranslateText(request);

                    if (!string.IsNullOrEmpty(translatedText))
                    {
                        SendMessageToWindower(translatedText);
                    }
                }
            }
        }

        UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
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

    private static bool ContainsRussianSymbols(string text)
    {
        return Regex.IsMatch(text, @"[\u0400-\u04FF]");
    }

    private static async Task<string> TranslateText(TranslationRequest request)
    {
        var port = 3000;
        if (request.engine == "deepl")
        {
            port = 3001;
        }

        string url = $"http://127.0.0.1:{port}/api/translate?engine={request.engine}&from={request.from}&to={request.to}&text={Uri.EscapeDataString(request.text ?? string.Empty)}";

        using (var httpClient = new HttpClient())
        {
            try
            {
                var response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync();
                var translationResponse = JsonSerializer.Deserialize<TranslationResponse>(responseBody);
                return translationResponse?.TranslatedText ?? string.Empty;
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"Error calling translation API: {e.Message}");
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
