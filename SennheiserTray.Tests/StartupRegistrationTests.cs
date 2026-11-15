using Xunit;

namespace SennheiserTray.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void EnablingStartupRegistersQuotedExecutablePath()
    {
        var store = new MemoryStartupCommandStore();
        var registration = new StartupRegistration(
            store,
            () => @"C:\Program Files\Sennheiser Tray\SennheiserTray.exe",
            () => null);

        registration.SetEnabled(true);

        Assert.Equal(
            "\"C:\\Program Files\\Sennheiser Tray\\SennheiserTray.exe\"",
            store.Command);
        Assert.True(registration.IsEnabled());
    }

    [Fact]
    public void DotnetHostStartupIncludesQuotedAssemblyPath()
    {
        var store = new MemoryStartupCommandStore();
        var registration = new StartupRegistration(
            store,
            () => @"C:\Program Files\dotnet\dotnet.exe",
            () => @"C:\Program Files\Sennheiser Tray\SennheiserTray.dll");

        registration.SetEnabled(true);

        Assert.Equal(
            "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\Program Files\\Sennheiser Tray\\SennheiserTray.dll\"",
            store.Command);
    }

    [Fact]
    public void DisablingStartupRemovesTheRegistration()
    {
        var store = new MemoryStartupCommandStore
        {
            Command = "\"C:\\SennheiserTray.exe\""
        };
        var registration = new StartupRegistration(store, () => null, () => null);

        registration.SetEnabled(false);

        Assert.Null(store.Command);
        Assert.False(registration.IsEnabled());
    }

    [Fact]
    public void DotnetHostRequiresAnAssemblyPath()
    {
        var registration = new StartupRegistration(
            new MemoryStartupCommandStore(),
            () => @"C:\dotnet.exe",
            () => null);

        Assert.Throws<InvalidOperationException>(() => registration.SetEnabled(true));
    }

    private sealed class MemoryStartupCommandStore : IStartupCommandStore
    {
        public string? Command { get; set; }

        public string? Read() => Command;

        public void Write(string command) => Command = command;

        public void Delete() => Command = null;
    }
}
