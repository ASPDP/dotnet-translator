namespace HotkeyListener.Services;

internal sealed class ClipboardService
{
    public Task<string> GetTextAsync(CancellationToken cancellationToken)
    {
        return Task.Run(ReadText, cancellationToken);
    }

    public Task SetTextAsync(string text, CancellationToken cancellationToken)
    {
        return Task.Run(() => WriteText(text), cancellationToken);
    }

    private static string ReadText()
    {
        string clipboardText = string.Empty;
        var thread = new Thread(() =>
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
                ConsoleLog.Error($"Error reading clipboard text: {ex}");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return clipboardText;
    }

    private static void WriteText(string text)
    {
        var thread = new Thread(() =>
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                ConsoleLog.Error($"Error writing clipboard text: {ex}");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
