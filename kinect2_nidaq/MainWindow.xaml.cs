using System;
using System.Windows;
using System.IO;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Kinect;
using Sensor;

namespace kinect2_nidaq
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        // Declarations 
        KinectSensor sensor;
        MultiSourceFrameReader reader;
        ColorSpacePoint[] fColorSpacepoints = null;
        ColorFrameEventArgs LastColorFrame;
        DepthFrameEventArgs LastDepthFrame;
        DispatcherTimer ImageTimer;


        BlockingCollection<ColorFrameEventArgs> ColorFrameQueue = new BlockingCollection<ColorFrameEventArgs>(Constants.kMaxFrames);
        BlockingCollection<DepthFrameEventArgs> DepthFrameQueue = new BlockingCollection<DepthFrameEventArgs>(Constants.kMaxFrames);
        Task ColorDumpTask;
        Task DepthDumpTask;
        int ColorFramesDropped = 0;
        int DepthFramesDropped = 0;

        string TestPath = @"C:\users\dattalab\desktop\testing.txt";

        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Main loop
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {

            sensor = KinectSensor.GetDefault();

            if (sensor != null)
            {
                sensor.Open();

                // Open the Kinect

                reader = sensor.OpenMultiSourceFrameReader(FrameSourceTypes.Color | FrameSourceTypes.Depth);
                reader.MultiSourceFrameArrived += Reader_MultiSourceFrameArrived;

                // Open the NiDAQ, set up timer

                // Display code

                ImageTimer = new DispatcherTimer();
                ImageTimer.Interval = TimeSpan.FromMilliseconds(50.0);
                ImageTimer.Tick += ImageTimer_Tick;
                ImageTimer.Start();

                // Start the tasks that empty each queue

                ColorDumpTask = Task.Factory.StartNew(() => ColorRunner());
                DepthDumpTask = Task.Factory.StartNew(() => DepthRunner());
                

            }
        }

        /// <summary>
        /// Clean up when the window closes
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Closed(object sender, EventArgs e)


        {
            if (reader != null)
            {
                reader.Dispose();
            }

            if (sensor != null)
            {
                sensor.Close();
            }

            ColorFrameQueue.CompleteAdding();
            DepthFrameQueue.CompleteAdding();




        }

        /// <summary>
        /// Grab the multi-source data, update display code and buffers
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void Reader_MultiSourceFrameArrived(object sender, MultiSourceFrameArrivedEventArgs e)
        {
            var reference = e.FrameReference.AcquireFrame();

            // grab and dispatch

            using (var frame = reference.ColorFrameReference.AcquireFrame())
            {

                ColorFrameEventArgs colorEventArgs = new ColorFrameEventArgs();

                if (frame != null)
                {

                    byte[] colorData = new byte[frame.FrameDescription.Width * frame.FrameDescription.Height * Constants.kBytesPerPixel];

                    if (frame.RawColorImageFormat == ColorImageFormat.Bgra)
                    {
                        frame.CopyRawFrameDataToArray(colorData);
                    }
                    else
                    {
                        frame.CopyConvertedFrameDataToArray(colorData, ColorImageFormat.Bgra);
                    }

                    colorEventArgs.RelativeTime = frame.RelativeTime;
                    colorEventArgs.ColorData = colorData;
                    colorEventArgs.ColorSpacepoints = fColorSpacepoints;
                    colorEventArgs.Height = frame.FrameDescription.Height;
                    colorEventArgs.Width = frame.FrameDescription.Width;
                    colorEventArgs.DepthWidth = 512;
                    colorEventArgs.DepthHeight = 424;

                    // update to include absolute timestamps with hi-rest stopwatch

                    LastColorFrame = colorEventArgs;

                    //oh boy this adds up fast...

                    ColorFrameQueue.Add(colorEventArgs);

                }
                else
                {
                    ColorFramesDropped++;
                }

            }

            using (var frame = reference.DepthFrameReference.AcquireFrame())
            {

                DepthFrameEventArgs depthEventArgs = new DepthFrameEventArgs();

                if (frame != null)
                {

                    ushort[] depthData = new ushort[frame.FrameDescription.Width * frame.FrameDescription.Height];
                    frame.CopyFrameDataToArray(depthData);

                    fColorSpacepoints = new ColorSpacePoint[depthData.Length];
                    sensor.CoordinateMapper.MapDepthFrameToColorSpace(depthData, fColorSpacepoints);

                    depthEventArgs.RelativeTime = frame.RelativeTime;
                    depthEventArgs.DepthData = depthData;
                    depthEventArgs.Height = frame.FrameDescription.Height;
                    depthEventArgs.Width = frame.FrameDescription.Width;
                    depthEventArgs.DepthMinReliableDistance = frame.DepthMinReliableDistance;
                    depthEventArgs.DepthMaxReliableDistance = frame.DepthMaxReliableDistance;

                    // update to include absolute timestamps with hi-rest stopwatch

                    LastDepthFrame = depthEventArgs;

                    //oh boy this adds up fast...

                    DepthFrameQueue.Add(depthEventArgs);

                }
                else
                {
                    DepthFramesDropped++;
                }

            }

        }

        /// <summary>
        /// Updates display
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void ImageTimer_Tick(object sender, EventArgs e)
        {
            if (null != LastColorFrame)
            {
                ColorDisplay.Source = LastColorFrame.ToBitmap();
            }

            if (null != LastDepthFrame)
            {
                DepthDisplay.Source = LastDepthFrame.ToBitmap();
            }
                
        }

        /// <summary>
        /// Chomps on color data
        /// </summary>
        private void ColorRunner()
        {
            foreach (var fColorFrame in ColorFrameQueue.GetConsumingEnumerable())
            {
                // write out all relevant color data stuff...

                File.AppendAllText(TestPath, String.Format("{0}\n", fColorFrame.RelativeTime.TotalMilliseconds));

            }
        }

        /// <summary>
        /// Chomps on depth data
        /// </summary>
        private void DepthRunner()
        {
            foreach (var fDepthFrame in DepthFrameQueue.GetConsumingEnumerable())
            {
                // Do something with the depth data...

            }

        }

    }

}