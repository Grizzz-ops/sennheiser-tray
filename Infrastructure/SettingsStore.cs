using System.IO;
using System.Text.Json;

namespace SennheiserTray
{
    public sealed class SettingsStore
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SennheiserTray");
        private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
        private readonly object _gate = new();

        public bool GamingLatencyHintDismissed { get; private set; }
        public bool IsDarkTheme { get; private set; }
        public bool AutoConnectEnabled { get; private set; } = true;
        public string MuteHotkeyKey { get; private set; } = "M";

        public void Load()
        {
            lock (_gate)
            {
                if (!File.Exists(SettingsPath))
                {
                    GamingLatencyHintDismissed = false;
                    IsDarkTheme = false;
                    AutoConnectEnabled = true;
                    MuteHotkeyKey = "M";
                    return;
                }

                SettingsData? data = JsonSerializer.Deserialize<SettingsData>(
                    File.ReadAllText(SettingsPath));
                GamingLatencyHintDismissed = data?.GamingLatencyHintDismissed ?? false;
                IsDarkTheme = data?.IsDarkTheme ?? false;
                AutoConnectEnabled = data?.AutoConnectEnabled ?? true;
                MuteHotkeyKey = data?.MuteHotkeyKey is "M" or "U" or "F9"
                    ? data.MuteHotkeyKey
                    : "M";
            }
        }

        public void DismissGamingLatencyHint()
        {
            lock (_gate)
            {
                SaveSettings(
                    gamingLatencyHintDismissed: true,
                    isDarkTheme: IsDarkTheme,
                    autoConnectEnabled: AutoConnectEnabled,
                    muteHotkeyKey: MuteHotkeyKey);
                GamingLatencyHintDismissed = true;
            }
        }

        public void SetDarkTheme(bool isDarkTheme)
        {
            lock (_gate)
            {
                SaveSettings(
                    GamingLatencyHintDismissed,
                    isDarkTheme,
                    AutoConnectEnabled,
                    MuteHotkeyKey);
                IsDarkTheme = isDarkTheme;
            }
        }

        public void SetAutoConnectEnabled(bool autoConnectEnabled)
        {
            lock (_gate)
            {
                SaveSettings(
                    GamingLatencyHintDismissed,
                    IsDarkTheme,
                    autoConnectEnabled,
                    MuteHotkeyKey);
                AutoConnectEnabled = autoConnectEnabled;
            }
        }

        public void SetMuteHotkeyKey(string muteHotkeyKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(muteHotkeyKey);
            if (muteHotkeyKey is not ("M" or "U" or "F9"))
            {
                throw new ArgumentOutOfRangeException(nameof(muteHotkeyKey));
            }

            lock (_gate)
            {
                SaveSettings(
                    GamingLatencyHintDismissed,
                    IsDarkTheme,
                    AutoConnectEnabled,
                    muteHotkeyKey);
                MuteHotkeyKey = muteHotkeyKey;
            }
        }

        private static void SaveSettings(
            bool gamingLatencyHintDismissed,
            bool isDarkTheme,
            bool autoConnectEnabled,
            string muteHotkeyKey)
        {
            Directory.CreateDirectory(SettingsDirectory);
            string temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new SettingsData(
                    gamingLatencyHintDismissed,
                    isDarkTheme,
                    autoConnectEnabled,
                    muteHotkeyKey)));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }

        private sealed record SettingsData(
            bool GamingLatencyHintDismissed,
            bool IsDarkTheme = false,
            bool? AutoConnectEnabled = null,
            string? MuteHotkeyKey = null);
    }
}
