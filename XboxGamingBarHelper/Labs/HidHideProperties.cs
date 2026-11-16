using NLog;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using XboxGamingBarHelper.ControllerEmulation;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Labs
{
    /// <summary>
    /// Utility class for HidHide detection and installation.
    /// </summary>
    internal static class HidHideHelper
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const string PrimaryHidHideWingetId = "Nefarius.HidHide";
        private const string FallbackHidHideWingetId = "NefariusSoftwareSolutions.HidHide";
        private const string HidHideDirectDownloadUrl = "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe";
        // SHA-256 of that exact release asset (fetched 2026-11-16; Authenticode signer
        // "Nefarius Software Solutions e.U."). Bump together with the URL.
        private const string HidHideDirectDownloadSha256 = "f4bbbcb82e6258641b887c74bc81c4c5f66e4aa811808dfc304347687b7605f6";

        public static bool IsInstalled()
        {
            string cliPath = ControllerSuppressionManager.GetDetectedCliPath();
            bool installed = !string.IsNullOrEmpty(cliPath);
            Logger.Debug($"HidHide installed check: {installed}{(installed ? $" ({cliPath})" : string.Empty)}");
            return installed;
        }

        public static bool Install()
        {
            if (IsInstalled())
            {
                Logger.Info("HidHide already installed; skipping installation");
                return true;
            }

            // Pinned, hash-verified direct download first: it is the only path that may run
            // silently with the helper's elevation. winget is the fallback and runs as the
            // non-elevated user (it resolves to a user-writable app alias, so it must never
            // be started elevated); the HidHide installer it fetches then asks for UAC.
            Logger.Info("Starting HidHide installation via verified direct download...");
            if (!TryInstallViaDirectDownload(out bool handedToUser))
            {
                if (handedToUser)
                {
                    // Already showing a UAC prompt for the installer; don't start a second one.
                    Logger.Info("HidHide installer is waiting for UAC confirmation; not trying winget");
                    return IsInstalled();
                }
                Logger.Warn("HidHide direct download install failed; attempting winget (non-elevated) fallback...");
                if (!TryInstallViaWinget(PrimaryHidHideWingetId) && !TryInstallViaWinget(FallbackHidHideWingetId))
                {
                    Logger.Warn("HidHide installation via winget failed");
                    return false;
                }
            }

            bool installed = IsInstalled();
            Logger.Info($"HidHide installation completed. Installed={installed}");
            return installed;
        }

        private static bool TryInstallViaWinget(string packageId)
        {
            // winget.exe is an app-execution alias under the user's
            // %LOCALAPPDATA%\Microsoft\WindowsApps (user-writable) and is found through the
            // user's PATH, so it is run as the NON-elevated user via cmd (which resolves it in
            // the user's own environment). The installer it downloads raises its own UAC prompt.
            string arguments = $"install --id {packageId} -e --accept-package-agreements --accept-source-agreements --silent --disable-interactivity";
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

            try
            {
                var result = Services.UnelevatedLauncher.Start(
                    cmd, $"\"{cmd}\" /d /c winget {arguments}", hideWindow: true, waitMilliseconds: 180000);
                if (!result.Started)
                {
                    Logger.Warn($"HidHide winget install failed to start: winget {arguments}");
                    return false;
                }

                Logger.Info($"HidHide winget install started (non-elevated), PID={result.ProcessId}");

                if (!result.Exited)
                {
                    Logger.Warn("HidHide winget installation did not finish within 3 minutes");
                    return false;
                }

                if (result.ExitCode == 0 || result.ExitCode == 3010)
                {
                    Logger.Info($"HidHide winget installation exited with code {result.ExitCode}");
                    return true;
                }

                Logger.Warn($"HidHide winget installation failed with exit code {result.ExitCode}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide winget installation exception: {ex.Message}");
                return false;
            }
        }

        private static bool TryInstallViaDirectDownload(out bool handedToUser)
        {
            handedToUser = false;
            XboxGamingBarHelper.Services.SecureInstaller.HeldFile installer = null;
            try
            {
                Logger.Info($"Downloading HidHide installer from {HidHideDirectDownloadUrl}...");
                // Admin-only random folder, held deny-write, pinned SHA-256 (it used to be a
                // fixed %TEMP% name any process of the user could swap before the elevated run).
                installer = XboxGamingBarHelper.Services.SecureInstaller.DownloadAndVerifyAsync(
                        new Uri(HidHideDirectDownloadUrl),
                        host => XboxGamingBarHelper.Services.SecureInstaller.HostMatches(host, "github.com", "githubusercontent.com"),
                        "HidHide_1.5.230_x64.exe",
                        new XboxGamingBarHelper.Services.SecureInstaller.Expectation
                        {
                            Sha256 = HidHideDirectDownloadSha256,
                            SignerNames = new[] { "Nefarius Software Solutions e.U." },
                        },
                        TimeSpan.FromSeconds(60))
                    .GetAwaiter().GetResult();

                Logger.Info($"HidHide installer downloaded to {installer.Path} ({installer.Verdict}: {installer.Reason})");

                var run = XboxGamingBarHelper.Services.SecureInstaller.Run(installer, "/quiet /norestart", 120000);
                if (!run.Started)
                {
                    Logger.Warn($"HidHide direct installer not started: {run.Message}");
                    return false;
                }

                if (!run.Elevated)
                {
                    // Handed to Windows for UAC confirmation; can't wait on it from here.
                    Logger.Info($"HidHide direct installer: {run.Message}");
                    XboxGamingBarHelper.Services.SecureInstaller.ReleaseLater(installer, TimeSpan.FromMinutes(10));
                    installer = null;
                    handedToUser = true;
                    return false;
                }

                if (!run.Exited)
                {
                    Logger.Warn("HidHide direct installation timed out after 2 minutes");
                    XboxGamingBarHelper.Services.SecureInstaller.ReleaseLater(installer, TimeSpan.FromMinutes(10));
                    installer = null;
                    return false;
                }

                if (run.ExitCode == 0 || run.ExitCode == 3010)
                {
                    Logger.Info($"HidHide direct installer exited with code {run.ExitCode}");
                    return true;
                }

                Logger.Warn($"HidHide direct installer failed with exit code {run.ExitCode}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide direct installation exception: {ex.Message}");
                return false;
            }
            finally
            {
                installer?.Dispose();
            }
        }
    }
}
