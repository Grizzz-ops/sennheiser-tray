namespace SennheiserTray
{
    public sealed record FocusCycleState(bool IsBreak, TimeSpan Remaining, int CompletedFocusCycles);

    public static class FocusCycleRules
    {
        public static readonly TimeSpan FocusDuration = TimeSpan.FromMinutes(25);
        public static readonly TimeSpan BreakDuration = TimeSpan.FromMinutes(5);

        public static FocusCycleState GetState(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            }

            long focusTicks = FocusDuration.Ticks;
            long breakTicks = BreakDuration.Ticks;
            long cycleTicks = focusTicks + breakTicks;
            long totalTicks = elapsed.Ticks;
            int completedCycles = (int)Math.Min(int.MaxValue, totalTicks / cycleTicks);
            long phaseTicks = totalTicks % cycleTicks;

            if (phaseTicks < focusTicks)
            {
                return new FocusCycleState(
                    false,
                    TimeSpan.FromTicks(focusTicks - phaseTicks),
                    completedCycles);
            }

            return new FocusCycleState(
                true,
                TimeSpan.FromTicks(cycleTicks - phaseTicks),
                completedCycles + 1);
        }
    }
}
