using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Threading;

namespace WpfWindower;
public static class WindowsServices
{
  const int WS_EX_TRANSPARENT = 0x00000020;
  const int GWL_EXSTYLE = (-20);

  [DllImport("user32.dll")]
  static extern int GetWindowLong(IntPtr hwnd, int index);

  [DllImport("user32.dll")]
  static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

  public static void SetWindowExTransparent(IntPtr hwnd)
  {
    var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
    SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
  }
}
/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private DispatcherTimer _overlayTimer;
protected override void OnSourceInitialized(EventArgs e)
{
  base.OnSourceInitialized(e);
  var hwnd = new WindowInteropHelper(this).Handle;
  WindowsServices.SetWindowExTransparent(hwnd);
}
    
    public MainWindow()
    {
        InitializeComponent();
        Task.Run(() => StartPipeServer());
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        OverlayPopup.PlacementTarget = this;
        OverlayPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
        OverlayPopup.MaxWidth = SystemParameters.PrimaryScreenWidth / 5;
        OverlayBorder.MaxWidth = SystemParameters.PrimaryScreenWidth / 5;
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
                using (var pipeServer = new NamedPipeServerStream("DotNetTranslatorPipe", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                {
                    pipeServer.WaitForConnection();
                    var reader = new StreamReader(pipeServer);
                    var message = reader.ReadToEnd();

                    if (!string.IsNullOrEmpty(message))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (message == "SHOW_RHOMBUS")
                            {
                                ShowRhombus();
                            }
                            else
                            {
                                Debug.WriteLine($"Received message: {message}");
                                ShowOverlay(message);
                            }
                        });
                    }
                }
            }
            catch (IOException)
            {
                // Pipe was closed, loop and wait for a new connection
            }
        }
    }

    private void ShowOverlay(string text)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        OverlayItems.ItemsSource = lines;
        OverlayPopup.IsOpen = true;

        // Stop any previous overlay timer to avoid closing a new popup
        if (_overlayTimer != null)
        {
            _overlayTimer.Stop();
            _overlayTimer = null;
        }

        var wordCount = text.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        var readingTimeSeconds = (wordCount / 130.0) * 60.0;
        var totalSeconds = readingTimeSeconds + 2;

        _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(totalSeconds) };
        _overlayTimer.Tick += (sender, args) =>
        {
            OverlayPopup.IsOpen = false;
            if (_overlayTimer != null)
            {
                _overlayTimer.Stop();
                _overlayTimer = null;
            }
        };
        _overlayTimer.Start();
    }

    private void CloseOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        OverlayPopup.IsOpen = false;
        if (_overlayTimer != null)
        {
            _overlayTimer.Stop();
            _overlayTimer = null;
        }
    }
}
