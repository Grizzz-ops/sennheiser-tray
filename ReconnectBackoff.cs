using System;

namespace SennheiserTray
{
    public static class ReconnectBackoff
    {
        private static readonly int[] DelaySeconds = { 2, 5, 10, 30 };

        public static TimeSpan DelayForAttempt(int failedAttempts)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(failedAttempts);
            return TimeSpan.FromSeconds(DelaySeconds[Math.Min(failedAttempts, DelaySeconds.Length - 1)]);
        }
    }
}
