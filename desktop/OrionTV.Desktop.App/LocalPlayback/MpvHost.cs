using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OrionTV.Desktop.App.LocalPlayback;

/// <summary>
/// 为 mpv --wid 提供可嵌入的 Win32 子窗口（单窗口内嵌本地播放）。
/// </summary>
public sealed class MpvHost : HwndHost
{
    private IntPtr _hwnd = IntPtr.Zero;

    public IntPtr HostHandle => _hwnd;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        const int wsChild = 0x40000000;
        const int wsVisible = 0x10000000;
        const int wsClipSiblings = 0x04000000;
        const int wsClipChildren = 0x02000000;

        var initialSize = ToDevicePixels(
            ActualWidth > 0 ? ActualWidth : 16,
            ActualHeight > 0 ? ActualHeight : 16);
        _hwnd = CreateWindowEx(
            0,
            "Static",
            "",
            wsChild | wsVisible | wsClipSiblings | wsClipChildren,
            0,
            0,
            initialSize.Width,
            initialSize.Height,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create mpv host window.");
        }

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }

        _hwnd = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_hwnd != IntPtr.Zero)
        {
            var size = ToDevicePixels(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height);
            MoveWindow(
                _hwnd,
                0,
                0,
                size.Width,
                size.Height,
                true);
        }
    }

    private (int Width, int Height) ToDevicePixels(double width, double height)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return (
            Math.Max(1, (int)Math.Round(width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(height * dpi.DpiScaleY)));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);
}
