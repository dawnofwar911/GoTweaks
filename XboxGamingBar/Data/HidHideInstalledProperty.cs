using Shared.Enums;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Read-only widget property mirroring whether HidHide is installed on the host.
    /// Synced from the helper; the Setup tab uses this for its status row.
    /// </summary>
    internal class HidHideInstalledProperty : WidgetProperty<bool>
    {
        public HidHideInstalledProperty() : base(false, null, Function.HidHideInstalled)
        {
        }

        public override bool SetValue(object inValue, long inUpdatedTime)
        {
            bool result = base.SetValue(inValue, inUpdatedTime);
            // Always notify so listeners (Setup tab) know the status arrived/settled,
            // unsticking "Installing..." even if value remained false (e.g. cancelled/failed install)
            NotifyPropertyChanged(nameof(Value));
            return result;
        }
    }
}
