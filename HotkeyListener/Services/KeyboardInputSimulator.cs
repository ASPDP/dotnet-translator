using HotkeyListener.Interop;

namespace HotkeyListener.Services;

internal sealed class KeyboardInputSimulator
{
    public void SendCopyShortcut()
    {
        NativeMethods.keybd_event(NativeMethods.VK_LCONTROL, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_C, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_C, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_LCONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
