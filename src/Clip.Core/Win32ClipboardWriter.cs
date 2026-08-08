using System.Runtime.InteropServices;
using System.Text;

namespace Clip.Core;

/// <summary>
/// Sets clipboard text formats through raw Win32 instead of WPF's DataObject.
///
/// WPF's Clipboard.SetDataObject(copy: true) re-renders every format inside
/// OleFlushClipboard, and a failure there (buffer size mismatch on HTML/RTF,
/// transient OLE errors) is answered with Environment.FailFast — the process
/// dies and no catch block ever runs (crashed Clip 1.1.10 on 2026-08-08).
/// Rendering the bytes ourselves and handing Windows finished HGLOBALs removes
/// that path entirely: failures surface as a normal false return.
/// </summary>
public static class Win32ClipboardWriter
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public static bool TrySetText(IntPtr ownerHwnd, string text, string? html, string? rtf)
    {
        // Another app can hold the clipboard open for a moment; same retry idea as the capture path.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(ownerHwnd))
            {
                try
                {
                    return EmptyClipboard() &&
                        SetData(CfUnicodeText, UnicodeTextBytes(text)) &&
                        (html is null || SetData(RegisterClipboardFormat("HTML Format"), Utf8Bytes(html))) &&
                        (rtf is null || SetData(RegisterClipboardFormat("Rich Text Format"), AnsiBytes(rtf)));
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(20);
        }

        return false;
    }

    // CF_UNICODETEXT: UTF-16LE with a two-byte null terminator.
    internal static byte[] UnicodeTextBytes(string text)
    {
        var bytes = new byte[Encoding.Unicode.GetByteCount(text) + 2];
        Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    // "HTML Format" (CF_HTML): UTF-8 bytes, null-terminated. The stored HtmlText is the
    // full CF_HTML string as captured, header offsets included; a UTF-8 decode/encode
    // round-trip reproduces the original bytes so the offsets stay valid.
    internal static byte[] Utf8Bytes(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    // "Rich Text Format" travels as ANSI bytes and was decoded with the system ANSI code
    // page on capture, so encode it back the same way. Latin-1 keeps tests runnable on
    // hosts where the code-page tables aren't registered.
    internal static byte[] AnsiBytes(string rtf)
    {
        Encoding ansi;
        try
        {
            ansi = Encoding.GetEncoding(1252);
        }
        catch (NotSupportedException)
        {
            ansi = Encoding.Latin1;
        }

        var bytes = new byte[ansi.GetByteCount(rtf) + 1];
        ansi.GetBytes(rtf, 0, rtf.Length, bytes, 0);
        return bytes;
    }

    private static bool SetData(uint format, byte[] bytes)
    {
        if (format == 0)
        {
            return false;
        }

        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }

        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }

        // The clipboard owns the handle after a successful SetClipboardData.
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
}
