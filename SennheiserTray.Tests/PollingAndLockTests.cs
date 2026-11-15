using Xunit;

namespace SennheiserTray.Tests;

public sealed class PollingAndLockTests
{
    [Fact]
    public void AutoConnectPollingSlowsAfterThreeMissesAndResetsWhenFound()
    {
        var policy = new AutoConnectPollingPolicy();

        Assert.Equal(TimeSpan.FromSeconds(20), policy.Interval);
        policy.RecordScan(compatibleDeviceFound: false);
        policy.RecordScan(compatibleDeviceFound: false);
        Assert.Equal(TimeSpan.FromSeconds(20), policy.Interval);

        policy.RecordScan(compatibleDeviceFound: false);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.Interval);

        policy.RecordScan(compatibleDeviceFound: false);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.Interval);
        policy.RecordScan(compatibleDeviceFound: true);

        Assert.Equal(0, policy.ConsecutiveMisses);
        Assert.Equal(TimeSpan.FromSeconds(20), policy.Interval);
    }

    [Fact]
    public async Task KeyedAsyncLockSerializesRequestsWithTheSameReplyId()
    {
        var requestLocks = new KeyedAsyncLock<ushort>();
        IDisposable firstLock = await requestLocks.AcquireAsync(0x1001);
        Task<IDisposable> secondLockTask = requestLocks.AcquireAsync(0x1001);

        Assert.False(secondLockTask.IsCompleted);

        firstLock.Dispose();
        using IDisposable secondLock = await secondLockTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task KeyedAsyncLockAllowsDifferentReplyIdsConcurrently()
    {
        var requestLocks = new KeyedAsyncLock<ushort>();
        using IDisposable firstLock = await requestLocks.AcquireAsync(0x1001);

        using IDisposable secondLock = await requestLocks.AcquireAsync(0x1002)
            .WaitAsync(TimeSpan.FromSeconds(1));
    }
}
