using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Clip.Launcher.NetFx
{
    internal static class Program
    {
        private const string ShellPaletteShowEventName = @"Local\ClipShellShowPalette";
        private const uint EventModifyState = 0x0002;
        private const uint CreateNoWindow = 0x08000000;

        private static int Main(string[] args)
        {
            var show = true;
            foreach (var arg in args)
            {
                if (arg.Equals("--background", StringComparison.OrdinalIgnoreCase))
                {
                    show = false;
                    break;
                }
            }

            try
            {
                if (show && TrySignalShellPalette())
                {
                    return 0;
                }

                return StartWatcher(show);
            }
            catch
            {
                return 1;
            }
        }

        private static bool TrySignalShellPalette()
        {
            var handle = OpenEvent(EventModifyState, false, ShellPaletteShowEventName);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return SetEvent(handle);
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static int StartWatcher(bool show = false)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var watcherPath = Path.Combine(baseDirectory, "Clip.Watcher.exe");
            if (!File.Exists(watcherPath))
            {
                return 2;
            }

            var commandLine = new StringBuilder(Quote(watcherPath));
            commandLine.Append(" watch");
            if (show)
            {
                commandLine.Append(" --show");
            }

            return StartProcess(commandLine, baseDirectory);
        }

        private static int StartProcess(StringBuilder commandLine, string workingDirectory)
        {
            var startupInfo = new StartupInfo
            {
                Cb = Marshal.SizeOf(typeof(StartupInfo)),
            };

            ProcessInformation processInfo;
            return CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateNoWindow,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out processInfo)
                    ? CloseProcessHandles(processInfo)
                    : 3;
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }

        private static int CloseProcessHandles(ProcessInformation processInfo)
        {
            if (processInfo.Process != IntPtr.Zero)
            {
                CloseHandle(processInfo.Process);
            }

            if (processInfo.Thread != IntPtr.Zero)
            {
                CloseHandle(processInfo.Thread);
            }

            return 0;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenEvent(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Cb;
            public string Reserved;
            public string Desktop;
            public string Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StdInput;
            public IntPtr StdOutput;
            public IntPtr StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }
    }
}
