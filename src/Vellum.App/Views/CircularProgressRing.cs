using System;
using System.Windows;
using System.Windows.Media;

namespace Vellum.App.Views
{
    public sealed class CircularProgressRing : FrameworkElement
    {
        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            nameof(Progress),
            typeof(double),
            typeof(CircularProgressRing),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        private static readonly Brush TrackBrush = CreateBrush(Color.FromArgb(110, 35, 91, 145));
        private static readonly Brush ProgressBrush = CreateBrush(Color.FromRgb(76, 205, 255));

        public double Progress
        {
            get => (double)GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0)
                return;

            var thickness = Math.Max(1, size * 0.13);
            var radius = (size - thickness) / 2;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var trackPen = CreatePen(TrackBrush, thickness);
            drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

            var progress = double.IsNaN(Progress) ? 0 : Math.Clamp(Progress, 0, 100);
            if (progress <= 0)
                return;

            var progressPen = CreatePen(ProgressBrush, thickness);
            if (progress >= 100)
            {
                drawingContext.DrawEllipse(null, progressPen, center, radius, radius);
                return;
            }

            var start = new Point(center.X, center.Y - radius);
            var angle = (progress / 100 * 360 - 90) * Math.PI / 180;
            var end = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(start, isFilled: false, isClosed: false);
                context.ArcTo(end, new Size(radius, radius), 0, progress > 50,
                    SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, progressPen, geometry);
        }

        private static Pen CreatePen(Brush brush, double thickness)
            => new(brush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };

        private static Brush CreateBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
