namespace SennheiserTray
{
    public enum AutoModeRule
    {
        None,
        Call,
        Gaming,
        Content
    }

    public enum AutoContentClass
    {
        Unknown,
        Speech,
        Music,
        Cinematic
    }

    public sealed record AudioContentFeatures(
        double SpectralCentroidHz,
        double LowFrequencyEnergyRatio,
        double CrestFactor);

    public sealed record AutoModeSignals(
        bool IsCallActive,
        string? CallProcessName,
        bool IsGameActive,
        string? GameProcessName,
        bool IsFullscreen,
        AudioContentFeatures? ContentFeatures);

    public sealed record AutoModeDecision(
        AutoModeRule Rule,
        string ModeId,
        string Caption,
        int ConfidenceSteps,
        bool RestorePrevious = false);

    public interface IAutoModeSignalSource
    {
        Task<AutoModeSignals> ReadAsync(CancellationToken cancellationToken);
    }

    public sealed class AutoModeController
    {
        private static readonly TimeSpan CallEnterDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan CallExitDelay = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan GamingExitDelay = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ContentWindow = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ContentMinimumDwell = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan OverrideLearningWindow = TimeSpan.FromSeconds(30);
        private const int MaximumLearningSteps = 3;

        private readonly Queue<(DateTimeOffset At, AudioContentFeatures Features)> _contentSamples = new();
        private readonly IAutoModeSignalSource? _signalSource;
        private readonly Dictionary<AutoModeRule, int> _learningSteps = new()
        {
            [AutoModeRule.Call] = 0,
            [AutoModeRule.Gaming] = 0,
            [AutoModeRule.Content] = 0
        };
        private DateTimeOffset? _callStartedAt;
        private DateTimeOffset? _callEndedAt;
        private DateTimeOffset? _gamingLostAt;
        private DateTimeOffset? _contentCandidateAt;
        private DateTimeOffset? _lastContentSwitchAt;
        private DateTimeOffset? _lastAutomaticSwitchAt;
        private AutoModeRule _lastAutomaticRule;
        private AutoModeRule _activeRule;
        private AutoContentClass _activeContentClass;
        private string? _baseModeId;
        private string? _lastCaption;

        public AutoModeController(IAutoModeSignalSource? signalSource = null)
        {
            _signalSource = signalSource;
        }

        public int RequiredConfidenceSteps(AutoModeRule rule) =>
            rule == AutoModeRule.None ? 0 : 1 + _learningSteps[rule];

        public AutoModeRule ActiveRule => _activeRule;
        public string? BaseModeId => _baseModeId;

        public async Task<AutoModeDecision?> ReadAndEvaluateAsync(
            string currentModeId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            if (_signalSource is null)
            {
                throw new InvalidOperationException("No auto-mode signal source was configured.");
            }

            AutoModeSignals signals = await _signalSource.ReadAsync(cancellationToken);
            return Evaluate(signals, currentModeId, now);
        }

        public AutoModeDecision? Evaluate(
            AutoModeSignals signals,
            string currentModeId,
            DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(signals);
            ArgumentException.ThrowIfNullOrWhiteSpace(currentModeId);
            AddContentSample(signals.ContentFeatures, now);

            AutoModeDecision? decision = EvaluateCall(signals, currentModeId, now);
            if (decision is not null)
            {
                return decision;
            }

            decision = EvaluateGaming(signals, currentModeId, now);
            if (decision is not null)
            {
                return decision;
            }

            decision = EvaluateContent(currentModeId, now);
            if (decision is not null)
            {
                return decision;
            }

            if (_activeRule != AutoModeRule.None && _baseModeId is not null)
            {
                string restoreId = _baseModeId;
                _baseModeId = null;
                _activeRule = AutoModeRule.None;
                _activeContentClass = AutoContentClass.Unknown;
                _lastCaption = null;
                _contentCandidateAt = null;
                _lastContentSwitchAt = null;
                _lastAutomaticSwitchAt = now;
                _lastAutomaticRule = AutoModeRule.None;
                return new AutoModeDecision(
                    AutoModeRule.None,
                    restoreId,
                    $"Auto · {restoreId} (restored)",
                    4,
                    RestorePrevious: true);
            }

            return null;
        }

        public void RegisterManualOverride(DateTimeOffset now)
        {
            if (_activeRule == AutoModeRule.None
                || _lastAutomaticRule != _activeRule
                || !_lastAutomaticSwitchAt.HasValue
                || now - _lastAutomaticSwitchAt.Value > OverrideLearningWindow)
            {
                return;
            }

            _learningSteps[_activeRule] = Math.Min(
                MaximumLearningSteps,
                _learningSteps[_activeRule] + 1);
        }

        public void ResetLearning()
        {
            foreach (AutoModeRule rule in _learningSteps.Keys.ToArray())
            {
                _learningSteps[rule] = 0;
            }
        }

        public static (AutoContentClass Class, int ConfidenceSteps) Classify(
            AudioContentFeatures features)
        {
            if (!double.IsFinite(features.SpectralCentroidHz)
                || !double.IsFinite(features.LowFrequencyEnergyRatio)
                || !double.IsFinite(features.CrestFactor))
            {
                return (AutoContentClass.Unknown, 0);
            }

            double lowRatio = Math.Clamp(features.LowFrequencyEnergyRatio, 0, 1);
            double centroid = Math.Max(0, features.SpectralCentroidHz);
            double crest = Math.Max(0, features.CrestFactor);

            if (centroid is >= 450 and <= 4200 && lowRatio < 0.28 && crest >= 2.2)
            {
                int confidence = centroid is >= 700 and <= 3500 && lowRatio < 0.18 && crest >= 3.2 ? 4 : 3;
                return (AutoContentClass.Speech, confidence);
            }

            if (lowRatio >= 0.38 && crest >= 2.8)
            {
                int confidence = lowRatio >= 0.52 && crest >= 4 ? 4 : 3;
                return (AutoContentClass.Cinematic, confidence);
            }

            if (centroid is >= 120 and <= 9500 && lowRatio is >= 0.12 and <= 0.65 && crest < 7)
            {
                int confidence = lowRatio is >= 0.18 and <= 0.5 && crest is >= 1.5 and <= 5 ? 3 : 2;
                return (AutoContentClass.Music, confidence);
            }

            return (AutoContentClass.Unknown, 0);
        }

        private AutoModeDecision? EvaluateCall(AutoModeSignals signals, string currentModeId, DateTimeOffset now)
        {
            if (signals.IsCallActive)
            {
                _callEndedAt = null;
                _callStartedAt ??= now;
                if (_activeRule == AutoModeRule.Call
                    || now - _callStartedAt.Value >= CallEnterDelay)
                {
                    if (_activeRule != AutoModeRule.Call)
                    {
                        BeginAutomaticOverride(AutoModeRule.Call, currentModeId);
                    }

                    string app = string.IsNullOrWhiteSpace(signals.CallProcessName)
                        ? "an app"
                        : signals.CallProcessName;
                    return CreateDecision(AutoModeRule.Call, "Calls", $"Auto · Calls ({app} is using your mic)", 4, now);
                }

                return null;
            }

            _callStartedAt = null;
            if (_activeRule == AutoModeRule.Call)
            {
                _callEndedAt ??= now;
                if (now - _callEndedAt.Value < CallExitDelay)
                {
                    string caption = _lastCaption ?? "Auto · Calls (waiting for mic activity)";
                    return new AutoModeDecision(AutoModeRule.Call, "Calls", caption, 4);
                }

                _callEndedAt = null;
            }
            else
            {
                _callEndedAt = null;
            }

            return null;
        }

        private AutoModeDecision? EvaluateGaming(AutoModeSignals signals, string currentModeId, DateTimeOffset now)
        {
            bool gameActive = signals.IsFullscreen || signals.IsGameActive;
            if (gameActive)
            {
                _gamingLostAt = null;
                if (ConfidenceForGaming(signals) < RequiredConfidenceSteps(AutoModeRule.Gaming))
                {
                    return null;
                }

                if (_activeRule != AutoModeRule.Gaming || _activeContentClass != AutoContentClass.Unknown)
                {
                    BeginAutomaticOverride(AutoModeRule.Gaming, currentModeId);
                }

                string app = signals.IsFullscreen
                    ? string.IsNullOrWhiteSpace(signals.GameProcessName) ? "fullscreen app" : signals.GameProcessName
                    : signals.GameProcessName ?? "a listed game";
                return CreateDecision(AutoModeRule.Gaming, "Gaming", $"Auto · Gaming ({app})", ConfidenceForGaming(signals), now);
            }

            if (_activeRule == AutoModeRule.Gaming)
            {
                _gamingLostAt ??= now;
                if (now - _gamingLostAt.Value < GamingExitDelay)
                {
                    return new AutoModeDecision(AutoModeRule.Gaming, "Gaming", _lastCaption ?? "Auto · Gaming", 4);
                }
            }

            _gamingLostAt = null;
            return null;
        }

        private AutoModeDecision? EvaluateContent(string currentModeId, DateTimeOffset now)
        {
            if (_contentSamples.Count == 0)
            {
                return null;
            }

            AudioContentFeatures average = new(
                _contentSamples.Average(sample => sample.Features.SpectralCentroidHz),
                _contentSamples.Average(sample => sample.Features.LowFrequencyEnergyRatio),
                _contentSamples.Average(sample => sample.Features.CrestFactor));
            (AutoContentClass contentClass, int confidence) = Classify(average);
            if (contentClass == AutoContentClass.Unknown
                || confidence < RequiredConfidenceSteps(AutoModeRule.Content))
            {
                _contentCandidateAt = null;
                return null;
            }

            string modeId = contentClass switch
            {
                AutoContentClass.Speech => "Calls",
                AutoContentClass.Music => "Music",
                AutoContentClass.Cinematic => "Movie",
                _ => throw new InvalidOperationException($"Unsupported content class {contentClass}.")
            };
            if (StringComparer.Ordinal.Equals(modeId, currentModeId) && _activeRule == AutoModeRule.None)
            {
                return null;
            }

            if (contentClass != _activeContentClass)
            {
                _activeContentClass = contentClass;
                _contentCandidateAt = now;
                return null;
            }

            if (_activeRule == AutoModeRule.Content
                && _lastContentSwitchAt.HasValue
                && now - _lastContentSwitchAt.Value < ContentMinimumDwell)
            {
                return new AutoModeDecision(
                    AutoModeRule.Content, _modeIdForContent(_activeContentClass),
                    _lastCaption ?? "Auto · content", confidence);
            }

            if (_contentCandidateAt.HasValue
                && now - _contentCandidateAt.Value >= ContentMinimumDwell)
            {
                if (_activeRule != AutoModeRule.Content)
                {
                    BeginAutomaticOverride(AutoModeRule.Content, currentModeId);
                }

                _lastContentSwitchAt = now;
                string contentName = contentClass.ToString();
                return CreateDecision(AutoModeRule.Content, modeId, $"Auto · {contentName} (best-effort)", confidence, now);
            }

            return null;
        }

        private void AddContentSample(AudioContentFeatures? features, DateTimeOffset now)
        {
            if (features is not null)
            {
                _contentSamples.Enqueue((now, features));
            }

            while (_contentSamples.Count > 0 && now - _contentSamples.Peek().At > ContentWindow)
            {
                _contentSamples.Dequeue();
            }
        }

        private static int ConfidenceForGaming(AutoModeSignals signals) =>
            signals.IsFullscreen ? 4 : signals.IsGameActive ? 3 : 0;

        private void BeginAutomaticOverride(AutoModeRule rule, string currentModeId)
        {
            _baseModeId ??= currentModeId;
            _activeRule = rule;
            _lastContentSwitchAt = null;
        }

        private AutoModeDecision CreateDecision(
            AutoModeRule rule,
            string modeId,
            string caption,
            int confidence,
            DateTimeOffset now)
        {
            _activeRule = rule;
            _lastCaption = caption;
            _lastAutomaticSwitchAt = now;
            _lastAutomaticRule = rule;
            return new AutoModeDecision(rule, modeId, caption, confidence);
        }

        private static string _modeIdForContent(AutoContentClass contentClass) => contentClass switch
        {
            AutoContentClass.Speech => "Calls",
            AutoContentClass.Music => "Music",
            AutoContentClass.Cinematic => "Movie",
            _ => "Music"
        };
    }
}
