using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace SennheiserTray
{
    public partial class OrbControl : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
            nameof(Level), typeof(double), typeof(OrbControl),
            new FrameworkPropertyMetadata(50d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDialChanged));
        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(OrbControl),
            new PropertyMetadata("Balanced", OnLabelChanged));
        public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
            nameof(IsReadOnly), typeof(bool), typeof(OrbControl), new PropertyMetadata(false));
        public static readonly DependencyProperty IsConnectedProperty = DependencyProperty.Register(
            nameof(IsConnected), typeof(bool), typeof(OrbControl), new PropertyMetadata(false, OnConnectionChanged));
        public static readonly DependencyProperty AudioLevelProperty = DependencyProperty.Register(
            nameof(AudioLevel), typeof(double), typeof(OrbControl), new PropertyMetadata(0d));
        public static readonly DependencyProperty IsCallsModeProperty = DependencyProperty.Register(
            nameof(IsCallsMode), typeof(bool), typeof(OrbControl), new PropertyMetadata(false, OnModeRingChanged));
        public static readonly DependencyProperty IsMicrophoneMutedProperty = DependencyProperty.Register(
            nameof(IsMicrophoneMuted), typeof(bool), typeof(OrbControl), new PropertyMetadata(false, OnModeRingChanged));
        public static readonly DependencyProperty IsFocusBreakProperty = DependencyProperty.Register(
            nameof(IsFocusBreak), typeof(bool), typeof(OrbControl), new PropertyMetadata(false, OnFocusBreakChanged));
        public static readonly DependencyProperty LevelCommandProperty = DependencyProperty.Register(
            nameof(LevelCommand), typeof(ICommand), typeof(OrbControl));
        public static readonly DependencyProperty ToggleCommandProperty = DependencyProperty.Register(
            nameof(ToggleCommand), typeof(ICommand), typeof(OrbControl));

        private readonly bool _animationsEnabled = SystemParameters.ClientAreaAnimation;
        private readonly SolidColorBrush _orbAccentBrush = new(System.Windows.Media.Color.FromRgb(169, 87, 54));
        private long _lastRenderTicks;
        private long _animationStartTicks;
        private long _pulseStartTicks;
        private double _smoothedAudioLevel;
        private long _focusTransitionStartTicks;
        private double _focusTransitionStartOpacity;
        private double _focusTransitionTargetOpacity;
        private bool _isFocusTransitionActive;
        private Point _dragStart;
        private bool _isDragging;
        private bool _pointerStartedOnCore;
        private bool _pointerStartedOnDial;

        private static readonly SolidColorBrush LiveCallBrush = CreateFrozenBrush(0xA7, 0x8B, 0xFA);
        private static readonly SolidColorBrush MutedCallBrush = CreateFrozenBrush(0xFF, 0x5D, 0x65);

        public OrbControl()
        {
            InitializeComponent();
            CreateRainbowWash();
            _animationStartTicks = Stopwatch.GetTimestamp();
            LevelArc.Stroke = _orbAccentBrush;
            LevelArcGlow.Stroke = _orbAccentBrush;
            DialThumb.Fill = _orbAccentBrush;
            Loaded += OrbControl_Loaded;
            Unloaded += OrbControl_Unloaded;
            DrawLevelArc();
            UpdateConnectionVisual();
        }

        public double Level
        {
            get => (double)GetValue(LevelProperty);
            set => SetValue(LevelProperty, Math.Clamp(value, 0, 100));
        }

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }

        public bool IsConnected
        {
            get => (bool)GetValue(IsConnectedProperty);
            set => SetValue(IsConnectedProperty, value);
        }

        public double AudioLevel
        {
            get => (double)GetValue(AudioLevelProperty);
            set => SetValue(AudioLevelProperty, Math.Clamp(value, 0, 1));
        }

        public bool IsCallsMode
        {
            get => (bool)GetValue(IsCallsModeProperty);
            set => SetValue(IsCallsModeProperty, value);
        }

        public bool IsMicrophoneMuted
        {
            get => (bool)GetValue(IsMicrophoneMutedProperty);
            set => SetValue(IsMicrophoneMutedProperty, value);
        }

        public bool IsFocusBreak
        {
            get => (bool)GetValue(IsFocusBreakProperty);
            set => SetValue(IsFocusBreakProperty, value);
        }

        public ICommand? LevelCommand
        {
            get => (ICommand?)GetValue(LevelCommandProperty);
            set => SetValue(LevelCommandProperty, value);
        }

        public ICommand? ToggleCommand
        {
            get => (ICommand?)GetValue(ToggleCommandProperty);
            set => SetValue(ToggleCommandProperty, value);
        }

        private static void OnDialChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
            ((OrbControl)dependencyObject).DrawLevelArc();

        private static void OnLabelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
            ((OrbControl)dependencyObject).CenterLabel.Text = (string?)e.NewValue ?? string.Empty;

        private static void OnConnectionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            var control = (OrbControl)dependencyObject;
            control.UpdateConnectionVisual();
            if (e.NewValue is true)
            {
                control._pulseStartTicks = Stopwatch.GetTimestamp();
            }
        }

        private static void OnModeRingChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
            ((OrbControl)dependencyObject).UpdateModeRing();

        private static void OnFocusBreakChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            var control = (OrbControl)dependencyObject;
            control._focusTransitionStartOpacity = control.OrbGlow?.Opacity ?? 0.2;
            control._focusTransitionTargetOpacity = (bool)e.NewValue ? 0.68 : 0.38;
            control._focusTransitionStartTicks = Stopwatch.GetTimestamp();
            control._isFocusTransitionActive = true;
            if (!control._animationsEnabled && control.OrbGlow != null)
            {
                control.OrbGlow.Opacity = control._focusTransitionTargetOpacity;
            }
        }

        private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private void OrbControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_animationsEnabled)
            {
                _lastRenderTicks = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += CompositionTarget_Rendering;
            }
        }

        private void OrbControl_Unloaded(object sender, RoutedEventArgs e) =>
            CompositionTarget.Rendering -= CompositionTarget_Rendering;

        private void CompositionTarget_Rendering(object? sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsedSinceRender = Math.Clamp(
                (now - _lastRenderTicks) / (double)Stopwatch.Frequency,
                0,
                0.1);
            _lastRenderTicks = now;
            double elapsed = (now - _pulseStartTicks) / (double)Stopwatch.Frequency;
            double colorElapsed = (now - _animationStartTicks) / (double)Stopwatch.Frequency;
            double targetAudioLevel = AudioLevel;
            double smoothingTime = targetAudioLevel > _smoothedAudioLevel ? 0.055 : 0.18;
            double smoothing = 1 - Math.Exp(-elapsedSinceRender / smoothingTime);
            _smoothedAudioLevel += (targetAudioLevel - _smoothedAudioLevel) * smoothing;
            double liveLevel = _smoothedAudioLevel;
            UpdateOrbColor(colorElapsed, liveLevel);
            double connectPulse = elapsed is >= 0 and < 0.55
                ? Math.Sin(Math.PI * elapsed / 0.55) * 0.16
                : 0;
            double idleBreath = (Math.Sin(now / (double)Stopwatch.Frequency * (2 * Math.PI / 4.2)) + 1) * 0.5;
            double scale = 1 + connectPulse + liveLevel * 0.2 + (liveLevel < 0.015 ? idleBreath * 0.03 : 0);
            double transitionProgress = Math.Clamp(
                (now - _focusTransitionStartTicks) / (double)Stopwatch.Frequency / 0.8,
                0,
                1);
            double focusOpacity = _focusTransitionStartOpacity
                + (_focusTransitionTargetOpacity - _focusTransitionStartOpacity) * transitionProgress;
            if (transitionProgress >= 1)
            {
                _isFocusTransitionActive = false;
            }

            GlowScale.ScaleX = scale;
            GlowScale.ScaleY = scale;
            RainbowWashScale.ScaleX = scale;
            RainbowWashScale.ScaleY = scale;
            double dialBounce = liveLevel * 0.9;
            LevelArc.StrokeThickness = 3.2 + dialBounce;
            LevelArcGlow.StrokeThickness = 12 + liveLevel * 14;
            LevelArcGlow.Opacity = 0.12 + liveLevel * 0.38;
            DialThumbScale.ScaleX = 1 + liveLevel * 0.65;
            DialThumbScale.ScaleY = DialThumbScale.ScaleX;
            CoreScale.ScaleX = 1 + connectPulse * 0.3 + liveLevel * 0.1;
            CoreScale.ScaleY = CoreScale.ScaleX;
            double normalOpacity = (IsConnected ? 0.42 + liveLevel * 0.5 : 0.12) + connectPulse;
            OrbGlow.Opacity = _isFocusTransitionActive
                ? focusOpacity + liveLevel * 0.24
                : IsFocusBreak
                    ? _focusTransitionTargetOpacity + liveLevel * 0.24
                    : normalOpacity;
            RainbowWashCanvas.Opacity = IsConnected ? 0.72 + liveLevel * 0.18 : 0.12;
            OrbCore.Opacity = IsConnected ? 1 : 0.58;
        }

        private void CreateRainbowWash()
        {
            const int glowCount = 24;
            const double center = 84;
            const double radius = 48;
            const double glowSize = 100;

            for (int index = 0; index < glowCount; index++)
            {
                double angle = index * (360d / glowCount) * Math.PI / 180;
                var gradient = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(ColorFromHue(index / (double)glowCount, 0x58), 0),
                        new GradientStop(ColorFromHue(index / (double)glowCount, 0x24), 0.48),
                        new GradientStop(ColorFromHue(index / (double)glowCount, 0), 1)
                    }
                };
                var glow = new Ellipse
                {
                    Width = glowSize,
                    Height = glowSize,
                    Fill = gradient,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(glow, center + Math.Cos(angle) * radius - glowSize / 2);
                Canvas.SetTop(glow, center + Math.Sin(angle) * radius - glowSize / 2);
                RainbowWashCanvas.Children.Add(glow);
            }
        }

        private void UpdateOrbColor(double elapsedSeconds, double liveLevel)
        {
            System.Windows.Media.Color color = GetSpectrumColor(elapsedSeconds);
            System.Windows.Media.Color secondaryColor = GetSpectrumColor(elapsedSeconds + 1.6);
            _orbAccentBrush.Color = color;
            RainbowWashRotation.Angle = elapsedSeconds * 18 % 360;

            if (OrbGlow.Fill is not RadialGradientBrush gradient || gradient.GradientStops.Count < 3)
            {
                return;
            }

            byte audioAlpha = (byte)Math.Round(liveLevel * 0x38);
            gradient.GradientStops[0].Color = System.Windows.Media.Color.FromArgb(
                (byte)(0x78 + audioAlpha), color.R, color.G, color.B);
            gradient.GradientStops[1].Color = System.Windows.Media.Color.FromArgb(
                (byte)(0x38 + audioAlpha / 2), secondaryColor.R, secondaryColor.G, secondaryColor.B);
            gradient.GradientStops[2].Color = System.Windows.Media.Color.FromArgb(0x00, color.R, color.G, color.B);
        }

        private static System.Windows.Media.Color ColorFromHue(double hue, byte alpha)
        {
            double scaledHue = hue * 6;
            int sector = (int)Math.Floor(scaledHue);
            double fraction = scaledHue - sector;
            double descending = 1 - fraction;

            return (sector % 6) switch
            {
                0 => System.Windows.Media.Color.FromArgb(alpha, 255, (byte)Math.Round(fraction * 255), 0),
                1 => System.Windows.Media.Color.FromArgb(alpha, (byte)Math.Round(descending * 255), 255, 0),
                2 => System.Windows.Media.Color.FromArgb(alpha, 0, 255, (byte)Math.Round(fraction * 255)),
                3 => System.Windows.Media.Color.FromArgb(alpha, 0, (byte)Math.Round(descending * 255), 255),
                4 => System.Windows.Media.Color.FromArgb(alpha, (byte)Math.Round(fraction * 255), 0, 255),
                _ => System.Windows.Media.Color.FromArgb(alpha, 255, 0, (byte)Math.Round(descending * 255))
            };
        }

        private static System.Windows.Media.Color GetSpectrumColor(double elapsedSeconds)
        {
            System.Windows.Media.Color[] colors =
            {
                System.Windows.Media.Color.FromRgb(0x25, 0xC9, 0xD0),
                System.Windows.Media.Color.FromRgb(0x8D, 0x6B, 0xF5),
                System.Windows.Media.Color.FromRgb(0xF0, 0x5D, 0xB5),
                System.Windows.Media.Color.FromRgb(0xFF, 0x86, 0x55),
                System.Windows.Media.Color.FromRgb(0x75, 0xD6, 0x84),
                System.Windows.Media.Color.FromRgb(0x42, 0x9D, 0xF5)
            };
            const double secondsPerColor = 3.8;
            double colorPosition = elapsedSeconds / secondsPerColor;
            int currentColorIndex = (int)colorPosition % colors.Length;
            int nextColorIndex = (currentColorIndex + 1) % colors.Length;
            double progress = colorPosition - Math.Floor(colorPosition);
            progress = progress * progress * (3 - 2 * progress);
            System.Windows.Media.Color current = colors[currentColorIndex];
            System.Windows.Media.Color next = colors[nextColorIndex];
            return System.Windows.Media.Color.FromRgb(
                (byte)Math.Round(current.R + (next.R - current.R) * progress),
                (byte)Math.Round(current.G + (next.G - current.G) * progress),
                (byte)Math.Round(current.B + (next.B - current.B) * progress));
        }

        private void UpdateModeRing()
        {
            if (ModeRing == null)
            {
                return;
            }

            if (!IsCallsMode)
            {
                ModeRing.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                ModeRing.Opacity = 0.22;
                return;
            }

            ModeRing.Stroke = IsMicrophoneMuted ? MutedCallBrush : LiveCallBrush;
            ModeRing.Opacity = 0.88;
        }

        private void UpdateConnectionVisual()
        {
            RainbowWashCanvas.Opacity = IsConnected ? 0.72 : 0.12;
            if (!_animationsEnabled)
            {
                OrbGlow.Opacity = IsConnected ? 0.38 : 0.12;
                OrbCore.Opacity = IsConnected ? 1 : 0.58;
            }
        }

        private void DrawLevelArc()
        {
            if (LevelArc == null || DialThumb == null || CenterLabel == null)
            {
                return;
            }

            double radius = 141;
            double center = 155;
            double startAngle = 135;
            double sweepAngle = 270 * Level / 100;
            double endAngle = startAngle + sweepAngle;
            Point start = PointOnCircle(center, radius, startAngle);
            Point end = PointOnCircle(center, radius, endAngle);
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(
                end,
                new System.Windows.Size(radius, radius),
                0,
                sweepAngle > 180,
                SweepDirection.Clockwise,
                true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            LevelArc.Data = geometry;
            LevelArcGlow.Data = geometry.Clone();
            Canvas.SetLeft(DialThumb, end.X - DialThumb.Width / 2);
            Canvas.SetTop(DialThumb, end.Y - DialThumb.Height / 2);
            LevelCaption.Text = $"{(int)Math.Round(Level)}";
            if (!IsReadOnly)
            {
                CenterLabel.Text = OrbDialRules.LabelForLevel(Level);
            }
        }

        private static Point PointOnCircle(double center, double radius, double angleDegrees)
        {
            double angle = angleDegrees * Math.PI / 180;
            return new Point(
                center + Math.Cos(angle) * radius,
                center + Math.Sin(angle) * radius);
        }

        private void DialCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsConnected)
            {
                return;
            }

            _dragStart = e.GetPosition(DialCanvas);
            double dx = _dragStart.X - 155;
            double dy = _dragStart.Y - 155;
            double distanceSquared = dx * dx + dy * dy;
            _pointerStartedOnCore = distanceSquared < 82 * 82;
            _pointerStartedOnDial = distanceSquared >= 84 * 84 && distanceSquared <= 153 * 153;
            if (!_pointerStartedOnCore && !_pointerStartedOnDial)
            {
                return;
            }

            _isDragging = false;
            DialCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void DialCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!DialCanvas.IsMouseCaptured || !_pointerStartedOnDial)
            {
                return;
            }

            Point position = e.GetPosition(DialCanvas);
            double moveX = position.X - _dragStart.X;
            double moveY = position.Y - _dragStart.Y;
            if (!_isDragging && moveX * moveX + moveY * moveY < 9)
            {
                return;
            }

            _isDragging = true;
            if (IsReadOnly)
            {
                return;
            }

            double angle = Math.Atan2(position.Y - 155, position.X - 155) * 180 / Math.PI;
            Level = OrbDialRules.LevelFromAngle(angle);
            Execute(LevelCommand, Level);
        }

        private void DialCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!DialCanvas.IsMouseCaptured)
            {
                return;
            }

            DialCanvas.ReleaseMouseCapture();
            if (!_isDragging && _pointerStartedOnCore)
            {
                Execute(ToggleCommand, null);
            }

            _isDragging = false;
            _pointerStartedOnCore = false;
            _pointerStartedOnDial = false;
            e.Handled = true;
        }

        private void DialCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _isDragging = false;
            }
        }

        private static void Execute(ICommand? command, object? parameter)
        {
            if (command?.CanExecute(parameter) == true)
            {
                command.Execute(parameter);
            }
        }
    }
}
