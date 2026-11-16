using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NLog;

namespace XboxGamingBarHelper.Services
{
    /// <summary>
    /// Starts processes as the interactive user WITHOUT the helper's elevation, by borrowing
    /// the token of the desktop shell (explorer.exe, found via GetShellWindow) and calling
    /// CreateProcessWithTokenW. Used for anything the user, a setting or a pipe client
    /// chooses (URLs, Labs "run command" remaps, installers we could not verify), so none of
    /// it ever runs at High IL. ShellExecute from the elevated helper is avoided on purpose:
    /// its file-type and protocol handlers come partly from HKCU\Software\Classes, which any
    /// medium-integrity process can rewrite.
    /// If the helper is not elevated, the process is started normally.
    /// If no shell token is available, nothing is started (never falls back to elevated).
    /// </summary>
    internal static class UnelevatedLauncher
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const int TokenElevation = 20;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        private const uint WAIT_OBJECT_0 = 0;

        /// <summary>Outcome of <see cref="Start"/>.</summary>
        internal sealed class LaunchResult
        {
            public bool Started;
            public int ProcessId;
            /// <summary>True when a wait was requested and the process exited within it.</summary>
            public bool Exited;
            public int ExitCode;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
            int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            out int tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string applicationName,
            string commandLine, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        /// <summary>
        /// Opens <paramref name="target"/> (an http/https URL, a folder or a document) the way
        /// Explorer would, as the non-elevated user: runs "explorer.exe &lt;target&gt;", which hands
        /// the request to the running shell. Callers validate the target first.
        /// </summary>
        public static bool ShellOpen(string target)
        {
            if (string.IsNullOrEmpty(target) || target.IndexOf('"') >= 0)
            {
                return false;
            }
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            return Start(explorer, "\"" + explorer + "\" \"" + target + "\"", hideWindow: false).Started;
        }

        /// <summary>
        /// Runs <paramref name="command"/> through "cmd.exe /d /c start "" &lt;command&gt;" as the
        /// non-elevated user, so it gets ShellExecute semantics (PATH lookup, documents,
        /// shortcuts, a visible UAC prompt for programs that require admin) without ever
        /// inheriting the helper's elevation. The command is the user's own (it runs with the
        /// user's own rights), so cmd parsing of it is not a privilege boundary.
        /// </summary>
        public static bool ShellExecuteCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command) || command.IndexOf('\r') >= 0 || command.IndexOf('\n') >= 0)
            {
                return false;
            }
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            return Start(cmd, "\"" + cmd + "\" /d /c start \"\" " + command, hideWindow: true).Started;
        }

        /// <summary>
        /// Starts <paramref name="applicationPath"/> with <paramref name="commandLine"/> (which
        /// must include argv[0]) as the non-elevated interactive user, optionally waiting up to
        /// <paramref name="waitMilliseconds"/> for it to exit. <paramref name="onStarted"/> is
        /// called once the process exists, before any wait.
        /// </summary>
        public static LaunchResult Start(string applicationPath, string commandLine, bool hideWindow,
            int waitMilliseconds = 0, Action onStarted = null)
        {
            var result = new LaunchResult();
            if (!ElevationBootstrapper.IsRunningAsAdmin())
            {
                return StartDirect(applicationPath, commandLine, hideWindow, waitMilliseconds, onStarted);
            }

            IntPtr shellProcess = IntPtr.Zero;
            IntPtr shellToken = IntPtr.Zero;
            IntPtr primaryToken = IntPtr.Zero;
            IntPtr environment = IntPtr.Zero;
            try
            {
                IntPtr shellWindow = GetShellWindow();
                if (shellWindow == IntPtr.Zero)
                {
                    Logger.Warn("UnelevatedLauncher: no shell window (Explorer not running); not starting anything");
                    return result;
                }
                GetWindowThreadProcessId(shellWindow, out uint shellPid);
                if (shellPid == 0)
                {
                    Logger.Warn("UnelevatedLauncher: could not resolve the shell process");
                    return result;
                }

                shellProcess = OpenProcess(PROCESS_QUERY_INFORMATION, false, shellPid);
                if (shellProcess == IntPtr.Zero)
                {
                    Logger.Warn($"UnelevatedLauncher: OpenProcess(shell {shellPid}) failed: {Marshal.GetLastWin32Error()}");
                    return result;
                }
                if (!OpenProcessToken(shellProcess, TOKEN_DUPLICATE | TOKEN_QUERY, out shellToken))
                {
                    Logger.Warn($"UnelevatedLauncher: OpenProcessToken failed: {Marshal.GetLastWin32Error()}");
                    return result;
                }

                // Refuse an elevated shell (e.g. Explorer started as admin): borrowing its
                // token would defeat the purpose.
                if (GetTokenInformation(shellToken, TokenElevation, out int elevated, sizeof(int), out _) && elevated != 0)
                {
                    Logger.Warn("UnelevatedLauncher: the shell is running elevated; not starting anything");
                    return result;
                }

                if (!DuplicateTokenEx(shellToken,
                        TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID,
                        IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
                {
                    Logger.Warn($"UnelevatedLauncher: DuplicateTokenEx failed: {Marshal.GetLastWin32Error()}");
                    return result;
                }

                uint flags = 0;
                if (CreateEnvironmentBlock(out environment, primaryToken, false))
                {
                    flags |= CREATE_UNICODE_ENVIRONMENT;
                }
                else
                {
                    environment = IntPtr.Zero;
                }
                if (hideWindow)
                {
                    flags |= CREATE_NO_WINDOW;
                }

                var startupInfo = new STARTUPINFO();
                startupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                startupInfo.lpDesktop = "winsta0\\default";

                string workingDirectory = Environment.SystemDirectory;
                if (!CreateProcessWithTokenW(primaryToken, 0, applicationPath, commandLine, flags, environment,
                        workingDirectory, ref startupInfo, out PROCESS_INFORMATION pi))
                {
                    Logger.Warn($"UnelevatedLauncher: CreateProcessWithTokenW({applicationPath}) failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                    return result;
                }

                try
                {
                    Logger.Info($"UnelevatedLauncher: started {Path.GetFileName(applicationPath)} as the non-elevated user (PID {pi.dwProcessId})");
                    result.Started = true;
                    result.ProcessId = pi.dwProcessId;
                    try { onStarted?.Invoke(); } catch { }
                    if (waitMilliseconds > 0
                        && WaitForSingleObject(pi.hProcess, (uint)waitMilliseconds) == WAIT_OBJECT_0
                        && GetExitCodeProcess(pi.hProcess, out uint exitCode))
                    {
                        result.Exited = true;
                        result.ExitCode = unchecked((int)exitCode);
                    }
                    return result;
                }
                finally
                {
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"UnelevatedLauncher: {ex.Message}");
                return result;
            }
            finally
            {
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
                if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
                if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
                if (shellProcess != IntPtr.Zero) CloseHandle(shellProcess);
            }
        }

        private static LaunchResult StartDirect(string applicationPath, string commandLine, bool hideWindow, int waitMilliseconds, Action onStarted)
        {
            var result = new LaunchResult();
            try
            {
                // Split argv[0] off the command line; the rest is passed through verbatim.
                string arguments = commandLine ?? "";
                string quotedApp = "\"" + applicationPath + "\"";
                if (arguments.StartsWith(quotedApp, StringComparison.OrdinalIgnoreCase))
                {
                    arguments = arguments.Substring(quotedApp.Length).TrimStart();
                }
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = applicationPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = hideWindow,
                    WorkingDirectory = Environment.SystemDirectory,
                }))
                {
                    if (process == null) return result;
                    result.Started = true;
                    result.ProcessId = process.Id;
                    try { onStarted?.Invoke(); } catch { }
                    if (waitMilliseconds > 0 && process.WaitForExit(waitMilliseconds))
                    {
                        result.Exited = true;
                        result.ExitCode = process.ExitCode;
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn($"UnelevatedLauncher: direct start of {applicationPath} failed: {ex.Message}");
                return result;
            }
        }
    }
}
