using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using NLog;
using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// One-click usbip-win2 installer. Write "install" to trigger.
    ///
    /// usbip-win2 is the VIIPER backend's bus driver — the only prerequisite a
    /// user must install by hand now that VIIPER is the default backend. The
    /// upstream installer is InnoSetup, so a silent elevated run is
    /// "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART". The ude driver may need a
    /// reboot before the virtual bus works; UsbipInstalledProperty.Refresh()
    /// runs after install so the widget's prereq line updates immediately
    /// either way.
    /// </summary>
    internal class InstallUsbipProperty : HelperProperty<string, SettingsManager>
    {
        private new static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Pinned release (matches the version verified against libviiper).
        // Bump deliberately — driver installers are not a place for "latest".
        private const string UsbipDownloadUrl =
            "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.8/USBip-0.9.7.8-x64.exe";

        // SHA-256 of that exact release asset (fetched 2026-11-16; Authenticode signer
        // "Cloudyne Systems (Scheibling Consulting AB)"). Bump together with the URL.
        private const string UsbipSha256 = "44451fe06f4186125c2a5ecd25b099c5560a61a60b1e56f5a0758e77a60afa44";

        public InstallUsbipProperty(SettingsManager inManager)
            : base("", null, Function.Viiper_InstallUsbip, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (Value?.ToLowerInvariant() == "install")
            {
                Logger.Info("usbip-win2 installation requested from widget");
                Task.Run(() => InstallUsbip());
                SetValue("");
            }
        }

        private void InstallUsbip()
        {
            Services.SecureInstaller.HeldFile installer = null;
            try
            {
                Logger.Info($"Downloading usbip-win2 installer from {UsbipDownloadUrl}...");
                // Admin-only random folder, held deny-write, pinned SHA-256: only that exact
                // installer is ever run silently with the helper's elevation (it used to be a
                // fixed %TEMP%\USBip_setup.exe that any process of the user could swap).
                installer = Services.SecureInstaller.DownloadAndVerifyAsync(
                        new Uri(UsbipDownloadUrl),
                        host => Services.SecureInstaller.HostMatches(host, "github.com", "githubusercontent.com"),
                        "USBip-0.9.7.8-x64.exe",
                        new Services.SecureInstaller.Expectation
                        {
                            Sha256 = UsbipSha256,
                            SignerNames = new[] { "Cloudyne Systems (Scheibling Consulting AB)" },
                        },
                        TimeSpan.FromSeconds(60))
                    .GetAwaiter().GetResult();
                Logger.Info($"usbip-win2 installer downloaded to {installer.Path} ({installer.Verdict}: {installer.Reason})");

                Logger.Info("Launching usbip-win2 installer...");
                var run = Services.SecureInstaller.Run(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", 180000);
                if (!run.Started)
                {
                    Logger.Warn($"usbip-win2 installer not started: {run.Message}");
                }
                else if (run.Exited)
                {
                    Logger.Info($"usbip-win2 installation completed with exit code: {run.ExitCode}");
                }
                else if (run.Elevated)
                {
                    Logger.Warn("usbip-win2 installation still running after 3 minutes");
                }
                else
                {
                    Logger.Info($"usbip-win2 installer handed to Windows for confirmation: {run.Message}");
                }

                if (run.Started && !run.Exited)
                {
                    // Still running (or awaiting UAC consent): keep the file locked a while.
                    Services.SecureInstaller.ReleaseLater(installer, TimeSpan.FromMinutes(10));
                    installer = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to install usbip-win2: {ex.Message}");
            }
            finally
            {
                installer?.Dispose();

                // Update the prereq line + setup banner. A reboot may still be
                // required before the virtual bus actually enumerates; detection
                // is registry/file based so it flips as soon as install lands.
                try { Manager?.UsbipInstalled?.Refresh(); } catch { }
                try { Program.SendSetupWarningsToWidget(); } catch { }
            }
        }
    }
}
