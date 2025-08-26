using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public class HotkeyListener
{
    public const int HOTKEY_ID = 9000;
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    public static void Main()
    {
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
                    // TODO: Implement text copying, translation, and pipe communication
                    SendMessageToWindower("Hello from HotkeyListener!");
                }
            }
        }

        UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
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
