using System.Diagnostics;
using System.Text.Json;
using HotkeyListener.Models;

namespace HotkeyListener.Services;

internal sealed class ExternalProcessManager : IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public async Task EnsureAsync()
    {
        EnsureMozhi();
        EnsureWindower();
        await EnsureDeepLAsync().ConfigureAwait(false);
    }

    private static void EnsureMozhi()
    {
        const string processName = "mozhi";
        const string processPath = @"C:\\Users\\Admin\\source\\education\\mozhi\\mozhi.exe";

        if (Process.GetProcessesByName(processName).Length > 0)
        {
            Console.WriteLine("Mozhi server is already running.");
            return;
        }

        try
        {
            Process.Start(processPath, "serve");
            Console.WriteLine("Mozhi server started.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not start Mozhi server: {ex.Message}");
        }
    }

    private async Task EnsureDeepLAsync()
    {
        const string healthUrl = "http://127.0.0.1:3001/health";

        try
        {
            using var response = await _httpClient.GetAsync(healthUrl).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var health = JsonSerializer.Deserialize<HealthResponse>(body);
                if (health?.Status == "alive")
                {
                    Console.WriteLine("DeepL server is running.");
                    return;
                }
            }
        }
        catch (HttpRequestException)
        {
            // Server is not running.
        }
        catch (TaskCanceledException)
        {
            // Timeout.
        }

        Console.WriteLine("DeepL server not found, starting it...");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "main.py --server --host 127.0.0.1 --port 3001",
                WorkingDirectory = @"C:\\Users\\Admin\\source\\education\\deepl-cli\\deepl",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process.Start(startInfo);
            Console.WriteLine("DeepL server started.");
            await Task.Delay(3000).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not start DeepL server: {ex.Message}");
        }
    }

    private static void EnsureWindower()
    {
        const string processName = "WpfWindower";
        const string processPath = @"C:\\Users\\Admin\\source\\education\\dotnet-translator\\WpfWindower\\bin\\Debug\\net9.0-windows\\WpfWindower.exe";

        if (Process.GetProcessesByName(processName).Length > 0)
        {
            Console.WriteLine("WpfWindower is already running.");
            return;
        }

        try
        {
            Process.Start(processPath);
            Console.WriteLine("WpfWindower started.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not start WpfWindower: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
