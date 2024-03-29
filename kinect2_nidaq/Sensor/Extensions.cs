﻿using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Drawing;
using Microsoft.Kinect;

namespace Sensor
{

    public static class Extensions
    {
        /// <summary>
        /// Convert to bitmap for writing out
        /// </summary>
        /// <param name="wbmp"></param>
        /// <returns></returns>
        public static System.Drawing.Bitmap ToSystemBitmap(this WriteableBitmap wbmp)
        {
            System.Drawing.Bitmap bitmap = null;

            using (MemoryStream stream = new MemoryStream())
            {
                BitmapEncoder enc = new BmpBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create((BitmapSource)wbmp));
                enc.Save(stream);
                bitmap = new System.Drawing.Bitmap(stream);
            }

            return bitmap;

        }

        /// <summary>
        /// Convert ColorFrame to bitmap
        /// </summary>
        /// <param name="e"></param>
        /// <returns></returns>
        public static WriteableBitmap ToBitmap(this ColorFrameEventArgs e)
        {
            WriteableBitmap bitmap;
            bitmap = new WriteableBitmap(e.DepthWidth, e.DepthHeight, Constants.kDpi, Constants.kDpi, Constants.kFormat, null);

            int stride = bitmap.PixelWidth * Constants.kBytesPerPixel;
            if (null != e.ColorSpacepoints)
            {
                byte[] colorData = new byte[bitmap.PixelWidth * bitmap.PixelHeight * Constants.kBytesPerPixel];
                int BytesPerPixel = bitmap.Format.BitsPerPixel / 8;

                int colorIndex = 0;
                for (int depthIndex = 0; depthIndex < e.ColorSpacepoints.Length; depthIndex++)
                {
                    ColorSpacePoint colorSpacePoint = e.ColorSpacepoints[depthIndex];
                    int colorX = (int)(colorSpacePoint.X);
                    int colorY = (int)(colorSpacePoint.Y);

                    if (colorX >= 0 && colorX < e.Width && colorY >= 0 && colorY < e.Height)
                    {
                        for (int offset = 0; offset < BytesPerPixel; ++offset)
                        {
                            colorData[colorIndex + offset] = e.ColorData[(colorY * e.Width + colorX) * BytesPerPixel + offset];
                        }
                    }

                    colorIndex += BytesPerPixel;
                }

                bitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), colorData, stride, 0);
            }

            return bitmap;
        }

        public static WriteableBitmap ToBitmap(this DepthFrameEventArgs e)
        {

            PixelFormat format = PixelFormats.Bgr32;

            //ushort minDepth = e.DepthMinReliableDistance;
            //ushort maxDepth = e.DepthMaxReliableDistance;

            ushort minDepth;
            ushort maxDepth;
            
            try
            {
                minDepth = kinect2_nidaq.Properties.Settings.Default.DepthMinValue;
                maxDepth = kinect2_nidaq.Properties.Settings.Default.DepthMaxValue;               
            }
            catch
            {
                minDepth = e.DepthMinReliableDistance;
                maxDepth = e.DepthMaxReliableDistance;
            }


            minDepth = (ushort)(minDepth >= 0 ? minDepth : 1);
            maxDepth = (ushort)(maxDepth > minDepth ? maxDepth : minDepth + 1);

            byte[] pixels = new byte[e.Width * e.Height * (format.BitsPerPixel + 7) / 8];
            int colorIndex = 0;
            
            for (int depthIndex = 0; depthIndex < e.DepthData.Length; ++depthIndex)
            {

                ushort depth = e.DepthData[depthIndex];

                //byte intensity = (byte)(depth >= minDepth ? depth : 0);
                //intensity = (byte)(depth <= maxDepth ? depth : 0);

                float intensity = (float)(depth);
                
                intensity = (intensity >= minDepth ? intensity : -1);
                intensity = (intensity <= maxDepth ? intensity : -1);
                intensity = (intensity - minDepth) / (maxDepth - minDepth);

                // negative values are mapped to 1 (which becomes 0)

                intensity = (intensity < 0 ? 1 : intensity);

                // from float to byte (256 values)

                byte intensityB = (byte)(255*(1-intensity));

                pixels[colorIndex++] = intensityB;
                pixels[colorIndex++] = intensityB;
                pixels[colorIndex++] = intensityB;

                ++colorIndex;

            }

            int stride = e.Width * format.BitsPerPixel / 8;
            WriteableBitmap bitmap;
            bitmap = new WriteableBitmap(e.Width, e.Height, Constants.kDpi, Constants.kDpi, Constants.kFormat, null);
            bitmap.WritePixels(new Int32Rect(0, 0, e.Width, e.Height), pixels, stride, 0);
            
            return bitmap;


        }


    }
   
}
