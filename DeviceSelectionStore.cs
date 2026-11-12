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
            File.WriteAllText(DevicePath, deviceId, Encoding.UTF8);
        }
    }
}
