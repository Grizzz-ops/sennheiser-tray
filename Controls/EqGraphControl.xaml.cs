using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;

namespace SennheiserTray
{
    public partial class EqGraphControl : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
            nameof(Values),
            typeof(IList<double>),
            typeof(EqGraphControl),
            new PropertyMetadata(null, OnValuesChanged));

        private int _draggingBand = -1;
        private int _hoveredBand = -1;
        private Point _lastPointer = new(-1, -1);
        private Ellipse? _cursorGlow;

        public EqGraphControl()
        {
            InitializeComponent();
        }

        public IList<double>? Values
        {
            get => (IList<double>?)GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        private static void OnValuesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            var control = (EqGraphControl)dependencyObject;
            if (e.OldValue is INotifyCollectionChanged oldValues)
            {
                oldValues.CollectionChanged -= control.Values_CollectionChanged;
            }

            if (e.NewValue is INotifyCollectionChanged newValues)
            {
                newValues.CollectionChanged += control.Values_CollectionChanged;
            }

            control.DrawGraph();
        }

        private void Values_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => DrawGraph();

        private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

        private void DrawGraph()
        {
            IList<double>? values = Values;
            if (GraphCanvas == null || values == null || values.Count < 2
                || GraphCanvas.ActualWidth <= 0 || GraphCanvas.ActualHeight <= 0)
            {
                return;
            }

            double width = GraphCanvas.ActualWidth;
            double height = GraphCanvas.ActualHeight;
            double left = 18;
            double right = width - 18;
            double top = 14;
            double bottom = height - 14;
            double plotHeight = bottom - top;
            Point[] points = new Point[values.Count];

            for (int i = 0; i < values.Count; i++)
            {
                double x = left + (right - left) * i / (values.Count - 1);
                double y = top + (6 - values[i] / 10.0) / 12 * plotHeight;
                points[i] = new Point(x, y);
            }

            GraphCanvas.Children.Clear();
            for (int i = 0; i < 5; i++)
            {
                double y = top + plotHeight * i / 4;
                GraphCanvas.Children.Add(new Line
                {
                    X1 = left,
                    X2 = right,
                    Y1 = y,
                    Y2 = y,
                    Stroke = new SolidColorBrush(i == 2 ? Color.FromRgb(66, 97, 116) : Color.FromRgb(37, 53, 67)),
                    StrokeThickness = i == 2 ? 1.2 : 0.7,
                    StrokeDashArray = i == 2 ? null : new DoubleCollection { 2, 3 },
                    IsHitTestVisible = false
                });
            }

            foreach (Point point in points)
            {
                GraphCanvas.Children.Add(new Line
                {
                    X1 = point.X,
                    X2 = point.X,
                    Y1 = top,
                    Y2 = bottom,
                    Stroke = new SolidColorBrush(Color.FromArgb(75, 74, 103, 123)),
                    StrokeThickness = 0.7,
                    IsHitTestVisible = false
                });
            }

            var curveFigure = new PathFigure { StartPoint = points[0] };
            AddCurveSegments(curveFigure, points);
            var curve = new PathGeometry();
            curve.Figures.Add(curveFigure);

            var fillFigure = new PathFigure
            {
                StartPoint = new Point(points[0].X, bottom),
                IsClosed = true
            };
            fillFigure.Segments.Add(new LineSegment(points[0], true));
            AddCurveSegments(fillFigure, points);
            fillFigure.Segments.Add(new LineSegment(new Point(points[^1].X, bottom), true));
            var fillGeometry = new PathGeometry();
            fillGeometry.Figures.Add(fillFigure);

            GraphCanvas.Children.Add(new Path
            {
                Data = fillGeometry,
                Fill = new LinearGradientBrush(
                    Color.FromArgb(75, 47, 201, 244),
                    Color.FromArgb(3, 47, 201, 244),
                    90),
                IsHitTestVisible = false
            });
            GraphCanvas.Children.Add(new Path
            {
                Data = curve,
                Stroke = new SolidColorBrush(Color.FromRgb(74, 220, 255)),
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            });

            _cursorGlow = new Ellipse
            {
                Width = 72,
                Height = 72,
                Fill = new RadialGradientBrush
                {
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(65, 50, 201, 255), 0),
                        new GradientStop(Color.FromArgb(0, 50, 201, 255), 1)
                    }
                },
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            GraphCanvas.Children.Add(_cursorGlow);
            if (_lastPointer.X >= 0)
            {
                Canvas.SetLeft(_cursorGlow, _lastPointer.X - _cursorGlow.Width / 2);
                Canvas.SetTop(_cursorGlow, _lastPointer.Y - _cursorGlow.Height / 2);
                _cursorGlow.Visibility = Visibility.Visible;
            }

            for (int i = 0; i < points.Length; i++)
            {
                bool hovered = i == _hoveredBand;
                double size = hovered ? 15 : 11;
                var marker = new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = new SolidColorBrush(hovered
                        ? Color.FromRgb(222, 251, 255)
                        : Color.FromRgb(83, 223, 255)),
                    Stroke = new SolidColorBrush(Color.FromRgb(224, 250, 255)),
                    StrokeThickness = 1.4,
                    Effect = hovered
                        ? new DropShadowEffect
                        {
                            Color = Color.FromRgb(54, 211, 255),
                            BlurRadius = 14,
                            ShadowDepth = 0,
                            Opacity = 0.8
                        }
                        : null,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(marker, points[i].X - size / 2);
                Canvas.SetTop(marker, points[i].Y - size / 2);
                GraphCanvas.Children.Add(marker);
            }
        }

        private static void AddCurveSegments(PathFigure figure, Point[] points)
        {
            for (int i = 0; i < points.Length - 1; i++)
            {
                double middleX = (points[i].X + points[i + 1].X) / 2;
                figure.Segments.Add(new BezierSegment(
                    new Point(middleX, points[i].Y),
                    new Point(middleX, points[i + 1].Y),
                    points[i + 1],
                    true));
            }
        }

        private void GraphCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Point position = e.GetPosition(GraphCanvas);
            int band = FindNearestBand(position, 22);
            if (band < 0)
            {
                return;
            }

            _draggingBand = band;
            GraphCanvas.CaptureMouse();
            SetBandFromGraph(band, position.Y);
            e.Handled = true;
        }

        private void GraphCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            Point position = e.GetPosition(GraphCanvas);
            _lastPointer = position;
            if (_draggingBand >= 0 && e.LeftButton == MouseButtonState.Pressed)
            {
                SetBandFromGraph(_draggingBand, position.Y);
                return;
            }

            if (_cursorGlow != null)
            {
                Canvas.SetLeft(_cursorGlow, position.X - _cursorGlow.Width / 2);
                Canvas.SetTop(_cursorGlow, position.Y - _cursorGlow.Height / 2);
                _cursorGlow.Visibility = Visibility.Visible;
            }

            int hovered = FindNearestBand(position, 20);
            if (hovered != _hoveredBand)
            {
                _hoveredBand = hovered;
                GraphCanvas.Cursor = hovered >= 0 ? Cursors.Hand : Cursors.Arrow;
                DrawGraph();
            }
        }

        private void GraphCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _draggingBand = -1;
            if (GraphCanvas.IsMouseCaptured)
            {
                GraphCanvas.ReleaseMouseCapture();
            }
        }

        private void GraphCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_draggingBand >= 0)
            {
                return;
            }

            _lastPointer = new Point(-1, -1);
            _hoveredBand = -1;
            GraphCanvas.Cursor = Cursors.Arrow;
            DrawGraph();
        }

        private int FindNearestBand(Point position, double maxDistance)
        {
            IList<double>? values = Values;
            if (values == null || values.Count < 2)
            {
                return -1;
            }

            double left = 18;
            double right = GraphCanvas.ActualWidth - 18;
            double top = 14;
            double bottom = GraphCanvas.ActualHeight - 14;
            double plotHeight = bottom - top;
            int nearest = -1;
            double nearestDistance = maxDistance;
            for (int i = 0; i < values.Count; i++)
            {
                double x = left + (right - left) * i / (values.Count - 1);
                double y = top + (6 - values[i] / 10.0) / 12 * plotHeight;
                double dx = position.X - x;
                double dy = position.Y - y;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= nearestDistance)
                {
                    nearest = i;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        private void SetBandFromGraph(int band, double y)
        {
            IList<double>? values = Values;
            if (values == null || band < 0 || band >= values.Count)
            {
                return;
            }

            double top = 14;
            double bottom = GraphCanvas.ActualHeight - 14;
            double normalized = Math.Clamp((bottom - y) / (bottom - top), 0, 1);
            double sliderValue = (normalized * 120) - 60;
            values[band] = Math.Round(sliderValue / 5) * 5;
        }
    }
}
