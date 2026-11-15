namespace SennheiserTray
{
    public sealed class AutoConnectPollingPolicy
    {
        private const int MissesBeforeSlowerPolling = 3;

        public static TimeSpan NormalInterval { get; } = TimeSpan.FromSeconds(20);
        public static TimeSpan SlowerInterval { get; } = TimeSpan.FromSeconds(60);

        public int ConsecutiveMisses { get; private set; }

        public TimeSpan Interval => ConsecutiveMisses >= MissesBeforeSlowerPolling
            ? SlowerInterval
            : NormalInterval;

        public void RecordScan(bool compatibleDeviceFound)
        {
            ConsecutiveMisses = compatibleDeviceFound
                ? 0
                : Math.Min(ConsecutiveMisses + 1, MissesBeforeSlowerPolling);
        }
    }
}
