using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenTranslator.Models;
using ScreenTranslator.Win32;

namespace ScreenTranslator.Services
{
    public sealed class ScreenCaptureService
    {
        private readonly LoggingService _logger;

        public ScreenCaptureService(LoggingService logger)
        {
            _logger = logger;
        }

        public static Rectangle ClampToVirtualScreen(Rectangle requested)
        {
            Rectangle virtualScreen = NativeMethods.GetVirtualScreenBounds();
            Rectangle intersected = Rectangle.Intersect(requested, virtualScreen);
            if (intersected.Width < 8 || intersected.Height < 8)
            {
                int x = Math.Max(virtualScreen.Left, Math.Min(requested.X, virtualScreen.Right - 32));
                int y = Math.Max(virtualScreen.Top, Math.Min(requested.Y, virtualScreen.Bottom - 32));
                int w = Math.Max(16, Math.Min(requested.Width, virtualScreen.Width));
                int h = Math.Max(16, Math.Min(requested.Height, virtualScreen.Height));
                return new Rectangle(x, y, w, h);
            }
            return intersected;
        }

        public Bitmap CaptureScreenRect(Rectangle screenRect)
        {
            Rectangle clamped = ClampToVirtualScreen(screenRect);
            var bmp = new Bitmap(clamped.Width, clamped.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(clamped.Left, clamped.Top, 0, 0, clamped.Size, CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        public static Bitmap CreateUpscaledBitmap(Bitmap source, int scaleFactor = 2)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (scaleFactor <= 1) return (Bitmap)source.Clone();

            int newW = source.Width * scaleFactor;
            int newH = source.Height * scaleFactor;
            var dest = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dest))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, newW, newH), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            }
            return dest;
        }

        public static SampledRegionStyle SampleRegionStyle(Bitmap bmp, Rectangle rect)
        {
            var result = new SampledRegionStyle();
            if (bmp == null || bmp.Width == 0 || bmp.Height == 0)
            {
                return result;
            }

            Rectangle bounds = new Rectangle(0, 0, bmp.Width, bmp.Height);
            Rectangle target = Rectangle.Intersect(rect, bounds);
            if (target.Width <= 0 || target.Height <= 0)
            {
                target = bounds;
            }

            // Expand slightly by 2px to sample background perimeter around glyphs
            Rectangle expanded = Rectangle.Intersect(Rectangle.Inflate(target, 2, 2), bounds);

            BitmapData data = null;
            try
            {
                data = bmp.LockBits(expanded, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int width = expanded.Width;
                int height = expanded.Height;
                int stride = Math.Abs(data.Stride);
                byte[] buffer = new byte[stride * height];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                var rValues = new List<byte>(256);
                var gValues = new List<byte>(256);
                var bValues = new List<byte>(256);
                var luminances = new List<double>(256);

                int stepX = Math.Max(1, width / 28);
                int stepY = Math.Max(1, height / 14);

                // Prioritize border ring pixels (top/bottom 2 rows, left/right 2 cols) where background is less occluded by glyphs
                for (int y = 0; y < height; y++)
                {
                    bool isBorderY = y < 2 || y >= height - 2;
                    if (!isBorderY && (y % stepY != 0)) continue;

                    int rowOffset = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        bool isBorderX = x < 2 || x >= width - 2;
                        if (!isBorderY && !isBorderX && (x % stepX != 0)) continue;

                        int idx = rowOffset + x * 4;
                        if (idx + 2 >= buffer.Length) continue;

                        byte b = buffer[idx];
                        byte g = buffer[idx + 1];
                        byte r = buffer[idx + 2];

                        rValues.Add(r);
                        gValues.Add(g);
                        bValues.Add(b);
                        luminances.Add(0.2126 * r + 0.7152 * g + 0.0722 * b);
                    }
                }

                if (rValues.Count == 0)
                {
                    return result;
                }

                rValues.Sort();
                gValues.Sort();
                bValues.Sort();

                int mid = rValues.Count / 2;
                byte medR = rValues[mid];
                byte medG = gValues[mid];
                byte medB = bValues[mid];

                double meanLum = 0;
                for (int i = 0; i < luminances.Count; i++) meanLum += luminances[i];
                meanLum /= luminances.Count;

                double varianceSum = 0;
                for (int i = 0; i < luminances.Count; i++)
                {
                    double diff = luminances[i] - meanLum;
                    varianceSum += diff * diff;
                }
                double stdDev = Math.Sqrt(varianceSum / luminances.Count);

                Color bg = Color.FromArgb(medR, medG, medB);
                double bgLum = 0.2126 * medR + 0.7152 * medG + 0.0722 * medB;

                // Choose optimal high-contrast foreground color (WCAG contrast >= 7:1)
                Color fg = bgLum < 140.0
                    ? Color.FromArgb(244, 247, 251)
                    : Color.FromArgb(15, 23, 42);

                result.BackgroundColor = bg;
                result.ForegroundColor = fg;
                result.BackgroundVariance = stdDev;
                return result;
            }
            catch (Exception)
            {
                return result;
            }
            finally
            {
                if (data != null)
                {
                    try { bmp.UnlockBits(data); } catch { }
                }
            }
        }
    }
}
