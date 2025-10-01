using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HotkeyListener.Services.SystemSpecificStuff.Interop;

internal sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public event EventHandler<KeyboardEvent>? KeyboardEvent;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
        {
            return;
        }

        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        var moduleHandle = currentModule != null
            ? NativeMethods.GetModuleHandle(currentModule.ModuleName)
            : IntPtr.Zero;

        _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, moduleHandle, 0);
        if (_hookId == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set keyboard hook.");
        }
    }

    public void Stop()
    {
        if (_hookId == IntPtr.Zero)
        {
            return;
        }

        if (!NativeMethods.UnhookWindowsHookEx(_hookId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to remove keyboard hook.");
        }

        _hookId = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (uint)wParam;
            if (message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_KEYUP)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
                if (data.VkCode != 0)
                {
                    var eventType = message == NativeMethods.WM_KEYDOWN
                        ? KeyboardEventType.KeyDown
                        : KeyboardEventType.KeyUp;

                    KeyboardEvent?.Invoke(this, new KeyboardEvent((Keys)data.VkCode, eventType));
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}

internal readonly record struct KeyboardEvent(Keys Key, KeyboardEventType Type);

internal enum KeyboardEventType
{
    KeyDown,
    KeyUp
}
