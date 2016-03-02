using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;


namespace Sensor
{

    public static class Extensions
    {
        /// <summary>
        /// Convert ColorFrame to bitmap
        /// </summary>
        /// <param name="e"></param>
        /// <returns></returns>
        public static ImageSource ToBitmap(this ColorFrameEventArgs e)
        {
            PixelFormat format = PixelFormats.Bgr32;
            int stride = e.Width * format.BitsPerPixel / 8;
            return BitmapSource.Create(e.Width, e.Height, 96, 96, format, null, e.ColorData, stride);
        }

        public static ImageSource ToBitmap(this DepthFrameEventArgs e)
        {

            PixelFormat format = PixelFormats.Bgr32;

            ushort minDepth = e.DepthMinReliableDistance;
            ushort maxDepth = e.DepthMaxReliableDistance;
            byte[] pixels = new byte[e.Width * e.Height * (format.BitsPerPixel + 7) / 8];

            int colorIndex = 0;
            byte den = (byte)(maxDepth - minDepth);

            for (int depthIndex = 0; depthIndex < e.DepthData.Length; ++depthIndex)
            {

                ushort depth = e.DepthData[depthIndex];

                byte intensity = (byte)(depth >= minDepth ? depth : minDepth);
                intensity = (byte)(depth <= maxDepth ? depth : maxDepth);

                pixels[colorIndex++] = intensity;
                pixels[colorIndex++] = intensity;
                pixels[colorIndex++] = intensity;

                ++colorIndex;

            }

            int stride = e.Width * format.BitsPerPixel / 8;
            return BitmapSource.Create(e.Width, e.Height, 96, 96, format, null, pixels, stride);



        }


    }
   
}
