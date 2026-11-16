using Microsoft.Gaming.XboxGameBar;
using Microsoft.Gaming.XboxGameBar.Input;
using Microsoft.UI.Xaml.Controls;
using NLog;
using Shared.Data;
using Shared.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.System.Power;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.UI;
using XboxGamingBar.Data;
using XboxGamingBar.Event;
using XboxGamingBar.IPC;
using XboxGamingBar.QuickSettings;
using Shared.Enums;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {

        private void UpdateGameProfileCardVisibility()
        {
            bool hasGame = HasValidGame(currentGameName);
            // When the per-game profile toggle is off, the GLOBAL profile is what's
            // actually applied to hardware. Showing a per-game card with default values
            // (which the user never picked and which aren't being applied) is misleading
            // — hide the entire card and let the rest of the widget UI surface the
            // active global profile via the slider/toggle states it already reflects.
            bool perGameProfileInUse = hasGame && (PerGameProfileToggle?.IsOn ?? false);
            bool powerSourceEnabled = perGameProfileInUse && GetPerGamePowerSourceProfileEnabled(currentGameName);
            UpdatePowerSourceProfileScopeText();

            if (perGameProfileInUse)
            {
                GameProfileCard.Visibility = Visibility.Visible;

                if (powerSourceEnabled)
                {
                    GameProfileWithPowerSource.Visibility = Visibility.Visible;
                    GameProfileWithoutPowerSource.Visibility = Visibility.Collapsed;
                    GameProfileTitleWithPower.Text = currentGameName;
                }
                else
                {
                    GameProfileWithPowerSource.Visibility = Visibility.Collapsed;
                    GameProfileWithoutPowerSource.Visibility = Visibility.Visible;
                    GameProfileTitleNoPower.Text = currentGameName;
                }
            }
            else
            {
                GameProfileCard.Visibility = Visibility.Collapsed;
            }
        }

        private static string GetXmlVal(System.Xml.Linq.XDocument doc, string elementName)
        {
            var el = doc.Descendants(elementName).FirstOrDefault();
            if (el == null) return null;
            var nilAttr = el.Attribute(System.Xml.Linq.XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "nil");
            if (nilAttr != null && (nilAttr.Value == "true" || nilAttr.Value == "1")) return null;
            return el.Value;
        }

        private static double GetXmlDouble(System.Xml.Linq.XDocument doc, string elementName, double fallback)
        {
            string val = GetXmlVal(doc, elementName);
            if (!string.IsNullOrEmpty(val) && double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double res))
                return res;
            return fallback;
        }

        private static int GetXmlInt(System.Xml.Linq.XDocument doc, string elementName, int fallback)
        {
            string val = GetXmlVal(doc, elementName);
            if (!string.IsNullOrEmpty(val) && int.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out int res))
                return res;
            return fallback;
        }

        private static bool GetXmlBool(System.Xml.Linq.XDocument doc, string elementName, bool fallback)
        {
            string val = GetXmlVal(doc, elementName);
            if (!string.IsNullOrEmpty(val) && bool.TryParse(val, out bool res))
                return res;
            return fallback;
        }

        /// <summary>
        /// Scans LocalState/profiles/*.xml (the helper's on-disk profile store) and ensures
        /// that every profile has a corresponding Profile_Game_* container in LocalSettings.
        /// This restores user profiles after MSIX updates, package reinstallations, or
        /// app data resets where LocalSettings was cleared but LocalState was preserved.
        /// </summary>
        internal void SyncProfilesFromHelperXmls()
        {
            // Recovery path only: run at most once per widget session, and only when the
            // local profile store has been wiped (no Profile_Game_* containers at all).
            // Running it on every listing / game switch resurrected profiles the user had
            // just deleted, because the helper deletes profiles/<exe>.xml asynchronously.
            if (profileXmlImportChecked) return;
            profileXmlImportChecked = true;

            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Containers.Keys.Any(k => k.StartsWith("Profile_Game_", StringComparison.Ordinal)))
                {
                    Logger.Debug("SyncProfilesFromHelperXmls: local game profiles present, skipping XML import");
                    return;
                }

                string profilesFolder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "profiles");
                if (!Directory.Exists(profilesFolder)) return;

                var xmlFiles = Directory.GetFiles(profilesFolder, "*.xml");
                if (xmlFiles.Length == 0) return;

                int restoredCount = 0;

                foreach (var xmlPath in xmlFiles)
                {
                    try
                    {
                        var doc = System.Xml.Linq.XDocument.Load(xmlPath);
                        var gameId = doc.Descendants("GameId").FirstOrDefault();
                        string rawTitle = gameId?.Element("Name")?.Value;
                        string fullPath = gameId?.Element("Path")?.Value;
                        string fileBasename = Path.GetFileNameWithoutExtension(xmlPath);

                        // Resolve a clean, valid title
                        string title = rawTitle?.Trim();
                        if (string.IsNullOrEmpty(title) || title.Contains(":\\") || title.Contains(":/"))
                        {
                            title = !string.IsNullOrEmpty(fileBasename) ? fileBasename : "Game";
                        }

                        // Sanitize title for UWP container name: container names cannot contain backslashes or slashes
                        title = title.Replace('\\', '_').Replace('/', '_').Trim();
                        if (string.IsNullOrEmpty(title)) continue;

                        var titlesToSeed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { title };
                        if (!string.IsNullOrEmpty(rawTitle) && !rawTitle.Contains(":\\") && !rawTitle.Contains(":/"))
                        {
                            titlesToSeed.Add(rawTitle.Replace('\\', '_').Replace('/', '_'));
                        }

                        // Never re-import a profile the user deleted (the helper may not have
                        // removed its XML yet, or was disconnected when the delete happened).
                        if (IsGameProfileTombstoned(fileBasename) || titlesToSeed.Any(IsGameProfileTombstoned))
                        {
                            Logger.Debug($"SyncProfilesFromHelperXmls: skipping deleted profile {Path.GetFileName(xmlPath)}");
                            continue;
                        }

                        // Check if containers already exist for any alias of this profile
                        bool alreadyExists = titlesToSeed.Any(t =>
                            settings.Containers.ContainsKey($"Profile_Game_{t}")
                            || settings.Containers.ContainsKey($"Profile_Game_{t}_AC")
                            || settings.Containers.ContainsKey($"Profile_Game_{t}_DC"));

                        if (alreadyExists)
                        {
                            continue;
                        }

                        // Extract profile settings from XML
                        double tdp = GetXmlDouble(doc, "TDP", 15.0);
                        bool cpuBoost = GetXmlBool(doc, "CPUBoost", false);
                        double cpuEpp = GetXmlDouble(doc, "CPUEPP", 80.0);
                        bool autoTdp = GetXmlBool(doc, "AutoTDPEnabled", false);
                        int targetFps = GetXmlInt(doc, "AutoTDPTargetFPS", 60);
                        int minTdp = GetXmlInt(doc, "AutoTDPMinTDP", 8);
                        int maxTdp = GetXmlInt(doc, "AutoTDPMaxTDP", 30);
                        bool useMl = GetXmlBool(doc, "AutoTDPUseMLMode", true);
                        int ctrlType = GetXmlInt(doc, "AutoTDPControllerType", 0);
                        int perfMode = GetXmlInt(doc, "LegionPerformanceMode", 255);
                        bool tdpBoost = GetXmlBool(doc, "TDPBoostEnabled", false);
                        int boostSppt = GetXmlInt(doc, "TDPBoostSPPT", 1);
                        int boostFppt = GetXmlInt(doc, "TDPBoostFPPT", 3);
                        int fpsLimit = GetXmlInt(doc, "FPSLimit", 0);
                        bool stickyTdp = GetXmlBool(doc, "StickyTDP", false);
                        bool hdr = GetXmlBool(doc, "HDREnabled", false);

                        // DC-specific values
                        double tdpDc = GetXmlDouble(doc, "TDP_DC", tdp);
                        bool cpuBoostDc = GetXmlBool(doc, "CPUBoost_DC", cpuBoost);
                        double cpuEppDc = GetXmlDouble(doc, "CPUEPP_DC", cpuEpp);
                        bool autoTdpDc = GetXmlBool(doc, "AutoTDPEnabled_DC", autoTdp);
                        int targetFpsDc = GetXmlInt(doc, "AutoTDPTargetFPS_DC", targetFps);
                        int fpsLimitDc = GetXmlInt(doc, "FPSLimit_DC", fpsLimit);

                        long lastMod = File.GetLastWriteTimeUtc(xmlPath).Ticks;

                        Action<ApplicationDataContainer, bool> populate = (cont, isDc) =>
                        {
                            cont.Values["TDP"] = (double)(isDc ? tdpDc : tdp);
                            cont.Values["CPUBoost"] = (bool)(isDc ? cpuBoostDc : cpuBoost);
                            cont.Values["CPUEPP"] = (double)(isDc ? cpuEppDc : cpuEpp);
                            cont.Values["FluidMotionFrames"] = false;
                            cont.Values["RadeonSuperResolution"] = false;
                            cont.Values["RadeonSuperResolutionSharpness"] = (double)80;
                            cont.Values["ImageSharpening"] = false;
                            cont.Values["ImageSharpeningSharpness"] = (double)80;
                            cont.Values["RadeonAntiLag"] = false;
                            cont.Values["RadeonBoost"] = false;
                            cont.Values["RadeonBoostResolution"] = (double)0;
                            cont.Values["RadeonChill"] = false;
                            cont.Values["RadeonChillMinFPS"] = (double)30;
                            cont.Values["RadeonChillMaxFPS"] = (double)60;
                            int effFps = isDc ? fpsLimitDc : fpsLimit;
                            cont.Values["FPSLimitEnabled"] = (bool)(effFps > 0);
                            cont.Values["FPSLimitValue"] = (int)(effFps > 0 ? effFps : 60);
                            cont.Values["AutoTDPEnabled"] = (bool)(isDc ? autoTdpDc : autoTdp);
                            cont.Values["AutoTDPTargetFPS"] = (int)(isDc ? targetFpsDc : targetFps);
                            cont.Values["AutoTDPMinTDP"] = (int)minTdp;
                            cont.Values["AutoTDPMaxTDP"] = (int)maxTdp;
                            cont.Values["AutoTDPUseMLMode"] = (bool)useMl;
                            cont.Values["AutoTDPControllerType"] = (int)ctrlType;
                            cont.Values["OSPowerMode"] = (int)1;
                            cont.Values["LegionPerformanceMode"] = (int)perfMode;
                            cont.Values["TDPModeIndex"] = (int)-1;
                            cont.Values["TDPBoostEnabled"] = (bool)tdpBoost;
                            cont.Values["TDPBoostSPPT"] = (int)boostSppt;
                            cont.Values["TDPBoostFPPT"] = (int)boostFppt;
                            cont.Values["HDREnabled"] = (bool)hdr;
                            cont.Values["Resolution"] = "";
                            cont.Values["StickyTDPEnabled"] = (bool)stickyTdp;
                            cont.Values["StickyTDPInterval"] = (int)5;
                            cont.Values["OverlayLevel"] = (int)0;
                            if (!string.IsNullOrEmpty(fullPath))
                                cont.Values["GameExePath"] = fullPath;
                            cont.Values["LastModifiedUtc"] = lastMod;
                        };

                        foreach (var tName in titlesToSeed)
                        {
                            var singleCont = settings.CreateContainer($"Profile_Game_{tName}", ApplicationDataCreateDisposition.Always);
                            populate(singleCont, false);

                            var acCont = settings.CreateContainer($"Profile_Game_{tName}_AC", ApplicationDataCreateDisposition.Always);
                            populate(acCont, false);

                            var dcCont = settings.CreateContainer($"Profile_Game_{tName}_DC", ApplicationDataCreateDisposition.Always);
                            populate(dcCont, true);

                            // Also restore controller profile if present in XML
                            if (!settings.Containers.ContainsKey($"ControllerProfile_Game_{tName}"))
                            {
                                var ctrlCont = settings.CreateContainer($"ControllerProfile_Game_{tName}", ApplicationDataCreateDisposition.Always);
                                string y1 = GetXmlVal(doc, "LegionButtonY1");
                                if (!string.IsNullOrEmpty(y1)) ctrlCont.Values["ButtonY1"] = y1;
                                string y2 = GetXmlVal(doc, "LegionButtonY2");
                                if (!string.IsNullOrEmpty(y2)) ctrlCont.Values["ButtonY2"] = y2;
                                string y3 = GetXmlVal(doc, "LegionButtonY3");
                                if (!string.IsNullOrEmpty(y3)) ctrlCont.Values["ButtonY3"] = y3;
                                string m1 = GetXmlVal(doc, "LegionButtonM1");
                                if (!string.IsNullOrEmpty(m1)) ctrlCont.Values["ButtonM1"] = m1;
                                string m2 = GetXmlVal(doc, "LegionButtonM2");
                                if (!string.IsNullOrEmpty(m2)) ctrlCont.Values["ButtonM2"] = m2;
                                string m3 = GetXmlVal(doc, "LegionButtonM3");
                                if (!string.IsNullOrEmpty(m3)) ctrlCont.Values["ButtonM3"] = m3;
                                string dt = GetXmlVal(doc, "LegionButtonDesktop");
                                if (!string.IsNullOrEmpty(dt)) ctrlCont.Values["ButtonDesktop"] = dt;
                                string pg = GetXmlVal(doc, "LegionButtonPage");
                                if (!string.IsNullOrEmpty(pg)) ctrlCont.Values["ButtonPage"] = pg;
                                string lb = GetXmlVal(doc, "LegionLightBrightness");
                                // LoadControllerProfileFromStorage reads "LightBrightness" as int.
                                if (!string.IsNullOrEmpty(lb) && double.TryParse(lb, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double pLb))
                                    ctrlCont.Values["LightBrightness"] = (int)Math.Round(pLb);
                                if (!string.IsNullOrEmpty(fullPath)) ctrlCont.Values["GameExePath"] = fullPath;
                            }
                        }

                        restoredCount++;
                        Logger.Info($"SyncProfilesFromHelperXmls: Restored profile containers for '{title}' (from {Path.GetFileName(xmlPath)})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"SyncProfilesFromHelperXmls: Failed to import {Path.GetFileName(xmlPath)}: {ex.Message}");
                    }
                }

                if (restoredCount > 0)
                {
                    Logger.Info($"SyncProfilesFromHelperXmls: Successfully restored {restoredCount} profile(s) from disk");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"SyncProfilesFromHelperXmls failed: {ex.Message}");
            }
        }

        // Set once SyncProfilesFromHelperXmls has evaluated the one-shot import this session.
        private bool profileXmlImportChecked;

        private const string DeletedGameProfilesContainer = "DeletedGameProfiles";

        /// <summary>
        /// Records that the user deleted the profile for <paramref name="gameName"/> so the
        /// XML recovery import never brings it back.
        /// </summary>
        internal static void TombstoneGameProfile(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                var cont = settings.CreateContainer(DeletedGameProfilesContainer, ApplicationDataCreateDisposition.Always);
                cont.Values[gameName.Trim()] = DateTime.UtcNow.Ticks;
            }
            catch (Exception ex)
            {
                Logger.Warn($"TombstoneGameProfile({gameName}) failed: {ex.Message}");
            }
        }

        internal static bool IsGameProfileTombstoned(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return false;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (!settings.Containers.TryGetValue(DeletedGameProfilesContainer, out var cont) || cont == null)
                    return false;
                string key = gameName.Trim();
                foreach (var k in cont.Values.Keys)
                {
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"IsGameProfileTombstoned({gameName}) failed: {ex.Message}");
            }
            return false;
        }

        private List<string> GetAllSavedGameProfiles()
        {
            SyncProfilesFromHelperXmls();

            var gameNames = new HashSet<string>();
            var settings = ApplicationData.Current.LocalSettings;

            // Enumerate all containers looking for game profiles
            foreach (var containerName in settings.Containers.Keys)
            {
                if (containerName.StartsWith("Profile_Game_"))
                {
                    // Extract game name from container key
                    string gameName = containerName.Substring("Profile_Game_".Length);

                    // Remove _AC or _DC suffix if present
                    if (gameName.EndsWith("_AC"))
                    {
                        gameName = gameName.Substring(0, gameName.Length - 3);
                    }
                    else if (gameName.EndsWith("_DC"))
                    {
                        gameName = gameName.Substring(0, gameName.Length - 3);
                    }

                    gameNames.Add(gameName);
                }
            }

            return gameNames.OrderBy(name => name).ToList();
        }

        // Set true once we've restored the user's saved sort mode into the ComboBox.
        // Without this guard, the SelectionChanged handler would keep re-running on
        // each restoration attempt, causing redundant re-renders.
        private bool _profileSortModeRestored;
        private bool _profilesTabNeedsRefresh = true;

        private void UpdateAllGameProfilesDisplay()
        {
            if (AllGameProfilesContainer == null)
                return;

            // PERFORMANCE OPTIMIZATION:
            // Only rebuild the entire profile cards list if the Game tab is currently visible.
            // When user is on Performance, Quick, or Settings tab, defer this expensive
            // disk I/O and visual tree reconstruction until they switch to the Game tab.
            if (GameScrollViewer == null || GameScrollViewer.Visibility != Visibility.Visible)
            {
                _profilesTabNeedsRefresh = true;
                Logger.Debug("UpdateAllGameProfilesDisplay deferred - Game tab is not visible");
                return;
            }
            _profilesTabNeedsRefresh = false;

            // Restore the persisted sort mode on the first render. The XAML default is
            // "name"; on subsequent app starts we honor whatever the user picked last.
            if (!_profileSortModeRestored && ProfileSortComboBox != null)
            {
                _profileSortModeRestored = true;
                try
                {
                    if (ApplicationData.Current.LocalSettings.Values.TryGetValue("ProfileSortMode", out var modeObj)
                        && modeObj is string saved)
                    {
                        foreach (var item in ProfileSortComboBox.Items)
                        {
                            if (item is ComboBoxItem cbi && (cbi.Tag as string) == saved)
                            {
                                if (ProfileSortComboBox.SelectedItem != cbi)
                                {
                                    ProfileSortComboBox.SelectedItem = cbi;
                                    // SelectionChanged will fire and call us back; bail
                                    // here so we don't render twice with stale data.
                                    return;
                                }
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Restoring ProfileSortMode failed: {ex.Message}");
                }
            }

            // Clear existing game profile cards
            AllGameProfilesContainer.Children.Clear();

            var savedGames = GetAllSavedGameProfiles();

            if (savedGames.Count == 0)
            {
                // Show "No saved game profiles" message
                var noProfilesText = new TextBlock
                {
                    Text = "No saved game profiles yet. Play a game with Per-Game Profiles enabled to create profiles.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 160, 160, 160)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                AllGameProfilesContainer.Children.Add(noProfilesText);
                return;
            }

            // Backfill GameExePath and LastModifiedUtc onto legacy widget profile
            // containers (created before we started stamping those keys at save time).
            // Without this step, profiles created in earlier builds never group, never
            // show their icon, and never show a "modified Xago" line — even though the
            // helper has all the info we need sitting in LocalState/profiles/*.xml.
            BackfillLegacyContainersFromHelperXmls();

            // Pull the title→exe-basename map from the helper's per-exe XML profiles
            // so legacy widget profiles (saved before we started stamping GameExePath
            // into LocalSettings containers) can still be grouped by their owning exe.
            var helperTitleMap = BuildTitleToExeBasenameMap();

            // Bucket every saved game profile by the exe it belongs to. Profiles for
            // the same exe (e.g. multiple titles played in Citron) collapse into one
            // parent card with an Expander; orphan profiles render flat.
            var groups = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var gameName in savedGames)
            {
                // Skip the active game — its card is rendered above this list.
                if (gameName == currentGameName && HasValidGame(currentGameName))
                    continue;

                string groupKey = ResolveGroupKeyForProfile(gameName, helperTitleMap) ?? gameName;
                if (!groups.TryGetValue(groupKey, out var list))
                {
                    list = new List<string>();
                    groups[groupKey] = list;
                }
                list.Add(gameName);
            }

            // Sort groups according to the user's choice in ProfileSortComboBox. Sorting
            // happens at two levels: across groups (group-key by name; max LastModified;
            // max TDP), and inside each group (always by name — within an exe, alphabetical
            // child order is the most predictable).
            string sortMode = (ProfileSortComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "name";

            IEnumerable<KeyValuePair<string, List<string>>> orderedGroups;
            switch (sortMode)
            {
                case "modified":
                    orderedGroups = groups.OrderByDescending(kv => kv.Value.Max(GetMostRecentLastModifiedTicks));
                    break;
                case "tdp":
                    orderedGroups = groups.OrderByDescending(kv => kv.Value.Max(GetProfileTopTdp));
                    break;
                default: // "name"
                    orderedGroups = groups; // SortedDictionary already alphabetical
                    break;
            }

            foreach (var kv in orderedGroups)
            {
                // Always wrap in an Expander, even for single-profile groups, so the
                // collapsed list is visually uniform (every entry the same height —
                // less eye-jumping when scanning, less scrolling overall).
                AllGameProfilesContainer.Children.Add(RenderProfileGroupExpander(kv.Key, kv.Value));
            }
        }

        private void ProfileSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var tag = (ProfileSortComboBox?.SelectedItem as ComboBoxItem)?.Tag as string;
                if (!string.IsNullOrEmpty(tag))
                {
                    ApplicationData.Current.LocalSettings.Values["ProfileSortMode"] = tag;
                }
                UpdateAllGameProfilesDisplay();
            }
            catch (Exception ex)
            {
                Logger.Debug($"ProfileSortComboBox_SelectionChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the highest TDP across the profile's single/_AC/_DC containers, used
        /// for "TDP (high → low)" sort. Falls back to int.MinValue for missing/legacy
        /// profiles so they sort to the bottom.
        /// </summary>
        private int GetProfileTopTdp(string gameName)
        {
            int max = int.MinValue;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                foreach (var suffix in new[] { "", "_AC", "_DC" })
                {
                    var key = $"Profile_Game_{gameName}{suffix}";
                    if (settings.Containers.ContainsKey(key)
                        && settings.Containers[key].Values.TryGetValue("TDP", out var tdpObj))
                    {
                        int v = Convert.ToInt32(tdpObj);
                        if (v > max) max = v;
                    }
                }
            }
            catch { }
            return max;
        }

        /// <summary>
        /// Tries to map a widget profile (keyed by window title) back to its owning exe
        /// basename. Order: container-stored GameExePath (new profiles), then helper XML
        /// title→exe map (legacy profiles whose title still matches the helper's last
        /// recorded name for that exe). Returns null when no mapping is available — the
        /// caller will fall back to using the title as the group key (1-profile group).
        /// </summary>
        private string ResolveGroupKeyForProfile(string gameName, Dictionary<string, string> helperTitleMap)
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                foreach (var suffix in new[] { "", "_AC", "_DC" })
                {
                    var key = $"Profile_Game_{gameName}{suffix}";
                    if (settings.Containers.ContainsKey(key)
                        && settings.Containers[key].Values.TryGetValue("GameExePath", out var pathObj)
                        && pathObj is string path
                        && !string.IsNullOrEmpty(path))
                    {
                        return Path.GetFileNameWithoutExtension(path);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"ResolveGroupKeyForProfile({gameName}) container read failed: {ex.Message}");
            }

            if (helperTitleMap != null && helperTitleMap.TryGetValue(gameName, out var helperBasename))
            {
                return helperBasename;
            }
            return null;
        }

        /// <summary>
        /// Walks every Profile_Game_* container in LocalSettings and stamps GameExePath
        /// + LastModifiedUtc on legacy ones using data from the helper's per-exe profile
        /// XMLs (LocalState/profiles/*.xml — same package, so widget can read them).
        ///
        /// Two matching strategies, in priority order:
        ///   1. Direct title match: helper XML's &lt;Name&gt; equals the widget's profile
        ///      key. Always safe — that's the most recent title the helper saw for that
        ///      exe.
        ///   2. Word-boundary substring match: the exe basename appears as a whole word
        ///      in the title (regex \b...\b, case-insensitive, basename ≥ 4 chars).
        ///      Catches the emulator pattern — Citron / Eden / Yuzu / RetroArch each
        ///      produce many distinct widget profiles (one per game played), but the
        ///      helper only retains the latest title in citron.xml etc. The substring
        ///      match recovers the rest.
        /// Substring match is restricted to UNAMBIGUOUS cases (only one helper basename
        /// matches) so generic exe basenames like "Code" don't grab unrelated titles
        /// like "Code Vein". Idempotent: containers that already have GameExePath are
        /// skipped, so this is cheap to call on every Profiles-tab render.
        /// </summary>
        private void BackfillLegacyContainersFromHelperXmls()
        {
            try
            {
                string profilesFolder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "profiles");
                if (!Directory.Exists(profilesFolder)) return;

                // Build helper-side lookup table once: per exe XML, the basename, full
                // exe path (from GameId/Path), file's last write time, and the latest
                // recorded title.
                var helperEntries = new List<(string basename, string fullPath, DateTime lastWrite, string title)>();
                foreach (var xmlPath in Directory.GetFiles(profilesFolder, "*.xml"))
                {
                    try
                    {
                        var doc = System.Xml.Linq.XDocument.Load(xmlPath);
                        var gameId = doc.Descendants("GameId").FirstOrDefault();
                        string title = gameId?.Element("Name")?.Value;
                        string fullPath = gameId?.Element("Path")?.Value;
                        if (string.IsNullOrEmpty(fullPath)) continue;

                        helperEntries.Add((
                            Path.GetFileNameWithoutExtension(xmlPath),
                            fullPath,
                            File.GetLastWriteTimeUtc(xmlPath),
                            title ?? string.Empty));
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Backfill: parse {xmlPath} failed: {ex.Message}");
                    }
                }
                if (helperEntries.Count == 0) return;

                var settings = ApplicationData.Current.LocalSettings;
                var containerNames = settings.Containers.Keys
                    .Where(k => k.StartsWith("Profile_Game_"))
                    .ToList();

                int filled = 0;
                foreach (var containerName in containerNames)
                {
                    var container = settings.Containers[containerName];
                    bool needsExe = !container.Values.ContainsKey("GameExePath");
                    bool needsMod = !container.Values.ContainsKey("LastModifiedUtc");
                    if (!needsExe && !needsMod) continue;

                    string suffixed = containerName.Substring("Profile_Game_".Length);
                    string title = suffixed;
                    if (suffixed.EndsWith("_AC")) title = suffixed.Substring(0, suffixed.Length - 3);
                    else if (suffixed.EndsWith("_DC")) title = suffixed.Substring(0, suffixed.Length - 3);

                    // 1) Direct title match.
                    var match = helperEntries.FirstOrDefault(e =>
                        string.Equals(e.title, title, StringComparison.OrdinalIgnoreCase));

                    // 2) Word-boundary substring match (emulators).
                    if (string.IsNullOrEmpty(match.fullPath))
                    {
                        var candidates = helperEntries
                            .Where(e => e.basename.Length >= 4
                                && System.Text.RegularExpressions.Regex.IsMatch(
                                    title,
                                    $"\\b{System.Text.RegularExpressions.Regex.Escape(e.basename)}\\b",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            .ToList();
                        if (candidates.Count == 1)
                        {
                            match = candidates[0];
                        }
                    }

                    if (string.IsNullOrEmpty(match.fullPath)) continue;

                    if (needsExe)
                    {
                        container.Values["GameExePath"] = match.fullPath;
                        filled++;
                    }
                    if (needsMod)
                    {
                        container.Values["LastModifiedUtc"] = match.lastWrite.Ticks;
                    }
                }

                if (filled > 0)
                {
                    Logger.Info($"Profiles backfill: stamped GameExePath on {filled} legacy container(s) using helper XML data");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"BackfillLegacyContainersFromHelperXmls failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the helper's per-exe profile XMLs (LocalState/profiles/*.xml — same
        /// package, so the widget can read them) and returns a map of the most recent
        /// window title → exe basename for each exe. Used to retroactively group
        /// pre-existing widget profiles that don't have GameExePath stamped in their
        /// LocalSettings container.
        /// </summary>
        private Dictionary<string, string> BuildTitleToExeBasenameMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string profilesFolder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "profiles");
                if (!Directory.Exists(profilesFolder)) return map;

                foreach (var xmlPath in Directory.GetFiles(profilesFolder, "*.xml"))
                {
                    try
                    {
                        var doc = System.Xml.Linq.XDocument.Load(xmlPath);
                        var nameEl = doc.Descendants("Name").FirstOrDefault();
                        if (nameEl != null && !string.IsNullOrEmpty(nameEl.Value))
                        {
                            string baseName = Path.GetFileNameWithoutExtension(xmlPath);
                            map[nameEl.Value] = baseName;
                            map[nameEl.Value.Trim()] = baseName;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"BuildTitleToExeBasenameMap parse {xmlPath}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"BuildTitleToExeBasenameMap enumerate failed: {ex.Message}");
            }
            return map;
        }

        /// <summary>
        /// Builds the parent Border for a multi-profile exe group. Header shows the exe
        /// name and a "N profiles" badge; the muxc:Expander collapses the children by
        /// default so users with lots of emulator-spawned per-title profiles don't
        /// scroll past everything to find what they want.
        /// </summary>
        private Border RenderProfileGroupExpander(string exeBasename, List<string> profileNames)
        {
            var inner = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var name in profileNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var child = RenderProfileCardInternal(name);
                if (child != null)
                {
                    inner.Children.Add(child);
                }
            }

            var headerStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Every child profile in this group shares the same exe (that's what put
            // them in the same group), so any child's stored GameExePath is enough
            // to surface the helper-cached icon next to the group header. First child
            // with a stamped path wins; legacy profiles in the group quietly skip.
            string groupExePath = null;
            foreach (var name in profileNames)
            {
                var path = TryGetExePathForGame(name);
                if (!string.IsNullOrEmpty(path))
                {
                    groupExePath = path;
                    break;
                }
            }
            if (!string.IsNullOrEmpty(groupExePath))
            {
                string iconPath = TryResolveCachedIconPath(groupExePath);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    headerStack.Children.Add(new Image
                    {
                        Width = 24,
                        Height = 24,
                        VerticalAlignment = VerticalAlignment.Center,
                        Source = new BitmapImage(new Uri(iconPath))
                    });
                }
            }

            headerStack.Children.Add(new TextBlock
            {
                Text = exeBasename,
                FontSize = 14,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 100)),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerStack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 60, 80)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 1, 8, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = profileNames.Count == 1 ? "1 profile" : $"{profileNames.Count} profiles",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White)
                }
            });

            var expander = new Microsoft.UI.Xaml.Controls.Expander
            {
                Header = headerStack,
                Content = inner,
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            return new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Child = expander
            };
        }

        /// <summary>
        /// Renders a single per-game profile card (title row, AC/DC split badge, and
        /// either the AC/DC comparison grid or the single-profile grid). Returns the
        /// constructed Border so the caller decides whether to drop it directly into
        /// AllGameProfilesContainer (single-profile group) or wrap it inside a
        /// multi-profile group's Expander body.
        /// </summary>
        private Border RenderProfileCardInternal(string gameName)
        {
            try
            {
                // Load profiles
                var settings = ApplicationData.Current.LocalSettings;
                bool hasAC = settings.Containers.ContainsKey($"Profile_Game_{gameName}_AC");
                bool hasDC = settings.Containers.ContainsKey($"Profile_Game_{gameName}_DC");
                bool hasACDC = hasAC || hasDC;
                bool hasSingle = settings.Containers.ContainsKey($"Profile_Game_{gameName}");
                bool gamePowerSourceSplit = GetPerGamePowerSourceProfileEnabled(gameName);

                Border profileCard = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 42, 26)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 58, 58)),
                    BorderThickness = new Thickness(1)
                };

                var stackPanel = new StackPanel();
                profileCard.Child = stackPanel;

                // Title row: [optional icon] [title + "modified Xago"] [delete button]
                var titleGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Try to surface the helper-cached exe icon next to the title.
                // GameIconHelper writes to LocalCache/icons/<basename>_<hash>.png; both
                // widget and helper share the same package so the widget can read it.
                string exePathForIcon = TryGetExePathForGame(gameName);
                if (!string.IsNullOrEmpty(exePathForIcon))
                {
                    string iconPath = TryResolveCachedIconPath(exePathForIcon);
                    if (!string.IsNullOrEmpty(iconPath))
                    {
                        var icon = new Image
                        {
                            Width = 24,
                            Height = 24,
                            Margin = new Thickness(0, 0, 8, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Source = new BitmapImage(new Uri(iconPath))
                        };
                        Grid.SetColumn(icon, 0);
                        titleGrid.Children.Add(icon);
                    }
                }

                // Title + last-modified subtitle stacked vertically.
                var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                titleStack.Children.Add(new TextBlock
                {
                    Text = gameName,
                    FontSize = 13,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 165, 0)),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                string modifiedText = GetMostRecentLastModifiedText(gameName);
                if (!string.IsNullOrEmpty(modifiedText))
                {
                    titleStack.Children.Add(new TextBlock
                    {
                        Text = modifiedText,
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150))
                    });
                }
                Grid.SetColumn(titleStack, 1);
                titleGrid.Children.Add(titleStack);

                // Delete button
                var deleteButton = new Button
                {
                    Content = "🗑️",
                    FontSize = 12,
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 0, 0)),
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = gameName,  // Store game name for delete handler
                    BorderBrush = new SolidColorBrush(Windows.UI.Colors.Transparent),
                    BorderThickness = new Thickness(2)
                };
                deleteButton.Click += DeleteProfileButton_Click;
                deleteButton.GotFocus += (s, args) =>
                {
                    deleteButton.BorderBrush = new SolidColorBrush(Windows.UI.Colors.White);
                    deleteButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 50, 50));
                };
                deleteButton.LostFocus += (s, args) =>
                {
                    deleteButton.BorderBrush = new SolidColorBrush(Windows.UI.Colors.Transparent);
                    deleteButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 0, 0));
                };
                Grid.SetColumn(deleteButton, 2);
                titleGrid.Children.Add(deleteButton);

                stackPanel.Children.Add(titleGrid);
                stackPanel.Children.Add(new TextBlock
                {
                    Text = $"AC/DC split: {(gamePowerSourceSplit ? "On" : "Off")}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 180, 180)),
                    Margin = new Thickness(0, 0, 0, 6)
                });

                if (gamePowerSourceSplit && hasACDC)
                {
                    // Load AC/DC profiles
                    var gameAC = new PerformanceProfile();
                    var gameDC = new PerformanceProfile();
                    if (hasAC)
                    {
                        LoadProfileFromStorage($"Game_{gameName}_AC", gameAC);
                    }
                    else if (hasSingle)
                    {
                        LoadProfileFromStorage($"Game_{gameName}", gameAC);
                    }

                    if (hasDC)
                    {
                        LoadProfileFromStorage($"Game_{gameName}_DC", gameDC);
                    }
                    else if (hasSingle)
                    {
                        LoadProfileFromStorage($"Game_{gameName}", gameDC);
                    }

                    // Create AC/DC comparison grid
                    var acDcGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                    // Add rows dynamically based on enabled settings
                    for (int i = 0; i < 20; i++) // Max rows
                        acDcGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    acDcGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    acDcGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    acDcGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    int rowIndex = 0;

                    // Headers
                    AddTextBlock(acDcGrid, rowIndex, 1, "AC", 10, "#FFD700", horizontalAlignment: HorizontalAlignment.Center);
                    AddTextBlock(acDcGrid, rowIndex, 2, "DC", 10, "#FF6B6B", horizontalAlignment: HorizontalAlignment.Center);
                    rowIndex++;

                    // TDP Mode (Legion only)
                    if (legionGoDetected?.Value == true && SaveTDP)
                    {
                        AddTextBlock(acDcGrid, rowIndex, 0, "Mode", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                        AddTextBlock(acDcGrid, rowIndex, 1, GetProfileTDPModeName(gameAC), 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        AddTextBlock(acDcGrid, rowIndex, 2, GetProfileTDPModeName(gameDC), 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        rowIndex++;
                    }

                    // TDP
                    if (SaveTDP)
                    {
                        AddTextBlock(acDcGrid, rowIndex, 0, "TDP", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                        AddTextBlock(acDcGrid, rowIndex, 1, $"{gameAC.TDP}W", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        AddTextBlock(acDcGrid, rowIndex, 2, $"{gameDC.TDP}W", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        rowIndex++;

                        // TDP Boost (saved with TDP)
                        AddTextBlock(acDcGrid, rowIndex, 0, "TDP Boost", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                        AddTextBlock(acDcGrid, rowIndex, 1, gameAC.TDPBoostEnabled ? "On" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        AddTextBlock(acDcGrid, rowIndex, 2, gameDC.TDPBoostEnabled ? "On" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        rowIndex++;
                    }

                    // Boost
                    if (SaveCPUBoost)
                    {
                        AddTextBlock(acDcGrid, rowIndex, 0, "Boost", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                        AddTextBlock(acDcGrid, rowIndex, 1, gameAC.CPUBoost ? "On" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        AddTextBlock(acDcGrid, rowIndex, 2, gameDC.CPUBoost ? "On" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        rowIndex++;
                    }

                    // EPP
                    if (SaveCPUEPP)
                    {
                        AddTextBlock(acDcGrid, rowIndex, 0, "EPP", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                        AddTextBlock(acDcGrid, rowIndex, 1, $"{gameAC.CPUEPP}", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        AddTextBlock(acDcGrid, rowIndex, 2, $"{gameDC.CPUEPP}", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        rowIndex++;
                    }

                    // FPS Limit (if enabled)
                    if (SaveFPSLimit)
                    {
                        AddTextBlock(acDcGrid, rowIndex, 0, "FPS Lim", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                        AddTextBlock(acDcGrid, rowIndex, 1, gameAC.FPSLimitEnabled ? $"{gameAC.FPSLimitValue}" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        AddTextBlock(acDcGrid, rowIndex, 2, gameDC.FPSLimitEnabled ? $"{gameDC.FPSLimitValue}" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        rowIndex++;
                    }

                    // AutoTDP (if enabled)
                    if (SaveAutoTDP)
                    {
                        AddTextBlock(acDcGrid, rowIndex, 0, "AutoTDP", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                        AddTextBlock(acDcGrid, rowIndex, 1, gameAC.AutoTDPEnabled ? $"{gameAC.AutoTDPTargetFPS}fps" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        AddTextBlock(acDcGrid, rowIndex, 2, gameDC.AutoTDPEnabled ? $"{gameDC.AutoTDPTargetFPS}fps" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        rowIndex++;
                    }

                    // Power Mode (if enabled)
                    if (SaveOSPowerMode)
                    {
                        AddTextBlock(acDcGrid, rowIndex, 0, "Power", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                        AddTextBlock(acDcGrid, rowIndex, 1, GetPowerModeShortName(gameAC.OSPowerMode), 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        AddTextBlock(acDcGrid, rowIndex, 2, GetPowerModeShortName(gameDC.OSPowerMode), 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        rowIndex++;
                    }

                    // AMD Features (if enabled)
                    if (SaveAMDFeatures)
                    {
                        // Build AMD features string for AC profile
                        var acAmdFeatures = GetAMDFeaturesShortString(gameAC);
                        var dcAmdFeatures = GetAMDFeaturesShortString(gameDC);

                        if (!string.IsNullOrEmpty(acAmdFeatures) || !string.IsNullOrEmpty(dcAmdFeatures))
                        {
                            AddTextBlock(acDcGrid, rowIndex, 0, "AMD", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                            AddTextBlock(acDcGrid, rowIndex, 1, string.IsNullOrEmpty(acAmdFeatures) ? "Off" : acAmdFeatures, 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                            AddTextBlock(acDcGrid, rowIndex, 2, string.IsNullOrEmpty(dcAmdFeatures) ? "Off" : dcAmdFeatures, 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                            rowIndex++;
                        }
                    }

                    // HDR (if enabled)
                    if (SaveHDR)
                    {
                        AddTextBlock(acDcGrid, rowIndex, 0, "HDR", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                        AddTextBlock(acDcGrid, rowIndex, 1, gameAC.HDREnabled ? "On" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        AddTextBlock(acDcGrid, rowIndex, 2, gameDC.HDREnabled ? "On" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        rowIndex++;
                    }

                    // Resolution (if enabled)
                    if (SaveResolution && (!string.IsNullOrEmpty(gameAC.Resolution) || !string.IsNullOrEmpty(gameDC.Resolution)))
                    {
                        AddTextBlock(acDcGrid, rowIndex, 0, "Res", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                        AddTextBlock(acDcGrid, rowIndex, 1, string.IsNullOrEmpty(gameAC.Resolution) ? "-" : gameAC.Resolution, 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        AddTextBlock(acDcGrid, rowIndex, 2, string.IsNullOrEmpty(gameDC.Resolution) ? "-" : gameDC.Resolution, 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        rowIndex++;
                    }

                    // Sticky TDP (if enabled)
                    if (SaveStickyTDP)
                    {
                        AddTextBlock(acDcGrid, rowIndex, 0, "Sticky", 10, "#AAAAAA", margin: new Thickness(0, 3, 8, 0));
                        AddTextBlock(acDcGrid, rowIndex, 1, gameAC.StickyTDPEnabled ? $"{gameAC.StickyTDPInterval}s" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        AddTextBlock(acDcGrid, rowIndex, 2, gameDC.StickyTDPEnabled ? $"{gameDC.StickyTDPInterval}s" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0), horizontalAlignment: HorizontalAlignment.Center);
                        rowIndex++;
                    }

                    stackPanel.Children.Add(acDcGrid);
                }
                else
                {
                    // Load single profile
                    var game = new PerformanceProfile();
                    if (hasSingle)
                    {
                        LoadProfileFromStorage($"Game_{gameName}", game);
                    }
                    else if (hasAC)
                    {
                        LoadProfileFromStorage($"Game_{gameName}_AC", game);
                    }
                    else if (hasDC)
                    {
                        LoadProfileFromStorage($"Game_{gameName}_DC", game);
                    }
                    else
                    {
                        // No usable container for this profile — skip without rendering.
                        return null;
                    }

                    // Create simple grid
                    var singleGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                    // Add rows dynamically based on enabled settings
                    for (int i = 0; i < 20; i++) // Max rows
                        singleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    singleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    singleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    int rowIndex = 0;

                    // TDP Mode (Legion only)
                    if (legionGoDetected?.Value == true && SaveTDP)
                    {
                        AddTextBlock(singleGrid, rowIndex, 0, "TDP Mode", 10, "#AAAAAA");
                        AddTextBlock(singleGrid, rowIndex, 1, GetProfileTDPModeName(game), 10, "#FFFFFF");
                        rowIndex++;
                    }

                    // TDP
                    if (SaveTDP)
                    {
                        AddTextBlock(singleGrid, rowIndex, 0, "TDP", 10, "#AAAAAA", margin: new Thickness(0, 3, 0, 0));
                        AddTextBlock(singleGrid, rowIndex, 1, $"{game.TDP}W", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0));
                        rowIndex++;

                        // TDP Boost (saved with TDP)
                        AddTextBlock(singleGrid, rowIndex, 0, "TDP Boost", 10, "#AAAAAA", margin: new Thickness(0, 3, 0, 0));
                        AddTextBlock(singleGrid, rowIndex, 1, game.TDPBoostEnabled ? "On" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0));
                        rowIndex++;
                    }

                    // CPU Boost
                    if (SaveCPUBoost)
                    {
                        AddTextBlock(singleGrid, rowIndex, 0, "CPU Boost", 10, "#AAAAAA", margin: new Thickness(0, 3, 0, 0));
                        AddTextBlock(singleGrid, rowIndex, 1, game.CPUBoost ? "On" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0));
                        rowIndex++;
                    }

                    // CPU EPP
                    if (SaveCPUEPP)
                    {
                        AddTextBlock(singleGrid, rowIndex, 0, "CPU EPP", 10, "#AAAAAA", margin: new Thickness(0, 3, 0, 0));
                        AddTextBlock(singleGrid, rowIndex, 1, $"{game.CPUEPP}", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0));
                        rowIndex++;
                    }

                    // FPS Limit (if enabled)
                    if (SaveFPSLimit)
                    {
                        AddTextBlock(singleGrid, rowIndex, 0, "FPS Limit", 10, "#AAAAAA", margin: new Thickness(0, 3, 0, 0));
                        AddTextBlock(singleGrid, rowIndex, 1, game.FPSLimitEnabled ? $"{game.FPSLimitValue}" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0));
                        rowIndex++;
                    }

                    // AutoTDP (if enabled)
                    if (SaveAutoTDP)
                    {
                        AddTextBlock(singleGrid, rowIndex, 0, "AutoTDP", 10, "#AAAAAA", margin: new Thickness(0, 3, 0, 0));
                        AddTextBlock(singleGrid, rowIndex, 1, game.AutoTDPEnabled ? $"{game.AutoTDPTargetFPS}fps" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0));
                        rowIndex++;
                    }

                    // Power Mode (if enabled)
                    if (SaveOSPowerMode)
                    {
                        AddTextBlock(singleGrid, rowIndex, 0, "Power Mode", 10, "#AAAAAA", margin: new Thickness(0, 3, 0, 0));
                        AddTextBlock(singleGrid, rowIndex, 1, GetPowerModeShortName(game.OSPowerMode), 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0));
                        rowIndex++;
                    }

                    // AMD Features (if enabled)
                    if (SaveAMDFeatures)
                    {
                        var amdFeatures = GetAMDFeaturesShortString(game);
                        AddTextBlock(singleGrid, rowIndex, 0, "AMD", 10, "#AAAAAA", margin: new Thickness(0, 3, 0, 0));
                        AddTextBlock(singleGrid, rowIndex, 1, string.IsNullOrEmpty(amdFeatures) ? "Off" : amdFeatures, 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0));
                        rowIndex++;
                    }

                    // HDR (if enabled)
                    if (SaveHDR)
                    {
                        AddTextBlock(singleGrid, rowIndex, 0, "HDR", 10, "#AAAAAA", margin: new Thickness(0, 3, 0, 0));
                        AddTextBlock(singleGrid, rowIndex, 1, game.HDREnabled ? "On" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0));
                        rowIndex++;
                    }

                    // Resolution (if enabled)
                    if (SaveResolution && !string.IsNullOrEmpty(game.Resolution))
                    {
                        AddTextBlock(singleGrid, rowIndex, 0, "Resolution", 10, "#AAAAAA", margin: new Thickness(0, 3, 0, 0));
                        AddTextBlock(singleGrid, rowIndex, 1, game.Resolution, 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0));
                        rowIndex++;
                    }

                    // Sticky TDP (if enabled)
                    if (SaveStickyTDP)
                    {
                        AddTextBlock(singleGrid, rowIndex, 0, "Sticky TDP", 10, "#AAAAAA", margin: new Thickness(0, 3, 0, 0));
                        AddTextBlock(singleGrid, rowIndex, 1, game.StickyTDPEnabled ? $"{game.StickyTDPInterval}s" : "Off", 10, "#FFFFFF", margin: new Thickness(0, 3, 0, 0));
                        rowIndex++;
                    }

                    stackPanel.Children.Add(singleGrid);
                }

                return profileCard;
            }
            catch (Exception ex)
            {
                Logger.Warn($"RenderProfileCardInternal({gameName}) failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolves a profile's exe path from the most recently-stamped GameExePath
        /// container value. Used both for icon lookup and for grouping; null when the
        /// profile is legacy (no exe path was stamped). The helper-XML-driven fallback
        /// path is intentionally NOT consulted here — that map is built once per render
        /// in UpdateAllGameProfilesDisplay; this function is a faster per-card lookup.
        /// </summary>
        private string TryGetExePathForGame(string gameName)
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                foreach (var suffix in new[] { "", "_AC", "_DC" })
                {
                    var key = $"Profile_Game_{gameName}{suffix}";
                    if (settings.Containers.ContainsKey(key)
                        && settings.Containers[key].Values.TryGetValue("GameExePath", out var pathObj)
                        && pathObj is string path
                        && !string.IsNullOrEmpty(path))
                    {
                        return path;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"TryGetExePathForGame({gameName}) failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Returns the path to the helper's cached icon for an exe, if it exists. Mirrors
        /// XboxGamingBarHelper.Icons.GameIconHelper.GetCachedIconPath naming exactly so
        /// the widget can read what the helper wrote (both paths land at the package's
        /// LocalCache/icons folder).
        /// </summary>
        private string TryResolveCachedIconPath(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            try
            {
                string iconsFolder = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "icons");
                if (!Directory.Exists(iconsFolder)) return null;

                string fileName;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(exePath.ToLowerInvariant()));
                    var hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    var exeName = Path.GetFileNameWithoutExtension(exePath);
                    foreach (var c in Path.GetInvalidFileNameChars()) exeName = exeName.Replace(c, '_');
                    if (exeName.Length > 32) exeName = exeName.Substring(0, 32);
                    fileName = $"{exeName}_{hashString.Substring(0, 8)}.png";
                }
                string fullPath = Path.Combine(iconsFolder, fileName);
                return File.Exists(fullPath) ? fullPath : null;
            }
            catch (Exception ex)
            {
                Logger.Debug($"TryResolveCachedIconPath({exePath}) failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns the most recent LastModifiedUtc across the single/_AC/_DC containers
        /// for the given profile, formatted as a relative-time string ("3d ago"). Null
        /// when no container has the value yet (legacy profile, never re-saved since the
        /// 2074 storage upgrade).
        /// </summary>
        private string GetMostRecentLastModifiedText(string gameName)
        {
            DateTime? newest = null;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                foreach (var suffix in new[] { "", "_AC", "_DC" })
                {
                    var key = $"Profile_Game_{gameName}{suffix}";
                    if (settings.Containers.ContainsKey(key)
                        && settings.Containers[key].Values.TryGetValue("LastModifiedUtc", out var ts)
                        && ts is long ticks)
                    {
                        var dt = new DateTime(ticks, DateTimeKind.Utc);
                        if (newest == null || dt > newest.Value) newest = dt;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"GetMostRecentLastModifiedText({gameName}) failed: {ex.Message}");
            }
            return newest.HasValue ? "modified " + FormatRelativeTime(newest.Value) : null;
        }

        /// <summary>
        /// Returns the most recent LastModifiedUtc as ticks, for sorting. Falls back to
        /// long.MinValue when no value exists so legacy profiles sort to the bottom of
        /// "Last Modified" mode.
        /// </summary>
        private long GetMostRecentLastModifiedTicks(string gameName)
        {
            long max = long.MinValue;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                foreach (var suffix in new[] { "", "_AC", "_DC" })
                {
                    var key = $"Profile_Game_{gameName}{suffix}";
                    if (settings.Containers.ContainsKey(key)
                        && settings.Containers[key].Values.TryGetValue("LastModifiedUtc", out var ts)
                        && ts is long ticks
                        && ticks > max)
                    {
                        max = ticks;
                    }
                }
            }
            catch { }
            return max;
        }

        private static string FormatRelativeTime(DateTime utc)
        {
            var diff = DateTime.UtcNow - utc;
            if (diff.TotalSeconds < 60) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)}w ago";
            if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)}mo ago";
            return utc.ToLocalTime().ToString("yyyy-MM-dd");
        }

    }
}
