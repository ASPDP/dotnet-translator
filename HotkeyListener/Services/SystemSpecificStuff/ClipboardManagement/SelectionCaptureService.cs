using HotkeyListener.Services.SystemSpecificStuff.Keyboard;

namespace HotkeyListener.Services.SystemSpecificStuff.ClipboardManagement;

internal sealed class SelectionCaptureService
{
    private readonly KeyboardInputSimulator _inputSimulator;
    private readonly TimeSpan _copyDelay;

    public SelectionCaptureService(KeyboardInputSimulator inputSimulator, TimeSpan copyDelay)
    {
        _inputSimulator = inputSimulator;
        _copyDelay = copyDelay;
    }

    public async Task CaptureSelectionAsync(CancellationToken cancellationToken)
    {
        _inputSimulator.SendCopyShortcut();
        try
        {
            await Task.Delay(_copyDelay, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            // Ignore cancellation when copy is aborted early.
        }
    }
}
