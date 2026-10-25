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
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(clamped.Left, clamped.Top, 0, 0, clamped.Size, CopyPixelOperation.SourceCopy);
                }
                return bmp;
            }
            catch (Exception)
            {
                // Fallback for isolated Win32 desktops (e.g. sandboxed desktop sessions) where GetDC(NULL) returns ERROR_INVALID_HANDLE
                CaptureFromDesktopWindowsFallback(bmp, clamped);
                return bmp;
            }
        }

        private void CaptureFromDesktopWindowsFallback(Bitmap targetBmp, Rectangle screenRect)
        {
            try
            {
                using (var g = Graphics.FromImage(targetBmp))
                {
                    g.Clear(Color.FromArgb(18, 20, 26));
                }

                var app = System.Windows.Application.Current;
                if (app != null)
                {
                    app.Dispatcher.Invoke(() =>
                    {
                        using var g = Graphics.FromImage(targetBmp);
                        foreach (System.Windows.Window win in app.Windows)
                        {
                            if (!win.IsVisible || win.Opacity <= 0.01) continue;
                            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                            if (hwnd != IntPtr.Zero &&
                                NativeMethods.GetWindowDisplayAffinity(hwnd, out uint affinity) &&
                                affinity == NativeMethods.WDA_EXCLUDEFROMCAPTURE)
                            {
                                continue;
                            }

                            var winRect = new Rectangle(
                                (int)Math.Round(win.Left),
                                (int)Math.Round(win.Top),
                                Math.Max(1, (int)Math.Round(win.ActualWidth > 0 ? win.ActualWidth : win.Width)),
                                Math.Max(1, (int)Math.Round(win.ActualHeight > 0 ? win.ActualHeight : win.Height)));

                            if (!winRect.IntersectsWith(screenRect)) continue;

                            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                winRect.Width,
                                winRect.Height,
                                96,
                                96,
                                System.Windows.Media.PixelFormats.Pbgra32);
                            rtb.Render(win);

                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                            using var ms = new System.IO.MemoryStream();
                            encoder.Save(ms);
                            ms.Position = 0;
                            using var winBmp = new Bitmap(ms);

                            g.DrawImage(
                                winBmp,
                                new Rectangle(winRect.Left - screenRect.Left, winRect.Top - screenRect.Top, winRect.Width, winRect.Height),
                                new Rectangle(0, 0, winBmp.Width, winBmp.Height),
                                GraphicsUnit.Pixel);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn("ScreenCaptureService", "Резервный захват окна завершился с предупреждением: " + ex.Message);
            }
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

            // Expand slightly by 3px to sample clean background perimeter around glyphs
            Rectangle expanded = Rectangle.Intersect(Rectangle.Inflate(target, 3, 3), bounds);

            BitmapData data = null;
            try
            {
                data = bmp.LockBits(expanded, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int width = expanded.Width;
                int height = expanded.Height;
                int stride = Math.Abs(data.Stride);
                byte[] buffer = new byte[stride * height];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                var rValues = new List<byte>(320);
                var gValues = new List<byte>(320);
                var bValues = new List<byte>(320);
                var samples = new List<(byte R, byte G, byte B, double Lum, bool IsBorder)>(320);

                int stepX = Math.Max(1, width / 27);
                if (stepX > 1 && stepX % 2 == 0) stepX++;
                int stepY = Math.Max(1, height / 13);
                if (stepY > 1 && stepY % 2 == 0) stepY++;

                // Prioritize border ring pixels (top/bottom 2 rows, left/right 2 cols) where background is not occluded by glyphs
                for (int y = 0; y < height; y++)
                {
                    bool isBorderY = y < 2 || y >= height - 2;
                    if (!isBorderY && (y % stepY != 0)) continue;

                    int rowOffset = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        bool isBorderX = x < 2 || x >= width - 2;
                        bool isBorder = isBorderY || isBorderX;
                        if (!isBorder && (x % stepX != 0)) continue;

                        int idx = rowOffset + x * 4;
                        if (idx + 2 >= buffer.Length) continue;

                        byte b = buffer[idx];
                        byte g = buffer[idx + 1];
                        byte r = buffer[idx + 2];
                        double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;

                        rValues.Add(r);
                        gValues.Add(g);
                        bValues.Add(b);
                        samples.Add((r, g, b, lum, isBorder));
                    }
                }

                if (samples.Count == 0)
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
                double medLum = 0.2126 * medR + 0.7152 * medG + 0.0722 * medB;

                // Filter out high-contrast text glyph strokes so they don't pollute background color or variance
                long sumR = 0, sumG = 0, sumB = 0;
                var bgLums = new List<double>(samples.Count);
                foreach (var s in samples)
                {
                    int dist = Math.Max(Math.Abs(s.R - medR), Math.Max(Math.Abs(s.G - medG), Math.Abs(s.B - medB)));
                    if (dist <= 55)
                    {
                        sumR += s.R;
                        sumG += s.G;
                        sumB += s.B;
                        bgLums.Add(s.Lum);
                    }
                }

                byte finalR = medR;
                byte finalG = medG;
                byte finalB = medB;
                double stdDev;

                if (bgLums.Count >= samples.Count * 0.65)
                {
                    finalR = (byte)Math.Round((double)sumR / bgLums.Count);
                    finalG = (byte)Math.Round((double)sumG / bgLums.Count);
                    finalB = (byte)Math.Round((double)sumB / bgLums.Count);

                    double meanLum = 0;
                    for (int i = 0; i < bgLums.Count; i++) meanLum += bgLums[i];
                    meanLum /= bgLums.Count;

                    double varianceSum = 0;
                    for (int i = 0; i < bgLums.Count; i++)
                    {
                        double diff = bgLums[i] - meanLum;
                        varianceSum += diff * diff;
                    }
                    stdDev = Math.Sqrt(varianceSum / bgLums.Count);
                }
                else
                {
                    double meanLum = 0;
                    for (int i = 0; i < samples.Count; i++) meanLum += samples[i].Lum;
                    meanLum /= samples.Count;

                    double varianceSum = 0;
                    for (int i = 0; i < samples.Count; i++)
                    {
                        double diff = samples[i].Lum - meanLum;
                        varianceSum += diff * diff;
                    }
                    stdDev = Math.Sqrt(varianceSum / samples.Count);
                }

                Color bg = Color.FromArgb(finalR, finalG, finalB);
                double bgLum = 0.2126 * finalR + 0.7152 * finalG + 0.0722 * finalB;

                // Choose optimal high-contrast foreground color (WCAG contrast >= 7:1)
                Color fg = bgLum < 145.0
                    ? Color.FromArgb(245, 248, 255)
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
