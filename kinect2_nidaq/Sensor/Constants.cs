using System.Windows.Media;

    
namespace Sensor
{
    public static class Constants
    {
        public static readonly double kDpi = 96.0;
        public static readonly PixelFormat kFormat = PixelFormats.Bgr32;
        public static readonly int kBytesPerPixel = (kFormat.BitsPerPixel + 7) / 8;
        public static readonly int kFramesPerSecond = 30;
        public static readonly int kDefaultFrameWidth = 512;
        public static readonly int kDefaultFrameHeight = 424;
        public static readonly int kDefaultColorFrameWidth = 1920;
        public static readonly int kDefaultColorFrameHeight = 1080;
        public static readonly int kMaxFrames = 1000;
        public static readonly int nMaxBuffer = 10000;
        public static readonly int tUpdateCount = 15;
        public static readonly int etaMaxBuffer = 5;
    }
}
