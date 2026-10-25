using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ScreenTranslator.UI
{
    /// <summary>
    /// Единый набор минималистичных векторных иконок с одинаковой толщиной штриха (1.75px).
    /// Эмодзи и разрозненные наборы иконок не используются.
    /// </summary>
    public static class VectorIcons
    {
        // Навигация и основные действия (координатная сетка 24x24)
        public const string Home = "M 3 10.5 L 12 3 L 21 10.5 M 5 9.5 L 5 20 L 19 20 L 19 9.5 M 9.5 20 L 9.5 14 L 14.5 14 L 14.5 20";
        public const string Translate = "M 4 5 L 14 5 M 9 3 L 9 5 M 6 5 C 6 10 9.5 13.5 13.5 15 M 11.5 5 C 10.5 9.5 7.5 13 4 15 M 13 19 L 16.5 10 L 20 19 M 14.2 16.5 L 18.8 16.5";
        public const string Hotkeys = "M 3 6 L 21 6 L 21 18 L 3 18 Z M 7 10 L 8 10 M 11.5 10 L 12.5 10 M 16 10 L 17 10 M 7 14 L 17 14";
        public const string Appearance = "M 12 3 A 9 9 0 1 0 21 12 C 21 10.5 19.8 9.5 18.2 9.5 L 16 9.5 C 14.5 9.5 13.5 8.5 13.5 7 L 13.5 5 C 13.5 3.8 12.8 3 12 3 Z M 8 10 A 1 1 0 1 1 8 10.1 M 10 7 A 1 1 0 1 1 10 7.1 M 7.5 14 A 1 1 0 1 1 7.5 14.1";
        public const string Performance = "M 13 2 L 4 14 L 11 14 L 10 22 L 20 10 L 13 10 Z";
        public const string Settings = "M 12 8.5 A 3.5 3.5 0 1 0 12 15.5 A 3.5 3.5 0 1 0 12 8.5 Z M 19.4 15 A 1.65 1.65 0 0 0 19.7 16.8 L 19.8 16.9 A 2 2 0 0 1 17 19.7 L 16.9 19.6 A 1.65 1.65 0 0 0 15.1 19.3 A 1.65 1.65 0 0 0 14 20.8 L 14 21 A 2 2 0 0 1 10 21 L 10 20.8 A 1.65 1.65 0 0 0 8.9 19.3 A 1.65 1.65 0 0 0 7.1 19.6 L 7 19.7 A 2 2 0 0 1 4.2 16.9 L 4.3 16.8 A 1.65 1.65 0 0 0 4.6 15 A 1.65 1.65 0 0 0 3.1 14 L 3 14 A 2 2 0 0 1 3 10 L 3.1 10 A 1.65 1.65 0 0 0 4.6 8.9 A 1.65 1.65 0 0 0 4.3 7.1 L 4.2 7 A 2 2 0 0 1 7 4.2 L 7.1 4.3 A 1.65 1.65 0 0 0 8.9 4.6 A 1.65 1.65 0 0 0 10 3.1 L 10 3 A 2 2 0 0 1 14 3 L 14 3.1 A 1.65 1.65 0 0 0 15.1 4.6 A 1.65 1.65 0 0 0 16.9 4.3 L 17 4.2 A 2 2 0 0 1 19.8 7 L 19.7 7.1 A 1.65 1.65 0 0 0 19.4 8.9 A 1.65 1.65 0 0 0 20.9 10 L 21 10 A 2 2 0 0 1 21 14 L 20.9 14 A 1.65 1.65 0 0 0 19.4 15 Z";
        public const string Info = "M 12 3 A 9 9 0 1 0 12 21 A 9 9 0 1 0 12 3 Z M 12 11 L 12 16 M 12 8 L 12 8.1";

        // Инструменты захвата и Overlay
        public const string CursorTarget = "M 5 3 L 19 11 L 12 13 L 15 20 L 12.5 21 L 9.5 14 L 5 18 Z";
        public const string RegionSelect = "M 4 8 L 4 4 L 8 4 M 16 4 L 20 4 L 20 8 M 20 16 L 20 20 L 16 20 M 8 20 L 4 20 L 4 16 M 9 9 L 15 9 L 15 15 L 9 15 Z";
        public const string EyeShow = "M 2 12 C 4.5 7 8 4.5 12 4.5 C 16 4.5 19.5 7 22 12 C 19.5 17 16 19.5 12 19.5 C 8 19.5 4.5 17 2 12 Z M 12 9 A 3 3 0 1 0 12 15 A 3 3 0 1 0 12 9 Z";
        public const string EyeHide = "M 3 3 L 21 21 M 10.5 6.2 C 11 6.1 11.5 6 12 6 C 16 6 19.5 8.5 22 12 C 21 13.8 19.7 15.3 18.2 16.4 M 6.2 6.3 C 4.4 7.6 3 9.5 2 12 C 4.5 16.5 8 19 12 19 C 13.8 19 15.5 18.5 17 17.6";
        public const string Refresh = "M 20 11 A 8 8 0 0 0 5.3 7.5 L 4 9 M 4 4 L 4 9 L 9 9 M 4 13 A 8 8 0 0 0 18.7 16.5 L 20 15 M 20 20 L 20 15 L 15 15";
        public const string Copy = "M 9 9 L 20 9 L 20 20 L 9 20 Z M 5 15 L 4 15 L 4 4 L 15 4 L 15 5";
        public const string MoveDrag = "M 12 2 L 12 22 M 2 12 L 22 12 M 9 5 L 12 2 L 15 5 M 9 19 L 12 22 L 15 19 M 5 9 L 2 12 L 5 15 M 19 9 L 22 12 L 19 15";
        public const string Close = "M 6 6 L 18 18 M 18 6 L 6 18";
        public const string Minimize = "M 5 12 L 19 12";
        public const string Swap = "M 7 16 L 3 12 L 7 8 M 3 12 L 21 12 M 17 8 L 21 12 L 17 16";
        public const string Sun = "M 12 7 A 5 5 0 1 0 12 17 A 5 5 0 1 0 12 7 Z M 12 2 L 12 4 M 12 20 L 12 22 M 4.9 4.9 L 6.3 6.3 M 17.7 17.7 L 19.1 19.1 M 2 12 L 4 12 M 20 12 L 22 12 M 4.9 19.1 L 6.3 17.7 M 17.7 6.3 L 19.1 4.9";
        public const string Moon = "M 20.5 14.5 A 8.5 8.5 0 0 1 9.5 3.5 A 8.5 8.5 0 1 0 20.5 14.5 Z";
        public const string Check = "M 5 12.5 L 10 17.5 L 19 6.5";
        public const string Pause = "M 8 5 L 8 19 M 16 5 L 16 19";
        public const string Play = "M 7 4 L 19 12 L 7 20 Z";
        public const string Trash = "M 4 7 L 20 7 M 9 7 L 9 4 L 15 4 L 15 7 M 6 7 L 7 20 L 17 20 L 18 7";

        public static Path CreateIcon(string pathData, double size = 16, string brushResourceKey = "TextPrimaryBrush", double strokeThickness = 1.75)
        {
            var path = new Path
            {
                Data = Geometry.Parse(pathData),
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                StrokeThickness = strokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            if (!string.IsNullOrEmpty(brushResourceKey))
            {
                path.SetResourceReference(Shape.StrokeProperty, brushResourceKey);
            }
            return path;
        }

        public static Path CreateColoredIcon(string pathData, double size, Brush strokeBrush, double strokeThickness = 1.75)
        {
            return new Path
            {
                Data = Geometry.Parse(pathData),
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                Stroke = strokeBrush,
                StrokeThickness = strokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
        }
    }
}
