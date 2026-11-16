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
        private static void SetScreenSaverEnabled(bool enabled)
        {
            screenSaverEnabled = enabled;
            if (enabled)
            {
                if (screenSaverTimer == null)
                {
                    ResolveHibernateXInput();
                    if (_resumeBaselineTickCount == 0)
                    {
                        _resumeBaselineTickCount = (uint)Environment.TickCount;
                    }
                    screenSaverTimer = new System.Threading.Timer(ScreenSaverIdleCheck, null, ScreenSaverCheckIntervalMs, ScreenSaverCheckIntervalMs);
                    Logger.Info("Screen Saver: Started idle monitoring timer");
                }
            }
            else
            {
                if (screenSaverTimer != null)
                {
                    screenSaverTimer.Dispose();
                    screenSaverTimer = null;
                    screenSaverTriggered = false;
                    Logger.Info("Screen Saver: Stopped idle monitoring timer");
                }
            }
        }

        private static void ScreenSaverIdleCheck(object state)
        {
            if (!screenSaverEnabled) return;

            try
            {
                // Never turn off screen while a game is actively in foreground. Only trust
                // RunningGame as "a game" when Profile "Games Only" is on: with it off,
                // SystemManager reports ANY foreground window (desktop apps, Explorer) as the
                // running game, which disabled the screen saver everywhere. Without that
                // classification fall through to the idle timers (keyboard/mouse + gamepad),
                // which already keep the screen on while a game is being played.
                bool gamesOnly = settingsManager?.ProfileGamesOnly?.Value == true;
                if (gamesOnly && systemManager?.RunningGame?.Value.IsValid() == true && systemManager.RunningGame.Value.IsForeground)
                {
                    return;
                }

                var lastInput = new LASTINPUTINFO();
                lastInput.cbSize = (uint)Marshal.SizeOf(lastInput);

                if (GetLastInputInfo(ref lastInput))
                {
                    uint nowTick = (uint)Environment.TickCount;
                    uint kbmIdleMs = nowTick - lastInput.dwTime;
                    uint gamepadIdleMs = GetGamepadIdleMs(nowTick);
                    uint idleMs = Math.Min(kbmIdleMs, gamepadIdleMs);

                    // Clamp to time-since-last-resume so a stale pre-sleep timestamp can't
                    // immediately blank the screen right after wake.
                    uint sinceResumeMs = nowTick - _resumeBaselineTickCount;
                    idleMs = Math.Min(idleMs, sinceResumeMs);

                    if (idleMs >= ScreenSaverIdleTimeoutMs)
                    {
                        if (!screenSaverTriggered)
                        {
                            screenSaverTriggered = true;
                            Logger.Info($"Screen Off: Idle for {idleMs}ms, turning off display");
                            SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, MONITOR_OFF);
                        }
                    }
                    else if (screenSaverTriggered)
                    {
                        // User moved mouse/pressed key/gamepad — wake display and re-arm for next idle period
                        screenSaverTriggered = false;
                        Logger.Info("Screen Saver: Input detected, re-armed");
                        SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, MONITOR_ON);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Screen Saver: Idle check failed: {ex.Message}");
            }
        }

    }
}
