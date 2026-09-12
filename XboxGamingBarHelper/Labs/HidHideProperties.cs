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

            Logger.Info("Starting HidHide installation via winget...");
            if (!TryInstallViaWinget(PrimaryHidHideWingetId) && !TryInstallViaWinget(FallbackHidHideWingetId))
            {
                Logger.Warn("HidHide installation via winget failed; attempting direct download fallback...");
                if (!TryInstallViaDirectDownload())
                {
                    Logger.Warn("HidHide installation via direct download failed");
                    return false;
                }
            }

            bool installed = IsInstalled();
            Logger.Info($"HidHide installation completed. Installed={installed}");
            return installed;
        }

        private static bool TryInstallViaWinget(string packageId)
        {
            string localWinget = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WindowsApps",
                "winget.exe");

            string[] candidates = new[]
            {
                "winget",
                localWinget,
            };

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Path.IsPathRooted(candidate) && !File.Exists(candidate))
                {
                    continue;
                }

                if (RunWingetInstall(candidate, packageId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool RunWingetInstall(string executable, string packageId)
        {
            string arguments = $"install --id {packageId} -e --accept-package-agreements --accept-source-agreements --silent --disable-interactivity";

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using Process process = Process.Start(startInfo);
                if (process == null)
                {
                    Logger.Warn($"HidHide install failed to start: {executable} {arguments}");
                    return false;
                }

                Logger.Info($"HidHide installer started ({executable}), PID={process.Id}");

                if (!process.WaitForExit(180000))
                {
                    try { process.Kill(); } catch { }
                    Logger.Warn("HidHide installation timed out after 3 minutes");
                    return false;
                }

                if (process.ExitCode == 0 || process.ExitCode == 3010)
                {
                    Logger.Info($"HidHide installation process exited with code {process.ExitCode}");
                    return true;
                }

                Logger.Warn($"HidHide installation failed with exit code {process.ExitCode}");
                return false;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Logger.Info("HidHide installation cancelled by user (UAC prompt declined)");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide installation exception: {ex.Message}");
                return false;
            }
        }

        private static bool TryInstallViaDirectDownload()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "HidHide_1.5.230_x64.exe");
            try
            {
                Logger.Info($"Downloading HidHide installer from {HidHideDirectDownloadUrl}...");
                using (var client = new TimedWebClient(TimeSpan.FromSeconds(60)))
                {
                    client.Headers.Add("User-Agent", "GoTweaks/1.0");
                    client.DownloadFile(HidHideDirectDownloadUrl, tempPath);
                }

                Logger.Info($"HidHide installer downloaded to {tempPath}");

                var startInfo = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/quiet /norestart",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using Process process = Process.Start(startInfo);
                if (process == null)
                {
                    Logger.Warn("Failed to start downloaded HidHide installer");
                    return false;
                }

                Logger.Info($"HidHide direct installer started, PID={process.Id}");
                if (!process.WaitForExit(120000))
                {
                    try { process.Kill(); } catch { }
                    Logger.Warn("HidHide direct installation timed out after 2 minutes");
                    return false;
                }

                if (process.ExitCode == 0 || process.ExitCode == 3010)
                {
                    Logger.Info($"HidHide direct installer exited with code {process.ExitCode}");
                    return true;
                }

                Logger.Warn($"HidHide direct installer failed with exit code {process.ExitCode}");
                return false;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Logger.Info("HidHide direct installation cancelled by user (UAC prompt declined)");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"HidHide direct installation exception: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch { }
            }
        }
    }
}
