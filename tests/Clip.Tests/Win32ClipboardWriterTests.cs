using System.Text;
using Clip.Core;

namespace Clip.Tests;

// Encoding-only tests: TrySetText touches the real clipboard, so it is exercised
// manually via paste verification, never from the test host.
public class Win32ClipboardWriterTests
{
    [Fact]
    public void UnicodeTextBytes_AreUtf16WithNullTerminator()
    {
        var bytes = Win32ClipboardWriter.UnicodeTextBytes("Ab");

        Assert.Equal(new byte[] { 0x41, 0x00, 0x62, 0x00, 0x00, 0x00 }, bytes);
    }

    [Fact]
    public void Utf8Bytes_RoundTripCfHtmlPayload()
    {
        var html = "Version:0.9\r\nStartHTML:0000000105\r\n<html>café — “quotes”</html>";

        var bytes = Win32ClipboardWriter.Utf8Bytes(html);

        Assert.Equal(0, bytes[^1]);
        Assert.Equal(html, Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1));
    }

    [Fact]
    public void AnsiBytes_KeepAsciiRtfIntact()
    {
        var rtf = @"{\rtf1\ansi\ansicpg1252 caf\'e9}";

        var bytes = Win32ClipboardWriter.AnsiBytes(rtf);

        Assert.Equal(0, bytes[^1]);
        Assert.Equal(rtf, Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1));
    }
}
