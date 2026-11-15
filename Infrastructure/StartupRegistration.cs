using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace SennheiserTray
{
    public interface IStartupCommandStore
    {
        string? Read();
        void Write(string command);
        void Delete();
    }

    public sealed class StartupRegistration
    {
        private readonly IStartupCommandStore _commandStore;
        private readonly Func<string?> _processPathProvider;
        private readonly Func<string?> _assemblyPathProvider;

        public StartupRegistration()
            : this(
                new WindowsStartupCommandStore(),
                () => Environment.ProcessPath,
                () => Assembly.GetEntryAssembly()?.Location)
        {
        }

        public StartupRegistration(
            IStartupCommandStore commandStore,
            Func<string?> processPathProvider,
            Func<string?> assemblyPathProvider)
        {
            _commandStore = commandStore ?? throw new ArgumentNullException(nameof(commandStore));
            _processPathProvider = processPathProvider
                ?? throw new ArgumentNullException(nameof(processPathProvider));
            _assemblyPathProvider = assemblyPathProvider
                ?? throw new ArgumentNullException(nameof(assemblyPathProvider));
        }

        public bool IsEnabled()
        {
            return !string.IsNullOrWhiteSpace(_commandStore.Read());
        }

        public void SetEnabled(bool enabled)
        {
            if (!enabled)
            {
                _commandStore.Delete();
                return;
            }

            string processPath = _processPathProvider()
                ?? throw new InvalidOperationException("The application executable path is unavailable.");
            string startupCommand = $"\"{processPath}\"";
            if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
            {
                string assemblyPath = _assemblyPathProvider()
                    ?? throw new InvalidOperationException("The application assembly path is unavailable.");
                startupCommand = $"\"{processPath}\" \"{assemblyPath}\"";
            }

            _commandStore.Write(startupCommand);
        }
    }

    public sealed class WindowsStartupCommandStore : IStartupCommandStore
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SennheiserTray";

        public string? Read()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }

        public void Write(string command)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }

        public void Delete()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
