using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// VIIPER virtual device type selector. Persisted globally.
    /// Valid values match libviiper device type strings:
    ///   xbox360, dualshock4, dualsenseedge, xboxelite2,
    ///   steam-generic, switchpro, joycon-pair.
    /// </summary>
    internal class ViiperDeviceTypeProperty : HelperProperty<string, SettingsManager>
    {
        public const string Default = "xbox360";
        private const string SettingsKey = "ViiperDeviceType";

        public ViiperDeviceTypeProperty(SettingsManager inManager)
            : base(LoadFromSettings(), null, Function.Viiper_DeviceType, inManager)
        {
            Logger.Info($"ViiperDeviceType loaded: {Value}");
        }

        private static string LoadFromSettings()
        {
            if (LocalSettingsHelper.TryGetValue<string>(SettingsKey, out var value) && !string.IsNullOrEmpty(value))
            {
                if (string.Equals(value, "dualsenseedge", System.StringComparison.OrdinalIgnoreCase))
                {
                    // One-time migration: the top-level "dualsenseedge" value only came from the
                    // short-lived default in 436a73a; the widget never offers it at top level
                    // (DualSense Edge is selected as "sony" + Sony sub-device "dualsense-edge",
                    // which is unaffected). Persist the migrated value so the saved setting,
                    // the helper and the widget agree instead of re-coercing on every start.
                    LocalSettingsHelper.SetValue(SettingsKey, Default);
                    return Default;
                }
                return value;
            }
            return Default;
        }

        private void SaveToSettings()
        {
            LocalSettingsHelper.SetValue(SettingsKey, Value ?? Default);
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"ViiperDeviceType changed: {Value}");
            SaveToSettings();
        }
    }
}
