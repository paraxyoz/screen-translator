using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ScreenTranslator.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ScreenTranslator.Services
{
    public sealed class TextRecognitionService
    {
        private readonly LoggingService _logger;
        private readonly SettingsService _settings;
        private readonly ScreenCaptureService _capture;
        private readonly ConcurrentDictionary<string, OcrEngine> _engineCache = new ConcurrentDictionary<string, OcrEngine>(StringComparer.OrdinalIgnoreCase);

        public TextRecognitionService(LoggingService logger, SettingsService settings, ScreenCaptureService capture)
        {
            _logger = logger;
            _settings = settings;
            _capture = capture;
        }

        public IReadOnlyList<string> GetAvailableOcrLanguages()
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages
                    .Select(l => l.LanguageTag)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger?.Error("TextRecognitionService", "Не удалось получить список языков Windows OCR", ex);
                return new[] { "en-US", "ru" };
            }
        }

        public async Task<OcrRecognitionResult> CaptureAndRecognizeUnderCursorAsync(
            Point cursorPhysicalPos,
            Action beforeCaptureAction,
            Action afterCaptureAction,
            CancellationToken ct)
        {
            int baseW = Math.Max(180, _settings.Current.CursorCaptureWidth);
            int baseH = Math.Max(60, _settings.Current.CursorCaptureHeight);

            Rectangle initialRect = ScreenCaptureService.ClampToVirtualScreen(new Rectangle(
                cursorPhysicalPos.X - baseW / 2,
                cursorPhysicalPos.Y - baseH / 2,
                baseW,
                baseH));

            var swCapture = Stopwatch.StartNew();
            Bitmap bmp = null;
            try
            {
                beforeCaptureAction?.Invoke();
                bmp = _capture.CaptureScreenRect(initialRect);
            }
            finally
            {
                afterCaptureAction?.Invoke();
            }
            swCapture.Stop();

            string preferredLang = ResolvePreferredOcrLanguage();
            var result = await RecognizeBitmapAsync(bmp, initialRect, preferredLang, ct);
            result.CaptureElapsedMs = swCapture.ElapsedMilliseconds;

            if (_settings.Current.AutoExpandCursorCapture && ShouldExpandCursorCapture( new Rectangle(0, 0, initialRect.Width, initialRect.Height), result.Lines))
            {
                ct.ThrowIfCancellationRequested();
                int expW = Math.Min(920, (int)(baseW * 1.75));
                int expH = Math.Min(280, (int)(baseH * 1.75));
                Rectangle expandedRect = ScreenCaptureService.ClampToVirtualScreen(new Rectangle(
                    cursorPhysicalPos.X - expW / 2,
                    cursorPhysicalPos.Y - expH / 2,
                    expW,
                    expH));

                if (expandedRect.Width > initialRect.Width || expandedRect.Height > initialRect.Height)
                {
                    var swExpCapture = Stopwatch.StartNew();
                    Bitmap expBmp = null;
                    try
                    {
                        beforeCaptureAction?.Invoke();
                        expBmp = _capture.CaptureScreenRect(expandedRect);
                    }
                    finally
                    {
                        afterCaptureAction?.Invoke();
                    }
                    swExpCapture.Stop();

                    var expResult = await RecognizeBitmapAsync(expBmp, expandedRect, preferredLang, ct);
                    expResult.CaptureElapsedMs = swCapture.ElapsedMilliseconds + swExpCapture.ElapsedMilliseconds;
                    expResult.WasExpanded = true;

                    if (expResult.Success && expResult.FullText.Length >= result.FullText.Length)
                    {
                        bmp?.Dispose();
                        FilterLinesNearCursor(expResult, cursorPhysicalPos);
                        return expResult;
                    }
                    else
                    {
                        expBmp?.Dispose();
                    }
                }
            }

            if (result.Success)
            {
                FilterLinesNearCursor(result, cursorPhysicalPos);
            }
            return result;
        }

        public async Task<OcrRecognitionResult> CaptureAndRecognizeRegionAsync(
            Rectangle screenRect,
            Action beforeCaptureAction,
            Action afterCaptureAction,
            CancellationToken ct)
        {
            Rectangle clamped = ScreenCaptureService.ClampToVirtualScreen(screenRect);
            var swCapture = Stopwatch.StartNew();
            Bitmap bmp = null;
            try
            {
                beforeCaptureAction?.Invoke();
                bmp = _capture.CaptureScreenRect(clamped);
            }
            finally
            {
                afterCaptureAction?.Invoke();
            }
            swCapture.Stop();

            string preferredLang = ResolvePreferredOcrLanguage();
            var res = await RecognizeBitmapAsync(bmp, clamped, preferredLang, ct);
            res.CaptureElapsedMs = swCapture.ElapsedMilliseconds;
            return res;
        }

        public static bool ShouldExpandCursorCapture(Rectangle localCaptureBounds, IReadOnlyList<RecognizedTextLine> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                return true;
            }

            const int edgeMarginX = 16;
            const int edgeMarginY = 10;

            foreach (var line in lines)
            {
                var b = line.BoundingBox;
                if (b.Left <= edgeMarginX || b.Right >= localCaptureBounds.Width - edgeMarginX)
                {
                    return true;
                }
                if (b.Top <= edgeMarginY || b.Bottom >= localCaptureBounds.Height - edgeMarginY)
                {
                    return true;
                }
            }

            return false;
        }

        public async Task<OcrRecognitionResult> RecognizeBitmapAsync(
            Bitmap bmp,
            Rectangle screenRect,
            string preferredLang,
            CancellationToken ct)
        {
            var swOcr = Stopwatch.StartNew();
            var output = new OcrRecognitionResult
            {
                CapturedScreenRect = screenRect,
                OriginalCapture = bmp
            };

            if (bmp == null || bmp.Width < 4 || bmp.Height < 4)
            {
                output.Success = false;
                output.ErrorMessage = "Выбранная область экрана слишком мала для распознавания.";
                return output;
            }

            try
            {
                ct.ThrowIfCancellationRequested();

                int scaleFactor = 1;
                if (_settings.Current.UpscaleSmallRegions && (bmp.Height < 110 || bmp.Width < 220))
                {
                    scaleFactor = 2;
                }

                using Bitmap ocrInputBmp = scaleFactor > 1
                    ? ScreenCaptureService.CreateUpscaledBitmap(bmp, scaleFactor)
                    : (Bitmap)bmp.Clone();

                using SoftwareBitmap softwareBmp = await ConvertToSoftwareBitmapAsync(ocrInputBmp);
                ct.ThrowIfCancellationRequested();

                var enginesToTry = GetCandidateEngines(preferredLang);
                OcrResult bestOcr = null;
                string bestLang = string.Empty;
                int bestScore = -1;

                foreach (var (langTag, engine) in enginesToTry)
                {
                    if (engine == null) continue;
                    var ocrRes = await engine.RecognizeAsync(softwareBmp).AsTask(ct);
                    int score = ScoreOcrResult(ocrRes, langTag);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestOcr = ocrRes;
                        bestLang = langTag;
                    }
                    // If specific language was requested or good text was found, avoid redundant second pass
                    if (!string.Equals(preferredLang, "auto", StringComparison.OrdinalIgnoreCase) || score >= 18)
                    {
                        break;
                    }
                }

                swOcr.Stop();
                output.OcrElapsedMs = swOcr.ElapsedMilliseconds;
                output.LanguageUsed = bestLang;
                output.OverallStyle = ScreenCaptureService.SampleRegionStyle(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));

                if (bestOcr == null || bestOcr.Lines == null || bestOcr.Lines.Count == 0 || string.IsNullOrWhiteSpace(bestOcr.Text))
                {
                    output.Success = false;
                    output.ErrorMessage = "Текст в выбранной области не обнаружен.";
                    _logger?.Info("TextRecognitionService", $"OCR завершён за {output.OcrElapsedMs} мс: текст не найден.");
                    return output;
                }

                var sb = new StringBuilder();
                foreach (var line in bestOcr.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line.Text)) continue;

                    var words = new List<RecognizedWordRect>();
                    int minX = int.MaxValue, minY = int.MaxValue;
                    int maxX = int.MinValue, maxY = int.MinValue;

                    foreach (var w in line.Words)
                    {
                        int wx = (int)Math.Round(w.BoundingRect.X / scaleFactor);
                        int wy = (int)Math.Round(w.BoundingRect.Y / scaleFactor);
                        int ww = Math.Max(4, (int)Math.Round(w.BoundingRect.Width / scaleFactor));
                        int wh = Math.Max(4, (int)Math.Round(w.BoundingRect.Height / scaleFactor));

                        var wordBox = new Rectangle(wx, wy, ww, wh);
                        words.Add(new RecognizedWordRect
                        {
                            Text = w.Text,
                            BoundingBox = wordBox
                        });

                        if (wordBox.Left < minX) minX = wordBox.Left;
                        if (wordBox.Top < minY) minY = wordBox.Top;
                        if (wordBox.Right > maxX) maxX = wordBox.Right;
                        if (wordBox.Bottom > maxY) maxY = wordBox.Bottom;
                    }

                    if (minX == int.MaxValue)
                    {
                        minX = 0; minY = 0; maxX = bmp.Width; maxY = bmp.Height;
                    }

                    var lineBox = Rectangle.Intersect(
                        new Rectangle(minX, minY, Math.Max(8, maxX - minX), Math.Max(8, maxY - minY)),
                        new Rectangle(0, 0, bmp.Width, bmp.Height));

                    string cleanedLine = line.Text.Trim();
                    var lineStyle = ScreenCaptureService.SampleRegionStyle(bmp, lineBox);

                    output.Lines.Add(new RecognizedTextLine
                    {
                        Text = cleanedLine,
                        BoundingBox = lineBox,
                        ScreenBoundingBox = new Rectangle(
                            screenRect.Left + lineBox.Left,
                            screenRect.Top + lineBox.Top,
                            lineBox.Width,
                            lineBox.Height),
                        Style = lineStyle,
                        Words = words
                    });

                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(cleanedLine);
                }

                output.FullText = sb.ToString().Trim();
                output.Success = !string.IsNullOrWhiteSpace(output.FullText);
                if (!output.Success)
                {
                    output.ErrorMessage = "Текст в выбранной области не обнаружен.";
                }

                _logger?.Info("TextRecognitionService", $"OCR ({output.LanguageUsed}) выполнен за {output.OcrElapsedMs} мс, распознано строк: {output.Lines.Count}.");
                return output;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                swOcr.Stop();
                output.OcrElapsedMs = swOcr.ElapsedMilliseconds;
                output.Success = false;
                output.ErrorMessage = "Ошибка локального распознавания текста (OCR): " + ex.Message;
                _logger?.Error("TextRecognitionService", "Сбой при распознавании изображения", ex);
                return output;
            }
        }

        private string ResolvePreferredOcrLanguage()
        {
            string pref = _settings.Current.PreferredOcrLanguage;
            if (!string.IsNullOrWhiteSpace(pref) && !string.Equals(pref, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return pref;
            }

            string src = _settings.Current.SourceLanguage;
            if (!string.IsNullOrWhiteSpace(src) && !string.Equals(src, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return src;
            }

            return "auto";
        }

        private List<(string Tag, OcrEngine Engine)> GetCandidateEngines(string preferredLang)
        {
            var list = new List<(string Tag, OcrEngine Engine)>();
            if (!string.IsNullOrWhiteSpace(preferredLang) && !string.Equals(preferredLang, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var eng = GetOrCreateEngine(preferredLang);
                if (eng != null)
                {
                    list.Add((eng.RecognizerLanguage.LanguageTag, eng));
                    return list;
                }
            }

            // In "auto" mode, prioritize language opposite to TargetLanguage first
            string target = (_settings.Current.TargetLanguage ?? "ru").ToLowerInvariant();
            string firstLang = target.StartsWith("en") ? "ru" : "en-US";
            string secondLang = target.StartsWith("en") ? "en-US" : "ru";

            var firstEngine = GetOrCreateEngine(firstLang);
            if (firstEngine != null) list.Add((firstEngine.RecognizerLanguage.LanguageTag, firstEngine));

            var secondEngine = GetOrCreateEngine(secondLang);
            if (secondEngine != null && (firstEngine == null || !string.Equals(firstEngine.RecognizerLanguage.LanguageTag, secondEngine.RecognizerLanguage.LanguageTag, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add((secondEngine.RecognizerLanguage.LanguageTag, secondEngine));
            }

            if (list.Count == 0)
            {
                var fallback = OcrEngine.TryCreateFromUserProfileLanguages();
                if (fallback != null) list.Add((fallback.RecognizerLanguage.LanguageTag, fallback));
            }

            return list;
        }

        private OcrEngine GetOrCreateEngine(string langCode)
        {
            if (string.IsNullOrWhiteSpace(langCode)) return null;
            return _engineCache.GetOrAdd(langCode, code =>
            {
                try
                {
                    var available = OcrEngine.AvailableRecognizerLanguages;
                    var match = available.FirstOrDefault(l =>
                        string.Equals(l.LanguageTag, code, StringComparison.OrdinalIgnoreCase) ||
                        l.LanguageTag.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase) ||
                        code.StartsWith(l.LanguageTag + "-", StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        return OcrEngine.TryCreateFromLanguage(match);
                    }

                    var lang = new Language(code);
                    if (OcrEngine.IsLanguageSupported(lang))
                    {
                        return OcrEngine.TryCreateFromLanguage(lang);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn("TextRecognitionService", $"Не удалось инициализировать OCR для '{code}': {ex.Message}");
                }
                return OcrEngine.TryCreateFromUserProfileLanguages();
            });
        }

        private static int ScoreOcrResult(OcrResult res, string langTag)
        {
            if (res == null || string.IsNullOrWhiteSpace(res.Text)) return 0;
            int lettersOrDigits = 0;
            int totalNonSpace = 0;
            foreach (char c in res.Text)
            {
                if (char.IsWhiteSpace(c)) continue;
                totalNonSpace++;
                if (char.IsLetterOrDigit(c)) lettersOrDigits++;
            }
            if (totalNonSpace == 0) return 0;
            return lettersOrDigits * 3 - (totalNonSpace - lettersOrDigits);
        }

        private static void FilterLinesNearCursor(OcrRecognitionResult result, Point cursorPhysicalPos)
        {
            if (result.Lines.Count <= 1) return;

            int localCursorY = cursorPhysicalPos.Y - result.CapturedScreenRect.Top;
            // Find line closest vertically to the cursor
            RecognizedTextLine closest = result.Lines
                .OrderBy(l =>
                {
                    int centerY = l.BoundingBox.Top + l.BoundingBox.Height / 2;
                    return Math.Abs(centerY - localCursorY);
                })
                .First();

            // Group lines that belong to the same paragraph around `closest` (vertical gap <= 2.2x line height)
            var ordered = result.Lines.OrderBy(l => l.BoundingBox.Top).ToList();
            int idx = ordered.IndexOf(closest);
            var kept = new List<RecognizedTextLine> { closest };

            int currentTop = closest.BoundingBox.Top;
            int currentBottom = closest.BoundingBox.Bottom;
            int avgHeight = Math.Max(14, closest.BoundingBox.Height);

            for (int i = idx - 1; i >= 0; i--)
            {
                var line = ordered[i];
                if (currentTop - line.BoundingBox.Bottom <= avgHeight * 2.2)
                {
                    kept.Insert(0, line);
                    currentTop = line.BoundingBox.Top;
                }
                else break;
            }

            for (int i = idx + 1; i < ordered.Count; i++)
            {
                var line = ordered[i];
                if (line.BoundingBox.Top - currentBottom <= avgHeight * 2.2)
                {
                    kept.Add(line);
                    currentBottom = line.BoundingBox.Bottom;
                }
                else break;
            }

            if (kept.Count > 0 && kept.Count < result.Lines.Count)
            {
                result.Lines = kept;
                result.FullText = string.Join(Environment.NewLine, kept.Select(k => k.Text));
            }
        }

        private static async Task<SoftwareBitmap> ConvertToSoftwareBitmapAsync(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
    }
}
