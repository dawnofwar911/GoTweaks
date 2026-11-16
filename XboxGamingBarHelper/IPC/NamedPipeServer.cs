using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace XboxGamingBarHelper.IPC
{
    /// <summary>
    /// Named Pipe server for IPC with the widget(s).
    /// Uses proper ACLs to allow UWP apps to connect (even when helper is elevated).
    ///
    /// Multi-client (task #12): with the desktop app and the Game Bar widget
    /// split into separate processes, BOTH connect here. Property pushes are
    /// broadcast to every client (which keeps the two UIs in sync for free);
    /// request responses are routed back to the client that sent the request
    /// via a RequestId → client map, so no call site had to change. Widget-side
    /// RequestIds are seeded per process so two clients can't collide.
    /// </summary>
    public class NamedPipeServer : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Pipe name - simple name without LOCAL\ prefix
        /// The ACLs handle UWP access permissions
        /// </summary>
        public const string PipeName = "GoTweaksHelper";

        /// <summary>
        /// Full pipe path for display/debugging
        /// </summary>
        public static readonly string FullPipePath = $"\\\\.\\pipe\\{PipeName}";

        /// <summary>
        /// Widget + desktop app + one spare for reconnect churn.
        /// </summary>
        private const int MaxClients = 4;

        private sealed class ClientConnection
        {
            public int Id;
            public NamedPipeServerStream Stream;
            public StreamReader Reader;
            public StreamWriter Writer;
            public readonly object WriteLock = new object();
        }

        private readonly List<ClientConnection> _clients = new List<ClientConnection>();
        private readonly object _clientsLock = new object();
        private int _nextClientId;

        // RequestId → client that sent it, so SendMessage can route the
        // response without any call-site changes. Bounded: entries are removed
        // when the response goes out, and the queue prunes leaks from requests
        // that never get a reply.
        private readonly ConcurrentDictionary<int, ClientConnection> _pendingRequestClients = new ConcurrentDictionary<int, ClientConnection>();
        private readonly ConcurrentQueue<int> _pendingRequestOrder = new ConcurrentQueue<int>();
        private const int MaxPendingRequestEntries = 512;

        private static readonly Regex RequestIdRegex = new Regex(@"""RequestId""\s*:\s*(\d+)", RegexOptions.Compiled);

        private CancellationTokenSource _cancellationTokenSource;
        private Task _listenerTask;
        private bool _isDisposed;

        /// <summary>
        /// Event raised when a message is received from a widget client
        /// </summary>
        public event EventHandler<PipeMessageEventArgs> MessageReceived;

        /// <summary>
        /// Event raised when a widget client connects
        /// </summary>
        public event EventHandler Connected;

        /// <summary>
        /// Event raised when a widget client disconnects
        /// </summary>
        public event EventHandler Disconnected;

        /// <summary>
        /// Whether at least one client is currently connected
        /// </summary>
        public bool IsConnected
        {
            get
            {
                lock (_clientsLock)
                {
                    return _clients.Any(c => c.Stream?.IsConnected == true);
                }
            }
        }

        /// <summary>
        /// Starts the pipe server and begins listening for connections
        /// </summary>
        public void Start()
        {
            if (_listenerTask != null)
            {
                Logger.Warn("Pipe server already started");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _listenerTask = Task.Run(() => AcceptLoop(_cancellationTokenSource.Token));
            Logger.Info($"Named pipe server started: {FullPipePath}");
        }

        /// <summary>
        /// Stops the pipe server
        /// </summary>
        public void Stop()
        {
            Logger.Info("Stopping pipe server...");
            _cancellationTokenSource?.Cancel();

            List<ClientConnection> clients;
            lock (_clientsLock)
            {
                clients = new List<ClientConnection>(_clients);
                _clients.Clear();
            }
            foreach (var client in clients)
            {
                CloseClient(client, notify: false);
            }

            try
            {
                _listenerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            _listenerTask = null;
            Logger.Info("Pipe server stopped");
        }

        /// <summary>
        /// Sends a message to widget clients. Messages carrying a RequestId we
        /// saw arrive from a specific client are routed back to that client
        /// only (request/response); everything else is broadcast to all
        /// connected clients (property pushes — keeps multiple UIs in sync).
        /// </summary>
        public bool SendMessage(string message)
        {
            // Response routing: match the RequestId back to its source client.
            var match = RequestIdRegex.Match(message);
            if (match.Success
                && int.TryParse(match.Groups[1].Value, out int requestId)
                && requestId > 0
                && _pendingRequestClients.TryRemove(requestId, out var sourceClient))
            {
                if (SendToClient(sourceClient, message))
                {
                    return true;
                }
                // Source client died before its response — nothing useful to do.
                Logger.Debug($"Response for request {requestId} dropped (client {sourceClient.Id} gone)");
                return false;
            }

            // Broadcast path.
            List<ClientConnection> clients;
            lock (_clientsLock)
            {
                clients = new List<ClientConnection>(_clients);
            }

            if (clients.Count == 0)
            {
                Logger.Debug("Cannot send message - not connected");
                return false;
            }

            bool anySent = false;
            foreach (var client in clients)
            {
                anySent |= SendToClient(client, message);
            }
            return anySent;
        }

        /// <summary>
        /// Broadcasts to every connected client EXCEPT the given one, skipping
        /// the RequestId response routing. Used to forward one client's
        /// property Set to the other client(s) so multiple UIs stay in sync —
        /// the property layer suppresses its own remote-sync on pipe-originated
        /// Sets (echo-loop prevention), so without this the second UI never
        /// hears about changes made in the first.
        /// </summary>
        public void BroadcastExcept(int excludeClientId, string message)
        {
            List<ClientConnection> clients;
            lock (_clientsLock)
            {
                clients = new List<ClientConnection>(_clients);
            }

            foreach (var client in clients)
            {
                if (client.Id == excludeClientId)
                {
                    continue;
                }
                SendToClient(client, message);
            }
        }

        private bool SendToClient(ClientConnection client, string message)
        {
            if (client?.Stream?.IsConnected != true)
            {
                return false;
            }

            try
            {
                lock (client.WriteLock)
                {
                    client.Writer?.WriteLine(message);
                    client.Writer?.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending message to client {client.Id}: {ex.Message}");
                RemoveClient(client);
                return false;
            }
        }

        /// <summary>
        /// Accept loop — keeps a listening pipe instance available and spawns a
        /// read task per connected client.
        /// </summary>
        private void AcceptLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = CreatePipeWithSecurity();
                    Logger.Info("Waiting for widget connection...");
                    pipe.WaitForConnection();

                    if (cancellationToken.IsCancellationRequested)
                    {
                        pipe.Dispose();
                        break;
                    }

                    // Only GoTweaks itself may drive the elevated helper: the widget (by
                    // package family name) or a GoTweaks helper exe from an admin-protected
                    // location. Anything else (any other process of the user) is dropped.
                    if (!PipeClientVerifier.IsTrustedClient(pipe, out string clientDescription))
                    {
                        Logger.Warn($"Rejected pipe client: {clientDescription}");
                        try { pipe.Disconnect(); } catch { }
                        try { pipe.Dispose(); } catch { }
                        continue;
                    }

                    var client = new ClientConnection
                    {
                        Id = Interlocked.Increment(ref _nextClientId),
                        Stream = pipe,
                        Reader = new StreamReader(pipe, Encoding.UTF8),
                        Writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = false },
                    };

                    int clientCount;
                    lock (_clientsLock)
                    {
                        _clients.Add(client);
                        clientCount = _clients.Count;
                    }

                    Logger.Info($"Widget connected via named pipe (client {client.Id}, {clientCount} connected, {clientDescription})");
                    Connected?.Invoke(this, EventArgs.Empty);

                    Task.Run(() => ReadMessages(client, cancellationToken));
                }
                catch (OperationCanceledException)
                {
                    try { pipe?.Dispose(); } catch { }
                    break;
                }
                catch (IOException ex)
                {
                    try { pipe?.Dispose(); } catch { }
                    if (cancellationToken.IsCancellationRequested) break;
                    // NOT just shutdown: "all pipe instances are busy" lands here when
                    // zombie clients (suspended UWP widgets whose streams never close, so
                    // ReadLine blocks forever and RemoveClient never runs) have consumed
                    // every instance. This used to be a Debug log with no sleep — a silent
                    // tight spin where the helper looked alive but no widget could ever
                    // connect again (field wedge 2026-07-27). Log it visibly, reclaim
                    // instances, and pace the loop.
                    Logger.Warn($"Pipe server cannot listen ({ex.Message}); reclaiming instances");
                    ReclaimPipeInstances();
                    Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Pipe server accept error: {ex.Message}");
                    try { pipe?.Dispose(); } catch { }
                    // Brief delay so a persistent failure can't spin the loop.
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Thread.Sleep(500);
                    }
                }
            }
            Logger.Info("Pipe server accept loop exited");
        }

        /// <summary>
        /// Frees pipe instances when a new listener can't be created: first closes clients
        /// whose stream already reads as disconnected, and if none were found while the
        /// client list is full, closes the OLDEST client — a suspended UWP widget reads as
        /// "connected" forever, while a genuinely live client just reconnects within
        /// seconds (the widget retries on disconnect). Closing the stream also unblocks
        /// that client's stuck ReadLine so its read task can exit.
        /// </summary>
        private void ReclaimPipeInstances()
        {
            List<ClientConnection> toClose = new List<ClientConnection>();
            lock (_clientsLock)
            {
                foreach (var c in _clients)
                {
                    bool connected;
                    try { connected = c.Stream?.IsConnected == true; }
                    catch { connected = false; }
                    if (!connected) toClose.Add(c);
                }
                if (toClose.Count == 0 && _clients.Count >= MaxClients - 1 && _clients.Count > 0)
                {
                    toClose.Add(_clients[0]); // oldest — most likely the zombie
                }
            }
            foreach (var c in toClose)
            {
                Logger.Warn($"Reclaiming pipe instance from client {c.Id}");
                RemoveClient(c);
            }
        }

        /// <summary>
        /// Restarts the accept loop if it exited without a shutdown being requested (an
        /// unforeseen escape from AcceptLoop would otherwise leave the helper permanently
        /// unreachable while every other subsystem keeps running). Called periodically from
        /// the helper's heartbeat path; cheap when healthy.
        /// </summary>
        public void EnsureListening()
        {
            if (_isDisposed) return;
            var task = _listenerTask;
            var cts = _cancellationTokenSource;
            if (task == null || cts == null || cts.IsCancellationRequested) return;
            if (!task.IsCompleted) return;

            Logger.Error("Pipe server listener task exited unexpectedly; restarting accept loop");
            _listenerTask = Task.Run(() => AcceptLoop(cts.Token));
        }

        /// <summary>
        /// Creates a named pipe server instance with security that allows UWP apps to connect
        /// </summary>
        private NamedPipeServerStream CreatePipeWithSecurity()
        {
            // Security:
            // 1. Current user: full control (the widget's token carries the user SID, and
            //    the helper's own instance probe connects as the user).
            // 2. ONLY the GoTweaks package's AppContainer SID (derived from its package family
            //    name) instead of ALL APPLICATION PACKAGES / ALL RESTRICTED APPLICATION
            //    PACKAGES, which let ANY installed packaged app talk to this elevated helper.
            // Every connection is additionally verified (PipeClientVerifier) because the user
            // ACE alone would still admit any medium-integrity process of the user.
            var pipeSecurity = new PipeSecurity();

            pipeSecurity.AddAccessRule(new PipeAccessRule(
                WindowsIdentity.GetCurrent().User,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            var packageSid = PipeClientVerifier.GetWidgetPackageSid();
            bool escapeHatch = PipeClientVerifier.VerificationDisabledByAdmin();
            if (packageSid != null)
            {
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    packageSid,
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
            }
            if (packageSid == null || escapeHatch)
            {
                // Could not derive the package SID, or an admin set the HKLM escape hatch
                // (see PipeClientVerifier): grant ALL APPLICATION PACKAGES like before the
                // hardening so the widget can't be locked out by the narrower ACL.
                Logger.Warn(packageSid == null
                    ? "Pipe ACL: could not derive the GoTweaks package SID; falling back to ALL APPLICATION PACKAGES (client verification still enforced)"
                    : "Pipe ACL: HKLM DisablePipeClientVerification=1; also granting ALL APPLICATION PACKAGES");
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier("S-1-15-2-1"),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
            }

            return new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                MaxClients,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096, // inBufferSize
                4096, // outBufferSize
                pipeSecurity);
        }

        /// <summary>
        /// Reads messages from one connected client
        /// </summary>
        private void ReadMessages(ClientConnection client, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && client.Stream.IsConnected)
                {
                    var line = client.Reader.ReadLine();
                    if (line == null)
                    {
                        // Client disconnected
                        Logger.Info($"Widget client {client.Id} disconnected (end of stream)");
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Logger.Debug($"Received from client {client.Id}: {line.Substring(0, Math.Min(100, line.Length))}...");
                        TrackRequestSource(client, line);
                        MessageReceived?.Invoke(this, new PipeMessageEventArgs(line, client.Id));
                    }
                }
            }
            catch (IOException)
            {
                // Normal disconnection
            }
            catch (ObjectDisposedException)
            {
                // Closed during shutdown
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe client {client.Id} read error: {ex.Message}");
            }
            finally
            {
                RemoveClient(client);
            }
        }

        /// <summary>
        /// Remembers which client a RequestId came from so SendMessage can
        /// route the response. Bounded against requests that never get replies.
        /// </summary>
        private void TrackRequestSource(ClientConnection client, string message)
        {
            var match = RequestIdRegex.Match(message);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int requestId) || requestId <= 0)
            {
                return;
            }

            _pendingRequestClients[requestId] = client;
            _pendingRequestOrder.Enqueue(requestId);
            while (_pendingRequestOrder.Count > MaxPendingRequestEntries && _pendingRequestOrder.TryDequeue(out int stale))
            {
                // Only remove if it's still the stale entry (a response may have
                // already removed it; an id reuse would have re-added it).
                _pendingRequestClients.TryRemove(stale, out _);
            }
        }

        private void RemoveClient(ClientConnection client)
        {
            bool removed;
            lock (_clientsLock)
            {
                removed = _clients.Remove(client);
            }

            if (!removed)
            {
                return;
            }

            CloseClient(client, notify: true);
        }

        private void CloseClient(ClientConnection client, bool notify)
        {
            Logger.Info($"Widget client {client.Id} disconnected");

            try { client.Reader?.Dispose(); } catch { }
            try { client.Writer?.Dispose(); } catch { }
            try { client.Stream?.Dispose(); } catch { }

            if (notify)
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Stop();
            _cancellationTokenSource?.Dispose();
        }
    }

    /// <summary>
    /// Event args for pipe messages
    /// </summary>
    public class PipeMessageEventArgs : EventArgs
    {
        public string Message { get; }

        /// <summary>
        /// Id of the pipe client the message arrived from (0 = unknown). Lets
        /// handlers forward Sets to the OTHER clients for multi-UI sync.
        /// </summary>
        public int SourceClientId { get; }

        public PipeMessageEventArgs(string message, int sourceClientId = 0)
        {
            Message = message;
            SourceClientId = sourceClientId;
        }
    }

    /// <summary>
    /// Decides which processes may talk to the elevated helper over the pipe, and derives
    /// the GoTweaks package SID used in the pipe ACL.
    /// Trusted: (a) a process whose package family name is GoTweaks' (the widget, or the
    /// helper launched from the package); (b) XboxGamingBarHelper.exe running from an
    /// admin-protected GoTweaks location (the deployed helper folder under Program Files,
    /// or the package's WindowsApps folder); (c) this process.
    /// Escape hatch for diagnosing lockouts (admin-only to set):
    /// HKLM\SOFTWARE\GoTweaks\DisablePipeClientVerification = 1 (DWORD). It admits every
    /// client again (checked on each connection, no restart needed) AND widens the pipe ACL
    /// back to ALL APPLICATION PACKAGES (applied when a pipe instance is created, so an ACL
    /// lockout needs a helper restart, e.g. sign out/in or restart the scheduled task).
    /// </summary>
    internal static class PipeClientVerifier
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Same family name the rest of the helper uses for LocalState paths
        // (Identity Name="PlayandBuildCustom.10365195AA1EC" Publisher="CN=dg").
        internal const string FallbackPackageFamilyName = Services.HelperDeploymentService.FallbackPackageFamilyName;
        private const string HelperExeName = "XboxGamingBarHelper.exe";

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ERROR_SUCCESS = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackageFamilyName(IntPtr process, ref uint packageFamilyNameLength, StringBuilder packageFamilyName);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int DeriveAppContainerSidFromAppContainerName(string appContainerName, out IntPtr appContainerSid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr FreeSid(IntPtr sid);

        private static string cachedFamilyName;
        private static bool loggedSid;

        internal static string GetOwnPackageFamilyName()
        {
            if (cachedFamilyName != null) return cachedFamilyName;
            string name = null;
            try { name = global::Windows.ApplicationModel.Package.Current.Id.FamilyName; }
            catch { name = null; }
            cachedFamilyName = string.IsNullOrEmpty(name) ? FallbackPackageFamilyName : name;
            return cachedFamilyName;
        }

        /// <summary>The widget package's AppContainer SID, or null if it can't be derived.</summary>
        internal static SecurityIdentifier GetWidgetPackageSid()
        {
            IntPtr sidPtr = IntPtr.Zero;
            try
            {
                int hr = DeriveAppContainerSidFromAppContainerName(GetOwnPackageFamilyName().ToLowerInvariant(), out sidPtr);
                if (hr != 0 || sidPtr == IntPtr.Zero)
                {
                    Logger.Warn($"DeriveAppContainerSidFromAppContainerName failed (hr=0x{hr:X8})");
                    return null;
                }
                var sid = new SecurityIdentifier(sidPtr);
                if (!loggedSid)
                {
                    loggedSid = true;
                    Logger.Info($"Pipe ACL: GoTweaks package SID {sid.Value} ({GetOwnPackageFamilyName()})");
                }
                return sid;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not derive GoTweaks package SID: {ex.Message}");
                return null;
            }
            finally
            {
                if (sidPtr != IntPtr.Zero) { try { FreeSid(sidPtr); } catch { } }
            }
        }

        internal static bool VerificationDisabledByAdmin()
        {
            try
            {
                // Always the 64-bit view, so an x86 build reads the same key the docs name.
                using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\GoTweaks"))
                {
                    object v = key?.GetValue("DisablePipeClientVerification");
                    return v is int i && i == 1;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsTrustedClient(NamedPipeServerStream pipe, out string description)
        {
            description = "unknown client";
            uint pid;
            try
            {
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid))
                {
                    description = $"GetNamedPipeClientProcessId failed (err {Marshal.GetLastWin32Error()})";
                    return VerificationDisabledByAdmin();
                }
            }
            catch (Exception ex)
            {
                description = $"client PID lookup threw: {ex.Message}";
                return VerificationDisabledByAdmin();
            }

            if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
            {
                description = $"pid {pid} (self)";
                return true;
            }

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                description = $"pid {pid}: OpenProcess failed (err {Marshal.GetLastWin32Error()})";
                return VerificationDisabledByAdmin();
            }

            try
            {
                string familyName = null;
                try
                {
                    uint len = 256;
                    var sb = new StringBuilder((int)len);
                    if (GetPackageFamilyName(hProcess, ref len, sb) == ERROR_SUCCESS)
                    {
                        familyName = sb.ToString();
                    }
                }
                catch { familyName = null; }

                string imagePath = null;
                try
                {
                    int size = 1024;
                    var sb = new StringBuilder(size);
                    if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                    {
                        imagePath = sb.ToString(0, size);
                    }
                }
                catch { imagePath = null; }

                description = $"pid {pid}, package={familyName ?? "(none)"}, image={imagePath ?? "?"}";

                if (!string.IsNullOrEmpty(familyName)
                    && string.Equals(familyName, GetOwnPackageFamilyName(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(imagePath) && IsTrustedHelperImage(imagePath))
                {
                    return true;
                }

                if (VerificationDisabledByAdmin())
                {
                    description += " (allowed: verification disabled by HKLM policy)";
                    return true;
                }
                return false;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        private static bool IsTrustedHelperImage(string imagePath)
        {
            try
            {
                string fullPath = Path.GetFullPath(imagePath);
                if (!string.Equals(Path.GetFileName(fullPath), HelperExeName, StringComparison.OrdinalIgnoreCase))
                    return false;
                string dir = Path.GetDirectoryName(fullPath) ?? "";

                // Deployed helper in its admin-only folder.
                string deployed = Path.GetFullPath(Services.HelperDeploymentService.HelperFolder).TrimEnd('\\');
                if (Services.HelperDeploymentService.IsHelperFolderAdminProtected
                    && string.Equals(dir.TrimEnd('\\'), deployed, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Helper inside the installed package (WindowsApps is admin/TrustedInstaller-owned).
                if (Services.HelperDeploymentService.IsTrustedPackageSourceDirectory(dir))
                    return true;
            }
            catch
            {
            }
            return false;
        }
    }
}
