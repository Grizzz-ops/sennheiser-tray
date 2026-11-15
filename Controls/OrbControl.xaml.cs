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
        private long _lastRenderTicks;
        private long _pulseStartTicks;
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
                CompositionTarget.Rendering += CompositionTarget_Rendering;
            }
        }

        private void OrbControl_Unloaded(object sender, RoutedEventArgs e) =>
            CompositionTarget.Rendering -= CompositionTarget_Rendering;

        private void CompositionTarget_Rendering(object? sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _lastRenderTicks < Stopwatch.Frequency / 30)
            {
                return;
            }

            _lastRenderTicks = now;
            double elapsed = (now - _pulseStartTicks) / (double)Stopwatch.Frequency;
            double connectPulse = elapsed is >= 0 and < 0.55
                ? Math.Sin(Math.PI * elapsed / 0.55) * 0.16
                : 0;
            double idleBreath = (Math.Sin(now / (double)Stopwatch.Frequency * (2 * Math.PI / 4.2)) + 1) * 0.5;
            double liveLevel = AudioLevel;
            double scale = 1 + connectPulse + liveLevel * 0.12 + (liveLevel < 0.015 ? idleBreath * 0.025 : 0);
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
            CoreScale.ScaleX = 1 + connectPulse * 0.25 + liveLevel * 0.055;
            CoreScale.ScaleY = CoreScale.ScaleX;
            double normalOpacity = (IsConnected ? 0.38 + liveLevel * 0.4 : 0.12) + connectPulse;
            OrbGlow.Opacity = _isFocusTransitionActive
                ? focusOpacity + liveLevel * 0.18
                : IsFocusBreak
                    ? _focusTransitionTargetOpacity + liveLevel * 0.18
                    : normalOpacity;
            OrbCore.Opacity = IsConnected ? 1 : 0.58;
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

            double radius = 123;
            double center = 135;
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
            double dx = _dragStart.X - 135;
            double dy = _dragStart.Y - 135;
            double distanceSquared = dx * dx + dy * dy;
            _pointerStartedOnCore = distanceSquared < 68 * 68;
            _pointerStartedOnDial = distanceSquared >= 70 * 70 && distanceSquared <= 134 * 134;
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

            double angle = Math.Atan2(position.Y - 135, position.X - 135) * 180 / Math.PI;
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
