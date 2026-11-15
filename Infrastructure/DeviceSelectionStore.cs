using System;
using System.IO;
using System.Text;

namespace SennheiserTray
{
    public static class DeviceSelectionStore
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SennheiserTray");
        private static readonly string DevicePath = Path.Combine(SettingsDirectory, "last-device.txt");

        public static string? ReadLastDeviceId()
        {
            if (!File.Exists(DevicePath))
            {
                return null;
            }

            string deviceId = File.ReadAllText(DevicePath, Encoding.UTF8).Trim();
            return deviceId.Length == 0 ? null : deviceId;
        }

        public static void SaveLastDeviceId(string deviceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
            Directory.CreateDirectory(SettingsDirectory);
            string temporaryPath = DevicePath + ".tmp";
            File.WriteAllText(temporaryPath, deviceId, Encoding.UTF8);
            File.Move(temporaryPath, DevicePath, overwrite: true);
        }
    }
}
