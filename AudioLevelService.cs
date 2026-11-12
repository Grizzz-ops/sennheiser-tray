using System.Diagnostics;
using System.Numerics;
using NAudio.Wave;

namespace SennheiserTray
{
    public enum AudioAnalysisMode
    {
        Visualizer,
        AdaptiveEq,
        AutoMode
    }

    public sealed class AudioLevelService : IDisposable
    {
        private const int FftSize = 2048;
        private const int ReportEveryFrames = 5;
        private static readonly long VisualizerIntervalTicks = Stopwatch.Frequency / 30;
        private static readonly (double Min, double Max)[] FrequencyBands =
        {
            (30, 120),
            (120, 500),
            (500, 1800),
            (1800, 5500),
            (5500, 16000)
        };

        private readonly object _gate = new();
        private readonly float[] _samples = new float[FftSize];
        private WasapiLoopbackCapture? _capture;
        private int _sampleCount;
        private int _framesSinceReport;
        private long _lastVisualizerReport;
        private bool _disposed;

        public event Action<float>? AudioLevelAvailable;
        public event Action<double[]>? SpectrumAnalyzed;
        public event Action<AudioContentFeatures>? ContentFeaturesAnalyzed;

        public AudioAnalysisMode Mode { get; set; } = AudioAnalysisMode.Visualizer;
        public bool IsRunning => _capture != null;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture != null)
            {
                return;
            }

            var capture = new WasapiLoopbackCapture();
            WaveFormat format = capture.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
            {
                capture.Dispose();
                throw new NotSupportedException(
                    $"Playback format {format.Encoding}, {format.BitsPerSample}-bit is not supported by the audio analyzer.");
            }

            _sampleCount = 0;
            _framesSinceReport = 0;
            _lastVisualizerReport = 0;
            capture.DataAvailable += OnDataAvailable;
            _capture = capture;
            capture.StartRecording();
        }

        public void Stop()
        {
            WasapiLoopbackCapture? capture = _capture;
            _capture = null;
            if (capture == null)
            {
                return;
            }

            capture.DataAvailable -= OnDataAvailable;
            capture.StopRecording();
            capture.Dispose();
            lock (_gate)
            {
                _sampleCount = 0;
                _framesSinceReport = 0;
            }
        }

        public static float NormalizeLevel(double rms)
        {
            if (!double.IsFinite(rms) || rms <= 0)
            {
                return 0;
            }

            return (float)Math.Clamp((Math.Sqrt(rms) - 0.008) / 0.22, 0, 1);
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            WasapiLoopbackCapture? capture = _capture;
            if (capture == null)
            {
                return;
            }

            int channels = capture.WaveFormat.Channels;
            int blockAlign = capture.WaveFormat.BlockAlign;
            int frames = e.BytesRecorded / blockAlign;
            if (frames == 0)
            {
                return;
            }

            double squaredTotal = 0;
            double peakAmplitude = 0;
            double[]? report = null;
            AudioContentFeatures? contentFeatures = null;
            AudioAnalysisMode mode = Mode;

            lock (_gate)
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    int frameOffset = frame * blockAlign;
                    double mono = 0;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        int sampleOffset = frameOffset + channel * sizeof(float);
                        mono += BitConverter.ToSingle(e.Buffer, sampleOffset);
                    }

                    float sample = (float)(mono / channels);
                    squaredTotal += sample * sample;
                    peakAmplitude = Math.Max(peakAmplitude, Math.Abs(sample));
                    if (mode is AudioAnalysisMode.AdaptiveEq or AudioAnalysisMode.AutoMode)
                    {
                        _samples[_sampleCount++] = sample;
                        if (_sampleCount == FftSize)
                        {
                            _framesSinceReport++;
                            if (_framesSinceReport >= ReportEveryFrames)
                            {
                                if (mode == AudioAnalysisMode.AdaptiveEq)
                                {
                                    report = AnalyzeFrame(_samples, capture.WaveFormat.SampleRate);
                                }
                                else
                                {
                                    contentFeatures = AnalyzeContentFeatures(
                                        _samples,
                                        capture.WaveFormat.SampleRate);
                                }
                                _framesSinceReport = 0;
                            }

                            _sampleCount = 0;
                        }
                    }
                }
            }

            long now = Stopwatch.GetTimestamp();
            if (now - _lastVisualizerReport >= VisualizerIntervalTicks)
            {
                _lastVisualizerReport = now;
                RaiseAudioLevel(NormalizeLevel(squaredTotal / frames));
            }

            if (report != null)
            {
                RaiseSpectrumAnalyzed(report);
            }

            if (contentFeatures is not null)
            {
                RaiseContentFeaturesAnalyzed(contentFeatures);
            }
        }

        private void RaiseAudioLevel(float level)
        {
            Action<float>? handlers = AudioLevelAvailable;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<float> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(level);
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Audio-level handler failed", ex);
                }
            }
        }

        private void RaiseSpectrumAnalyzed(double[] spectrum)
        {
            Action<double[]>? handlers = SpectrumAnalyzed;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<double[]> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(spectrum);
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Audio spectrum handler failed", ex);
                }
            }
        }

        private void RaiseContentFeaturesAnalyzed(AudioContentFeatures features)
        {
            Action<AudioContentFeatures>? handlers = ContentFeaturesAnalyzed;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<AudioContentFeatures> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(features);
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Auto-mode audio feature handler failed", ex);
                }
            }
        }

        private static double[] AnalyzeFrame(float[] samples, int sampleRate)
        {
            var spectrum = new Complex[FftSize];
            for (int i = 0; i < FftSize; i++)
            {
                double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
                spectrum[i] = new Complex(samples[i] * window, 0);
            }

            Transform(spectrum);
            var levels = new double[FrequencyBands.Length];
            for (int band = 0; band < FrequencyBands.Length; band++)
            {
                int firstBin = Math.Max(1, (int)Math.Ceiling(FrequencyBands[band].Min * FftSize / sampleRate));
                int lastBin = Math.Min(FftSize / 2, (int)Math.Floor(FrequencyBands[band].Max * FftSize / sampleRate));
                double power = 0;
                int count = 0;
                for (int bin = firstBin; bin <= lastBin; bin++)
                {
                    power += spectrum[bin].Magnitude * spectrum[bin].Magnitude;
                    count++;
                }

                double rms = count == 0 ? 0 : Math.Sqrt(power / count) / FftSize;
                levels[band] = 20 * Math.Log10(Math.Max(rms, 1e-8));
            }

            return levels;
        }

        private static AudioContentFeatures AnalyzeContentFeatures(
            float[] samples,
            int sampleRate)
        {
            var spectrum = new Complex[FftSize];
            double weightedFrequency = 0;
            double magnitudeSum = 0;
            double totalEnergy = 0;
            double lowFrequencyEnergy = 0;
            double timeDomainEnergy = 0;
            double peakAmplitude = 0;
            for (int i = 0; i < FftSize; i++)
            {
                double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
                spectrum[i] = new Complex(samples[i] * window, 0);
                timeDomainEnergy += samples[i] * samples[i];
                peakAmplitude = Math.Max(peakAmplitude, Math.Abs(samples[i]));
            }

            Transform(spectrum);
            for (int bin = 1; bin <= FftSize / 2; bin++)
            {
                double frequency = bin * (double)sampleRate / FftSize;
                double magnitude = spectrum[bin].Magnitude;
                double energy = magnitude * magnitude;
                totalEnergy += energy;
                weightedFrequency += frequency * magnitude;
                magnitudeSum += magnitude;
                if (frequency <= 200)
                {
                    lowFrequencyEnergy += energy;
                }
            }

            double rms = Math.Sqrt(timeDomainEnergy / FftSize);
            return new AudioContentFeatures(
                magnitudeSum <= 1e-20 ? 0 : weightedFrequency / magnitudeSum,
                totalEnergy <= 1e-20 ? 0 : lowFrequencyEnergy / totalEnergy,
                rms <= 1e-10 ? 0 : peakAmplitude / rms);
        }

        private static void Transform(Complex[] values)
        {
            int length = values.Length;
            for (int i = 1, j = 0; i < length; i++)
            {
                int bit = length >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                {
                    j ^= bit;
                }

                j ^= bit;
                if (i < j)
                {
                    (values[i], values[j]) = (values[j], values[i]);
                }
            }

            for (int size = 2; size <= length; size <<= 1)
            {
                double angle = -2 * Math.PI / size;
                Complex step = new(Math.Cos(angle), Math.Sin(angle));
                for (int start = 0; start < length; start += size)
                {
                    Complex factor = Complex.One;
                    int halfSize = size / 2;
                    for (int offset = 0; offset < halfSize; offset++)
                    {
                        Complex even = values[start + offset];
                        Complex odd = factor * values[start + offset + halfSize];
                        values[start + offset] = even + odd;
                        values[start + offset + halfSize] = even - odd;
                        factor *= step;
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }
    }
}
