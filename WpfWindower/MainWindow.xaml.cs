using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Pipes;
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

    private readonly ObservableCollection<VariantDisplayItem> _variantItems = new();

    private DispatcherTimer? _overlayTimer;
    private DateTime? _overlayExpiresAtUtc;
    private string? _currentSessionId;

    public MainWindow()
    {
        InitializeComponent();
        OverlayView.CloseButton.Click += CloseOverlayButton_Click;
        OverlayView.VariantsItems.ItemsSource = _variantItems;
        Task.Run(StartPipeServer);
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

    private void RestartOverlayTimer(double totalSeconds, bool extendExisting = false)
    {
        totalSeconds = Math.Max(4, totalSeconds);
        var additional = TimeSpan.FromSeconds(totalSeconds);
        var now = DateTime.UtcNow;

        if (extendExisting && _overlayExpiresAtUtc.HasValue && _overlayExpiresAtUtc.Value > now && OverlayPopup.IsOpen)
        {
            _overlayExpiresAtUtc = _overlayExpiresAtUtc.Value + additional;
        }
        else
        {
            _overlayExpiresAtUtc = now + additional;
        }

        var interval = _overlayExpiresAtUtc.Value - now;
        if (interval < TimeSpan.FromMilliseconds(200))
        {
            interval = TimeSpan.FromMilliseconds(200);
        }

        if (_overlayTimer is null)
        {
            _overlayTimer = new DispatcherTimer();
            _overlayTimer.Tick += OnOverlayTimerTick;
        }
        else
        {
            _overlayTimer.Stop();
        }

        _overlayTimer.Interval = interval;
        _overlayTimer.Start();
    }

    private void OnOverlayTimerTick(object? sender, EventArgs e)
    {
        CloseOverlay();
    }

    private void CloseOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlay();
    }

    private void CloseOverlay()
    {
        OverlayPopup.IsOpen = false;

        if (_overlayTimer != null)
        {
            _overlayTimer.Tick -= OnOverlayTimerTick;
            _overlayTimer.Stop();
            _overlayTimer = null;
        }

        _overlayExpiresAtUtc = null;

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
            wordCount += CountWords(variant.Text);
        }

        var readingTimeSeconds = (wordCount / 130.0) * 60.0;
        return readingTimeSeconds + 2;
    }

    private bool UpsertVariantItem(string? variantName, string? text, bool isInitial)
    {
        var normalizedText = text ?? string.Empty;
        var lines = SplitLines(normalizedText);
        if (lines.Length == 0)
        {
            return false;
        }

        var providerLabel = string.IsNullOrWhiteSpace(variantName) ? "unknown" : variantName.Trim();
        var header = providerLabel;
        var newItem = new VariantDisplayItem(header, lines, normalizedText, isInitial);

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

    private sealed record class VariantPayload(string SessionId, string VariantName, string Text);

    private sealed record VariantDisplayItem(string Header, string[] Lines, string Text, bool IsInitial);
}
