namespace SennheiserTray
{
    public enum AdaptiveEqFrameState
    {
        Silent,
        Learning,
        Paused,
        ProfileReady,
        Locked
    }

    public sealed record AdaptiveEqFrameResult(
        AdaptiveEqFrameState State,
        int SampleCount,
        double[]? Profile = null);

    public sealed class AdaptiveEqProfileLearner
    {
        private const int BandCount = 5;
        private const int StableWindowSamples = 15;
        private const int SilentTicksBeforeReset = 3;
        private readonly Queue<double[]> _samples = new();
        private double[]? _baseline;
        private int _silentTicks;
        private bool _profileLocked;

        public int SampleCount => _samples.Count;

        public double[]? ManualProfile => _baseline is null
            ? null
            : (double[])_baseline.Clone();

        public void Start(double[] baseline)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ValidateBandValues(baseline, nameof(baseline));
            _baseline = (double[])baseline.Clone();
            ResetLearning();
        }

        public void UpdateBaseline(double[] baseline)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ValidateBandValues(baseline, nameof(baseline));
            _baseline = (double[])baseline.Clone();
            ResetLearning();
        }

        public AdaptiveEqFrameResult ProcessSpectrum(double[] spectrum)
        {
            ArgumentNullException.ThrowIfNull(spectrum);
            if (spectrum.Length != BandCount || spectrum.Any(level => !double.IsFinite(level)))
            {
                throw new ArgumentException(
                    $"A playback spectrum must contain {BandCount} finite band levels.",
                    nameof(spectrum));
            }

            if (spectrum.All(level => level < -72))
            {
                _silentTicks++;
                if (_silentTicks >= SilentTicksBeforeReset)
                {
                    ResetLearning();
                    return new AdaptiveEqFrameResult(AdaptiveEqFrameState.Paused, 0);
                }

                return new AdaptiveEqFrameResult(AdaptiveEqFrameState.Silent, _samples.Count);
            }

            _silentTicks = 0;
            if (_profileLocked)
            {
                return new AdaptiveEqFrameResult(AdaptiveEqFrameState.Locked, _samples.Count);
            }

            _samples.Enqueue((double[])spectrum.Clone());
            while (_samples.Count > StableWindowSamples)
            {
                _samples.Dequeue();
            }

            if (_samples.Count < StableWindowSamples)
            {
                return new AdaptiveEqFrameResult(AdaptiveEqFrameState.Learning, _samples.Count);
            }

            double[] stableSpectrum = GetMedianSpectrum(_samples);
            double averageLevel = stableSpectrum.Average();
            var profile = new double[BandCount];
            for (int band = 0; band < BandCount; band++)
            {
                double correctionDb = Math.Clamp((averageLevel - stableSpectrum[band]) * 0.08, -2, 2);
                double baseline = _baseline?[band] ?? 0;
                double targetTenthsDb = Math.Round((baseline + correctionDb * 10) / 5) * 5;
                profile[band] = Math.Clamp(targetTenthsDb, -60, 60);
            }

            _profileLocked = true;
            return new AdaptiveEqFrameResult(AdaptiveEqFrameState.ProfileReady, StableWindowSamples, profile);
        }

        public void ResetLearning()
        {
            _samples.Clear();
            _silentTicks = 0;
            _profileLocked = false;
        }

        public void Stop()
        {
            ResetLearning();
            _baseline = null;
        }

        private static double[] GetMedianSpectrum(IEnumerable<double[]> samples)
        {
            double[][] frames = samples.ToArray();
            var median = new double[BandCount];
            for (int band = 0; band < BandCount; band++)
            {
                double[] levels = frames.Select(frame => frame[band]).OrderBy(level => level).ToArray();
                median[band] = levels[levels.Length / 2];
            }

            return median;
        }

        private static void ValidateBandValues(double[] values, string parameterName)
        {
            if (values.Length != BandCount || values.Any(value => !double.IsFinite(value)))
            {
                throw new ArgumentException(
                    $"An EQ profile must contain {BandCount} finite band values.",
                    parameterName);
            }
        }
    }
}
