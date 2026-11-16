using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using NLog;
using XboxGamingBarHelper.Services;

namespace XboxGamingBarHelper
{
    /// <summary>
    /// Handles self-elevation via scheduled task to avoid repeated UAC prompts.
    ///
    /// Architecture:
    /// - Helper files are deployed to %ProgramFiles%\GoTweaks\Helper\ (admin-only, stable
    ///   location; was the user-writable package LocalCache before the security hardening)
    /// - Scheduled task points to the deployed location (survives MSIX updates)
    /// - First run (and the one-time migration off LocalCache): UAC prompt once to deploy
    ///   and create/repoint the task
    /// - Subsequent launches: task runs without UAC
    /// </summary>
    public static class ElevationBootstrapper
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const int ProcessStartTimeoutMs = 5000;

        // Same mutex name Program.cs's --setup path uses (SetupInProgressMutexName) to tell
        // the freshly-spawned deployed helper's AnotherHelperIsAlive() peer-check "the other
        // XboxGamingBarHelper.exe you're seeing is a relaunch bootstrapper, not a duplicate -
        // don't wait for it to exit". Without this, a still-alive bootstrap process here raced
        // against RunTaskNow()'s own 5s mutex-wait below: the new helper would see this process
        // as a peer and wait up to 5s for IT to exit, while this process was simultaneously
        // waiting up to 5s for the new helper's mutex - both timers expire around the same
        // moment and BOTH processes give up and exit, leaving no helper running at all. Only
        // hit when the helper genuinely wasn't running already (so this path actually has to
        // call RunTaskNow), which in practice only happens right after killing the helper - a
        // cold boot's ONLOGON task trigger (or the --setup flow's own copy of this mutex)
        // starts the helper before the widget ever gets here.
        private const string RelaunchInProgressMutexName = "Global\\GoTweaks_SetupInProgress";
        private static Mutex relaunchInProgressMutex;

        // Zombie-setup guard (2026-07-29 field wedge): an elevated --setup launched from
        // the WindowsApps path spun at 100% CPU before ever reaching Main — it never
        // acquired the setup mutex and never logged, so nothing deduped it, and the
        // widget's 15s force-launch escalation piled up ~25 such zombies. The marker
        // file records when we last fired a UAC setup; within the cooldown we refuse
        // to fire another. Lives in the deploy folder so uninstall sweeps it.
        private const int SetupLaunchCooldownSeconds = 120;
        // Written by the NON-elevated bootstrapper, so it must live in the user-writable
        // staging folder (HelperFolder is admin-only now). It's only a throttle.
        private static readonly string SetupLaunchMarkerPath =
            Path.Combine(HelperDeploymentService.StagingFolder, ".setup_launch");

        /// <summary>
        /// Ensures the helper is running with admin privileges.
        /// Returns true if execution should continue, false if we're relaunching elevated.
        /// </summary>
        public static bool EnsureElevated(string[] args)
        {
            Logger.Debug($"EnsureElevated called, IsAdmin={IsRunningAsAdmin()}");

            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    Logger.Error("Could not determine executable path");
                    return true; // Continue anyway, may fail later
                }

                // Already running as admin
                if (IsRunningAsAdmin())
                {
                    Logger.Info("Already running as administrator");

                    // If running from MSIX, trigger background deployment for future launches
                    if (HelperDeploymentService.IsRunningFromMsix())
                    {
                        EnsureDeploymentAsync();
                    }
                    else if (!HelperDeploymentService.IsRunningFromDeployedLocation())
                    {
                        // Pre-hardening install: the logon task still runs the helper from the
                        // user-writable LocalCache copy. We're elevated, so move it to Program
                        // Files and repoint the task now; the next start runs from there.
                        MigrateLegacyDeploymentAsync(exePath);
                    }

                    return true; // Continue with normal execution immediately
                }

                // Not admin - check if setup is needed BEFORE mutex check
                // This ensures we can update even if an old instance is running
                Logger.Info($"Not running as administrator. ExePath={exePath}");
                Logger.Info($"HelperFolder: {HelperDeploymentService.HelperFolder}");
                Logger.Info($"DeployedExePath: {HelperDeploymentService.DeployedExePath}");
                Logger.Info($"Running from MSIX: {HelperDeploymentService.IsRunningFromMsix()}");
                Logger.Info($"Running from deployed location: {HelperDeploymentService.IsRunningFromDeployedLocation()}");
                LogManager.Flush(); // Ensure logs are written before checks

                Logger.Debug($"ExePath={exePath}, HelperFolder={HelperDeploymentService.HelperFolder}, DeployedExeExists={File.Exists(HelperDeploymentService.DeployedExePath)}");

                bool deploymentValid = HelperDeploymentService.IsDeploymentValid();
                bool deploymentNeeded = HelperDeploymentService.IsDeploymentNeeded();
                bool taskConfigured = ScheduledTaskService.IsTaskConfiguredCorrectly();

                Logger.Info($"Deployment valid: {deploymentValid}, Deployment needed: {deploymentNeeded}, Task configured: {taskConfigured}");
                LogManager.Flush(); // Ensure logs are written before decision

                // If deployment is needed (missing or outdated), refresh it HERE, without
                // elevation: the deploy target (LocalCache\GoTweaks\Helper) is user-writable,
                // so copying files never needed admin — only scheduled-task creation does.
                // This kills two birds (2026-07-29 field wedge): routine updates skip UAC
                // entirely when the task already exists, and when elevation IS needed we
                // elevate the freshly-DEPLOYED exe instead of the WindowsApps one — the
                // elevated-from-WindowsApps launch is the combination that spun pre-Main
                // at 100% CPU and wedged the "Initial setup" banner.
                // Security hardening: the deployed helper now lives in an admin-only folder
                // (Program Files) because the elevated logon task used to run it from the
                // user-writable LocalCache, a silent UAC bypass. A non-elevated bootstrap can
                // therefore no longer refresh it. Instead, stage the package files in the
                // old LocalCache location (user-writable) so the UAC --setup below can be
                // launched from a plain filesystem path rather than from WindowsApps (the
                // combination that wedged pre-Main, 2026-07-29). The elevated setup copies
                // from the validated package folder (--source), never from the staging copy.
                // Routine updates while the elevated helper is running still need no UAC:
                // the widget's UpgradeHelper path has the running (elevated) helper do it.
                bool stagedOk = false;
                if (deploymentNeeded)
                {
                    Logger.Info($"Deployment needed - staging helper files in {HelperDeploymentService.StagingFolder} for the elevated setup...");
                    try
                    {
                        stagedOk = HelperDeploymentService.DeployToStaging();
                        Logger.Info($"Staging finished: {stagedOk}");
                    }
                    catch (Exception dex)
                    {
                        Logger.Warn($"Staging threw: {dex.Message}");
                    }
                }

                if (deploymentNeeded || !taskConfigured)
                {
                    // Dedupe before firing UAC. A healthy in-flight setup holds the
                    // Global setup mutex (probe below treats NoReadUp access-denied as
                    // "exists"); a pre-Main zombie holds nothing, so the launch-marker
                    // cooldown is the backstop that stops the every-15s widget
                    // escalation from multiplying stuck setups.
                    if (IsSetupMutexHeld())
                    {
                        Logger.Info("A setup/relaunch process is already in flight (mutex held) — not launching another; exiting");
                        LogManager.Flush();
                        return false;
                    }
                    if (SetupLaunchedRecently())
                    {
                        Logger.Warn($"A setup was launched <{SetupLaunchCooldownSeconds}s ago and hasn't completed — skipping relaunch; exiting");
                        LogManager.Flush();
                        return false;
                    }

                    // Prefer the deployed exe for the elevated setup: a plain filesystem
                    // path with no MSIX/appmodel involvement — the path elevated task
                    // launches have used safely all along — and admin-protected, so what
                    // the user consents to is what runs. An older deployed copy is fine:
                    // setup copies from the package folder passed with --source, renaming
                    // its own locked files out of the way. Next best is the staged copy
                    // (plain path, but user-writable); only fall back to the current
                    // (WindowsApps) exe when neither exists. A deployed copy only counts
                    // when it carries a .version stamp: DeployFiles removes the stamp before
                    // copying and writes it only after a complete copy, so a stamped copy is a
                    // consistent set of files that will actually start.
                    string setupExePath;
                    if (HelperDeploymentService.IsDeployedCopyIntact()
                        && !string.IsNullOrEmpty(HelperDeploymentService.GetDeployedVersion()))
                        setupExePath = HelperDeploymentService.DeployedExePath;
                    else if (stagedOk && File.Exists(HelperDeploymentService.StagedExePath))
                        setupExePath = HelperDeploymentService.StagedExePath;
                    else
                        setupExePath = exePath;
                    Logger.Info($"Setup needed - launching with --setup flag (will trigger UAC)... exe={setupExePath}");
                    StampSetupLaunchMarker();
                    if (LaunchElevatedForSetup(setupExePath, args))
                    {
                        Logger.Info("Launched setup process via UAC, exiting current instance");
                        LogManager.Flush();
                        return false; // Exit this non-elevated instance
                    }

                    Logger.Error("Failed to launch setup - continuing without admin rights (some features may not work)");
                    LogManager.Flush();
                    return true; // Continue anyway, but warn user
                }

                // Deployment fresh (possibly just refreshed above) and task configured —
                // no elevation needed; fall through to the scheduled-task launch path.

                // Deployment is valid and task is configured - check for existing instance
                const string mutexName = "Global\\XboxGamingBarHelper_SingleInstance";
                try
                {
                    using (var testMutex = Mutex.OpenExisting(mutexName))
                    {
                        // Mutex exists = another instance is running
                        Logger.Info("Another helper instance is running (mutex exists). Exiting gracefully without elevation.");
                        return false; // Don't continue, don't relaunch
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // Mutex doesn't exist = no other instance running, fall through to
                    // process-enumeration as belt-and-suspenders before firing schtasks.
                    Logger.Debug("No existing helper instance detected (mutex not found)");
                }
                catch (Exception ex)
                {
                    // OpenExisting on a Global\ mutex created by an elevated (High-IL)
                    // helper fails with UnauthorizedAccessException from a Medium-IL
                    // launcher (mandatory integrity policy: NoReadUp). Don't treat that
                    // as "no helper exists" — fall through to process enumeration below.
                    Logger.Debug($"Mutex check inconclusive ({ex.GetType().Name}: {ex.Message}); falling back to process enumeration");
                }

                // Process-enumeration fallback. Same-session XboxGamingBarHelper.exe
                // peers indicate either an alive elevated helper (the common case for
                // a Medium-IL launcher unable to OpenExisting the High-IL mutex) or a
                // zombie helper that released its mutex but didn't fully exit. Either
                // way, do NOT fire schtasks /Run — that would spawn a duplicate.
                try
                {
                    var self = Process.GetCurrentProcess();
                    int selfPid = self.Id;
                    int selfSession = self.SessionId;
                    var peers = Process.GetProcessesByName("XboxGamingBarHelper");
                    var foundPids = new System.Collections.Generic.List<int>();
                    foreach (var p in peers)
                    {
                        try
                        {
                            if (p.Id != selfPid && !p.HasExited && p.SessionId == selfSession)
                            {
                                foundPids.Add(p.Id);
                            }
                        }
                        catch { /* race with process exit */ }
                        finally { try { p.Dispose(); } catch { } }
                    }
                    if (foundPids.Count > 0)
                    {
                        Logger.Info($"Another helper instance is running (PIDs={string.Join(",", foundPids)}). Exiting gracefully without elevation.");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Process-enumeration fallback failed: {ex.Message}");
                    // Continue — Main()'s own AnotherHelperIsAlive gate is the final guard.
                }

                // Try to launch via scheduled task (no UAC)
                Logger.Info("Deployment and task are ready, launching via scheduled task (no UAC)...");
                try
                {
                    relaunchInProgressMutex = new Mutex(true, RelaunchInProgressMutexName, out _);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not acquire relaunch-in-progress mutex: {ex.Message} — continuing anyway");
                }
                if (ScheduledTaskService.RunTaskNow())
                {
                    Logger.Info("Launched via scheduled task, exiting current instance");
                    LogManager.Flush();
                    return false; // Exit this non-elevated instance
                }

                Logger.Warn("Failed to launch via task, falling back to UAC elevation");
                if (LaunchElevatedForSetup(exePath, args))
                {
                    Logger.Info("Launched setup process via UAC, exiting current instance");
                    LogManager.Flush();
                    return false;
                }

                Logger.Error("Failed to elevate - continuing without admin rights (some features may not work)");
                LogManager.Flush();
                return true; // Continue anyway, but warn user
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error in elevation bootstrapper");
                return true; // Continue anyway on error
            }
        }

        /// <summary>
        /// Asynchronously ensures deployment is up to date.
        /// Runs in a background thread to avoid blocking startup.
        /// Called when running elevated from MSIX.
        /// </summary>
        private static void EnsureDeploymentAsync()
        {
            Thread deployThread = new Thread(() =>
            {
                try
                {
                    // Always remove legacy task first (cleanup from old implementation)
                    Logger.Info("Background: Removing legacy task if present...");
                    ScheduledTaskService.RemoveLegacyTaskIfExists();

                    Logger.Info("Background: Checking if deployment update is needed...");

                    if (HelperDeploymentService.IsDeploymentNeeded())
                    {
                        Logger.Info("Background: Deploying updated helper files...");
                        var timer = Stopwatch.StartNew();

                        if (HelperDeploymentService.DeployHelper())
                        {
                            timer.Stop();
                            Logger.Info($"Background: Helper deployed successfully in {timer.ElapsedMilliseconds}ms");

                            // Also ensure task is configured correctly
                            if (!ScheduledTaskService.IsTaskConfiguredCorrectly())
                            {
                                Logger.Info("Background: Creating/updating scheduled task...");
                                if (ScheduledTaskService.CreateOrUpdateTask())
                                {
                                    Logger.Info("Background: Scheduled task created successfully");
                                }
                                else
                                {
                                    Logger.Warn("Background: Failed to create scheduled task");
                                }
                            }
                        }
                        else
                        {
                            Logger.Warn("Background: Failed to deploy helper files");
                        }
                    }
                    else
                    {
                        Logger.Info("Background: Deployment is up to date");

                        // Still check if task needs update
                        if (!ScheduledTaskService.IsTaskConfiguredCorrectly())
                        {
                            Logger.Info("Background: Task needs update, creating...");
                            ScheduledTaskService.CreateOrUpdateTask();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Background: Error during deployment check: {ex.Message}");
                }
            })
            {
                IsBackground = true,
                Name = "DeploymentUpdater"
            };
            deployThread.Start();
        }

        /// <summary>
        /// One-time migration for installs from before the security hardening: the elevated
        /// logon task still points at the user-writable LocalCache copy (the one this process
        /// was started from). Copies the installed package's helper to Program Files and
        /// repoints the task. Runs in the background; the current process keeps running and
        /// the next logon / task run starts the Program Files copy. Needs no UAC (we're
        /// already elevated). Failure only logs: the next widget-launched bootstrap still
        /// sees the task misconfigured and falls back to the UAC setup.
        /// </summary>
        private static void MigrateLegacyDeploymentAsync(string currentExePath)
        {
            var migrateThread = new Thread(() =>
            {
                try
                {
                    Logger.Warn($"Elevated helper is running from a legacy location ({currentExePath}); migrating to {HelperDeploymentService.HelperFolder}");

                    string source = HelperDeploymentService.FindInstalledPackageHelperDirectory();
                    if (source == null)
                    {
                        Logger.Warn("Legacy migration: installed GoTweaks package not found; skipped");
                        return;
                    }

                    string packageVersion = HelperDeploymentService.GetPackageVersionFromPath(source);
                    bool copyNeeded = !HelperDeploymentService.IsDeployedCopyIntact()
                        || packageVersion == null
                        || !string.Equals(packageVersion, HelperDeploymentService.GetDeployedVersion(), StringComparison.OrdinalIgnoreCase);
                    if (copyNeeded && !HelperDeploymentService.DeployHelper(source))
                    {
                        Logger.Warn("Legacy migration: deploy to Program Files failed; task left unchanged");
                        return;
                    }

                    if (!ScheduledTaskService.IsTaskConfiguredCorrectly())
                    {
                        Logger.Info("Legacy migration: repointing the scheduled task to the Program Files copy...");
                        if (!ScheduledTaskService.CreateOrUpdateTask())
                        {
                            Logger.Warn("Legacy migration: could not repoint the scheduled task");
                            return;
                        }
                    }

                    Logger.Info("Legacy migration complete: the next logon/task run starts the helper from Program Files");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Legacy migration failed: {ex.Message}");
                }
            })
            {
                IsBackground = true,
                Name = "LegacyDeploymentMigration"
            };
            migrateThread.Start();
        }

        /// <summary>
        /// Value of "--source &lt;dir&gt;" on this process's command line, or null.
        /// HelperDeploymentService.DeployHelper validates it (must be the GoTweaks package
        /// folder under WindowsApps).
        /// </summary>
        private static string GetSetupSourceArgument()
        {
            try
            {
                var cmd = Environment.GetCommandLineArgs();
                for (int i = 0; i < cmd.Length - 1; i++)
                {
                    if (string.Equals(cmd[i], "--source", StringComparison.OrdinalIgnoreCase))
                    {
                        return cmd[i + 1];
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>
        /// Performs the setup process: deploys files and creates task.
        /// Called from Program.cs when --setup flag is present.
        /// Returns true if setup succeeded and task was launched.
        /// </summary>
        public static bool PerformSetup()
        {
            // Debug file for tracing setup issues (independent of NLog)
            var debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "setup_debug.txt");
            void DebugLog(string msg) { try { File.AppendAllText(debugPath, $"{DateTime.Now}: {msg}\n"); } catch { } }

            try
            {
                DebugLog($"PerformSetup started, IsAdmin={IsRunningAsAdmin()}");

                if (!IsRunningAsAdmin())
                {
                    Logger.Error("PerformSetup called without admin privileges");
                    DebugLog("ERROR: Not admin");
                    return false;
                }

                Logger.Info("=== Starting Setup ===");
                DebugLog($"Source: {HelperDeploymentService.GetSourceDirectory()}");
                DebugLog($"Target: {HelperDeploymentService.HelperFolder}");
                DebugLog($"Version: {HelperDeploymentService.GetCurrentPackageVersion()}");
                Logger.Info($"Source directory: {HelperDeploymentService.GetSourceDirectory()}");
                Logger.Info($"Target directory: {HelperDeploymentService.HelperFolder}");
                Logger.Info($"Package version: {HelperDeploymentService.GetCurrentPackageVersion()}");

                // Step 0: stop any running/zombie helper instances first (#97). A helper
                // still running from the deployed folder keeps its exe/DLLs locked, so
                // every copy attempt fails, .version is never stamped, and each launch
                // re-fires the UAC setup loop. We're elevated here, so we have the rights
                // to kill them regardless of who started them.
                Logger.Info("Step 0: Stopping running helper instances...");
                DebugLog("Step 0: Killing running helper instances...");
                KillRunningHelperInstances(DebugLog);

                // Step 1: Deploy helper files
                Logger.Info("Step 1: Deploying helper files...");
                DebugLog("Step 1: Deploying...");
                string setupSource = GetSetupSourceArgument();
                DebugLog($"--source: {setupSource ?? "(none)"}");
                if (!HelperDeploymentService.DeployHelper(setupSource))
                {
                    Logger.Error("Failed to deploy helper files");
                    DebugLog("ERROR: DeployHelper returned false");
                    return false;
                }
                Logger.Info("Helper files deployed successfully");
                DebugLog($"Deployed OK, DeployedExePath exists: {File.Exists(HelperDeploymentService.DeployedExePath)}");

                // Step 2: Create scheduled task
                Logger.Info("Step 2: Creating scheduled task...");
                DebugLog("Step 2: Creating task...");
                if (!ScheduledTaskService.CreateOrUpdateTask())
                {
                    Logger.Error("Failed to create scheduled task");
                    DebugLog("ERROR: CreateOrUpdateTask returned false");
                    return false;
                }
                Logger.Info("Scheduled task created successfully");
                DebugLog("Task created OK");

                // Note: After setup, we EXIT and let the widget relaunch the helper.
                // The relaunched helper will detect setup is complete and run via scheduled task.
                // This is cleaner than having the UAC-launched helper continue running.
                Logger.Info("Setup complete - will exit and let widget relaunch");
                Logger.Info("=== Setup Complete ===");
                DebugLog("=== Setup Complete ===");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error during setup");
                DebugLog($"EXCEPTION: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// <summary>
        /// Kills every other XboxGamingBarHelper process so the deploy that follows
        /// doesn't fail on locked files (#97). All sessions, not just ours — a stale
        /// scheduled-task helper holds the same locks wherever it lives. The launcher
        /// instance that spawned this setup has already exited by the time UAC consent
        /// completes, so "everything except self" is safe.
        /// </summary>
        private static void KillRunningHelperInstances(Action<string> debugLog)
        {
            try
            {
                int selfPid = Process.GetCurrentProcess().Id;
                var peers = Process.GetProcessesByName("XboxGamingBarHelper");
                foreach (var p in peers)
                {
                    try
                    {
                        if (p.Id == selfPid || p.HasExited)
                        {
                            continue;
                        }

                        Logger.Info($"Killing running helper instance (PID={p.Id}) before deploy");
                        debugLog?.Invoke($"Killing helper PID={p.Id}");
                        p.Kill();
                        if (!p.WaitForExit(5000))
                        {
                            Logger.Warn($"Helper PID={p.Id} did not exit within 5s; deploy may hit locked files");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not kill helper PID={p.Id}: {ex.Message}");
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not enumerate helper instances: {ex.Message}");
            }
        }

        /// <summary>
        /// True when the Global setup/relaunch mutex exists — a setup or relaunch is in
        /// flight. From this Medium-IL bootstrap, OpenExisting on a mutex created by a
        /// High-IL setup process throws UnauthorizedAccessException (mandatory integrity
        /// policy: NoReadUp) — that still proves the mutex EXISTS, so it counts as held.
        /// </summary>
        private static bool IsSetupMutexHeld()
        {
            try
            {
                using (Mutex.OpenExisting(RelaunchInProgressMutexName))
                {
                    return true;
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Setup mutex probe failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// True when a UAC setup launch was stamped within the cooldown window. Catches
        /// the pre-Main zombie setups that never acquire the mutex: without this, every
        /// widget force-launch (~15s) fires another UAC setup on top of the stuck one.
        /// </summary>
        private static bool SetupLaunchedRecently()
        {
            try
            {
                if (!File.Exists(SetupLaunchMarkerPath))
                {
                    return false;
                }
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(SetupLaunchMarkerPath);
                return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(SetupLaunchCooldownSeconds);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Setup launch marker probe failed: {ex.Message}");
                return false;
            }
        }

        private static void StampSetupLaunchMarker()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SetupLaunchMarkerPath));
                File.WriteAllText(SetupLaunchMarkerPath, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not stamp setup launch marker: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if the current process is running with administrator privileges.
        /// </summary>
        public static bool IsRunningAsAdmin()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error checking admin status");
                return false;
            }
        }

        /// <summary>
        /// Launches the helper with --setup flag and UAC elevation.
        /// </summary>
        private static bool LaunchElevatedForSetup(string exePath, string[] args)
        {
            try
            {
                // Build arguments: original args + --setup flag + --source "<package helper
                // folder>". The setup exe may be the staged copy (no package identity), so
                // it is told where the trusted package files are; PerformSetup validates it.
                var argsList = new System.Collections.Generic.List<string>();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--setup") continue;
                    if (args[i] == "--source") { i++; continue; } // drop flag and its value
                    argsList.Add(args[i]);
                }
                argsList.Add("--setup");
                string packageSource = null;
                try { packageSource = HelperDeploymentService.GetSourceDirectory(); } catch { packageSource = null; }
                if (HelperDeploymentService.LooksLikePackageSourceDirectory(packageSource)
                    && packageSource.IndexOf('"') < 0)
                {
                    argsList.Add("--source");
                    argsList.Add("\"" + packageSource.TrimEnd('\\') + "\"");
                }
                string arguments = string.Join(" ", argsList);

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas" // This triggers UAC
                };

                Logger.Info($"Launching with UAC: {exePath} {arguments}");
                var process = Process.Start(startInfo);
                return process != null;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // User cancelled UAC prompt
                Logger.Warn("User cancelled UAC elevation prompt");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error launching elevated setup process");
                return false;
            }
        }

        /// <summary>
        /// Legacy method for direct UAC elevation (fallback).
        /// </summary>
        private static bool LaunchElevated(string exePath, string[] args)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args.Length > 0 ? string.Join(" ", args) : "",
                    UseShellExecute = true,
                    Verb = "runas" // This triggers UAC
                };

                var process = Process.Start(startInfo);
                return process != null;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // User cancelled UAC prompt
                Logger.Warn("User cancelled UAC elevation prompt");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error launching elevated process");
                return false;
            }
        }

        /// <summary>
        /// Removes the scheduled task. Call this during uninstall.
        /// </summary>
        public static void UnregisterTask()
        {
            ScheduledTaskService.RemoveTask();
        }

        /// <summary>
        /// Full uninstall: removes task and deployed files.
        /// </summary>
        public static void Uninstall()
        {
            ScheduledTaskService.Uninstall();
        }
    }
}
