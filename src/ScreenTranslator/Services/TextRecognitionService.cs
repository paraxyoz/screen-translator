using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services
{
    public sealed class TextRecognitionService
    {
        public TextRecognitionService(LoggingService logger, SettingsService settings, ScreenCaptureService capture) { }
        public Task<OcrRecognitionResult> RecognizeBitmapAsync(Bitmap bmp, Rectangle screenRect, string preferredLang, CancellationToken ct)
            => Task.FromResult(new OcrRecognitionResult { Success = false, ErrorMessage = "Not implemented" });
        public static bool ShouldExpandCursorCapture(Rectangle captureRect, IReadOnlyList<RecognizedTextLine> lines) => false;
    }
}
