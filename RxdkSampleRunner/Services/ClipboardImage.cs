using System.Runtime.InteropServices;

namespace RxdkSampleRunner.Services;

/// <summary>
/// Puts a bitmap on the Windows clipboard as CF_DIB so it can be pasted as an image into
/// chat, docs, Paint, etc. Avalonia's IClipboard only does text/data-objects reliably, and
/// image paste across apps on Windows wants CF_DIB — hence the small Win32 shim. No-op
/// (returns false) on non-Windows.
/// </summary>
public static class ClipboardImage
{
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>
    /// Copy a BMP (raw .bmp file bytes) to the clipboard as CF_DIB. Call on the UI (STA)
    /// thread. Returns false on non-Windows or if the clipboard could not be written.
    /// </summary>
    public static bool SetBmp(byte[] bmpFileBytes)
    {
        // CF_DIB is a BMP without its 14-byte BITMAPFILEHEADER.
        if (!OperatingSystem.IsWindows() || bmpFileBytes.Length <= 14) return false;

        int dibLen = bmpFileBytes.Length - 14;
        IntPtr hMem = IntPtr.Zero;
        bool opened = false;
        try
        {
            hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)dibLen);
            if (hMem == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(hMem);
            if (p == IntPtr.Zero) return false;
            Marshal.Copy(bmpFileBytes, 14, p, dibLen);
            GlobalUnlock(hMem);

            if (!OpenClipboard(IntPtr.Zero)) return false;
            opened = true;
            EmptyClipboard();
            if (SetClipboardData(CF_DIB, hMem) == IntPtr.Zero) return false;
            hMem = IntPtr.Zero; // clipboard owns the handle now
            return true;
        }
        catch { return false; }
        finally
        {
            if (opened) CloseClipboard();
            if (hMem != IntPtr.Zero) GlobalFree(hMem);
        }
    }
}
