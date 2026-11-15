namespace SennheiserTray
{
    public sealed class BatteryTrendEstimator
    {
        private static readonly TimeSpan MinimumSampleWindow = TimeSpan.FromMinutes(6);
        private static readonly TimeSpan MaximumSampleAge = TimeSpan.FromHours(6);
        private static readonly TimeSpan MaximumEstimate = TimeSpan.FromHours(72);
        private const int MaximumSamples = 200;
        private const int MinimumSamples = 4;
        private const double MinimumTrendPercentPerMinute = 0.02;
        private readonly Queue<(DateTimeOffset Timestamp, int Level)> _samples = new();
        public int SampleCount => _samples.Count;
        public bool HasMinimumSampleWindow =>
            _samples.Count >= MinimumSamples
            && _samples.Last().Timestamp - _samples.Peek().Timestamp >= MinimumSampleWindow;

        public void AddSample(DateTimeOffset timestamp, int level)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(level);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 100);

            if (_samples.Count > 0 && timestamp <= _samples.Last().Timestamp)
            {
                return;
            }

            if (_samples.Count > 0)
            {
                (DateTimeOffset Timestamp, int Level) latest = _samples.Last();
                if (latest.Level == level
                    && timestamp - latest.Timestamp < TimeSpan.FromMinutes(1))
                {
                    return;
                }
            }

            _samples.Enqueue((timestamp, level));
            (DateTimeOffset Timestamp, int Level) oldest;
            while (_samples.Count > MaximumSamples
                || (_samples.TryPeek(out oldest) && timestamp - oldest.Timestamp > MaximumSampleAge))
            {
                _samples.Dequeue();
            }
        }

        public bool? GetChargingState()
        {
            if (!TryGetTrend(out double trendPerMinute))
            {
                return null;
            }

            if (trendPerMinute > MinimumTrendPercentPerMinute)
            {
                return true;
            }

            return trendPerMinute < -MinimumTrendPercentPerMinute ? false : null;
        }

        public string? GetEstimate(int currentLevel)
        {
            if (!TryGetTrend(out double trendPerMinute))
            {
                return null;
            }

            bool isCharging = trendPerMinute > MinimumTrendPercentPerMinute;
            if (!isCharging && trendPerMinute >= -MinimumTrendPercentPerMinute)
            {
                return null;
            }

            int remainingPercent = isCharging ? 100 - currentLevel : currentLevel;
            if (remainingPercent == 0)
            {
                return isCharging ? "Charging · almost full" : "Time remaining unavailable";
            }

            double estimateMinutes = remainingPercent / Math.Abs(trendPerMinute);
            if (!double.IsFinite(estimateMinutes) || estimateMinutes > MaximumEstimate.TotalMinutes)
            {
                return null;
            }

            string duration = FormatDuration(TimeSpan.FromMinutes(Math.Ceiling(estimateMinutes)));
            return isCharging
                ? $"Charging · ~{duration} to full"
                : $"~{duration} left";
        }

        private bool TryGetTrend(out double trendPerMinute)
        {
            trendPerMinute = 0;
            if (_samples.Count < MinimumSamples)
            {
                return false;
            }

            (DateTimeOffset Timestamp, int Level)[] samples = _samples.ToArray();
            double elapsedMinutes = (samples[^1].Timestamp - samples[0].Timestamp).TotalMinutes;
            if (elapsedMinutes < MinimumSampleWindow.TotalMinutes
                || Math.Abs(samples[^1].Level - samples[0].Level) < 1)
            {
                return false;
            }

            double meanTime = samples.Average(sample =>
                (sample.Timestamp - samples[0].Timestamp).TotalMinutes);
            double meanLevel = samples.Average(sample => (double)sample.Level);
            double covariance = 0;
            double timeVariance = 0;
            foreach ((DateTimeOffset timestamp, int level) in samples)
            {
                double time = (timestamp - samples[0].Timestamp).TotalMinutes - meanTime;
                covariance += time * (level - meanLevel);
                timeVariance += time * time;
            }

            if (timeVariance <= 0)
            {
                return false;
            }

            trendPerMinute = covariance / timeVariance;
            return double.IsFinite(trendPerMinute);
        }

        public void Reset() => _samples.Clear();

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes < 60)
            {
                return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))}m";
            }

            int hours = (int)duration.TotalHours;
            int minutes = duration.Minutes;
            return minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
        }
    }
}
