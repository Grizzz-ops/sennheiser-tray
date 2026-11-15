namespace SennheiserTray
{
    public sealed class ModeManager
    {
        public static readonly TimeSpan ManualPinDuration = TimeSpan.FromSeconds(60);
        private static readonly string[] ModeOrder = { "Music", "Movie", "Gaming", "Calls", "Focus" };

        private readonly Dictionary<string, SoundMode> _modes;
        private readonly SemaphoreSlim _applySemaphore = new(1, 1);
        private string _currentModeId = "Music";
        private string _previousModeId = "Music";
        private DateTimeOffset? _pinExpiresAt;
        private bool _isLocked;

        public ModeManager()
        {
            _modes = CreateBuiltInModes().ToDictionary(mode => mode.Id, StringComparer.Ordinal);
        }

        public IReadOnlyList<SoundMode> Modes =>
            ModeOrder.Select(id => _modes[id].Copy()).ToArray();

        public SoundMode CurrentMode => GetMode(_currentModeId);
        public SoundMode PreviousMode => GetMode(_previousModeId);
        public bool IsLocked => _isLocked;
        public DateTimeOffset? PinExpiresAt => _pinExpiresAt;
        public bool IsPinned(DateTimeOffset now) =>
            _isLocked || (_pinExpiresAt.HasValue && _pinExpiresAt.Value > now);
        public bool CanAutoSwitch(DateTimeOffset now) => !IsPinned(now);

        public SoundMode GetMode(string id)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            return _modes.TryGetValue(id, out SoundMode? mode)
                ? mode.Copy()
                : throw new KeyNotFoundException($"Unknown sound mode '{id}'.");
        }

        public SoundMode SelectManually(string id, DateTimeOffset now)
        {
            SoundMode mode = GetMode(id);
            SwitchCurrentMode(mode.Id);
            _isLocked = false;
            _pinExpiresAt = now + ManualPinDuration;
            return mode;
        }

        public async Task<SoundMode> ApplyManuallyAsync(
            string id,
            DateTimeOffset now,
            Func<SoundMode, Task> apply)
        {
            ArgumentNullException.ThrowIfNull(apply);
            await _applySemaphore.WaitAsync();
            try
            {
                SoundMode mode = SelectManually(id, now);
                await apply(mode);
                return mode;
            }
            finally
            {
                _applySemaphore.Release();
            }
        }

        public bool TrySelectAutomatically(string id, DateTimeOffset now, out SoundMode? mode)
        {
            if (!CanAutoSwitch(now))
            {
                mode = null;
                return false;
            }

            mode = GetMode(id);
            SwitchCurrentMode(mode.Id);
            return true;
        }

        public async Task<bool> ApplyAutomaticallyAsync(
            string id,
            DateTimeOffset now,
            Func<SoundMode, Task> apply)
        {
            ArgumentNullException.ThrowIfNull(apply);
            await _applySemaphore.WaitAsync();
            try
            {
                if (!CanAutoSwitch(now))
                {
                    return false;
                }

                SoundMode mode = GetMode(id);
                SwitchCurrentMode(mode.Id);
                await apply(mode);
                return true;
            }
            finally
            {
                _applySemaphore.Release();
            }
        }

        public SoundMode RestorePreviousManually(DateTimeOffset now)
        {
            string previousModeId = _previousModeId;
            SwitchCurrentMode(previousModeId);
            _isLocked = false;
            _pinExpiresAt = now + ManualPinDuration;
            return CurrentMode;
        }

        public async Task<SoundMode> RestorePreviousManuallyAsync(
            DateTimeOffset now,
            Func<SoundMode, Task> apply)
        {
            ArgumentNullException.ThrowIfNull(apply);
            await _applySemaphore.WaitAsync();
            try
            {
                SoundMode mode = RestorePreviousManually(now);
                await apply(mode);
                return mode;
            }
            finally
            {
                _applySemaphore.Release();
            }
        }

        public void ToggleLock(DateTimeOffset now)
        {
            if (_isLocked)
            {
                _isLocked = false;
                _pinExpiresAt = null;
                return;
            }

            _isLocked = true;
            _pinExpiresAt = null;
        }

        public bool ExpirePin(DateTimeOffset now)
        {
            if (_isLocked || !_pinExpiresAt.HasValue || _pinExpiresAt.Value > now)
            {
                return false;
            }

            _pinExpiresAt = null;
            return true;
        }

        public void UpdateCurrentState(
            int? ancMode = null,
            int? ancLevel = null,
            IReadOnlyList<int>? eqBands = null,
            bool? bassBoost = null)
        {
            SoundMode current = _modes[_currentModeId];
            int[] updatedBands = eqBands is null
                ? (int[])current.EqBands.Clone()
                : NormalizeEqBands(eqBands);

            if (eqBands is not null && eqBands.Count != 5)
            {
                throw new ArgumentException("A sound mode must contain exactly five EQ bands.", nameof(eqBands));
            }

            int updatedAncMode = ancMode ?? current.AncMode;
            if (updatedAncMode is not (AncModeRules.Off or AncModeRules.Custom or AncModeRules.Adaptive))
            {
                throw new ArgumentOutOfRangeException(nameof(ancMode));
            }

            int updatedAncLevel = ancLevel.HasValue
                ? Math.Clamp(ancLevel.Value, 0, 100)
                : current.AncLevel;

            _modes[_currentModeId] = current with
            {
                AncMode = updatedAncMode,
                AncLevel = updatedAncLevel,
                EqBands = updatedBands,
                BassBoost = bassBoost ?? current.BassBoost
            };
        }

        public static int NormalizeEqBand(int value)
        {
            int clamped = Math.Clamp(value, -60, 60);
            int stepped = (int)Math.Round(clamped / 5d, MidpointRounding.AwayFromZero) * 5;
            return Math.Clamp(stepped, -60, 60);
        }

        public static int[] NormalizeEqBands(IReadOnlyList<int> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (values.Count != 5)
            {
                throw new ArgumentException("A sound mode must contain exactly five EQ bands.", nameof(values));
            }

            return values.Select(NormalizeEqBand).ToArray();
        }

        private void SwitchCurrentMode(string id)
        {
            _ = GetMode(id);
            if (!StringComparer.Ordinal.Equals(_currentModeId, id))
            {
                _previousModeId = _currentModeId;
                _currentModeId = id;
            }
        }

        private static IEnumerable<SoundMode> CreateBuiltInModes()
        {
            yield return new SoundMode(
                "Music", "Music", "\uE8D6", "#2C7A68",
                AncModeRules.Custom, 50, new[] { 0, 0, 0, 0, 0 }, false);
            yield return new SoundMode(
                "Movie", "Movie", "\uE8B2", "#AD6C2F",
                AncModeRules.Custom, 10, new[] { 10, 5, 0, 10, 10 }, false);
            yield return new SoundMode(
                "Gaming", "Gaming", "\uE7FC", "#3F8060",
                AncModeRules.Custom, 50, new[] { -10, -5, 10, 25, 15 }, false);
            yield return new SoundMode(
                "Calls", "Calls", "\uE717", "#7760A5",
                AncModeRules.Custom, 65, new[] { -15, 0, 15, 20, 0 }, false);
            yield return new SoundMode(
                "Focus", "Focus", "\uE8A7", "#59645E",
                AncModeRules.Custom, 0, new[] { 0, 0, 0, 0, 0 }, false);
        }
    }
}
