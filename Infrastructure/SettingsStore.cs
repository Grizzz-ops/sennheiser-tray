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

        public void Load()
        {
            lock (_gate)
            {
                if (!File.Exists(SettingsPath))
                {
                    GamingLatencyHintDismissed = false;
                    IsDarkTheme = false;
                    return;
                }

                SettingsData? data = JsonSerializer.Deserialize<SettingsData>(
                    File.ReadAllText(SettingsPath));
                GamingLatencyHintDismissed = data?.GamingLatencyHintDismissed ?? false;
                IsDarkTheme = data?.IsDarkTheme ?? false;
            }
        }

        public void DismissGamingLatencyHint()
        {
            lock (_gate)
            {
                SaveSettings(gamingLatencyHintDismissed: true, isDarkTheme: IsDarkTheme);
                GamingLatencyHintDismissed = true;
            }
        }

        public void SetDarkTheme(bool isDarkTheme)
        {
            lock (_gate)
            {
                SaveSettings(GamingLatencyHintDismissed, isDarkTheme);
                IsDarkTheme = isDarkTheme;
            }
        }

        private static void SaveSettings(bool gamingLatencyHintDismissed, bool isDarkTheme)
        {
            Directory.CreateDirectory(SettingsDirectory);
            string temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new SettingsData(gamingLatencyHintDismissed, isDarkTheme)));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }

        private sealed record SettingsData(bool GamingLatencyHintDismissed, bool IsDarkTheme = false);
    }
}
