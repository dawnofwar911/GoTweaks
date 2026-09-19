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
using System.Threading;
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

        private int isReapplyingStickyTDP = 0; // 0 = idle, 1 = reapply in flight

        private void StickyTDPToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // Skip during initialization - don't capture TDP or start timer until profile loads
            if (isLoadingStickyTDPSettings) return;
            // Skip during mode changes - don't save forced-off state
            if (isUpdatingTDPMode) return;

            Logger.Info($"StickyTDPToggle toggled to: {StickyTDPToggle.IsOn}");

            // Save setting to LocalSettings
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["StickyTDPEnabled"] = StickyTDPToggle.IsOn;

            if (StickyTDPToggle.IsOn)
            {
                // Store current TDP limit as target
                targetTDPLimit = TDPSlider.Value;
                Logger.Info($"Sticky TDP enabled - monitoring TDP limit: {targetTDPLimit}W");

                // Start the monitoring timer and reactive listener
                StartStickyTDPTimer();
            }
            else
            {
                // Stop the monitoring timer and reactive listener
                StopStickyTDPTimer();
                Logger.Info("Sticky TDP disabled");
            }

            // Trigger profile save if SaveStickyTDP is enabled
            SettingChanged(sender, e);
        }

        private void StickyTDPIntervalSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (StickyTDPIntervalSlider == null) return;
            if (isLoadingStickyTDPSettings) return;

            stickyTDPCheckIntervalSeconds = (int)Math.Round(e.NewValue);
            Logger.Info($"Sticky TDP check interval changed to: {stickyTDPCheckIntervalSeconds}s");

            // Save setting to LocalSettings
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["StickyTDPInterval"] = stickyTDPCheckIntervalSeconds;

            // Update the value display
            if (StickyTDPIntervalValue != null)
            {
                StickyTDPIntervalValue.Text = $"{stickyTDPCheckIntervalSeconds}s";
            }

            // Restart timer with new interval if it's running
            if (StickyTDPToggle?.IsOn == true)
            {
                StopStickyTDPTimer();
                StartStickyTDPTimer();
            }

            // Trigger profile save if SaveStickyTDP is enabled
            SettingChanged(sender, e);
        }

        private void StartStickyTDPTimer()
        {
            if (stickyTDPTimer == null)
            {
                stickyTDPTimer = new DispatcherTimer();
                stickyTDPTimer.Tick += StickyTDPTimer_Tick;
            }

            stickyTDPTimer.Interval = TimeSpan.FromSeconds(stickyTDPCheckIntervalSeconds);
            stickyTDPTimer.Start();

            // Subscribe to reactive hardware updates for instant drift detection
            if (currentTdp != null)
            {
                currentTdp.PropertyChanged -= CurrentTdp_PropertyChanged;
                currentTdp.PropertyChanged += CurrentTdp_PropertyChanged;
            }

            Logger.Info($"Sticky TDP timer started with {stickyTDPCheckIntervalSeconds}s interval (smart drift detection + periodic lock active)");
        }

        private void StopStickyTDPTimer()
        {
            if (stickyTDPTimer != null)
            {
                stickyTDPTimer.Stop();
                Logger.Info("Sticky TDP timer stopped");
            }

            if (currentTdp != null)
            {
                currentTdp.PropertyChanged -= CurrentTdp_PropertyChanged;
            }
        }

        /// <summary>
        /// Parses the current hardware TDP limit from the helper's formatted string.
        /// Supports Legion WMI / PawnIO ("SPL:17W SPPL:17W FPPT:17W"),
        /// RyzenAdj ("STAPM:17W FAST:17W SLOW:17W"), and legacy formats ("S:17W F:17W L:17W").
        /// </summary>
        private bool TryParseHardwareTDP(string tdpStr, out int hardwareTdp)
        {
            hardwareTdp = -1;
            if (string.IsNullOrWhiteSpace(tdpStr)) return false;

            var parts = tdpStr.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                // Check for SPL:17W (Legion WMI & PawnIO format)
                if (part.StartsWith("SPL:", StringComparison.OrdinalIgnoreCase))
                {
                    var valStr = part.Substring(4).Replace("W", "").Trim();
                    if (int.TryParse(valStr, out hardwareTdp) && hardwareTdp > 0)
                    {
                        return true;
                    }
                }
                // Check for STAPM:17W (RyzenAdj format)
                else if (part.StartsWith("STAPM:", StringComparison.OrdinalIgnoreCase))
                {
                    var valStr = part.Substring(6).Replace("W", "").Trim();
                    if (int.TryParse(valStr, out hardwareTdp) && hardwareTdp > 0)
                    {
                        return true;
                    }
                }
                // Check for legacy S:17W or SLOW:17W
                else if (part.StartsWith("SLOW:", StringComparison.OrdinalIgnoreCase))
                {
                    var valStr = part.Substring(5).Replace("W", "").Trim();
                    if (int.TryParse(valStr, out hardwareTdp) && hardwareTdp > 0)
                    {
                        return true;
                    }
                }
                else if (part.StartsWith("S:", StringComparison.OrdinalIgnoreCase) && !part.StartsWith("SPPL:", StringComparison.OrdinalIgnoreCase) && !part.StartsWith("SPPT:", StringComparison.OrdinalIgnoreCase))
                {
                    var valStr = part.Substring(2).Replace("W", "").Trim();
                    if (int.TryParse(valStr, out hardwareTdp) && hardwareTdp > 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Sends a clean ReapplyTDP request to the helper, pushing targetTDPLimit directly to hardware
        /// without modifying profile storage, causing UI flicker, or triggering N-1/N flapping.
        /// </summary>
        private async Task<bool> ReapplyStickyTDPAsync(string reason)
        {
            if (!App.IsConnected)
            {
                Logger.Warn($"Sticky TDP: Cannot reapply ({reason}) - no connection to helper app.");
                return false;
            }

            // Concurrency guard: only one reapply in flight at a time
            if (Interlocked.CompareExchange(ref isReapplyingStickyTDP, 1, 0) != 0)
            {
                Logger.Debug($"Sticky TDP: Reapply already in progress, skipping duplicate request ({reason}).");
                return false;
            }

            isStickyTDPReapplying = true;
            try
            {
                var request = new Windows.Foundation.Collections.ValueSet
                {
                    { "ReapplyTDP", true },
                    { "TargetTDP", (int)targetTDPLimit }
                };

                var response = await App.SendMessageAsync(request);
                if (response != null)
                {
                    Logger.Info($"Sticky TDP: Successfully reapplied target TDP {targetTDPLimit}W to hardware ({reason}).");
                    return true;
                }
                else
                {
                    Logger.Warn($"Sticky TDP: No response from helper when reapplying TDP ({reason}).");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Sticky TDP: Error sending ReapplyTDP ({reason}): {ex.Message}");
                return false;
            }
            finally
            {
                isStickyTDPReapplying = false;
                Interlocked.Exchange(ref isReapplyingStickyTDP, 0);
            }
        }

        /// <summary>
        /// Periodic watchdog timer tick: performs smart drift detection against live hardware TDP,
        /// restores if drift is detected, and reinforces the hardware lock periodically to prevent silent EC resets.
        /// </summary>
        private async void StickyTDPTimer_Tick(object sender, object e)
        {
            try
            {
                // Skip if AutoTDP is active - AutoTDP dynamically manages wattage
                if (autoTDPEnabled?.Value == true || AutoTDPToggle?.IsOn == true)
                {
                    Logger.Debug("Sticky TDP: Skipping tick - AutoTDP is active");
                    return;
                }

                // Skip Sticky TDP in non-Custom modes - preset modes manage TDP automatically
                if (legionGoDetected?.Value == true && legionPerformanceMode?.Value != 255)
                {
                    Logger.Debug($"Sticky TDP: Skipping tick - using {GetLegionModeShortName(legionPerformanceMode?.Value ?? 0)} preset mode");
                    return;
                }

                bool hasReading = TryParseHardwareTDP(currentTdp?.Value, out int currentHardwareLimit);
                if (hasReading)
                {
                    if (currentHardwareLimit != (int)targetTDPLimit)
                    {
                        // Smart Detection: hardware limit drifted away from target (e.g. OEM app changed it)
                        Logger.Info($"Sticky TDP: Hardware TDP drift detected ({currentHardwareLimit}W != {targetTDPLimit}W), restoring target TDP...");
                        await ReapplyStickyTDPAsync("drift detected on tick");
                    }
                    else
                    {
                        // Hardware matches target, in lock - no drift detected
                        Logger.Debug($"Sticky TDP: Hardware limit ({currentHardwareLimit}W) matches target ({targetTDPLimit}W). In lock.");
                    }
                }
                else
                {
                    // Hardware reading unparseable or initializing - reassert target as fallback
                    Logger.Debug($"Sticky TDP: Live hardware limit unparseable ('{currentTdp?.Value}'). Re-asserting target {targetTDPLimit}W.");
                    await ReapplyStickyTDPAsync("unparseable live read fallback");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Sticky TDP timer tick: {ex.Message}");
            }
        }

        /// <summary>
        /// Reactive drift listener: when the helper publishes a new CurrentTDP reading, immediately
        /// detects external tampering without waiting up to 5 seconds for the next timer tick.
        /// </summary>
        private async void CurrentTdp_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            try
            {
                // Skip if Sticky TDP is disabled
                if (StickyTDPToggle?.IsOn != true) return;

                // Skip during initialization or mode transitions
                if (isLoadingStickyTDPSettings || isUpdatingTDPMode) return;

                // Skip if AutoTDP is actively managing TDP
                if (autoTDPEnabled?.Value == true || AutoTDPToggle?.IsOn == true) return;

                // Skip in non-Custom preset modes
                if (legionGoDetected?.Value == true && legionPerformanceMode?.Value != 255) return;

                if (TryParseHardwareTDP(currentTdp?.Value, out int currentHardwareLimit))
                {
                    if (currentHardwareLimit != (int)targetTDPLimit)
                    {
                        Logger.Info($"Sticky TDP: Reactive drift detected from hardware reading ({currentHardwareLimit}W != {targetTDPLimit}W). Restoring target TDP...");
                        await ReapplyStickyTDPAsync("reactive external change");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error in Sticky TDP reactive listener: {ex.Message}");
            }
        }

    }
}
