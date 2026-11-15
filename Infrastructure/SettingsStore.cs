using System.IO;
using System.Text.Json;

namespace SennheiserTray
{
    public sealed class SettingsStore
    {
        private readonly string _settingsDirectory;
        private readonly string _settingsPath;
        private readonly object _gate = new();

        public bool GamingLatencyHintDismissed { get; private set; }
        public bool IsDarkTheme { get; private set; }
        public bool AutoConnectEnabled { get; private set; } = true;
        public string MuteHotkeyKey { get; private set; } = "M";

        public SettingsStore()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SennheiserTray",
                "settings.json"))
        {
        }

        public SettingsStore(string settingsPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
            _settingsPath = Path.GetFullPath(settingsPath);
            _settingsDirectory = Path.GetDirectoryName(_settingsPath)
                ?? throw new ArgumentException("The settings path must include a directory.", nameof(settingsPath));
        }

        public void Load()
        {
            lock (_gate)
            {
                if (!File.Exists(_settingsPath))
                {
                    ApplyDefaults();
                    return;
                }

                SettingsData? data = JsonSerializer.Deserialize<SettingsData>(
                    File.ReadAllText(_settingsPath));
                GamingLatencyHintDismissed = data?.GamingLatencyHintDismissed ?? false;
                IsDarkTheme = data?.IsDarkTheme ?? false;
                AutoConnectEnabled = data?.AutoConnectEnabled ?? true;
                MuteHotkeyKey = data?.MuteHotkeyKey is "M" or "U" or "F9"
                    ? data.MuteHotkeyKey
                    : "M";
            }
        }

        private void ApplyDefaults()
        {
            GamingLatencyHintDismissed = false;
            IsDarkTheme = false;
            AutoConnectEnabled = true;
            MuteHotkeyKey = "M";
        }

        public void DismissGamingLatencyHint()
        {
            lock (_gate)
            {
                SaveSettings(
                    _settingsDirectory,
                    _settingsPath,
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
                    _settingsDirectory,
                    _settingsPath,
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
                    _settingsDirectory,
                    _settingsPath,
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
                    _settingsDirectory,
                    _settingsPath,
                    GamingLatencyHintDismissed,
                    IsDarkTheme,
                    AutoConnectEnabled,
                    muteHotkeyKey);
                MuteHotkeyKey = muteHotkeyKey;
            }
        }

        private static void SaveSettings(
            string settingsDirectory,
            string settingsPath,
            bool gamingLatencyHintDismissed,
            bool isDarkTheme,
            bool autoConnectEnabled,
            string muteHotkeyKey)
        {
            Directory.CreateDirectory(settingsDirectory);
            string temporaryPath = settingsPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new SettingsData(
                    gamingLatencyHintDismissed,
                    isDarkTheme,
                    autoConnectEnabled,
                    muteHotkeyKey)));
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }

        private sealed record SettingsData(
            bool GamingLatencyHintDismissed,
            bool IsDarkTheme = false,
            bool? AutoConnectEnabled = null,
            string? MuteHotkeyKey = null);
    }
}
