using Shared.Enums;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Read-only widget property mirroring whether usbip-win2 is installed on the host.
    /// Synced from the helper; widget uses this to show/hide the install prompt.
    /// </summary>
    internal class UsbipInstalledProperty : WidgetProperty<bool>
    {
        public UsbipInstalledProperty() : base(false, null, Function.Viiper_UsbipInstalled)
        {
        }

        public override bool SetValue(object inValue, long inUpdatedTime)
        {
            bool result = base.SetValue(inValue, inUpdatedTime);
            // base.SetValue only notifies when the value actually changed. Listeners (Setup
            // tab) still need to know a status arrived/settled, to unstick "Installing..."
            // when the value stayed false (cancelled/failed install), so raise a local
            // change event in that case only - never a second one when base already did.
            if (!result || !changedInLastSet)
            {
                InvokePropertyChanged(nameof(Value));
            }
            changedInLastSet = false;
            return result;
        }

        private bool changedInLastSet;

        /// <summary>
        /// Read-only, helper-authoritative status: update listeners locally but never send the
        /// widget's (possibly stale) value back to the helper as a Set, which overwrote the
        /// helper's detected install state on the individual-sync path.
        /// </summary>
        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            changedInLastSet = true;
            InvokePropertyChanged(propertyName);
        }
    }
}
