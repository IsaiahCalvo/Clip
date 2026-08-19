using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Clip.Elevated;

/// <summary>
/// Presses Ctrl+V on Clip's behalf when the paste target is running as administrator.
///
/// Windows blocks a medium-integrity process from sending synthetic input to a higher-integrity
/// window (UIPI), which is why Clip's own SendInput silently does nothing against an elevated app.
/// The clipboard is not restricted that way — Clip has already put the text there — so the only
/// missing piece is the keystroke, and a process at high integrity can supply it.
///
/// This is deliberately the smallest thing that closes that gap:
///
/// * It accepts exactly one command, <c>paste</c>, with no arguments. It cannot be told which keys
///   to send, what to type, or which window to target. The worst an attacker who reached the pipe
///   could do is cause a Ctrl+V in whatever already had focus — no arbitrary input, no arbitrary
///   code. That bound is the reason there is no "send these keys" command, and it should stay that
///   way even when a caller would find one convenient.
/// * The pipe is ACL'd to the owning user alone, so other accounts on the machine cannot reach it.
/// * It exits on its own after <see cref="IdleTimeout"/> without work, so an elevated process is
///   not left running all day for a feature used occasionally.
/// </summary>
internal static class Program
{
    internal const string PipeName = "Clip.Elevated.Paste";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(8);

    private static int Main()
    {
        try
        {
            Serve();
            return 0;
        }
        catch (Exception ex)
        {
            // Nowhere to report to: no console, and the shell log belongs to the other process.
            // A non-zero exit is the whole signal, and Clip treats a dead helper as "unavailable"
            // and falls back to telling the user to press Ctrl+V.
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Serve()
    {
        var security = new PipeSecurity();
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("No user SID on the current token.");
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        while (true)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                security);

            using var idle = new CancellationTokenSource(IdleTimeout);
            try
            {
                pipe.WaitForConnectionAsync(idle.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Handle(pipe);
        }
    }

    private static void Handle(NamedPipeServerStream pipe)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        var command = reader.ReadLine();
        if (!string.Equals(command, "paste", StringComparison.Ordinal))
        {
            writer.WriteLine("unknown");
            return;
        }

        writer.WriteLine(SendCtrlV() ? "ok" : "failed");
    }

    /// <summary>
    /// Scan codes matter as much here as they do in Clip: an app reading raw input treats a zero
    /// scan code as no key at all, which is the failure this whole path exists to avoid.
    /// </summary>
    private static bool SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VirtualKeyControl, false),
            Key(VirtualKeyV, false),
            Key(VirtualKeyV, true),
            Key(VirtualKeyControl, true),
        };

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input Key(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = virtualKey,
                Scan = (ushort)MapVirtualKey(virtualKey, MapVirtualKeyToScanCode),
                Flags = keyUp ? KeyEventKeyUp : 0,
            },
        },
    };

    private const int InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyV = 0x56;
    private const uint MapVirtualKeyToScanCode = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
