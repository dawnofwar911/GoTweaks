using NLog;
using Shared.Constants;
using Shared.Data;
using Shared.IPC;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.Input.Preview.Injection;
using XboxGamingBarHelper.AMD;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.ControllerEmulation;
using XboxGamingBarHelper.Devices.Libraries.GPD;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.LosslessScaling;
using XboxGamingBarHelper.OnScreenDisplay;
using XboxGamingBarHelper.Performance;
using XboxGamingBarHelper.Power;
using XboxGamingBarHelper.Profile;
using XboxGamingBarHelper.RTSS;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Systems;
using XboxGamingBarHelper.AutoTDP;
using XboxGamingBarHelper.DefaultGameProfiles;
using XboxGamingBarHelper.Labs;
using Shared.Enums;

namespace XboxGamingBarHelper
{
    internal partial class Program
    {

        /// <summary>
        /// Initialize the package data folder path for uninstall detection.
        /// Called once during startup.
        /// We check the LocalState folder, not LocalCache, because:
        /// - LocalCache contains the running helper (can't be deleted while in use)
        /// - LocalState is the app's data folder that Windows removes during uninstall
        /// </summary>
        private static void InitializePackageDataFolder()
        {
            try
            {
                // Try to get the package's LocalState folder path
                try
                {
                    var localState = global::Windows.Storage.ApplicationData.Current.LocalFolder;
                    packageDataFolder = localState.Path;
                    Logger.Info($"Package LocalState folder for uninstall detection: {packageDataFolder}");
                }
                catch
                {
                    // Not running in package context - try to extract from exe path
                    // Helper runs from: C:\Users\<user>\AppData\Local\Packages\<PackageFamilyName>\LocalCache\GoTweaks\Helper\
                    var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (exePath.Contains("Packages") && exePath.Contains("LocalCache"))
                    {
                        int packagesIdx = exePath.IndexOf("Packages", StringComparison.OrdinalIgnoreCase);
                        int localCacheIdx = exePath.IndexOf("LocalCache", StringComparison.OrdinalIgnoreCase);
                        if (packagesIdx > 0 && localCacheIdx > packagesIdx)
                        {
                            // Extract base: C:\Users\<user>\AppData\Local\Packages\<PackageFamilyName>
                            var packageBase = exePath.Substring(0, localCacheIdx).TrimEnd('\\');
                            // LocalState folder is at the same level as LocalCache
                            packageDataFolder = Path.Combine(packageBase, "LocalState");
                            Logger.Info($"Package LocalState folder (from exe path): {packageDataFolder}");
                        }
                    }
                }

                // Deployed helper runs from Program Files (no package identity, exe path not
                // under the package folder): use the known package family name.
                if (string.IsNullOrEmpty(packageDataFolder))
                {
                    var byFamilyName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Packages", Services.HelperDeploymentService.FallbackPackageFamilyName, "LocalState");
                    if (Directory.Exists(byFamilyName))
                    {
                        packageDataFolder = byFamilyName;
                        Logger.Info($"Package LocalState folder (from family name): {packageDataFolder}");
                    }
                    else if (Services.HelperDeploymentService.FindInstalledPackageHelperDirectory() == null)
                    {
                        // The Program Files copy outlives the package (unlike the old LocalCache
                        // copy, which Windows deleted with it): with neither the package nor its
                        // LocalState present, let the uninstall check below clean up the task
                        // and the deployed files instead of running elevated at every logon.
                        packageDataFolder = byFamilyName;
                        Logger.Warn($"GoTweaks package not installed and {byFamilyName} missing - treating as uninstalled");
                    }
                }

                if (string.IsNullOrEmpty(packageDataFolder))
                {
                    Logger.Warn("Could not determine package LocalState folder for uninstall detection");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Error initializing package data folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if the MSIX package is still installed.
        /// We check if the LocalState folder exists - this is removed during uninstall.
        /// The LocalCache folder (where the helper runs from) may remain due to the running process.
        /// Returns true if installed, false if uninstalled.
        /// </summary>
        private static bool IsPackageInstalled()
        {
            if (string.IsNullOrEmpty(packageDataFolder))
                return true; // Can't detect, assume installed

            try
            {
                // Check if LocalState folder exists
                // When MSIX is uninstalled, LocalState is removed but LocalCache may remain
                // because files in use (like the running helper) can't be deleted
                return Directory.Exists(packageDataFolder);
            }
            catch
            {
                return true; // Can't check, assume installed
            }
        }

        /// <summary>
        /// Periodic check for package uninstallation.
        /// If uninstalled, cleans up the scheduled task and exits.
        /// </summary>
        private static void CheckForPackageUninstall()
        {
            if ((DateTime.Now - lastUninstallCheck).TotalMilliseconds < UninstallCheckIntervalMs)
                return;

            lastUninstallCheck = DateTime.Now;

            if (!IsPackageInstalled())
            {
                Logger.Info("=== Package uninstall detected! Cleaning up... ===");

                try
                {
                    // Remove the scheduled task
                    Logger.Info("Removing scheduled task...");
                    Services.ScheduledTaskService.RemoveTask();
                    Services.ScheduledTaskService.RemoveLegacyTaskIfExists();

                    // Delete heartbeat file
                    DeleteHeartbeatFile();

                    // Try to remove deployed files (may fail if in use, but that's OK)
                    try
                    {
                        Logger.Info("Attempting to remove deployed files...");
                        Services.HelperDeploymentService.RemoveDeployment();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Could not remove deployed files (may be in use): {ex.Message}");
                    }

                    // Launch cleanup script to delete the package folder after helper exits
                    LaunchCleanupScript();

                    Logger.Info("Cleanup complete. Exiting helper.");
                    LogManager.Flush();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error during uninstall cleanup: {ex.Message}");
                }

                // Exit the helper
                _isShuttingDown = true;

                // Don't ReleaseMutex here: this isn't the thread that acquired it (that always
                // threw and was swallowed). The kernel releases (abandons) it when the process
                // exits, and the next helper already handles AbandonedMutexException.

                Environment.Exit(0);
            }
        }

        /// <summary>
        /// Launches a batch script that waits for the helper to exit, then deletes the
        /// remaining package folder, the deployed helper (Program Files) and
        /// %ProgramData%\GoTweaks. The script runs with the helper's elevated token, so it is
        /// written to the admin-only %ProgramData%\GoTweaks\Scripts folder with a random name
        /// (cmd re-reads a batch file while it runs; a user-writable %TEMP% copy could be
        /// edited mid-run) and only ever receives paths free of batch metacharacters.
        /// </summary>
        private static void LaunchCleanupScript()
        {
            try
            {
                // Package folder (parent of LocalState). It may already be gone; the deployed
                // helper still has to be removed then.
                string packageFolder = string.IsNullOrEmpty(packageDataFolder) ? null : Path.GetDirectoryName(packageDataFolder);
                if (string.IsNullOrEmpty(packageFolder) || !IsSafeForBatch(packageFolder))
                {
                    Logger.Warn($"Cleanup script: not removing package folder '{packageFolder}'");
                    packageFolder = null;
                }

                string deployFolder = Services.HelperDeploymentService.HelperFolder;
                string deployParent = Path.GetDirectoryName(deployFolder); // %ProgramFiles%\GoTweaks
                string scriptDir = Services.HelperDeploymentService.EnsureAdminOnlyDirectory("Scripts", usersCanRead: false);
                string dataFolder = Path.GetDirectoryName(scriptDir); // %ProgramData%\GoTweaks
                if (!IsSafeForBatch(deployFolder) || !IsSafeForBatch(deployParent) || !IsSafeForBatch(dataFolder))
                {
                    Logger.Warn("Cleanup script: refusing a path with batch metacharacters");
                    return;
                }

                int helperPid = Process.GetCurrentProcess().Id;
                string packageFolderValue = packageFolder ?? string.Empty;
                string scriptPath = Path.Combine(scriptDir, $"GoTweaksCleanup_{Guid.NewGuid():N}.cmd");

                string script = BatchScriptPreamble() + $@"
:: GoTweaks Uninstall Cleanup Script
set ""HELPER_PID={helperPid}""
set ""PACKAGE_FOLDER={packageFolderValue}""
set ""DEPLOY_FOLDER={deployFolder}""
set ""DEPLOY_PARENT={deployParent}""
set ""DATA_FOLDER={dataFolder}""

:: Wait for helper process to exit (poll every second, max 30 times)
set /a COUNT=0
:WAIT_LOOP
tasklist /FI ""PID eq %HELPER_PID%"" 2>nul | find /i ""%HELPER_PID%"" >nul
if errorlevel 1 goto PROCESS_EXITED
set /a COUNT+=1
if %COUNT% geq 30 goto PROCESS_EXITED
timeout /t 1 /nobreak >nul
goto WAIT_LOOP

:PROCESS_EXITED
:: Wait a bit more for file handles to release
timeout /t 3 /nobreak >nul

:: Delete the package folder (rd /s /q is silent and doesn't show error dialogs)
if not ""%PACKAGE_FOLDER%""=="""" if exist ""%PACKAGE_FOLDER%"" rd /s /q ""%PACKAGE_FOLDER%"" 2>nul

:: Delete the deployed helper (admin-only Program Files folder) and its parent if empty
if exist ""%DEPLOY_FOLDER%"" rd /s /q ""%DEPLOY_FOLDER%"" 2>nul
rd ""%DEPLOY_PARENT%"" 2>nul

:: Delete %ProgramData%\GoTweaks (scripts, downloads) - this script included, so last
rd /s /q ""%DATA_FOLDER%"" 2>nul
";

                WriteBatchScript(scriptPath, script);
                Logger.Info($"Cleanup script created: {scriptPath}");
                StartBatchScript(scriptPath);
                Logger.Info("Cleanup script launched");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to launch cleanup script: {ex.Message}");
            }
        }

        /// <summary>
        /// Launches an upgrade script that waits for the helper to exit,
        /// copies new files from MSIX, and restarts the helper via scheduled task.
        /// This allows UAC-free upgrades since the current helper is already elevated.
        /// Hardened: the source must be a GoTweaks package folder under WindowsApps
        /// (TrustedInstaller-owned) with no batch metacharacters and, unless
        /// <paramref name="allowDowngrade"/>, not older than the deployed copy; the script is
        /// written to the admin-only %ProgramData%\GoTweaks\Scripts folder with a random name
        /// (cmd re-reads batch files while running, so a user-writable %TEMP% script could be
        /// edited mid-run); values are passed as quoted set "VAR=value"; tools are run from
        /// System32 with PATH/PATHEXT pinned. The version stamp comes from the package folder
        /// name (never the 1.0.0.0 assembly-version fallback), is removed before copying and
        /// rewritten only when the copy succeeded.
        /// </summary>
        /// <param name="msixSourcePath">Package helper folder (WindowsApps) to copy from</param>
        /// <param name="allowDowngrade">True when the source is the installed package itself
        /// (authoritative, e.g. after a rollback), false for anything a client asked for.</param>
        /// <returns>True when the script was started; the caller must then exit.</returns>
        internal static bool LaunchUpgradeScript(string msixSourcePath, bool allowDowngrade)
        {
            try
            {
                string deployedFolder = Services.HelperDeploymentService.HelperFolder;
                string taskName = "GoTweaks\\GoTweaksHelper";
                int helperPid = Process.GetCurrentProcess().Id;

                string sourcePath;
                try { sourcePath = Path.GetFullPath(msixSourcePath ?? "").TrimEnd('\\'); }
                catch { sourcePath = null; }

                if (string.IsNullOrEmpty(sourcePath)
                    || !Services.HelperDeploymentService.IsTrustedPackageSourceDirectory(sourcePath)
                    || !File.Exists(Path.Combine(sourcePath, "XboxGamingBarHelper.exe")))
                {
                    Logger.Error($"UpgradeHelper: refusing source '{msixSourcePath}' (must be the GoTweaks package helper folder under WindowsApps)");
                    return false;
                }
                if (!IsSafeForBatch(sourcePath) || !IsSafeForBatch(deployedFolder))
                {
                    Logger.Error("UpgradeHelper: refusing a path with batch metacharacters");
                    return false;
                }
                if (!ElevationBootstrapper.IsRunningAsAdmin())
                {
                    Logger.Error($"UpgradeHelper: not elevated; cannot write {deployedFolder}");
                    return false;
                }
                if (!Services.HelperDeploymentService.IsHelperFolderAdminProtected)
                {
                    Logger.Error($"UpgradeHelper: {deployedFolder} is not an admin-only directory; refusing");
                    return false;
                }

                string newVersion = Services.HelperDeploymentService.GetPackageVersionFromPath(sourcePath);
                if (newVersion == null)
                {
                    Logger.Error($"UpgradeHelper: could not read the package version from '{sourcePath}'; refusing");
                    return false;
                }
                string deployedVersion = Services.HelperDeploymentService.GetDeployedVersion();
                if (!allowDowngrade
                    && Version.TryParse(deployedVersion ?? "", out var oldV) && Version.TryParse(newVersion, out var newV) && newV < oldV)
                {
                    Logger.Error($"UpgradeHelper: refusing downgrade {deployedVersion} -> {newVersion}");
                    return false;
                }

                string scriptPath = Path.Combine(
                    Services.HelperDeploymentService.EnsureAdminOnlyDirectory("Scripts", usersCanRead: false),
                    $"GoTweaksUpgrade_{Guid.NewGuid():N}.cmd");
                string versionFile = Path.Combine(deployedFolder, ".version");

                Logger.Info($"Creating upgrade script: {sourcePath} -> {deployedFolder} (version {newVersion})");

                // Batch script that:
                // 1. Waits for the old helper to exit
                // 2. Removes the version stamp, copies all files from the package
                // 3. Re-stamps the version only if the copy succeeded
                // 4. Runs the scheduled task to start new helper
                // 5. Deletes itself
                string script = BatchScriptPreamble() + $@"
:: GoTweaks Upgrade Script - UAC-free upgrade
set ""HELPER_PID={helperPid}""
set ""SOURCE_PATH={sourcePath}""
set ""DEPLOY_PATH={deployedFolder}""
set ""TASK_NAME={taskName}""
set ""VERSION_FILE={versionFile}""
set ""NEW_VERSION={newVersion}""

:: Wait for old helper process to exit (poll every second, max 30 times)
set /a COUNT=0
:WAIT_LOOP
tasklist /FI ""PID eq %HELPER_PID%"" 2>nul | find /i ""%HELPER_PID%"" >nul
if errorlevel 1 goto PROCESS_EXITED
set /a COUNT+=1
if %COUNT% geq 30 goto PROCESS_EXITED
timeout /t 1 /nobreak >nul
goto WAIT_LOOP

:PROCESS_EXITED
:: Wait a bit more for file handles to release
timeout /t 2 /nobreak >nul

:: Create deploy directory if needed
if not exist ""%DEPLOY_PATH%"" mkdir ""%DEPLOY_PATH%""

:: A partial copy must never keep the old stamp (see HelperDeploymentService.DeployFiles)
if exist ""%VERSION_FILE%"" del /f /q ""%VERSION_FILE%""

:: Copy all files from MSIX source to deployed location
xcopy /Y /Q ""%SOURCE_PATH%\*.*"" ""%DEPLOY_PATH%\"" >nul 2>&1
if errorlevel 1 goto RUN_TASK

:: Write version file (only after a successful copy)
>""%VERSION_FILE%"" echo %NEW_VERSION%

:RUN_TASK
:: Run the scheduled task to start the new helper
schtasks /Run /TN ""%TASK_NAME%"" >nul 2>&1

:: Delete this script
timeout /t 1 /nobreak >nul
del /f /q ""%~f0"" 2>nul
";

                WriteBatchScript(scriptPath, script);
                Logger.Info($"Upgrade script created: {scriptPath}");
                StartBatchScript(scriptPath);
                Logger.Info("Upgrade script launched - helper will exit now");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to launch upgrade script: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// First lines of every elevated batch script: no delayed expansion, and PATH /
        /// PATHEXT pinned to System32 (from GetSystemDirectory, not the environment) so a
        /// user-controlled PATH, PATHEXT or current directory can't substitute tasklist,
        /// xcopy, schtasks etc.
        /// </summary>
        private static string BatchScriptPreamble()
        {
            string system32 = Environment.SystemDirectory;
            if (!IsSafeForBatch(system32))
            {
                throw new InvalidOperationException("System directory contains batch metacharacters");
            }
            return "@echo off\r\n"
                + "setlocal DisableDelayedExpansion\r\n"
                + $"set \"PATH={system32}\"\r\n"
                + "set \"PATHEXT=.COM;.EXE;.BAT;.CMD\"\r\n";
        }

        /// <summary>
        /// Writes a batch script with CRLF line endings whatever the source file's line
        /// endings are (cmd's label lookup for goto is unreliable with LF-only files).
        /// </summary>
        private static void WriteBatchScript(string scriptPath, string script)
        {
            string crlf = script.Replace("\r\n", "\n").Replace("\n", "\r\n");
            File.WriteAllText(scriptPath, crlf);
        }

        /// <summary>Runs an elevated batch script hidden with System32's cmd.exe, cwd = System32.</summary>
        private static void StartBatchScript(string scriptPath)
        {
            string system32 = Environment.SystemDirectory;
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(system32, "cmd.exe"),
                Arguments = $"/d /c \"{scriptPath}\"",
                WorkingDirectory = system32,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (Process.Start(startInfo)) { }
        }

        /// <summary>
        /// True when <paramref name="value"/> can be embedded in a set "VAR=value" batch line
        /// without being interpreted (no quotes, %, !, ^, &amp;, |, &lt;, &gt;, or line breaks).
        /// </summary>
        private static bool IsSafeForBatch(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.IndexOfAny(new[] { '"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n' }) < 0;
        }
    }
}
