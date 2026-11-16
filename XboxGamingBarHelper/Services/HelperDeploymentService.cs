using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;

namespace XboxGamingBarHelper.Services
{
    /// <summary>
    /// Manages deployment of the helper executable to a stable location outside the MSIX package.
    /// This ensures the scheduled task path remains valid across MSIX updates.
    /// </summary>
    public static class HelperDeploymentService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Package family name used when the process has no package identity (deployed
        /// helper). Must match Package.appxmanifest's Identity (Name + Publisher CN=dg); the
        /// same constant is used for LocalState paths throughout the helper.
        /// </summary>
        public const string FallbackPackageFamilyName = "PlayandBuildCustom.10365195AA1EC_8edemd50ez3gg";
        private const string PackageNamePrefix = "PlayandBuildCustom.10365195AA1EC_";

        /// <summary>
        /// Folder the elevated scheduled task runs the helper from. It lives under
        /// %ProgramFiles% (inherits Administrators/SYSTEM/TrustedInstaller full control,
        /// Users read+execute) so that no medium-integrity process can replace the exe or
        /// its DLLs and get silently elevated at the next logon / task run. Only an elevated
        /// process (setup, or the running elevated helper's upgrade path) writes here.
        /// </summary>
        public static readonly string HelperFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "GoTweaks", "Helper"
        );

        /// <summary>
        /// The pre-hardening deploy location (package LocalCache, user-writable). Now only a
        /// staging copy: the non-elevated bootstrapper copies the package files here so the
        /// UAC --setup can be launched from a plain filesystem path instead of WindowsApps
        /// (elevating the WindowsApps exe directly wedged pre-Main, 2026-07-29). It is never
        /// run by the scheduled task, and setup never copies FROM it (it copies from the
        /// validated package folder passed with --source).
        /// </summary>
        public static readonly string StagingFolder = Path.Combine(
            GetPackageLocalCachePath(),
            "GoTweaks", "Helper"
        );

        public static readonly string StagedExePath = Path.Combine(StagingFolder, "XboxGamingBarHelper.exe");

        /// <summary>
        /// True when HelperFolder (and its GoTweaks parent) are real directories under
        /// Program Files, not reparse points redirecting writes elsewhere.
        /// </summary>
        public static bool IsHelperFolderAdminProtected
        {
            get
            {
                try
                {
                    string programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).TrimEnd('\\') + "\\";
                    string full = Path.GetFullPath(HelperFolder);
                    if (!full.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)) return false;
                    foreach (var dir in new[] { Path.GetDirectoryName(full), full })
                    {
                        if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                            return false;
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        // Owner SIDs Windows uses for the WindowsApps root on a non-system volume
        // (apps moved / installed to another drive): TrustedInstaller and SYSTEM.
        private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        private const string LocalSystemSid = "S-1-5-18";

        /// <summary>
        /// Splits a full path under a WindowsApps root into the part below that root.
        /// %ProgramFiles%\WindowsApps is always accepted. &lt;drive&gt;:\WindowsApps (packages
        /// installed to another volume) is accepted only when <paramref name="verifyOwner"/>
        /// is false (lexical check) or its owner is TrustedInstaller/SYSTEM, since a
        /// standard user can create a folder with that name on a secondary NTFS volume.
        /// </summary>
        private static bool TryGetWindowsAppsRelativePath(string fullPath, bool verifyOwner, out string relative)
        {
            relative = null;
            if (string.IsNullOrEmpty(fullPath)) return false;

            string programFilesWindowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\";
            if (fullPath.StartsWith(programFilesWindowsApps, StringComparison.OrdinalIgnoreCase))
            {
                relative = fullPath.Substring(programFilesWindowsApps.Length);
                return true;
            }

            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) return false; // no UNC
            string volumeWindowsApps = Path.Combine(root, "WindowsApps") + "\\";
            if (!fullPath.StartsWith(volumeWindowsApps, StringComparison.OrdinalIgnoreCase)) return false;

            if (verifyOwner)
            {
                try
                {
                    string dir = volumeWindowsApps.TrimEnd('\\');
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
                    var owner = Directory.GetAccessControl(dir, System.Security.AccessControl.AccessControlSections.Owner)
                        .GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
                    string ownerSid = owner?.Value ?? "";
                    if (ownerSid != TrustedInstallerSid && ownerSid != LocalSystemSid)
                    {
                        Logger.Warn($"{dir} is not owned by TrustedInstaller/SYSTEM (owner {ownerSid}); not a trusted package root");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not verify owner of {volumeWindowsApps}: {ex.Message}");
                    return false;
                }
            }

            relative = fullPath.Substring(volumeWindowsApps.Length);
            return true;
        }

        private static bool IsGoTweaksPackageRelativePath(string relative)
        {
            return !string.IsNullOrEmpty(relative)
                && relative.StartsWith(PackageNamePrefix, StringComparison.OrdinalIgnoreCase)
                && relative.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        /// <summary>
        /// True when <paramref name="dir"/> is a GoTweaks package folder (or a subfolder of
        /// one) under a WindowsApps root (TrustedInstaller-owned, so its contents can't be
        /// swapped by a medium-integrity process). Verifies the owner of a non-system-volume
        /// WindowsApps root, so call it from the elevated side.
        /// </summary>
        public static bool IsTrustedPackageSourceDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                string full = Path.GetFullPath(dir).TrimEnd('\\');
                return TryGetWindowsAppsRelativePath(full, verifyOwner: true, out string rest)
                    && IsGoTweaksPackageRelativePath(rest);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Lexical-only variant of <see cref="IsTrustedPackageSourceDirectory"/> for the
        /// non-elevated bootstrapper (which may not be allowed to read the owner of a
        /// secondary-volume WindowsApps root). It only decides whether to pass --source;
        /// the elevated setup re-validates with the owner check.
        /// </summary>
        public static bool LooksLikePackageSourceDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                string full = Path.GetFullPath(dir).TrimEnd('\\');
                return TryGetWindowsAppsRelativePath(full, verifyOwner: false, out string rest)
                    && IsGoTweaksPackageRelativePath(rest);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Package version parsed from a WindowsApps package folder name
        /// (PlayandBuildCustom.10365195AA1EC_0.3.2796.0_x64__8edemd50ez3gg\...), or null.
        /// Used when the deploying process has no package identity (e.g. the staged
        /// --setup), where the assembly-version fallback (1.0.0.0) would poison .version.
        /// </summary>
        public static string GetPackageVersionFromPath(string dir)
        {
            if (!IsTrustedPackageSourceDirectory(dir)) return null;
            try
            {
                string full = Path.GetFullPath(dir).TrimEnd('\\');
                if (!TryGetWindowsAppsRelativePath(full, verifyOwner: false, out string rest)) return null;
                string packageFolder = rest.Split('\\')[0];
                string[] parts = packageFolder.Split('_');
                if (parts.Length >= 2 && Version.TryParse(parts[1], out var v))
                {
                    return v.ToString();
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>
        /// Helper folder of the installed GoTweaks package for the current user, for
        /// processes WITHOUT package identity (the Program Files / legacy LocalCache copy
        /// started by the elevated scheduled task). Uses this process's own identity when
        /// it has one, otherwise PackageManager (current user, no admin needed). Returns
        /// null unless the result is a trusted WindowsApps package folder.
        /// </summary>
        public static string FindInstalledPackageHelperDirectory()
        {
            string candidate = null;
            try
            {
                var package = global::Windows.ApplicationModel.Package.Current;
                candidate = Path.Combine(package.InstalledLocation.Path, "XboxGamingBarHelper");
            }
            catch
            {
                candidate = null; // no package identity
            }

            if (candidate == null)
            {
                try
                {
                    var packageManager = new global::Windows.Management.Deployment.PackageManager();
                    Version bestVersion = null;
                    foreach (var package in packageManager.FindPackagesForUser(string.Empty, FallbackPackageFamilyName))
                    {
                        var pv = package.Id.Version;
                        var version = new Version(pv.Major, pv.Minor, pv.Build, pv.Revision);
                        if (bestVersion == null || version > bestVersion)
                        {
                            bestVersion = version;
                            candidate = Path.Combine(package.InstalledLocation.Path, "XboxGamingBarHelper");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"FindInstalledPackageHelperDirectory: PackageManager lookup failed: {ex.Message}");
                    candidate = null;
                }
            }

            if (candidate == null)
            {
                return null;
            }
            if (!IsTrustedPackageSourceDirectory(candidate) || !File.Exists(Path.Combine(candidate, "XboxGamingBarHelper.exe")))
            {
                Logger.Warn($"FindInstalledPackageHelperDirectory: '{candidate}' is not a trusted GoTweaks package helper folder");
                return null;
            }
            return candidate;
        }

        /// <summary>
        /// True when the Program Files copy has every mandatory file (it may still be an
        /// older version). Such a copy is admin-protected and can safely be elevated to
        /// run --setup / a self-upgrade instead of the user-writable staged copy.
        /// </summary>
        public static bool IsDeployedCopyIntact()
        {
            try
            {
                return IsHelperFolderAdminProtected
                    && MandatoryKeyFiles.All(f => File.Exists(Path.Combine(HelperFolder, f)));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// %ProgramData%\GoTweaks\&lt;name&gt; with an explicit, non-inherited ACL: Administrators
        /// and SYSTEM full control (owner Administrators), plus read/execute for Users when
        /// <paramref name="usersCanRead"/> (needed when a non-elevated process must open a
        /// file there, e.g. an installer launched through a visible UAC prompt). %ProgramData%
        /// lets Users create and modify their own files, so the ACL is (re)applied on every
        /// call and reparse points are refused. Requires elevation.
        /// </summary>
        public static string EnsureAdminOnlyDirectory(string name, bool usersCanRead)
        {
            string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GoTweaks");
            string dir = Path.Combine(parent, name);

            var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
            const System.Security.AccessControl.InheritanceFlags inherit =
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;

            System.Security.AccessControl.DirectorySecurity BuildSecurity(bool allowUsersRead)
            {
                var security = new System.Security.AccessControl.DirectorySecurity();
                security.SetAccessRuleProtection(true, false); // drop inherited ACEs (ProgramData lets Users create/modify)
                foreach (var sid in new[] { admins, system })
                {
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        sid, System.Security.AccessControl.FileSystemRights.FullControl, inherit,
                        System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                }
                if (allowUsersRead)
                {
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        users, System.Security.AccessControl.FileSystemRights.ReadAndExecute, inherit,
                        System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                }
                security.SetOwner(admins);
                return security;
            }

            foreach (var d in new[] { parent, dir })
            {
                if (Directory.Exists(d) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"{d} is a reparse point; refusing to use it for elevated files");
                }
                // The parent only ever needs traverse/read for Users (subfolders carry their own ACL).
                var security = BuildSecurity(allowUsersRead: d == parent || usersCanRead);
                if (!Directory.Exists(d))
                {
                    Directory.CreateDirectory(d, security);
                }
                // Re-apply: takes ownership back from any user who pre-created the folder.
                new DirectoryInfo(d).SetAccessControl(security);
            }
            return dir;
        }

        /// <summary>
        /// Gets the actual package LocalCache path (not virtualized).
        /// This path is accessible from outside the MSIX context.
        /// </summary>
        private static string GetPackageLocalCachePath()
        {
            try
            {
                // Get the actual LocalCache folder path from the package
                var localCache = global::Windows.Storage.ApplicationData.Current.LocalCacheFolder;
                return localCache.Path;
            }
            catch (Exception)
            {
                // Fallback if not running in package context (e.g., deployed helper)
                // Use the path we're running from if it's in the LocalCache
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (exePath.Contains("LocalCache"))
                {
                    // Extract the LocalCache path from current exe path
                    int idx = exePath.IndexOf("LocalCache", StringComparison.OrdinalIgnoreCase);
                    if (idx > 0)
                    {
                        return exePath.Substring(0, idx + "LocalCache".Length);
                    }
                }

                // Final fallback: the package's LocalCache by its known family name (the
                // deployed helper now runs from Program Files, so the exe path no longer
                // tells us where the package data lives).
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", FallbackPackageFamilyName, "LocalCache");
            }
        }

        /// <summary>
        /// Path to the deployed helper executable
        /// </summary>
        public static readonly string DeployedExePath = Path.Combine(HelperFolder, "XboxGamingBarHelper.exe");

        /// <summary>
        /// Gets the current MSIX helper executable path (in WindowsApps).
        /// This path changes with each MSIX update.
        /// </summary>
        public static string GetMsixHelperPath()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath) && exePath.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return exePath;
                }

                // If not running from MSIX, try to construct the path from package info
                var package = global::Windows.ApplicationModel.Package.Current;
                var installPath = package.InstalledLocation.Path;
                return Path.Combine(installPath, "XboxGamingBarHelper", "XboxGamingBarHelper.exe");
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not get MSIX helper path: {ex.Message}");
                // Fallback to current process path
                return Process.GetCurrentProcess().MainModule?.FileName ?? DeployedExePath;
            }
        }

        /// <summary>
        /// Path to the version tracking file
        /// </summary>
        private static readonly string VersionFilePath = Path.Combine(HelperFolder, ".version");

        /// <summary>
        /// Files that must be copied to the deployed location
        /// </summary>
        private static readonly string[] RequiredFiles = new[]
        {
            "XboxGamingBarHelper.exe",
            "XboxGamingBarHelper.exe.config",
            "ADLXCSharpBind.dll",
            "libryzenadj.dll",
            "inpoutx64.dll",
            "PresentMon.exe", // #66 Intel PresentMon CLI for FPS / displayed / FrameType capture
            "NLog.config",
            "NLog.dll",
            "Shared.dll",
            // Managed DLLs
            "LibreHardwareMonitorLib.dll",
            "HidSharp.dll",
            "RTSSSharedMemoryNET.dll",
            "RAMSPDToolkit-NDD.dll",
            // System dependencies
            "System.Text.Json.dll",
            "System.Text.Encodings.Web.dll",
            "System.Buffers.dll",
            "System.Memory.dll",
            "System.Numerics.Vectors.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "System.Threading.Tasks.Extensions.dll",
            "System.ValueTuple.dll",
            "Microsoft.Bcl.AsyncInterfaces.dll",
            "Nefarius.Drivers.HidHide.dll",
            "Nefarius.Utilities.DeviceManagement.dll",
            // VIIPER controller emulation backend (phase 1: runtime-pluggable alongside ViGEm)
            "libviiper.dll",
            // NOTE: usbip.exe is deliberately NOT listed. It is an external dependency
            // installed by usbip-win2 under C:\Program Files\USBip\ (see UsbipCli.ExePaths),
            // never shipped in the package, so it must not take part in deployment.
        };

        /// <summary>
        /// Files that must exist in the deployed folder for the deployment to be
        /// considered complete. Only files that are always shipped in the package.
        /// </summary>
        private static readonly string[] MandatoryKeyFiles = new[]
        {
            "XboxGamingBarHelper.exe",
            "Shared.dll",
            "NLog.dll",
        };

        /// <summary>
        /// Files that are checked only when the package source actually contains them,
        /// so a build that omits one degrades gracefully instead of forcing a redeploy
        /// (and the elevated --setup UAC prompt) on every launch.
        /// </summary>
        private static readonly string[] OptionalKeyFiles = new[]
        {
            "libviiper.dll",
        };

        /// <summary>
        /// Gets the package version from MSIX identity, or null when the process has no
        /// package identity (e.g. running from the deployed LocalCache copy). Use this
        /// wherever a wrong-but-plausible version is worse than no version at all —
        /// the assembly-version fallback below is a hardcoded 1.0.0.0 and stamping it
        /// into .version would wedge IsDeploymentNeeded into a permanent mismatch.
        /// </summary>
        public static string GetPackageVersionFromIdentity()
        {
            try
            {
                var package = global::Windows.ApplicationModel.Package.Current;
                var version = package.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not get package version (not running in MSIX?): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets the current package version from MSIX
        /// </summary>
        public static string GetCurrentPackageVersion()
        {
            var identityVersion = GetPackageVersionFromIdentity();
            if (identityVersion != null)
            {
                return identityVersion;
            }

            // Fall back to assembly version
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return version?.ToString() ?? "0.0.0.0";
            }
            catch
            {
                return "0.0.0.0";
            }
        }

        /// <summary>
        /// Reads the deployed version from .version file
        /// </summary>
        public static string GetDeployedVersion()
        {
            try
            {
                if (File.Exists(VersionFilePath))
                {
                    return File.ReadAllText(VersionFilePath).Trim();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not read deployed version: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Checks if deployment is needed (package version != deployed version, or files missing)
        /// </summary>
        public static bool IsDeploymentNeeded()
        {
            try
            {
                // If deployed exe doesn't exist, we definitely need deployment
                if (!File.Exists(DeployedExePath))
                {
                    Logger.Info("Deployment needed: Deployed executable not found");
                    return true;
                }

                // Check version
                var currentVersion = GetCurrentPackageVersion();
                var deployedVersion = GetDeployedVersion();

                if (string.IsNullOrEmpty(deployedVersion))
                {
                    Logger.Info("Deployment needed: No deployed version found");
                    return true;
                }

                if (!string.Equals(currentVersion, deployedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info($"Deployment needed: Version mismatch (package: {currentVersion}, deployed: {deployedVersion})");
                    return true;
                }

                // Quick sanity check: verify key files exist in deployed folder.
                // Only package-shipped files count: external dependencies (usbip.exe)
                // are never deployed, and checking them made this return true forever,
                // re-triggering the elevated --setup/UAC loop (#97).
                foreach (var file in MandatoryKeyFiles)
                {
                    if (!File.Exists(Path.Combine(HelperFolder, file)))
                    {
                        Logger.Info($"Deployment needed: Key file missing: {file}");
                        return true;
                    }
                }

                string sourceDir = null;
                try { sourceDir = GetSourceDirectory(); } catch { sourceDir = null; }
                foreach (var file in OptionalKeyFiles)
                {
                    if (string.IsNullOrEmpty(sourceDir) || !File.Exists(Path.Combine(sourceDir, file)))
                    {
                        continue; // not shipped in this build: nothing to deploy, nothing to check
                    }

                    if (!File.Exists(Path.Combine(HelperFolder, file)))
                    {
                        Logger.Info($"Deployment needed: Key file missing: {file}");
                        return true;
                    }
                }

                Logger.Debug($"Deployment up to date (version {currentVersion})");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error checking if deployment is needed");
                // On error, assume deployment is needed to be safe
                return true;
            }
        }

        /// <summary>
        /// Checks if currently running from the MSIX WindowsApps folder
        /// </summary>
        public static bool IsRunningFromMsix()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                return exePath.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not determine if running from MSIX: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if currently running from the deployed location
        /// </summary>
        public static bool IsRunningFromDeployedLocation()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                return string.Equals(exePath, DeployedExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not determine if running from deployed location: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the source directory where helper files are located (MSIX package)
        /// </summary>
        public static string GetSourceDirectory()
        {
            try
            {
                // Try to get from package location first
                var package = global::Windows.ApplicationModel.Package.Current;
                var packagePath = package.InstalledLocation.Path;
                var helperSubdir = Path.Combine(packagePath, "XboxGamingBarHelper");

                if (Directory.Exists(helperSubdir))
                {
                    return helperSubdir;
                }

                // Fallback to package root
                return packagePath;
            }
            catch
            {
                // Fallback: directory of currently executing assembly
                var assemblyLocation = Assembly.GetExecutingAssembly().Location;
                return Path.GetDirectoryName(assemblyLocation) ?? "";
            }
        }

        /// <summary>
        /// Deploys helper files from the MSIX package to the admin-only HelperFolder
        /// (Program Files). Requires elevation. The source must be the installed package
        /// folder under WindowsApps (or HelperFolder itself, a no-op); a user-writable source
        /// is refused, so the elevated task can never be fed files a medium-integrity
        /// process could have swapped. Writes .version on completion.
        /// </summary>
        /// <param name="sourceOverride">Package helper folder passed to --setup with
        /// --source (the staged setup exe has no package identity).</param>
        /// <returns>True if deployment succeeded</returns>
        public static bool DeployHelper(string sourceOverride = null)
        {
            if (!ElevationBootstrapper.IsRunningAsAdmin())
            {
                Logger.Warn($"DeployHelper: not elevated; cannot write {HelperFolder}");
                return false;
            }

            string sourceDir = sourceOverride;
            if (string.IsNullOrEmpty(sourceDir) || !IsTrustedPackageSourceDirectory(sourceDir))
            {
                if (!string.IsNullOrEmpty(sourceDir))
                {
                    Logger.Warn($"DeployHelper: ignoring untrusted source override '{sourceDir}'");
                }
                sourceDir = GetSourceDirectory();
                if (!IsTrustedPackageSourceDirectory(sourceDir))
                {
                    // No package identity (staged or Program Files copy run without a usable
                    // --source): look the installed package up instead of trusting our own dir.
                    string installed = FindInstalledPackageHelperDirectory();
                    if (installed != null)
                    {
                        Logger.Info($"DeployHelper: using installed package folder {installed}");
                        sourceDir = installed;
                    }
                }
            }
            if (!PathsAreSame(sourceDir, HelperFolder) && !IsTrustedPackageSourceDirectory(sourceDir))
            {
                Logger.Error($"DeployHelper: refusing untrusted source '{sourceDir}' (must be the GoTweaks package folder under WindowsApps)");
                return false;
            }

            if (!IsHelperFolderAdminProtected)
            {
                Logger.Error($"DeployHelper: {HelperFolder} is not a plain Program Files directory (reparse point?) - refusing to deploy");
                return false;
            }

            return DeployFiles(sourceDir, HelperFolder, stampVersion: true);
        }

        /// <summary>
        /// Non-elevated: copies the package's helper files into the user-writable
        /// StagingFolder so the UAC --setup can be launched from a plain path. The staged
        /// copy is never run by the scheduled task.
        /// </summary>
        public static bool DeployToStaging()
        {
            return DeployFiles(GetSourceDirectory(), StagingFolder, stampVersion: false);
        }

        private static string ReadVersionFile(string path)
        {
            try
            {
                if (File.Exists(path)) return File.ReadAllText(path).Trim();
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not read version file {path}: {ex.Message}");
            }
            return null;
        }

        private static bool DeployFiles(string sourceDir, string targetFolder, bool stampVersion)
        {
            string targetExe = Path.Combine(targetFolder, "XboxGamingBarHelper.exe");
            string targetVersionFile = Path.Combine(targetFolder, ".version");
            try
            {
                if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
                {
                    Logger.Error($"Source directory not found: {sourceDir}");
                    return false;
                }

                Logger.Info($"Deploying helper from {sourceDir} to {targetFolder}");

                // CRITICAL: never deploy onto ourselves. When the *deployed* exe (the one
                // already living in targetFolder) is the process invoking a deploy/--setup,
                // GetSourceDirectory() resolves to targetFolder, making source == target.
                // CopyFileWithRetry deletes the target before copying, and with source==target
                // that delete removes the only copy — then File.Copy fails with "Could not find
                // file", permanently destroying the deployed install (observed 2026-05-26:
                // 24/24 files lost, leaving only .old backups + a stale .version, which then
                // makes IsDeploymentNeeded believe the install is fine forever → widget stuck
                // on "Initial Setup in progress"). If we're already running from targetFolder,
                // the files are by definition present — treat as a successful no-op.
                if (PathsAreSame(sourceDir, targetFolder))
                {
                    Logger.Warn($"Deploy source equals deployed folder ({targetFolder}); skipping self-copy (files already in place).");

                    // #97: if the files are in place but .version is missing/stale, this
                    // no-op path used to leave the mismatch in place forever. Re-stamp —
                    // but only with a real package-identity version; the assembly-version
                    // fallback (1.0.0.0) would poison the stamp and recreate the loop.
                    if (File.Exists(targetExe))
                    {
                        var identityVersion = GetPackageVersionFromIdentity();
                        if (stampVersion && identityVersion != null && ReadVersionFile(targetVersionFile) != identityVersion)
                        {
                            try
                            {
                                File.WriteAllText(targetVersionFile, identityVersion);
                                Logger.Info($"Version file re-stamped on self-copy no-op: {identityVersion}");
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"Could not re-stamp version file: {ex.Message}");
                            }
                        }
                        return true;
                    }
                    return false;
                }

                // Create target directory
                Directory.CreateDirectory(targetFolder);

                // Drop the stamp before touching any file: if this copy fails part-way the
                // folder holds a mix of versions, and a missing stamp is what makes
                // IsDeploymentNeeded (and the bootstrapper's choice of setup exe) treat it
                // as incomplete instead of trusting the old stamp.
                if (stampVersion)
                {
                    try { if (File.Exists(targetVersionFile)) File.Delete(targetVersionFile); }
                    catch (Exception ex) { Logger.Debug($"Could not remove old version stamp: {ex.Message}"); }
                }

                // Best-effort sweep of "<file>.old.<id>" leftovers from earlier deploys that
                // had to rename a locked (running) file out of the way.
                try
                {
                    foreach (var stale in Directory.GetFiles(targetFolder, "*.old.*"))
                    {
                        try { File.Delete(stale); } catch { /* still locked: next deploy */ }
                    }
                }
                catch { }

                int successCount = 0;
                int failCount = 0;

                // Copy all required files
                foreach (var fileName in RequiredFiles)
                {
                    var sourcePath = Path.Combine(sourceDir, fileName);
                    var targetPath = Path.Combine(targetFolder, fileName);

                    if (!File.Exists(sourcePath))
                    {
                        // Some files may be optional
                        Logger.Debug($"Source file not found (may be optional): {fileName}");
                        continue;
                    }

                    try
                    {
                        CopyFileWithRetry(sourcePath, targetPath);
                        successCount++;
                        Logger.Debug($"Copied: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Failed to copy {fileName}: {ex.Message}");
                        failCount++;
                    }
                }

                // Also copy any other DLLs that might be in the source directory
                try
                {
                    foreach (var dllPath in Directory.GetFiles(sourceDir, "*.dll"))
                    {
                        var fileName = Path.GetFileName(dllPath);
                        if (RequiredFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                            continue; // Already handled

                        var targetPath = Path.Combine(targetFolder, fileName);
                        try
                        {
                            CopyFileWithRetry(dllPath, targetPath);
                            successCount++;
                            Logger.Debug($"Copied additional DLL: {fileName}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Could not copy additional DLL {fileName}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Error scanning for additional DLLs: {ex.Message}");
                }

                // Only stamp the .version file when the deploy actually succeeded — the
                // exe is present and nothing failed to copy. Writing it on a partial/failed
                // deploy is what let a half-wiped folder masquerade as "up to date" and
                // blocked the self-healing redeploy path. With this guard, a broken deploy
                // leaves no (or a stale-mismatched) version stamp, so IsDeploymentNeeded
                // returns true next launch and we recover automatically.
                bool deployOk = failCount == 0 && File.Exists(targetExe);
                if (deployOk && stampVersion)
                {
                    // Stamp the version of the package the files actually came from (its
                    // WindowsApps folder name), falling back to this process's identity. Never
                    // the assembly-version fallback (1.0.0.0): that stamp never matches the
                    // package version, so IsDeploymentNeeded would re-fire setup/UAC forever.
                    var currentVersion = GetPackageVersionFromPath(sourceDir)
                        ?? GetPackageVersionFromIdentity();
                    try
                    {
                        if (currentVersion == null)
                        {
                            Logger.Warn($"Could not determine the package version of {sourceDir}; .version not written");
                        }
                        else
                        {
                            File.WriteAllText(targetVersionFile, currentVersion);
                            Logger.Info($"Version file written: {currentVersion}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not write version file: {ex.Message}");
                    }
                }
                else
                {
                    Logger.Warn($"Skipping .version stamp — deploy incomplete (failCount={failCount}, exePresent={File.Exists(targetExe)}); next launch will retry.");
                }

                Logger.Info($"Deployment complete: {successCount} files copied, {failCount} failures");
                // #97: success must match the .version stamp condition. Returning true on a
                // partial deploy (failCount>0) let PerformSetup report "Setup Complete" while
                // .version was never written — every launch then re-detected a mismatch and
                // re-fired UAC, forever. Fail loudly instead so setup surfaces the error.
                return deployOk;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error deploying helper");
                return false;
            }
        }

        /// <summary>
        /// True when two paths resolve to the same filesystem location (case-insensitive,
        /// separator- and trailing-slash-normalized). Used to refuse self-deploys.
        /// </summary>
        private static bool PathsAreSame(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                string na = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string nb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Copies a file with retry logic for locked files
        /// </summary>
        private static void CopyFileWithRetry(string sourcePath, string targetPath, int maxRetries = 3)
        {
            // Defense-in-depth against the destructive self-copy: if source and target are
            // the same file, the delete-then-copy below would wipe it. Nothing to do.
            if (PathsAreSame(sourcePath, targetPath))
            {
                return;
            }

            Exception lastException = null;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    // Try to delete existing file first
                    if (File.Exists(targetPath))
                    {
                        try
                        {
                            File.Delete(targetPath);
                        }
                        catch
                        {
                            // Try rename-then-delete approach
                            var tempPath = targetPath + ".old." + Guid.NewGuid().ToString("N").Substring(0, 8);
                            try
                            {
                                File.Move(targetPath, tempPath);
                                // Schedule deletion on reboot if we can't delete now
                                try { File.Delete(tempPath); } catch { }
                            }
                            catch
                            {
                                // File is truly locked, will try overwrite
                            }
                        }
                    }

                    File.Copy(sourcePath, targetPath, overwrite: true);
                    return; // Success
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (i < maxRetries - 1)
                    {
                        System.Threading.Thread.Sleep(100 * (i + 1)); // Exponential backoff
                    }
                }
            }

            throw lastException ?? new IOException($"Failed to copy {sourcePath}");
        }

        /// <summary>
        /// Removes the deployed helper files (for uninstall)
        /// </summary>
        public static void RemoveDeployment()
        {
            try
            {
                if (Directory.Exists(HelperFolder))
                {
                    Logger.Info($"Removing deployment folder: {HelperFolder}");
                    Directory.Delete(HelperFolder, recursive: true);
                    Logger.Info("Deployment removed successfully");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not remove deployment folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if deployment exists and is valid
        /// </summary>
        public static bool IsDeploymentValid()
        {
            return File.Exists(DeployedExePath) && !string.IsNullOrEmpty(GetDeployedVersion());
        }
    }
}
