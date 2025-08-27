using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WpfWindower;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Task.Run(() => StartPipeServer());
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
                            Debug.WriteLine($"Received message: {message}");
                            ShowOverlay(message);
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
        var overlay = new Window
        {
            Content = new TextBlock { Text = text, Padding = new Thickness(10), TextWrapping = TextWrapping.Wrap },
            Width = 300,
            Height = 150,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.LightGray,
            Topmost = true,
            ShowInTaskbar = false,
            Left = SystemParameters.PrimaryScreenWidth - 310,
            Top = SystemParameters.PrimaryScreenHeight - 160
        };

        overlay.Show();

        Task.Delay(3000).ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                overlay.Close();
            });
        });
    }
}