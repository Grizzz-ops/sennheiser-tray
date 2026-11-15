using System.Text.Json;
using Xunit;

namespace SennheiserTray.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void MissingSettingsUseSafeDefaults()
    {
        string settingsPath = CreateSettingsPath();
        try
        {
            var store = new SettingsStore(settingsPath);
            store.Load();

            Assert.False(store.GamingLatencyHintDismissed);
            Assert.False(store.IsDarkTheme);
            Assert.True(store.AutoConnectEnabled);
            Assert.Equal("M", store.MuteHotkeyKey);
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public void InvalidHotkeyFallsBackWhileValidSettingsAreLoaded()
    {
        string settingsPath = CreateSettingsPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(
                settingsPath,
                """{"GamingLatencyHintDismissed":true,"IsDarkTheme":true,"AutoConnectEnabled":false,"MuteHotkeyKey":"invalid"}""");

            var store = new SettingsStore(settingsPath);
            store.Load();

            Assert.True(store.GamingLatencyHintDismissed);
            Assert.True(store.IsDarkTheme);
            Assert.False(store.AutoConnectEnabled);
            Assert.Equal("M", store.MuteHotkeyKey);
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public void SettingsChangesPersistAndInvalidHotkeysAreRejected()
    {
        string settingsPath = CreateSettingsPath();
        try
        {
            var store = new SettingsStore(settingsPath);
            store.Load();
            store.SetDarkTheme(true);
            store.SetAutoConnectEnabled(false);
            store.SetMuteHotkeyKey("F9");

            Assert.Throws<ArgumentOutOfRangeException>(() => store.SetMuteHotkeyKey("Q"));

            var reloaded = new SettingsStore(settingsPath);
            reloaded.Load();
            Assert.True(reloaded.IsDarkTheme);
            Assert.False(reloaded.AutoConnectEnabled);
            Assert.Equal("F9", reloaded.MuteHotkeyKey);
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    [Fact]
    public void SettingsWithInvalidBooleanTypesFailExplicitly()
    {
        string settingsPath = CreateSettingsPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, """{"IsDarkTheme":"yes"}""");

            Assert.Throws<JsonException>(() => new SettingsStore(settingsPath).Load());
        }
        finally
        {
            DeleteSettingsDirectory(settingsPath);
        }
    }

    private static string CreateSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"SennheiserTray.Tests.{Guid.NewGuid():N}", "settings.json");

    private static void DeleteSettingsDirectory(string settingsPath)
    {
        string? directory = Path.GetDirectoryName(settingsPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
