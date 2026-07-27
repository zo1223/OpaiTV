using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace OrionTV.Desktop.App;

/// <summary>
/// Captures player shortcuts even when keyboard focus belongs to an embedded native video HWND
/// (LibVLC or the out-of-process mpv child window).
/// </summary>
internal sealed class PlayerKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int GaRoot = 2;
    private const int GaRootOwner = 3;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private readonly IntPtr _ownerHandle;
    private readonly Func<Key, ModifierKeys, bool> _handler;
    private readonly HookProc _hookProc;
    private IntPtr _hookHandle;

    private PlayerKeyboardHook(IntPtr ownerHandle, Func<Key, ModifierKeys, bool> handler)
    {
        _ownerHandle = ownerHandle;
        _handler = handler;
        _hookProc = HookCallback;
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
    }

    internal static PlayerKeyboardHook? TryCreate(
        Window owner,
        Func<Key, ModifierKeys, bool> handler)
    {
        var ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
        if (ownerHandle == IntPtr.Zero)
        {
            return null;
        }

        var hook = new PlayerKeyboardHook(ownerHandle, handler);
        if (hook._hookHandle != IntPtr.Zero)
        {
            return hook;
        }

        hook.Dispose();
        return null;
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0
            && message.ToInt32() == WmKeyDown
            && IsOwnerForeground())
        {
            var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(data);
            var key = KeyInterop.KeyFromVirtualKey((int)keyboard.VirtualKey);
            try
            {
                if (_handler(key, ReadModifiers()))
                {
                    return new IntPtr(1);
                }
            }
            catch
            {
                // A shortcut failure must never break the process-wide keyboard hook chain.
            }
        }

        return CallNextHookEx(_hookHandle, code, message, data);
    }

    private bool IsOwnerForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        return GetAncestor(foreground, GaRoot) == _ownerHandle
            || GetAncestor(foreground, GaRootOwner) == _ownerHandle;
    }

    private static ModifierKeys ReadModifiers()
    {
        var modifiers = ModifierKeys.None;
        if (IsKeyDown(VkShift)) modifiers |= ModifierKeys.Shift;
        if (IsKeyDown(VkControl)) modifiers |= ModifierKeys.Control;
        if (IsKeyDown(VkMenu)) modifiers |= ModifierKeys.Alt;
        if (IsKeyDown(VkLWin) || IsKeyDown(VkRWin)) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _hookHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KbdLlHookStruct
    {
        internal readonly uint VirtualKey;
        internal readonly uint ScanCode;
        internal readonly uint Flags;
        internal readonly uint Time;
        internal readonly UIntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        HookProc hookProc,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr message,
        IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, int flags);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
