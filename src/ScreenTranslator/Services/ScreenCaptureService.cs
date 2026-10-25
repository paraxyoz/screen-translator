using System.Drawing;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services
{
    public sealed class ScreenCaptureService
    {
        public ScreenCaptureService(LoggingService logger) { }
        public Bitmap CaptureScreenRect(Rectangle screenRect) => null;
        public static SampledRegionStyle SampleRegionStyle(Bitmap bmp, Rectangle rect) => new SampledRegionStyle();
    }
}
