using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace XboxGamingBarHelper.Services
{
    /// <summary>
    /// Download-verify-run for the third-party installers the elevated helper launches
    /// (usbip-win2, HidHide, PawnIO, Lenovo drivers).
    ///
    /// - Downloads only over https from an allow-listed host (exact host or "." + domain
    ///   suffix), re-checked after redirects.
    /// - Writes into %ProgramData%\GoTweaks\Downloads\&lt;random&gt;\ (Administrators/SYSTEM
    ///   write, Users read only), never the user's %TEMP% under a predictable name.
    /// - Opens the file deny-write/deny-delete and keeps that handle open while verifying
    ///   and running it, so nothing can swap it in between.
    /// - Verified = pinned SHA-256 matches, or (no pin) a valid Authenticode signature
    ///   (WinVerifyTrust) whose leaf signer CN/O is one of the expected publishers. Only a
    ///   verified file is run silently with the helper's elevation (CreateProcess, or
    ///   System32\msiexec for .msi; never ShellExecute, whose handlers can come from HKCU).
    /// - Unverified (unsigned, other publisher, untrusted root, revocation offline) is run
    ///   as the non-elevated user through the shell, so Windows shows the normal UAC prompt
    ///   with the publisher. Rejected (pin mismatch, tampered, revoked or distrusted
    ///   signature) is not run at all.
    /// </summary>
    internal static class SecureInstaller
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        internal enum Verdict
        {
            Verified,
            Unverified,
            Rejected,
        }

        /// <summary>What a download must match to be run silently elevated.</summary>
        internal sealed class Expectation
        {
            /// <summary>Pinned SHA-256 (hex). When set, a mismatch is Rejected.</summary>
            public string Sha256;

            /// <summary>Accepted leaf-signer CN or O values (exact, case-insensitive).</summary>
            public string[] SignerNames = new string[0];
        }

        /// <summary>A downloaded file held open deny-write until disposed.</summary>
        internal sealed class HeldFile : IDisposable
        {
            internal FileStream Stream;
            public string Path { get; internal set; }
            public string Folder { get; internal set; }
            public Verdict Verdict { get; internal set; }
            public string Reason { get; internal set; }

            public void Dispose()
            {
                try { Stream?.Dispose(); } catch { }
                Stream = null;
                try
                {
                    if (!string.IsNullOrEmpty(Folder) && Directory.Exists(Folder))
                    {
                        Directory.Delete(Folder, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"SecureInstaller: could not delete {Folder}: {ex.Message}");
                }
            }
        }

        /// <summary>Outcome of <see cref="Run"/>.</summary>
        internal sealed class RunResult
        {
            public bool Started;
            public bool Elevated;
            public bool Exited;
            public int ExitCode;
            public string Message = "";
        }

        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            }
            catch { }
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.Add("User-Agent", "GoTweaks/1.0");
            return client;
        }

        /// <summary>
        /// True when <paramref name="host"/> equals one of <paramref name="allowedDomains"/> or
        /// ends with "." + one of them (so evillenovo.com does not match lenovo.com).
        /// </summary>
        public static bool HostMatches(string host, params string[] allowedDomains)
        {
            if (string.IsNullOrEmpty(host) || allowedDomains == null) return false;
            host = host.TrimEnd('.').ToLowerInvariant();
            foreach (var domain in allowedDomains)
            {
                string d = (domain ?? "").ToLowerInvariant();
                if (d.Length == 0) continue;
                if (host == d || host.EndsWith("." + d, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Downloads <paramref name="uri"/> into a fresh admin-only folder and returns the file
        /// held open deny-write, with its verdict. <paramref name="isAllowedHost"/> is checked
        /// for the requested URL and again for the final URL after redirects. Throws on
        /// network / policy errors.
        /// </summary>
        public static async Task<HeldFile> DownloadAndVerifyAsync(Uri uri, Func<string, bool> isAllowedHost,
            string fileName, Expectation expectation, TimeSpan timeout)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Only absolute https URLs can be downloaded");
            if (!string.IsNullOrEmpty(uri.UserInfo) || isAllowedHost == null || !isAllowedHost(uri.Host))
                throw new InvalidOperationException("Host not allowed: " + uri.Host);

            string safeName = SanitizeFileName(fileName, "installer.exe");
            string folder = CreateDownloadFolder();
            string target = System.IO.Path.Combine(folder, safeName);
            try
            {
                using (var cts = new CancellationTokenSource(timeout))
                using (var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    Uri finalUri = response.RequestMessage?.RequestUri ?? uri;
                    if (finalUri.Scheme != Uri.UriSchemeHttps || !isAllowedHost(finalUri.Host))
                        throw new InvalidOperationException("Redirected to a host that is not allowed: " + finalUri.Host);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("HTTP " + (int)response.StatusCode);

                    using (var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await src.CopyToAsync(dst, 81920, cts.Token).ConfigureAwait(false);
                    }
                }
                return OpenAndVerify(target, folder, expectation);
            }
            catch
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Opens <paramref name="path"/> deny-write/deny-delete and decides its verdict. The
        /// returned handle must stay open until the installer has been started (and ideally
        /// has exited); disposing it deletes <paramref name="folderToDelete"/>.
        /// </summary>
        public static HeldFile OpenAndVerify(string path, string folderToDelete, Expectation expectation)
        {
            var held = new HeldFile { Path = path, Folder = folderToDelete };
            held.Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                string sha256;
                using (var sha = SHA256.Create())
                {
                    held.Stream.Position = 0;
                    sha256 = BitConverter.ToString(sha.ComputeHash(held.Stream)).Replace("-", "").ToLowerInvariant();
                    held.Stream.Position = 0;
                }

                int trust = VerifyAuthenticode(path, held.Stream.SafeFileHandle.DangerousGetHandle());
                string signer = GetSignerDescription(path, out string[] signerNames);
                Logger.Info($"SecureInstaller: {System.IO.Path.GetFileName(path)} sha256={sha256} WinVerifyTrust=0x{trust:X8} signer={signer}");

                if (!string.IsNullOrEmpty(expectation?.Sha256))
                {
                    if (string.Equals(sha256, expectation.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        held.Verdict = Verdict.Verified;
                        held.Reason = "pinned SHA-256 matches";
                    }
                    else
                    {
                        held.Verdict = Verdict.Rejected;
                        held.Reason = $"SHA-256 mismatch (expected {expectation.Sha256})";
                    }
                    return held;
                }

                if (IsTamperedOrDistrusted(trust))
                {
                    held.Verdict = Verdict.Rejected;
                    held.Reason = $"signature invalid (0x{trust:X8})";
                }
                else if (trust == 0
                    && expectation?.SignerNames != null
                    && signerNames.Any(n => expectation.SignerNames.Any(e => string.Equals(n, e, StringComparison.OrdinalIgnoreCase))))
                {
                    held.Verdict = Verdict.Verified;
                    held.Reason = "valid Authenticode signature by " + signer;
                }
                else
                {
                    held.Verdict = Verdict.Unverified;
                    held.Reason = trust == 0
                        ? "signed by an unexpected publisher: " + signer
                        : $"signature not verifiable (0x{trust:X8})";
                }
                return held;
            }
            catch
            {
                held.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Runs a held, verified file. Verified: silently with the helper's elevation (waits up
        /// to <paramref name="waitMilliseconds"/>). Unverified: as the non-elevated user via the
        /// shell, so Windows shows its UAC prompt (not waited for). Rejected: not run.
        /// </summary>
        public static RunResult Run(HeldFile file, string arguments, int waitMilliseconds)
        {
            var result = new RunResult();
            if (file == null || file.Stream == null)
            {
                result.Message = "No file";
                return result;
            }
            if ((arguments ?? "").IndexOfAny(new[] { '"', '&', '|', '<', '>', '^', '%', '\r', '\n' }) >= 0)
            {
                result.Message = "Unsupported installer arguments";
                return result;
            }

            string ext = (System.IO.Path.GetExtension(file.Path) ?? "").ToLowerInvariant();
            if (ext != ".exe" && ext != ".msi")
            {
                result.Message = "Not an installer (.exe/.msi)";
                return result;
            }

            if (file.Verdict == Verdict.Rejected)
            {
                Logger.Warn($"SecureInstaller: NOT running {file.Path}: {file.Reason}");
                result.Message = "Installer failed verification: " + file.Reason;
                return result;
            }

            bool elevated = ElevationBootstrapper.IsRunningAsAdmin();
            if (file.Verdict == Verdict.Verified && elevated)
            {
                string system32 = Environment.SystemDirectory;
                var psi = ext == ".msi"
                    ? new ProcessStartInfo
                    {
                        FileName = System.IO.Path.Combine(system32, "msiexec.exe"),
                        Arguments = "/i \"" + file.Path + "\" " + (arguments ?? ""),
                    }
                    : new ProcessStartInfo
                    {
                        FileName = file.Path,
                        Arguments = arguments ?? "",
                    };
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = system32;

                Logger.Info($"SecureInstaller: running verified {System.IO.Path.GetFileName(file.Path)} elevated ({file.Reason})");
                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        result.Message = "Installer failed to start";
                        return result;
                    }
                    result.Started = true;
                    result.Elevated = true;
                    if (waitMilliseconds > 0)
                    {
                        if (process.WaitForExit(waitMilliseconds))
                        {
                            result.Exited = true;
                            result.ExitCode = process.ExitCode;
                        }
                        else
                        {
                            Logger.Warn($"SecureInstaller: {System.IO.Path.GetFileName(file.Path)} still running after {waitMilliseconds} ms");
                        }
                    }
                }
                result.Message = "Installer launched.";
                return result;
            }

            // Unverified (or the helper isn't elevated): let Windows ask the user. "start"
            // ShellExecutes as the non-elevated user, which raises the normal UAC prompt
            // for an installer that needs admin.
            Logger.Warn($"SecureInstaller: {System.IO.Path.GetFileName(file.Path)} is {file.Verdict} ({file.Reason}); launching through a visible UAC prompt instead of silently elevated");
            string command = "\"" + file.Path + "\"" + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments);
            result.Started = UnelevatedLauncher.ShellExecuteCommand(command);
            result.Message = result.Started
                ? "Installer could not be verified; Windows will ask for permission."
                : "Installer could not be verified and could not be launched for confirmation.";
            return result;
        }

        /// <summary>
        /// Keeps the file held open (deny-write) for <paramref name="hold"/> after an
        /// un-awaited launch, then releases it and deletes its folder.
        /// </summary>
        public static void ReleaseLater(HeldFile file, TimeSpan hold)
        {
            if (file == null) return;
            Task.Run(async () =>
            {
                try { await Task.Delay(hold).ConfigureAwait(false); }
                finally { file.Dispose(); }
            });
        }

        /// <summary>
        /// A new random folder under the admin-only %ProgramData%\GoTweaks\Downloads (Users can
        /// read, so a non-elevated UAC launch can open the file). Older leftovers are swept.
        /// When the helper isn't elevated it can't create admin-only folders; it then uses a
        /// random %TEMP% folder and everything it downloads is run through UAC anyway.
        /// </summary>
        public static string CreateDownloadFolder()
        {
            string root;
            if (ElevationBootstrapper.IsRunningAsAdmin())
            {
                root = HelperDeploymentService.EnsureAdminOnlyDirectory("Downloads", usersCanRead: true);
                SweepOldDownloads(root);
            }
            else
            {
                root = System.IO.Path.GetTempPath();
            }
            string folder = System.IO.Path.Combine(root, "GoTweaks_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void SweepOldDownloads(string root)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root, "GoTweaks_*"))
                {
                    try
                    {
                        if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > TimeSpan.FromDays(1))
                        {
                            Directory.Delete(dir, recursive: true);
                        }
                    }
                    catch { /* in use: next time */ }
                }
            }
            catch { }
        }

        internal static string SanitizeFileName(string fileName, string fallback)
        {
            string name = string.IsNullOrWhiteSpace(fileName) ? fallback : System.IO.Path.GetFileName(fileName.Trim());
            foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
            {
                name = name.Replace(bad, '_');
            }
            name = name.Replace('"', '_').Replace('%', '_').Replace('&', '_').Replace('^', '_');
            return string.IsNullOrWhiteSpace(name) || name.Trim('.').Length == 0 ? fallback : name;
        }

        // ---------------------------------------------------------------- Authenticode

        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_WHOLECHAIN = 1;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x80;

        // Results that mean "this file or its signature is bad", as opposed to "can't tell".
        private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
        private const int TRUST_E_CERT_SIGNATURE = unchecked((int)0x80096004);
        private const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
        private const int CERT_E_REVOKED = unchecked((int)0x800B010C);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

        private static bool IsTamperedOrDistrusted(int trust)
        {
            return trust == TRUST_E_BAD_DIGEST
                || trust == TRUST_E_CERT_SIGNATURE
                || trust == TRUST_E_EXPLICIT_DISTRUST
                || trust == CERT_E_REVOKED;
        }

        /// <summary>WinVerifyTrust (Authenticode, whole-chain revocation) on the held handle; 0 = trusted.</summary>
        private static int VerifyAuthenticode(string path, IntPtr fileHandle)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                pcwszFilePath = path,
                hFile = fileHandle,
                pgKnownSubject = IntPtr.Zero,
            };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT,
            };
            try
            {
                return WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);
            }
            catch (Exception ex)
            {
                Logger.Warn($"WinVerifyTrust threw: {ex.Message}");
                return unchecked((int)0x800B0100); // treat as unsigned (unverifiable)
            }
            finally
            {
                try
                {
                    data.dwStateAction = WTD_STATEACTION_CLOSE;
                    WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);
                }
                catch { }
                Marshal.DestroyStructure(pFile, typeof(WINTRUST_FILE_INFO));
                Marshal.FreeHGlobal(pFile);
            }
        }

        /// <summary>
        /// Leaf signer of the file's (first) Authenticode signature: returns a display string
        /// and the CN and O values in <paramref name="names"/> (empty when unsigned).
        /// </summary>
        private static string GetSignerDescription(string path, out string[] names)
        {
            names = new string[0];
            try
            {
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                {
                    var values = new System.Collections.Generic.List<string>();
                    string cn = cert.GetNameInfo(X509NameType.SimpleName, false);
                    if (!string.IsNullOrEmpty(cn)) values.Add(cn.Trim());
                    string decoded = cert.SubjectName.Decode(X500DistinguishedNameFlags.UseNewLines) ?? "";
                    foreach (var line in decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string l = line.Trim();
                        if (l.StartsWith("O=", StringComparison.OrdinalIgnoreCase) || l.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                        {
                            string v = l.Substring(l.IndexOf('=') + 1).Trim().Trim('"');
                            if (v.Length > 0 && !values.Contains(v)) values.Add(v);
                        }
                    }
                    names = values.ToArray();
                    return cert.Subject;
                }
            }
            catch (Exception ex)
            {
                return "(none: " + ex.Message + ")";
            }
        }
    }
}
