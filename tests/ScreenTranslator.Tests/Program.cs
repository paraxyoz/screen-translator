using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScreenTranslator.Models;
using ScreenTranslator.Services;

namespace ScreenTranslator.Tests
{
    public static class Program
    {
        private static int _passed;
        private static int _failed;

        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== ScreenTranslator Automated Test Suite ===");

            RunTest("TranslationCache_StoresAndRetrievesNormalizedKeys", TestTranslationCacheNormalization);
            RunTest("TranslationCache_EvictsLeastRecentlyUsedWhenFull", TestTranslationCacheLruEviction);
            RunTest("LoggingService_RedactsSecretsAndApiKeys", TestLoggingServiceRedaction);
            RunTest("SettingsService_EncryptsApiKeysWithDpapiAndPersists", TestSettingsServiceDpapiRoundtrip);
            RunTest("ScreenCaptureService_SamplesSolidDarkAndLightBackgrounds", TestScreenCaptureStyleSampling);
            RunTest("ScreenCaptureService_DetectsComplexBackgroundVariance", TestScreenCaptureComplexBackground);
            RunTest("TextRecognitionService_RecognizesEnglishAndRussianLocally", () => TestLocalOcrEnglishAndRussian().GetAwaiter().GetResult());
            RunTest("TextRecognitionService_DetectsWhenCursorRegionNeedsExpansion", TestAdaptiveCursorExpansionLogic);
            RunTest("TranslationService_ReturnsClearErrorWhenDeepLKeyMissing", () => TestTranslationMissingKeyError().GetAwaiter().GetResult());
            RunTest("TranslationService_UsesCacheOnRepeatedRequests", () => TestTranslationCacheHit().GetAwaiter().GetResult());

            Console.WriteLine("---------------------------------------------");
            Console.WriteLine($"Total: {_passed + _failed} | Passed: {_passed} | Failed: {_failed}");
            return _failed == 0 ? 0 : 1;
        }

        private static void RunTest(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine($"[PASS] {name}");
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine($"[FAIL] {name}: {ex.Message}");
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"{message} (Expected: '{expected}', Actual: '{actual}')");
            }
        }

        private static void TestTranslationCacheNormalization()
        {
            var cache = new TranslationCache(10);
            var res = new TranslationResult
            {
                Success = true,
                SourceText = "Hello   world",
                TranslatedText = "Привет мир",
                DetectedSourceLanguage = "en",
                TargetLanguage = "ru",
                Provider = TranslationProviderType.GoogleGtx
            };

            cache.Put(TranslationProviderType.GoogleGtx, "auto", "ru", "  Hello   world \r\n", res);
            bool found = cache.TryGet(TranslationProviderType.GoogleGtx, "auto", "ru", "Hello world", out var cached);

            AssertTrue(found, "Expected normalized cache lookup to hit");
            AssertEqual("Привет мир", cached.TranslatedText, "Cached translation text mismatch");
            AssertTrue(cached.FromCache, "Cached item should have FromCache=true");
            AssertEqual(1, cache.HitCount, "HitCount should be 1");
        }

        private static void TestTranslationCacheLruEviction()
        {
            var cache = new TranslationCache(2);
            cache.Put(TranslationProviderType.GoogleGtx, "en", "ru", "one", new TranslationResult { Success = true, TranslatedText = "один" });
            cache.Put(TranslationProviderType.GoogleGtx, "en", "ru", "two", new TranslationResult { Success = true, TranslatedText = "два" });

            // Touch "one" so "two" becomes LRU
            cache.TryGet(TranslationProviderType.GoogleGtx, "en", "ru", "one", out _);

            cache.Put(TranslationProviderType.GoogleGtx, "en", "ru", "three", new TranslationResult { Success = true, TranslatedText = "три" });

            AssertTrue(cache.TryGet(TranslationProviderType.GoogleGtx, "en", "ru", "one", out _), "'one' should remain in cache");
            AssertTrue(!cache.TryGet(TranslationProviderType.GoogleGtx, "en", "ru", "two", out _), "'two' should have been evicted");
            AssertTrue(cache.TryGet(TranslationProviderType.GoogleGtx, "en", "ru", "three", out _), "'three' should be in cache");
        }

        private static void TestLoggingServiceRedaction()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ScreenTranslatorTests_" + Guid.NewGuid().ToString("N"));
            try
            {
                var logger = new LoggingService(tempDir);
                logger.Info("TranslationService", "Auth header Bearer sk-proj-1234567890abcdef and key=secret999");

                string sanitized = LoggingService.SanitizeMessage("Bearer sk-proj-1234567890abcdef and apiKey=mysecret123");
                AssertTrue(!sanitized.Contains("1234567890abcdef"), "Sanitized log must not contain Bearer token");
                AssertTrue(!sanitized.Contains("mysecret123"), "Sanitized log must not contain apiKey value");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        private static void TestSettingsServiceDpapiRoundtrip()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ScreenTranslatorSettings_" + Guid.NewGuid().ToString("N"));
            try
            {
                var logger = new LoggingService(tempDir);
                var service = new SettingsService(logger, Path.Combine(tempDir, "settings.json"));
                service.SetDeepLApiKey("deepl-secret-key-abc-987");
                service.SetOpenAiApiKey("sk-openai-secret-xyz-111");
                service.Current.TargetLanguage = "ru";
                service.Save();

                string rawJson = File.ReadAllText(Path.Combine(tempDir, "settings.json"));
                AssertTrue(!rawJson.Contains("deepl-secret-key-abc-987"), "Raw settings.json must NOT store plaintext DeepL key");
                AssertTrue(!rawJson.Contains("sk-openai-secret-xyz-111"), "Raw settings.json must NOT store plaintext OpenAI key");

                var reloaded = new SettingsService(logger, Path.Combine(tempDir, "settings.json"));
                reloaded.Load();
                AssertEqual("deepl-secret-key-abc-987", reloaded.GetDeepLApiKey(), "Decrypted DeepL key mismatch");
                AssertEqual("sk-openai-secret-xyz-111", reloaded.GetOpenAiApiKey(), "Decrypted OpenAI key mismatch");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        private static void TestScreenCaptureStyleSampling()
        {
            using var darkBmp = new Bitmap(120, 40);
            using (var g = Graphics.FromImage(darkBmp))
            {
                g.Clear(Color.FromArgb(20, 24, 32));
            }
            var darkStyle = ScreenCaptureService.SampleRegionStyle(darkBmp, new Rectangle(5, 5, 110, 30));
            AssertTrue(darkStyle.BackgroundColor.R < 40 && darkStyle.BackgroundColor.B < 50, "Expected sampled dark background");
            AssertTrue(darkStyle.ForegroundColor.R > 200, "Expected high-contrast light foreground on dark background");
            AssertTrue(!darkStyle.IsComplexBackground, "Uniform dark background should not be marked complex");

            using var lightBmp = new Bitmap(120, 40);
            using (var g = Graphics.FromImage(lightBmp))
            {
                g.Clear(Color.FromArgb(248, 250, 252));
            }
            var lightStyle = ScreenCaptureService.SampleRegionStyle(lightBmp, new Rectangle(5, 5, 110, 30));
            AssertTrue(lightStyle.BackgroundColor.R > 230, "Expected sampled light background");
            AssertTrue(lightStyle.ForegroundColor.R < 50, "Expected dark foreground on light background");
        }

        private static void TestScreenCaptureComplexBackground()
        {
            using var noisyBmp = new Bitmap(120, 40);
            for (int y = 0; y < 40; y++)
            {
                for (int x = 0; x < 120; x++)
                {
                    int v = ((x * 37 + y * 53) % 2 == 0) ? 10 : 245;
                    noisyBmp.SetPixel(x, y, Color.FromArgb(v, v, v));
                }
            }
            var style = ScreenCaptureService.SampleRegionStyle(noisyBmp, new Rectangle(5, 5, 110, 30));
            AssertTrue(style.IsComplexBackground, $"Expected noisy background to be detected as complex (variance={style.BackgroundVariance:F1})");
        }

        private static async Task TestLocalOcrEnglishAndRussian()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ScreenTranslatorOcr_" + Guid.NewGuid().ToString("N"));
            try
            {
                var logger = new LoggingService(tempDir);
                var settings = new SettingsService(logger, Path.Combine(tempDir, "settings.json"));
                var capture = new ScreenCaptureService(logger);
                var ocr = new TextRecognitionService(logger, settings, capture);

                using var enBmp = new Bitmap(420, 90);
                using (var g = Graphics.FromImage(enBmp))
                {
                    g.Clear(Color.FromArgb(24, 26, 32));
                    using var f = new Font("Segoe UI", 20, FontStyle.Regular);
                    g.DrawString("Settings and Preferences", f, Brushes.White, new PointF(18, 24));
                }
                var enRes = await ocr.RecognizeBitmapAsync(enBmp, new Rectangle(100, 100, 420, 90), "en-US", CancellationToken.None);
                AssertTrue(enRes.Success, "English OCR failed: " + enRes.ErrorMessage);
                AssertTrue(enRes.FullText.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0, $"Expected 'Settings' in '{enRes.FullText}'");
                AssertTrue(enRes.Lines.Count >= 1, "Expected at least 1 OCR line");

                using var ruBmp = new Bitmap(460, 90);
                using (var g = Graphics.FromImage(ruBmp))
                {
                    g.Clear(Color.FromArgb(245, 247, 250));
                    using var f = new Font("Segoe UI", 20, FontStyle.Regular);
                    g.DrawString("Проверка распознавания текста", f, Brushes.Black, new PointF(18, 24));
                }
                var ruRes = await ocr.RecognizeBitmapAsync(ruBmp, new Rectangle(100, 100, 460, 90), "ru", CancellationToken.None);
                AssertTrue(ruRes.Success, "Russian OCR failed: " + ruRes.ErrorMessage);
                AssertTrue(ruRes.FullText.IndexOf("Проверка", StringComparison.OrdinalIgnoreCase) >= 0, $"Expected 'Проверка' in '{ruRes.FullText}'");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        private static void TestAdaptiveCursorExpansionLogic()
        {
            var captureRect = new Rectangle(0, 0, 400, 100);
            var clippedLines = new List<RecognizedTextLine>
            {
                new RecognizedTextLine { Text = "Partial line touching right edge", BoundingBox = new Rectangle(12, 30, 384, 24) }
            };
            bool needsExpand = TextRecognitionService.ShouldExpandCursorCapture(captureRect, clippedLines);
            AssertTrue(needsExpand, "Expected expansion when text touches right edge of cursor capture box");

            var centeredLines = new List<RecognizedTextLine>
            {
                new RecognizedTextLine { Text = "Short button", BoundingBox = new Rectangle(140, 35, 110, 22) }
            };
            bool centeredNeedsExpand = TextRecognitionService.ShouldExpandCursorCapture(captureRect, centeredLines);
            AssertTrue(!centeredNeedsExpand, "Centered short text should not trigger unnecessary expansion");
        }

        private static async Task TestTranslationMissingKeyError()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ScreenTranslatorTrans_" + Guid.NewGuid().ToString("N"));
            try
            {
                var logger = new LoggingService(tempDir);
                var settings = new SettingsService(logger, Path.Combine(tempDir, "settings.json"));
                settings.Current.Provider = TranslationProviderType.DeepL;
                settings.SetDeepLApiKey("");
                var cache = new TranslationCache(50);
                var translator = new TranslationService(logger, settings, cache);

                var res = await translator.TranslateAsync("Hello world", "en", "ru", CancellationToken.None);
                AssertTrue(!res.Success, "DeepL without API key must return Success=false");
                AssertTrue(res.ErrorMessage.IndexOf("DeepL", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           res.ErrorMessage.IndexOf("ключ", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected clear Russian error about missing DeepL API key, got: '{res.ErrorMessage}'");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        private static async Task TestTranslationCacheHit()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ScreenTranslatorCacheHit_" + Guid.NewGuid().ToString("N"));
            try
            {
                var logger = new LoggingService(tempDir);
                var settings = new SettingsService(logger, Path.Combine(tempDir, "settings.json"));
                settings.Current.Provider = TranslationProviderType.GoogleGtx;
                var cache = new TranslationCache(50);
                cache.Put(TranslationProviderType.GoogleGtx, "en", "ru", "Cached phrase", new TranslationResult
                {
                    Success = true,
                    SourceText = "Cached phrase",
                    TranslatedText = "Кэшированная фраза",
                    DetectedSourceLanguage = "en",
                    TargetLanguage = "ru",
                    Provider = TranslationProviderType.GoogleGtx
                });

                var translator = new TranslationService(logger, settings, cache);
                var res = await translator.TranslateAsync("Cached phrase", "en", "ru", CancellationToken.None);
                AssertTrue(res.Success, "Cached translation should succeed");
                AssertTrue(res.FromCache, "Result should come from cache");
                AssertEqual("Кэшированная фраза", res.TranslatedText, "Translated text mismatch");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }
    }
}
