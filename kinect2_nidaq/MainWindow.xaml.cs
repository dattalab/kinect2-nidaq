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

        /// <summary>
        /// Kinect Sensor
        /// </summary>
        KinectSensor sensor;

        /// <summary>
        /// Color frame Reader
        /// </summary>
        ColorFrameReader ColorReader;

        /// <summary>
        /// Depth frame reader
        /// </summary>
        DepthFrameReader DepthReader;

        /// <summary>
        /// Map from depth to color
        /// </summary>
        ColorSpacePoint[] fColorSpacepoints = null;

        /// <summary>
        /// For color frame display
        /// </summary>
        ColorFrameEventArgs LastColorFrame;

        /// <summary>
        /// For depth frame display
        /// </summary>
        DepthFrameEventArgs LastDepthFrame;

        /// <summary>
        /// Display updater
        /// </summary>
        DispatcherTimer ImageTimer;

        /// <summary>
        /// Color data storage buffer
        /// </summary>
        byte[] colorData = new byte[Constants.kDefaultColorFrameHeight * Constants.kDefaultColorFrameWidth * Constants.kBytesPerPixel];

        /// <summary>
        /// Depth data storage buffer
        /// </summary>
        ushort[] depthData = new ushort[Constants.kDefaultFrameHeight * Constants.kDefaultFrameWidth];

        /// <summary>
        /// National Instruments reader
        /// </summary>
        AnalogMultiChannelReader NidaqReader;

        /// <summary>
        /// Class for storing national instruments data
        /// </summary>
        NidaqData NidaqDump = new NidaqData();
        
        /// <summary>
        /// National Instruments callback
        /// </summary>
        AsyncCallback AnalogInCallback;

        /// <summary>
        /// National Instruments task
        /// </summary>
        NationalInstruments.DAQmx.Task AnalogInTask;
        NationalInstruments.DAQmx.Task runningTask;

        /// <summary>
        /// Common timestamp
        /// </summary>
        Stopwatch StampingWatch;

        /// <summary>
        /// Queue for color frames
        /// </summary>
        BlockingCollection<ColorFrameEventArgs> ColorFrameQueue = new BlockingCollection<ColorFrameEventArgs>(Constants.kMaxFrames);

        /// <summary>
        /// Queue for depth frames
        /// </summary>
        BlockingCollection<DepthFrameEventArgs> DepthFrameQueue = new BlockingCollection<DepthFrameEventArgs>(Constants.kMaxFrames);

        /// <summary>
        /// Queue for National Instruments data
        /// </summary>
        BlockingCollection<AnalogWaveform<double>[]> NidaqQueue = new BlockingCollection<AnalogWaveform<double>[]>(Constants.nMaxBuffer);
       

        /// <summary>
        /// For writing out color video files
        /// </summary>
        VideoFileWriter VideoWriter = new VideoFileWriter();
        
        /// <summary>
        /// Video timestamp
        /// </summary>
        TimeSpan VideoWriterInitialTimeSpan;

        /// <summary>
        /// Color queue dump
        /// </summary>
        System.Threading.Tasks.Task ColorDumpTask;

        /// <summary>
        /// Depth queue dump
        /// </summary>
        System.Threading.Tasks.Task DepthDumpTask;

        /// <summary>
        /// National Instruments queue dump
        /// </summary>
        System.Threading.Tasks.Task NidaqDumpTask;

        /// <summary>
        /// How many frames dropped?
        /// </summary>
        int ColorFramesDropped = 0;
        int DepthFramesDropped = 0;

        string TestPath_ColorTs = @"C:\users\dattalab\desktop\testing_color_ts.txt";
        string TestPath_ColorVid = @"C:\users\dattalab\desktop\testing_color.mp4";
        string TestPath_DepthTs = @"C:\users\dattalab\desktop\testing_depth_ts.txt";
        string TestPath_DepthVid = @"C:\users\dattalab\desktop\testing_depth.bin";
        string TestPath_Nidaq = @"C:\users\dattalab\desktop\testing_nidaq.txt";

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
            NidaqReader.BeginReadWaveform(1, AnalogInCallback, AnalogInTask);
            
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

                AnalogWaveform<double>[] Waveforms = NidaqReader.EndReadWaveform(ar);
                NidaqQueue.Add(Waveforms);
                NidaqReader.BeginReadWaveform(1, AnalogIn_Callback, AnalogInTask);
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
                AnalogWaveform<double>[] NIDatum = null;
                
                while (NidaqQueue.TryTake(out NIDatum, timeout))
                {

                    int nsamples = NIDatum[0].SampleCount;
                    int nchannels = NIDatum.Length;

                    double[,] data = new double[nsamples, nchannels];
                    NationalInstruments.PrecisionDateTime[,] timestamps = new NationalInstruments.PrecisionDateTime[nsamples,nchannels];

                    // write out nidaq data, etc. etc.

                    for (int i = 0; i < NIDatum.Length; i++) 
                    {
                        double[] tmp = NIDatum[i].GetScaledData();
                        NationalInstruments.PrecisionDateTime[] tmp1 = NIDatum[i].GetPrecisionTimeStamps();

                        // do something with each datapoint and timestamp

                        for (int ii = 0; ii < tmp.Length; ii++)
                        {
                            data[ii, i] = tmp[ii];
                            timestamps[ii, i] = tmp1[ii];
                        }

                    }

                    // now we can write out to the file in an ordered fashion...



                }
            }
        }

    }

}