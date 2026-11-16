using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// VIIPER input source selector. Persisted globally.
    /// Valid values: "XInput" (any connected Xbox-style controller)
    /// or "LegionHid" (default; Legion Go controllers via raw HID for gyro/paddles).
    /// "LegionHid" is only honoured when a Legion Go is detected
    /// (ViiperEmulationManager.ResolveInputSource falls back to XInput elsewhere),
    /// so the default is safe on non-Legion handhelds.
    /// </summary>
    internal class ViiperInputSourceProperty : HelperProperty<string, SettingsManager>
    {
        public const string Default = "LegionHid";
        private const string SettingsKey = "ViiperInputSource";

        public ViiperInputSourceProperty(SettingsManager inManager)
            : base(LoadFromSettings(), null, Function.Viiper_InputSource, inManager)
        {
            Logger.Info($"ViiperInputSource loaded: {Value}");
        }

        private static string LoadFromSettings()
        {
            if (LocalSettingsHelper.TryGetValue<string>(SettingsKey, out var value) && !string.IsNullOrEmpty(value))
            {
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
            Logger.Info($"ViiperInputSource changed: {Value}");
            SaveToSettings();
        }
    }
}
