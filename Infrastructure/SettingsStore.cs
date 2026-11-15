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

        public void Load()
        {
            lock (_gate)
            {
                if (!File.Exists(SettingsPath))
                {
                    GamingLatencyHintDismissed = false;
                    IsDarkTheme = false;
                    AutoConnectEnabled = true;
                    return;
                }

                SettingsData? data = JsonSerializer.Deserialize<SettingsData>(
                    File.ReadAllText(SettingsPath));
                GamingLatencyHintDismissed = data?.GamingLatencyHintDismissed ?? false;
                IsDarkTheme = data?.IsDarkTheme ?? false;
                AutoConnectEnabled = data?.AutoConnectEnabled ?? true;
            }
        }

        public void DismissGamingLatencyHint()
        {
            lock (_gate)
            {
                SaveSettings(
                    gamingLatencyHintDismissed: true,
                    isDarkTheme: IsDarkTheme,
                    autoConnectEnabled: AutoConnectEnabled);
                GamingLatencyHintDismissed = true;
            }
        }

        public void SetDarkTheme(bool isDarkTheme)
        {
            lock (_gate)
            {
                SaveSettings(GamingLatencyHintDismissed, isDarkTheme, AutoConnectEnabled);
                IsDarkTheme = isDarkTheme;
            }
        }

        public void SetAutoConnectEnabled(bool autoConnectEnabled)
        {
            lock (_gate)
            {
                SaveSettings(GamingLatencyHintDismissed, IsDarkTheme, autoConnectEnabled);
                AutoConnectEnabled = autoConnectEnabled;
            }
        }

        private static void SaveSettings(
            bool gamingLatencyHintDismissed,
            bool isDarkTheme,
            bool autoConnectEnabled)
        {
            Directory.CreateDirectory(SettingsDirectory);
            string temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new SettingsData(
                    gamingLatencyHintDismissed,
                    isDarkTheme,
                    autoConnectEnabled)));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }

        private sealed record SettingsData(
            bool GamingLatencyHintDismissed,
            bool IsDarkTheme = false,
            bool? AutoConnectEnabled = null);
    }
}
