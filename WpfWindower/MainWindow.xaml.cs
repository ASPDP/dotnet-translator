using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WpfWindower;

public static class WindowsServices
{
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public static void SetWindowExTransparent(IntPtr hwnd)
    {
        var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
    }
}

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan OverlayProgressTickInterval = TimeSpan.FromMilliseconds(120);

    private readonly ObservableCollection<VariantDisplayItem> _variantItems = new();

    private DispatcherTimer? _overlayTimer;
    private DateTime? _overlayExpiresAtUtc;
    private string? _currentSessionId;

    private DateTime? _overlayStartedAtUtc;
    private TimeSpan _overlayTotalDuration = TimeSpan.Zero;

    private int _overlayPauseRequests;
    private TimeSpan? _overlayRemainingWhenPaused;
    private TimeSpan _overlayElapsedBeforePause = TimeSpan.Zero;

    private DispatcherTimer? _clipboardTimer;

    private string? _serverProcessPath;
    private string? _serverWorkingDirectory;

    public MainWindow()
    {
        InitializeComponent();
        OverlayView.CloseButton.Click += CloseOverlayButton_Click;
        OverlayView.VariantsItems.ItemsSource = _variantItems;
        OverlayView.VariantExplanationHoverChanged += OnVariantExplanationHoverChanged;
        ClipboardOverlayView.CloseButton.Click += CloseClipboardOverlayButton_Click;
        Task.Run(StartPipeServer);
        Task.Run(TrackServerProcess);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowsServices.SetWindowExTransparent(hwnd);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        OverlayPopup.PlacementTarget = this;
        OverlayPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
        OverlayPopup.MaxWidth = SystemParameters.PrimaryScreenWidth / 5;
        OverlayView.OverlayBorder.MaxWidth = SystemParameters.PrimaryScreenWidth / 5;

        ClipboardOverlayPopup.PlacementTarget = this;
        ClipboardOverlayPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
        ClipboardOverlayPopup.MaxWidth = SystemParameters.PrimaryScreenWidth / 5;
        ClipboardOverlayView.OverlayBorder.MaxWidth = SystemParameters.PrimaryScreenWidth / 5;
    }

    private void ShowRhombus()
    {
        RhombusPopup.IsOpen = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (sender, args) =>
        {
            RhombusPopup.IsOpen = false;
            timer.Stop();
        };
        timer.Start();
    }

    private void StartPipeServer()
    {
        while (true)
        {
            try
            {
                using var pipeServer = new NamedPipeServerStream("DotNetTranslatorPipe", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                pipeServer.WaitForConnection();

                using var reader = new StreamReader(pipeServer);
                var message = reader.ReadToEnd();

                if (string.IsNullOrEmpty(message))
                {
                    continue;
                }

                Dispatcher.Invoke(() => HandleMessage(message));
            }
            catch (IOException)
            {
                // Pipe was closed, loop and wait for a new connection
            }
        }
    }

    private void HandleMessage(string message)
    {
        if (message == "SHOW_RHOMBUS")
        {
            ShowRhombus();
            return;
        }

        if (message.StartsWith("SHOW_VARIANT:", StringComparison.Ordinal))
        {
            var payloadJson = message["SHOW_VARIANT:".Length..];
            HandleVariantPayload(payloadJson);
            return;
        }

        if (message.StartsWith("SHOW_CLIPBOARD:", StringComparison.Ordinal))
        {
            var text = message["SHOW_CLIPBOARD:".Length..];
            ShowClipboardOverlay(text);
            return;
        }

        ConsoleLog.Warning($"Received unsupported message: {message}");
    }

    private void HandleVariantPayload(string payloadJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<VariantPayload>(payloadJson, SerializerOptions);
            if (payload == null || string.IsNullOrWhiteSpace(payload.SessionId))
            {
                return;
            }

            var isNewSession = !string.Equals(payload.SessionId, _currentSessionId, StringComparison.Ordinal);

            if (isNewSession)
            {
                StartVariantSession(payload);
                return;
            }

            AddVariant(payload);
        }
        catch (JsonException ex)
        {
            ConsoleLog.Error($"Failed to parse variant payload: {ex}");
        }
    }



    private void StartVariantSession(VariantPayload payload)
    {
        var newText = payload.Text ?? string.Empty;
        var provider = string.IsNullOrWhiteSpace(payload.VariantName) ? "unknown" : payload.VariantName;
        var shouldExtend = ShouldExtendForInitialVariant(newText);

        _currentSessionId = payload.SessionId;

        ConsoleLog.Highlight($"VariantSessionStart session={payload.SessionId} provider={provider}");

        OverlayView.OverlayItems.ItemsSource = Array.Empty<string>();
        _variantItems.Clear();
        UpdateVariantVisibility();
        ResetOverlayPauseState();

        if (!UpsertVariantItem(provider, newText, isInitial: true))
        {
            return;
        }

        OverlayPopup.IsOpen = true;
        RestartOverlayTimer(CalculateDisplayDurationSeconds(), shouldExtend);
    }



    private void AddVariant(VariantPayload payload)
    {
        if (!string.Equals(payload.SessionId, _currentSessionId, StringComparison.Ordinal))
        {
            ConsoleLog.Info($"Ignoring variant from old session: {payload.SessionId} (current: {_currentSessionId})");
            return;
        }

        if (!UpsertVariantItem(payload.VariantName, payload.Text, isInitial: false))
        {
            return;
        }

        OverlayPopup.IsOpen = true;
        RestartOverlayTimer(CalculateDisplayDurationSeconds());
    }

    private void UpdateVariantVisibility()
    {
        var hasVariants = _variantItems.Count > 0;
        // OverlayView.VariantsHeader.Visibility = hasVariants ? Visibility.Visible : Visibility.Collapsed;
        OverlayView.VariantsItems.Visibility = hasVariants ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnVariantExplanationHoverChanged(object? sender, bool isHovering)
    {
        if (isHovering)
        {
            PauseOverlay();
        }
        else
        {
            ResumeOverlay();
        }
    }

    private void ResetOverlayPauseState()
    {
        _overlayPauseRequests = 0;
        _overlayRemainingWhenPaused = null;
        _overlayElapsedBeforePause = TimeSpan.Zero;
    }

    private void PauseOverlay()
    {
        if (!_overlayExpiresAtUtc.HasValue || _overlayTimer is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var remaining = _overlayExpiresAtUtc.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        _overlayPauseRequests++;
        if (_overlayPauseRequests > 1)
        {
            return;
        }

        UpdateOverlayProgress(now);

        _overlayRemainingWhenPaused = remaining;
        _overlayElapsedBeforePause = _overlayStartedAtUtc.HasValue
            ? now - _overlayStartedAtUtc.Value
            : TimeSpan.Zero;

        _overlayTimer.Stop();
    }

    private void ResumeOverlay()
    {
        if (_overlayPauseRequests == 0)
        {
            return;
        }

        _overlayPauseRequests = Math.Max(0, _overlayPauseRequests - 1);
        if (_overlayPauseRequests > 0)
        {
            return;
        }

        if (!_overlayExpiresAtUtc.HasValue || _overlayTimer is null || !_overlayRemainingWhenPaused.HasValue)
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (_overlayElapsedBeforePause > _overlayTotalDuration)
        {
            _overlayElapsedBeforePause = _overlayTotalDuration;
        }

        _overlayStartedAtUtc = now - _overlayElapsedBeforePause;
        _overlayExpiresAtUtc = now + _overlayRemainingWhenPaused.Value;

        UpdateOverlayProgress(now);
        _overlayTimer.Start();

        _overlayRemainingWhenPaused = null;
        _overlayElapsedBeforePause = TimeSpan.Zero;
    }

    private void RestartOverlayTimer(double totalSeconds, bool extendExisting = false)
    {
        totalSeconds = Math.Max(4, totalSeconds);
        var additional = TimeSpan.FromSeconds(totalSeconds);
        var now = DateTime.UtcNow;

        if (extendExisting && _overlayExpiresAtUtc.HasValue && _overlayStartedAtUtc.HasValue && _overlayExpiresAtUtc.Value > now && OverlayPopup.IsOpen)
        {
            _overlayExpiresAtUtc = _overlayExpiresAtUtc.Value + additional;
            _overlayTotalDuration = _overlayExpiresAtUtc.Value - _overlayStartedAtUtc.Value;
        }
        else
        {
            _overlayStartedAtUtc = now;
            _overlayExpiresAtUtc = now + additional;
            _overlayTotalDuration = additional;
        }

        if (_overlayTotalDuration <= TimeSpan.Zero)
        {
            _overlayTotalDuration = TimeSpan.FromSeconds(1);
        }

        if (_overlayTimer is null)
        {
            _overlayTimer = new DispatcherTimer { Interval = OverlayProgressTickInterval };
            _overlayTimer.Tick += OnOverlayTimerTick;
        }
        else
        {
            _overlayTimer.Stop();
            _overlayTimer.Interval = OverlayProgressTickInterval;
        }

        OverlayView.ElapsedProgress.Visibility = Visibility.Visible;
        UpdateOverlayProgress(now);

        if (_overlayPauseRequests > 0)
        {
            if (_overlayExpiresAtUtc.HasValue)
            {
                var remaining = _overlayExpiresAtUtc.Value - now;
                _overlayRemainingWhenPaused = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            }

            _overlayElapsedBeforePause = _overlayStartedAtUtc.HasValue
                ? now - _overlayStartedAtUtc.Value
                : TimeSpan.Zero;

            _overlayTimer.Stop();
        }
        else
        {
            _overlayTimer.Start();
        }
    }

    private void OnOverlayTimerTick(object? sender, EventArgs e)
    {
        if (!_overlayExpiresAtUtc.HasValue)
        {
            return;
        }

        var now = DateTime.UtcNow;
        UpdateOverlayProgress(now);

        if (now >= _overlayExpiresAtUtc.Value)
        {
            CloseOverlay();
        }
    }

    private void UpdateOverlayProgress(DateTime now)
    {
        if (!_overlayStartedAtUtc.HasValue || _overlayTotalDuration <= TimeSpan.Zero)
        {
            return;
        }

        var elapsed = now - _overlayStartedAtUtc.Value;
        var ratio = Math.Clamp(elapsed.TotalMilliseconds / _overlayTotalDuration.TotalMilliseconds, 0.0, 1.0);
        OverlayView.ElapsedProgress.Value = ratio;
    }

    private void CloseOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlay();
    }

    private void CloseOverlay()
    {
        OverlayPopup.IsOpen = false;
        OverlayView.ElapsedProgress.Visibility = Visibility.Collapsed;
        OverlayView.ElapsedProgress.Value = 0;
        ResetOverlayPauseState();

        if (_overlayTimer != null)
        {
            _overlayTimer.Tick -= OnOverlayTimerTick;
            _overlayTimer.Stop();
            _overlayTimer = null;
        }

        _overlayExpiresAtUtc = null;
        _overlayStartedAtUtc = null;
        _overlayTotalDuration = TimeSpan.Zero;

        // OverlayView.TranslationHeader.Visibility = Visibility.Collapsed;
        // OverlayView.TranslationHeader.Text = string.Empty;
        OverlayView.OverlayItems.ItemsSource = Array.Empty<string>();
        _currentSessionId = null;

        _variantItems.Clear();
        UpdateVariantVisibility();
    }

    private double CalculateDisplayDurationSeconds()
    {
        var wordCount = 0;
        foreach (var variant in _variantItems)
        {
            wordCount += CountWords(variant.DisplayText);
        }

        var readingTimeSeconds = (wordCount / 130.0) * 60.0;
        return readingTimeSeconds + 2;
    }

    private bool UpsertVariantItem(string? variantName, string? text, bool isInitial)
    {
        var normalizedText = text ?? string.Empty;
        var (displayText, explanation) = SplitTextAndExplanation(normalizedText);
        var lines = SplitLines(displayText);
        if (lines.Length == 0)
        {
            return false;
        }

        var providerLabel = string.IsNullOrWhiteSpace(variantName) ? "unknown" : variantName.Trim();
        var header = providerLabel;
        var hasExplanation = !string.IsNullOrWhiteSpace(explanation);
        var newItem = new VariantDisplayItem(header, lines, normalizedText, isInitial, hasExplanation, explanation);

        if (isInitial)
        {
            if (_variantItems.Count > 0 && _variantItems[0].IsInitial)
            {
                _variantItems[0] = newItem;
            }
            else
            {
                _variantItems.Insert(0, newItem);
            }
        }
        else
        {
            var existingIndex = FindVariantIndex(header, isInitial: false);
            if (existingIndex >= 0)
            {
                _variantItems[existingIndex] = newItem;
            }
            else
            {
                _variantItems.Add(newItem);
            }
        }

        UpdateVariantVisibility();
        return true;
    }

    private int FindVariantIndex(string header, bool isInitial)
    {
        for (var i = 0; i < _variantItems.Count; i++)
        {
            var candidate = _variantItems[i];
            if (candidate.IsInitial == isInitial && string.Equals(candidate.Header, header, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private bool ShouldExtendForInitialVariant(string newText)
    {
        if (!OverlayPopup.IsOpen || _variantItems.Count != 1)
        {
            return false;
        }

        var existingInitial = _variantItems[0];
        if (!existingInitial.IsInitial)
        {
            return false;
        }

        return string.Equals(existingInitial.Text, newText, StringComparison.Ordinal);
    }

    private static (string DisplayText, string? Explanation) SplitTextAndExplanation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, null);
        }

        var separatorIndex = text.IndexOf("---", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return (text, null);
        }

        var displayPart = text[..separatorIndex].TrimEnd();
        var explanationStart = separatorIndex + 3;
        var explanationPart = explanationStart < text.Length
            ? text[explanationStart..].Trim()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(displayPart))
        {
            displayPart = string.Empty;
        }

        return (displayPart, string.IsNullOrWhiteSpace(explanationPart) ? null : explanationPart);
    }

    private static string[] SplitLines(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var separators = new[] { ' ', '\r', '\n', '\t' };
        return text.Split(separators, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private void ShowClipboardOverlay(string message)
    {
        // Close other windows first
        CloseOverlay();
        RhombusPopup.IsOpen = false;

        ClipboardOverlayView.MessageText.Text = message;
        ClipboardOverlayPopup.IsOpen = true;

        // Auto-close after 0.5 seconds
        if (_clipboardTimer == null)
        {
            _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _clipboardTimer.Tick += (s, e) =>
            {
                _clipboardTimer.Stop();
                CloseClipboardOverlay();
            };
        }
        else
        {
            _clipboardTimer.Stop();
        }

        _clipboardTimer.Start();
    }

    private void CloseClipboardOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        CloseClipboardOverlay();
    }

    private void CloseClipboardOverlay()
    {
        _clipboardTimer?.Stop();
        ClipboardOverlayPopup.IsOpen = false;
        ClipboardOverlayView.MessageText.Text = string.Empty;
    }

    private async Task TrackServerProcess()
    {
        await Task.Delay(1000); // Wait for server to connect

        try
        {
            var hotkeyProcesses = Process.GetProcessesByName("HotkeyListener");
            if (hotkeyProcesses.Length > 0)
            {
                var process = hotkeyProcesses[0];
                _serverProcessPath = process.MainModule?.FileName;
                _serverWorkingDirectory = Path.GetDirectoryName(_serverProcessPath);
                ConsoleLog.Success($"Tracked HotkeyListener: {_serverProcessPath}");
            }
            else
            {
                ConsoleLog.Warning("HotkeyListener process not found for tracking.");
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Error tracking server process: {ex.Message}");
        }
    }

    private async void RestartServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_serverProcessPath) || !File.Exists(_serverProcessPath))
        {
            ConsoleLog.Error("Server process path not found. Cannot restart.");
            return;
        }

        RestartServerButton.IsEnabled = false;
        RestartServerButton.Content = "Restarting...";

        try
        {
            await Task.Run(async () =>
            {
                // Kill all HotkeyListener processes using multiple methods
                if (!await KillHotkeyListenerProcessesAsync())
                {
                    ConsoleLog.Error("Failed to kill all HotkeyListener processes. Aborting restart.");
                    return;
                }

                // Wait a bit more to ensure cleanup
                await Task.Delay(1000);

                // Start the server only if all processes are killed
                ConsoleLog.Info($"Starting HotkeyListener from: {_serverProcessPath}");
                var startInfo = new ProcessStartInfo
                {
                    FileName = _serverProcessPath,
                    WorkingDirectory = _serverWorkingDirectory ?? Path.GetDirectoryName(_serverProcessPath),
                    UseShellExecute = true
                };

                var newProcess = Process.Start(startInfo);
                if (newProcess != null)
                {
                    ConsoleLog.Success($"HotkeyListener restarted with PID {newProcess.Id}");
                }
                else
                {
                    ConsoleLog.Error("Failed to start HotkeyListener process.");
                }
            });
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Error restarting server: {ex.Message}");
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                RestartServerButton.IsEnabled = true;
                RestartServerButton.Content = "Restart Server";
            });
        }
    }

    private static async Task<bool> KillHotkeyListenerProcessesAsync()
    {
        var processes = Process.GetProcessesByName("HotkeyListener");
        ConsoleLog.Info($"Found {processes.Length} HotkeyListener process(es) to kill");

        if (processes.Length == 0)
        {
            return true;
        }

        // Method 1: Try graceful kill with process tree termination
        foreach (var process in processes)
        {
            try
            {
                ConsoleLog.Info($"Attempting to kill process {process.Id} (graceful with tree)...");
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                ConsoleLog.Warning($"Graceful kill failed for {process.Id}: {ex.Message}");
            }
        }

        // Wait for processes to exit
        var maxWait = TimeSpan.FromSeconds(5);
        var waitStart = DateTime.UtcNow;

        while (DateTime.UtcNow - waitStart < maxWait)
        {
            await Task.Delay(200);
            var remaining = Process.GetProcessesByName("HotkeyListener");

            if (remaining.Length == 0)
            {
                ConsoleLog.Success("All HotkeyListener processes terminated gracefully.");
                return true;
            }

            ConsoleLog.Info($"Waiting for {remaining.Length} process(es) to exit...");
        }

        // Method 2: Force kill using taskkill /F
        ConsoleLog.Warning("Graceful termination failed. Attempting force kill with taskkill...");
        try
        {
            var taskkillProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = "/F /IM HotkeyListener.exe /T",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (taskkillProcess != null)
            {
                await taskkillProcess.WaitForExitAsync();
                var output = await taskkillProcess.StandardOutput.ReadToEndAsync();
                var error = await taskkillProcess.StandardError.ReadToEndAsync();

                ConsoleLog.Info($"taskkill output: {output}");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    ConsoleLog.Warning($"taskkill error: {error}");
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Failed to run taskkill: {ex.Message}");
        }

        // Wait again after force kill
        maxWait = TimeSpan.FromSeconds(5);
        waitStart = DateTime.UtcNow;

        while (DateTime.UtcNow - waitStart < maxWait)
        {
            await Task.Delay(200);
            var remaining = Process.GetProcessesByName("HotkeyListener");

            if (remaining.Length == 0)
            {
                ConsoleLog.Success("All HotkeyListener processes terminated forcefully.");
                return true;
            }

            ConsoleLog.Info($"Still waiting for {remaining.Length} process(es) to exit...");
        }

        // Method 3: Try direct process termination via WMI/Process.CloseMainWindow
        ConsoleLog.Warning("Force kill failed. Attempting alternative termination methods...");
        var stubborn = Process.GetProcessesByName("HotkeyListener");

        foreach (var process in stubborn)
        {
            try
            {
                ConsoleLog.Info($"Attempting CloseMainWindow on process {process.Id}...");
                process.CloseMainWindow();
                await Task.Delay(500);

                if (!process.HasExited)
                {
                    ConsoleLog.Info($"CloseMainWindow failed, trying Kill() on {process.Id}...");
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                ConsoleLog.Warning($"Alternative termination failed for {process.Id}: {ex.Message}");
            }
        }

        // Final check
        await Task.Delay(1000);
        var finalRemaining = Process.GetProcessesByName("HotkeyListener");

        if (finalRemaining.Length == 0)
        {
            ConsoleLog.Success("All HotkeyListener processes eventually terminated.");
            return true;
        }

        ConsoleLog.Error($"Failed to kill all processes. {finalRemaining.Length} process(es) still running.");
        return false;
    }

    private sealed record class VariantPayload(string SessionId, string VariantName, string Text);

    internal sealed record VariantDisplayItem(string Header, string[] Lines, string Text, bool IsInitial, bool HasExplanation, string? Explanation)
    {
        public string DisplayText => Lines.Length == 0
            ? string.Empty
            : string.Join(Environment.NewLine, Lines);
    }
}

