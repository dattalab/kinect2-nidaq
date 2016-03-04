using System;
using System.Windows;
using System.IO;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Kinect;
using Sensor;
using NationalInstruments;
using NationalInstruments.DAQmx;
using AForge.Video.FFMPEG;

namespace kinect2_nidaq
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        // Declarations 
        KinectSensor sensor;
        ColorFrameReader ColorReader;
        DepthFrameReader DepthReader;

        ColorSpacePoint[] fColorSpacepoints = null;
        ColorFrameEventArgs LastColorFrame;
        DepthFrameEventArgs LastDepthFrame;
        DispatcherTimer ImageTimer;
        byte[] colorData = new byte[Constants.kDefaultColorFrameHeight * Constants.kDefaultColorFrameWidth * Constants.kBytesPerPixel];
        ushort[] depthData = new ushort[Constants.kDefaultFrameHeight * Constants.kDefaultFrameWidth];

        AnalogMultiChannelReader NidaqReader;
        NidaqData NidaqDump = new NidaqData();
        
        AsyncCallback AnalogInCallback;
        NationalInstruments.DAQmx.Task AnalogInTask;
        NationalInstruments.DAQmx.Task runningTask;
        Stopwatch StampingWatch;

        BlockingCollection<ColorFrameEventArgs> ColorFrameQueue = new BlockingCollection<ColorFrameEventArgs>(Constants.kMaxFrames);
        BlockingCollection<DepthFrameEventArgs> DepthFrameQueue = new BlockingCollection<DepthFrameEventArgs>(Constants.kMaxFrames);
        BlockingCollection<NidaqData> NidaqQueue = new BlockingCollection<NidaqData>(Constants.nMaxBuffer);
        
        VideoFileWriter VideoWriter = new VideoFileWriter();
        TimeSpan VideoWriterInitialTimeSpan;

        System.Threading.Tasks.Task ColorDumpTask;
        System.Threading.Tasks.Task DepthDumpTask;
        System.Threading.Tasks.Task NidaqDumpTask;

        int ColorFramesDropped = 0;
        int DepthFramesDropped = 0;

        string TestPath_ColorTs = @"C:\users\dattalab\desktop\testing_color_ts.txt";
        string TestPath_ColorVid = @"C:\users\dattalab\desktop\testing_color.mp4";
        string TestPath_DepthTs = @"C:\users\dattalab\desktop\testing_depth_ts.txt";
        string TestPath_DepthVid = @"C:\users\dattalab\desktop\testing_depth.bin";
        
        // write out metadata...

        TimeSpan timeout = new TimeSpan(100000);

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

                // Start the time stamper

                StampingWatch = new Stopwatch();
                StampingWatch.Start(); 

                // Open the Kinect

                ColorReader = sensor.ColorFrameSource.OpenReader();
                DepthReader = sensor.DepthFrameSource.OpenReader();
                
                ColorReader.FrameArrived += ColorReader_FrameArrived;
                DepthReader.FrameArrived += DepthReader_FrameArrived;

                // Open the NiDAQ, set up timer

                // Display code

                ImageTimer = new DispatcherTimer();
                ImageTimer.Interval = TimeSpan.FromMilliseconds(50.0);
                ImageTimer.Tick += ImageTimer_Tick;
                ImageTimer.Start();

                // Start the tasks that empty each queue

                ColorDumpTask = System.Threading.Tasks.Task.Factory.StartNew(ColorRunner,TaskCreationOptions.LongRunning);
                DepthDumpTask = System.Threading.Tasks.Task.Factory.StartNew(DepthRunner,TaskCreationOptions.LongRunning);
               

            }

            // fire up the nidaq, for now just record channel ai0
            
            AIChannel MyAiChannel;

            AnalogInTask = new NationalInstruments.DAQmx.Task();
            AnalogInTask.AIChannels.CreateVoltageChannel(
                "dev1/ai0",
                "MyAiChannel",
                AITerminalConfiguration.Nrse,
                0,
                5,
                AIVoltageUnits.Volts);

            AnalogInTask.Timing.ConfigureSampleClock("", 30, SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, 1);
            AnalogInTask.Control(TaskAction.Verify);

            NidaqReader = new AnalogMultiChannelReader(AnalogInTask.Stream);
            NidaqReader.SynchronizeCallbacks = true;
           
            AnalogInCallback = new AsyncCallback(AnalogIn_Callback);
            NidaqReader.BeginReadMultiSample(1, AnalogInCallback, AnalogInTask);
            
            runningTask = AnalogInTask;
            NidaqDumpTask = System.Threading.Tasks.Task.Factory.StartNew(NidaqRunner,TaskCreationOptions.LongRunning);
            
        }

        /// <summary>
        /// Clean up when the window closes
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Closed(object sender, EventArgs e)

        {
            if (ColorReader != null)
            {
                ColorReader.Dispose();
            }

            if (DepthReader != null)
            {
                DepthReader.Dispose();
            }

            if (sensor != null)
            {
                sensor.Close();
            }

            ColorFrameQueue.CompleteAdding();
            DepthFrameQueue.CompleteAdding();
            NidaqQueue.CompleteAdding();
            
            foreach (System.Threading.Tasks.Task task in new List<System.Threading.Tasks.Task>
            {
                DepthDumpTask,
                ColorDumpTask,
                NidaqDumpTask
            })
            {
                if (task != null)
                {
                    try
                    {
                        task.Wait();
                    }
                    catch (AggregateException ae)
                    {
                        ae.Handle((x) =>
                            {
                                // do something if the task doesn't finish...
                                return false;
                            });
                    }
                }
            }

        }

        /// <summary>
        /// Grab the multi-source data, update display code and buffers
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void ColorReader_FrameArrived(object sender, ColorFrameArrivedEventArgs e)
        {
            
            // grab and dispatch

            using (ColorFrame frame = e.FrameReference.AcquireFrame())
            {
                
                ColorFrameEventArgs colorEventArgs = new ColorFrameEventArgs();
                colorEventArgs.RelativeTime = e.FrameReference.RelativeTime;
                colorEventArgs.TimeStamp = StampingWatch.ElapsedMilliseconds;

                if (frame == null)
                {
                    ColorFramesDropped++;
                }
                else
                {   

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

                    ColorFrameArrived(colorEventArgs);

                }
            }
        }

        void ColorFrameArrived(ColorFrameEventArgs e)
        {
            LastColorFrame=e;
            ColorFrameQueue.Add(e);
        }

        void DepthReader_FrameArrived(object sender, DepthFrameArrivedEventArgs e)
        {
            using (DepthFrame frame = e.FrameReference.AcquireFrame())
            {

                DepthFrameEventArgs depthEventArgs = new DepthFrameEventArgs();

                if (frame == null)
                {
                    DepthFramesDropped++;
                }
                else
                {
                    depthEventArgs.RelativeTime = frame.RelativeTime;                   
                    frame.CopyFrameDataToArray(depthData);

                    fColorSpacepoints = new ColorSpacePoint[depthData.Length];
                    sensor.CoordinateMapper.MapDepthFrameToColorSpace(depthData, fColorSpacepoints);

                    depthEventArgs.TimeStamp = StampingWatch.ElapsedMilliseconds;
                    depthEventArgs.DepthData = depthData;
                    depthEventArgs.Height = frame.FrameDescription.Height;
                    depthEventArgs.Width = frame.FrameDescription.Width;
                    depthEventArgs.DepthMinReliableDistance = frame.DepthMinReliableDistance;
                    depthEventArgs.DepthMaxReliableDistance = frame.DepthMaxReliableDistance;

                    // update to include absolute timestamps with hi-rest stopwatch

                    DepthFrameArrived(depthEventArgs);

                }
            }
        }

        void DepthFrameArrived(DepthFrameEventArgs e)
        {
            LastDepthFrame=e;
            DepthFrameQueue.Add(e);
        }
      
        /// <summary>
        /// Deal with the National instruments data
        /// </summary>
        /// <param name="ar"></param>
        void AnalogIn_Callback(IAsyncResult ar)
        {

            if (null != runningTask && runningTask == ar.AsyncState)
            {
                // read in with waveform data type to get timestamp

                NidaqDump.Data = NidaqReader.EndReadMultiSample(ar);
                NidaqDump.TimeStamp = StampingWatch.ElapsedTicks;
                NidaqQueue.Add(NidaqDump);
                NidaqReader.BeginReadMultiSample(1, AnalogInCallback, AnalogInTask);
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
        /// Chomps on color data (write out to mp4)
        /// </summary>
        private void ColorRunner()
        {
            while (!ColorFrameQueue.IsCompleted)
            {
                ColorFrameEventArgs colorData = null;
                while (ColorFrameQueue.TryTake(out colorData, timeout))
                {
                    if (!VideoWriter.IsOpen)
                    {
                        VideoWriter.Open(TestPath_ColorVid, 512, 424);
                        this.VideoWriterInitialTimeSpan = colorData.RelativeTime;
                    }
                    File.AppendAllText(TestPath_ColorTs, String.Format("{0} {1}\n", colorData.RelativeTime.TotalMilliseconds, colorData.TimeStamp));
                    VideoWriter.WriteVideoFrame(colorData.ToBitmap().ToSystemBitmap(), colorData.RelativeTime - this.VideoWriterInitialTimeSpan);
                }
            }
            
        }

        /// <summary>
        /// Chomps on depth data (write out to bin)
        /// </summary>
        private void DepthRunner()
        {
            while (!DepthFrameQueue.IsCompleted)
            {
                DepthFrameEventArgs depthData = null;
                while (DepthFrameQueue.TryTake(out depthData, timeout))
                {
                    File.AppendAllText(TestPath_DepthTs, String.Format("{0} {1}\n", depthData.RelativeTime.TotalMilliseconds, depthData.TimeStamp));
                }
            }
          
        }

        /// <summary> 
        /// Chomps on nidaq data (write out to bin)
        /// </summary>
        private void NidaqRunner()
        {
            while (!NidaqQueue.IsCompleted)
            {
                NidaqData nidaqDatum = null;
                while (NidaqQueue.TryTake(out nidaqDatum, timeout))
                {
                    // write out nidaq data, etc. etc.
                    ;
                }
            }
        }

    }

}