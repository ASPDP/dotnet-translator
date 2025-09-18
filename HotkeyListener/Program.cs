namespace HotkeyListener;

internal static class Program
{
    [STAThread]
    private static async Task Main()
    {
        using var app = HotkeyApplication.CreateDefault();
        await app.InitializeAsync();
        app.Run();
    }
}
