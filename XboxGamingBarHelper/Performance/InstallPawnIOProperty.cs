using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using NLog;
using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Performance
{
    /// <summary>
    /// Property to trigger PawnIO installation.
    /// Write "install" to trigger the installation.
    /// Downloads the installer from GitHub and runs with -install -silent.
    /// </summary>
    internal class InstallPawnIOProperty : HelperProperty<string, PerformanceManager>
    {
        private new static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const string PawnIODownloadUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

        public InstallPawnIOProperty(PerformanceManager inManager)
            : base("", null, Function.TdpMethod_InstallPawnIO, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (Value?.ToLowerInvariant() == "install")
            {
                Logger.Info("PawnIO installation requested from widget");
                // Run installation asynchronously to avoid blocking
                Task.Run(() => InstallPawnIO());
                // Reset the value
                SetValue("");
            }
        }

        /// <summary>
        /// Downloads and installs PawnIO. The installer is fetched into an admin-only random
        /// folder (held deny-write) and only run silently with the helper's elevation when it
        /// carries a valid Authenticode signature from namazso (the "latest" URL can't be
        /// hash-pinned). Otherwise, or when the helper isn't elevated, Windows' UAC prompt
        /// asks the user (it used to be a fixed %TEMP% file run elevated with no check).
        /// </summary>
        private void InstallPawnIO()
        {
            Services.SecureInstaller.HeldFile installer = null;
            try
            {
                // Step 1: Download the installer
                Logger.Info($"Downloading PawnIO installer from {PawnIODownloadUrl}...");
                installer = Services.SecureInstaller.DownloadAndVerifyAsync(
                        new Uri(PawnIODownloadUrl),
                        host => Services.SecureInstaller.HostMatches(host, "github.com", "githubusercontent.com"),
                        "PawnIO_setup.exe",
                        new Services.SecureInstaller.Expectation
                        {
                            SignerNames = new[] { "namazso.eu", "namazso" },
                        },
                        TimeSpan.FromSeconds(60))
                    .GetAwaiter().GetResult();

                Logger.Info($"PawnIO installer downloaded to {installer.Path} ({installer.Verdict}: {installer.Reason})");

                // Step 2: Run installer with silent install (elevated only when verified).
                Logger.Info("Launching PawnIO installer with -install -silent...");
                var run = Services.SecureInstaller.Run(installer, "-install -silent", 120000);
                if (!run.Started)
                {
                    Logger.Warn($"Failed to start PawnIO installer: {run.Message}");
                }
                else if (run.Exited)
                {
                    Logger.Info($"PawnIO installation completed with exit code: {run.ExitCode}");
                }
                else if (run.Elevated)
                {
                    Logger.Warn("PawnIO installation timed out after 2 minutes");
                }
                else
                {
                    Logger.Info($"PawnIO installer handed to Windows for confirmation: {run.Message}");
                }

                if (run.Started && !run.Exited)
                {
                    Services.SecureInstaller.ReleaseLater(installer, TimeSpan.FromMinutes(10));
                    installer = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to install PawnIO: {ex.Message}");
            }
            finally
            {
                // Step 3: Release / clean up the downloaded installer
                installer?.Dispose();

                // Step 4: Refresh the installed status
                Logger.Info("Refreshing PawnIO installed status...");
                Manager?.RefreshPawnIOInstalledStatus();

                // Step 5: If PawnIO is now installed, restart helper to reinitialize with PawnIO support
                if (Manager?.IsPawnIOInstalled == true)
                {
                    Logger.Info("PawnIO is now installed - restarting helper to reinitialize...");
                    // Give time for the status to be sent to widget
                    System.Threading.Thread.Sleep(1000);
                    // Exit helper - widget will detect disconnection and relaunch
                    Environment.Exit(0);
                }
            }
        }
    }
}
