using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices.DeviceExtensions;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// SimHub device extension for MOZA mBooster Pedals. Unlike the wheel/dash
    /// extensions, mBooster has no LEDs at all, so the device.json ships with
    /// LedsFeature disabled and this extension never touches the fake-LED-driver
    /// injection trick — there is no LedModuleDevice sub-instance to find.
    ///
    /// Placeholder tab only for now — binding this to the existing
    /// MBoosterDeviceSettings model (identity resolution against
    /// MozaMBoosterRegistry, GetSettings/SetSettings persistence) is separate,
    /// larger follow-up work.
    /// </summary>
    internal class MBoosterDeviceExtension : DeviceExtension
    {
        public override string ExtentionTabTitle => "mBooster";

        public override void Init(PluginManager pluginManager)
        {
            MozaLog.Debug(
                $"[AZOM] MBoosterDeviceExtension Init — DeviceTypeID={LinkedDevice.DeviceDescriptor.DeviceTypeID}");
        }

        public override void End(PluginManager pluginManager)
        {
        }

        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
        }

        // No settings model to persist yet — see class remarks.
        public override void LoadDefaultSettings()
        {
        }

        public override JToken GetSettings()
        {
            return new JObject();
        }

        public override void SetSettings(JToken settings, bool isDefault)
        {
        }

        public override Control CreateSettingControl()
        {
            return new Label
            {
                Content = "mBooster settings — coming soon",
                Margin = new Thickness(12)
            };
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            yield break;
        }
    }
}
