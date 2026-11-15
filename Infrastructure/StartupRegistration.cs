using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace SennheiserTray
{
    public static class StartupRegistration
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SennheiserTray";

        public static bool IsEnabled()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string command
                && !string.IsNullOrWhiteSpace(command);
        }

        public static void SetEnabled(bool enabled)
        {
            if (!enabled)
            {
                using RegistryKey? existingKey = Registry.CurrentUser.OpenSubKey(
                    RunKeyPath,
                    writable: true);
                existingKey?.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            string processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The application executable path is unavailable.");
            string startupCommand = $"\"{processPath}\"";
            if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
            {
                string assemblyPath = Assembly.GetEntryAssembly()?.Location
                    ?? throw new InvalidOperationException("The application assembly path is unavailable.");
                startupCommand = $"\"{processPath}\" \"{assemblyPath}\"";
            }

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");
            key.SetValue(ValueName, startupCommand, RegistryValueKind.String);
        }
    }
}
