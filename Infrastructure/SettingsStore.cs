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

        public void Load()
        {
            lock (_gate)
            {
                if (!File.Exists(SettingsPath))
                {
                    GamingLatencyHintDismissed = false;
                    return;
                }

                SettingsData? data = JsonSerializer.Deserialize<SettingsData>(
                    File.ReadAllText(SettingsPath));
                GamingLatencyHintDismissed = data?.GamingLatencyHintDismissed ?? false;
            }
        }

        public void DismissGamingLatencyHint()
        {
            lock (_gate)
            {
                Directory.CreateDirectory(SettingsDirectory);
                string temporaryPath = SettingsPath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(new SettingsData(true)));
                File.Move(temporaryPath, SettingsPath, overwrite: true);
                GamingLatencyHintDismissed = true;
            }
        }

        private sealed record SettingsData(bool GamingLatencyHintDismissed);
    }
}
