using System;
using System.Windows;
using System.IO;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using Microsoft.Kinect;
using Sensor;
using NationalInstruments;
using NationalInstruments.DAQmx;
using AForge.Video.FFMPEG;
using Microsoft.WindowsAPICodePack.Dialogs;
using Newtonsoft.Json;
using Metadata;
using GzTar;

// TODO 1: Make sure we exit gracefully if start button has not been clicked (without the use of try/catch)
// TODO 2: Control sync signal in software?
// TODO 3: Make sure we're not being too clever with pre-allocation...

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
        ColorSpacePoint[] fColorSpacepoints;

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
        NidaqData NidaqDump;
        
        /// <summary>
        /// National Instruments callback
        /// </summary>
        AsyncCallback AnalogInCallback;

        /// <summary>
        /// National Instruments task
        /// </summary>
        NationalInstruments.DAQmx.Task AnalogInTask;
        NationalInstruments.DAQmx.Task runningTask;

        PerformanceCounter CPUPerformance;
        PerformanceCounter RAMPerformance;

        // By default blocking collections use FIFO (ConcurrentQueue), no need to specify

        /// <summary>
        /// Queue for color frames
        /// </summary>
        //ConcurrentQueue<ColorFrameEventArgs> ColorFrameQueue;
        BlockingCollection<ColorFrameEventArgs> ColorFrameCollection;

        /// <summary>
        /// Queue for depth frames
        /// </summary>
        /// 
        //ConcurrentQueue<DepthFrameEventArgs> DepthFrameQueue;
        BlockingCollection<DepthFrameEventArgs> DepthFrameCollection;

        /// <summary>
        /// Queue for National Instruments data
        /// </summary>
        BlockingCollection<AnalogWaveform<double>[]> NidaqQueue;
       

        /// <summary>
        /// For writing out color video files
        /// </summary>
        VideoFileWriter VideoWriter = new VideoFileWriter();
      
        /// <summary>
        /// Color queue dump
        /// </summary>
        System.Threading.Tasks.Task ColorDumpTask;

        /// <summary>
        /// Depth queue dump
        /// </summary>
        System.Threading.Tasks.Task DepthDumpTask;

        /// <summary>
        /// Compression task
        /// </summary>
        System.Threading.Tasks.Task CompressTask;

        /// <summary>
        /// Timer for the recording
        /// </summary>
        DispatcherTimer RecTimer;

        /// <summary>
        /// National Instruments queue dump
        /// </summary>
        System.Threading.Tasks.Task NidaqDumpTask;

        /// <summary>
        /// How many color frames dropped?
        /// </summary>
        private int ColorFramesDropped = 0;
        
        /// <summary>
        /// How many depth frames dropped?
        /// </summary>
        private int DepthFramesDropped = 0;
        
        /// <summary>
        /// Stores metadata
        /// </summary>
        private kMetadata fMetadata = new kMetadata();

        /// <summary>
        /// Directory initialization
        /// </summary>
        private string FilePath_ColorTs;
        private string FilePath_ColorVid;
        private string FilePath_DepthTs;
        private string FilePath_Nidaq;
        private string FilePath_DepthVid;
        private string FilePath_Metadata;
        private string FilePath_Tar;
        private string FilePath_TarMetadata;
        
        /// <summary>
        /// Session flags
        /// </summary>
        private bool IsRecordingEnabled = false;
        private bool IsColorStreamEnabled = false;
        private bool IsDepthStreamEnabled = false;
        private bool IsDataCompressed = false;
        private bool IsSessionClean = false;
        private bool IsNidaqEnabled = false;
        private bool IsNidaqDevice = true;
        private bool IsPreviewEnabled = false;

        /// <summary>
        /// Video writer timespan
        /// </summary>
        private TimeSpan VideoWriterInitialTimeSpan;

        /// <summary>
        /// Terminal configuration
        /// </summary>
        private AITerminalConfiguration MyTerminalConfig;
        
        /// <summary>
        /// Status bar timer 
        /// </summary>
        private DispatcherTimer CheckTimer;

        /// <summary>
        /// Maximum sampling rate
        /// </summary>
        private double MaxRate = 1000;

        /// <summary>
        /// Actual sampling rate
        /// </summary>
        private double SamplingRate;


        /// <summary>
        /// Save folder
        /// </summary>
        private String SaveFolder;
        
        /// <summary>
        /// All of the files!
        /// </summary>
        private FileStream NidaqFile;
        private BinaryWriter NidaqStream;

        private FileStream ColorTSFile;
        private FileStream DepthTSFile;

        private StreamWriter ColorTSStream;
        private StreamWriter DepthTSStream;

        private FileStream DepthVidFile;
        private BinaryWriter DepthVidStream;

        private ColorFrameEventArgs colorEventArgs;
        private DepthFrameEventArgs depthEventArgs;

        private AnalogWaveform<double>[] Waveforms;
        
        private double tarballProgress;
        private AutoResetEvent resetEvent;
        private List<string[]> FilePaths;
        private Queue<double> ETAQueue;
        private double ETAAve;
        private bool ContinuousMode = true;
        private double RecordingTime;
        private DateTime RecStartTime;
        private DateTime RecEndTime; 

        NationalInstruments.PrecisionDateTime[] TmpTimeStamp;
        double CurrentNITimeStamp;

        CancellationTokenSource fCancellationTokenSource = new CancellationTokenSource();

        // TODO check timeout it seems to have a major performance impact!

        /// <summary>
        /// Buffer timeout
        /// </summary>
        TimeSpan timeout = new TimeSpan(10000);
        
        /// <summary>
        /// Startup
        /// </summary>
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
               
               
                // get all channels...

                // Load up defaults using settings...

                string[] deviceList = DaqSystem.Local.Devices;
                foreach (string currentDevice in deviceList)
                {
                    DevBox.Items.Add(currentDevice.ToString());
                }

                // If the DevBox is empty (e.g. if we did not find any devices, bail)...

                if (DevBox.Items.Count > 0)
                {
                    DevBox.SelectedIndex = 0;
                    TerminalConfigBox.SelectedIndex = 0;
                    VoltageRangeBox.SelectedIndex = 0;
                }
                else
                {
                    // Inactive the Nidaq stream if we didn't find any devices
                    IsNidaqDevice = false;
                }

                CPUPerformance = new PerformanceCounter();
                RAMPerformance = new PerformanceCounter();

                CPUPerformance.CategoryName = "Processor";
                CPUPerformance.CounterName = "% Processor Time";
                CPUPerformance.InstanceName = "_Total";

                RAMPerformance.CategoryName = "Memory";
                RAMPerformance.CounterName = "Available MBytes";

                ImageTimer = new DispatcherTimer();
                ImageTimer.Interval = TimeSpan.FromMilliseconds(50.0);
                ImageTimer.Tick += ImageTimer_Tick;

                CheckTimer = new DispatcherTimer();
                CheckTimer.Interval = TimeSpan.FromMilliseconds(1000);
                CheckTimer.Tick += CheckTimer_Tick;
                CheckTimer.Start();

                SettingsChanged();
            }
            else
            {
               MessageBox.Show("No Kinect found");
               
            }  

        }

        /// <summary>
        /// Start a recording session
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            
            // Inactivate the settings, preven the user from doing something unintentional

            InactivateSettings();

            // Progress bar starts at 0

            StatusBarProgress.Value = 0;
            fColorSpacepoints = null;

            // When we the start the session, we have not compressed the data and cleaned up
            // everything

            IsDataCompressed = false;
            IsSessionClean = false;

            // and obvi no dropped frames yet...

            ColorFramesDropped = 0;
            DepthFramesDropped = 0;

            // if the user checked previewmode, don't record any data

            if (PreviewMode.IsChecked == true)
            {
                IsRecordingEnabled = false;
                IsPreviewEnabled = true;
            }
            else
            {
                IsRecordingEnabled = true;
                IsPreviewEnabled = false;
            }

            // Only create new files if we're not in preview mode


            if (CheckColorStream.IsChecked==true)
            {
                //ColorFrameQueue = new ConcurrentQueue<ColorFrameEventArgs>();
                ColorFrameCollection = new BlockingCollection<ColorFrameEventArgs>(Constants.kMaxFrames);

                if (IsRecordingEnabled == true)
                {
                    ColorTSFile = new FileStream(FilePath_ColorTs, FileMode.Append);
                    ColorTSStream = new StreamWriter(ColorTSFile);
                    // throw an exception if the queue is already completed!
                    ColorDumpTask = System.Threading.Tasks.Task.Factory.StartNew(ColorRunner, fCancellationTokenSource.Token);

                }

                ColorReader = sensor.ColorFrameSource.OpenReader();
                ColorReader.FrameArrived += ColorReader_FrameArrived;
                IsColorStreamEnabled = true;
            }
            else
            {
                IsColorStreamEnabled = false;
            }

            // Same for depth stream

            if (CheckDepthStream.IsChecked == true)
            {
                //DepthFrameQueue = new ConcurrentQueue<DepthFrameEventArgs>();
                DepthFrameCollection = new BlockingCollection<DepthFrameEventArgs>(Constants.kMaxFrames);

                if (IsRecordingEnabled==true)
                {                    
                    DepthTSFile = new FileStream(FilePath_DepthTs, FileMode.Append);
                    DepthTSStream = new StreamWriter(DepthTSFile);
                    DepthVidFile = new FileStream(FilePath_DepthVid, FileMode.Append);
                    DepthVidStream = new BinaryWriter(DepthVidFile);
                    // throw an exception if the queue is already completed!
                    DepthDumpTask = System.Threading.Tasks.Task.Factory.StartNew(DepthRunner, fCancellationTokenSource.Token);   
                }

                DepthReader = sensor.DepthFrameSource.OpenReader();
                DepthReader.FrameArrived += DepthReader_FrameArrived;
                IsDepthStreamEnabled = true;

            }
            else
            {
                IsDepthStreamEnabled = false;
            }


           // Start the display timer

            ImageTimer.Start();

            // HD check

            //CheckTimer.Start();

            // Start the tasks that empty each queue
            // TODO:  allow the user to not record Nidaq data
            // TODO:  output simple sync signal (allow user to map depth/rgb for this)
            // TODO:  simple preview mode (don't write anything out)
            // TODO:  better nidaq status indicators (ready to roll, are rolling, etc.)
            // TODO:  throw exception if we restart a new task with the queue already completed (task will just finish right away!)

            if (CheckNidaqStream.IsChecked==true && IsRecordingEnabled==true)
            {
                NidaqPrepare();

                // If NidaqPrepare worked, we should be rolling now, with the nidaqqueue re-initialized!
               
                if (IsNidaqEnabled==true)
                {
                    // throw an exception if the queue is already completed!
                    NidaqDumpTask = System.Threading.Tasks.Task.Factory.StartNew(NidaqRunner, fCancellationTokenSource.Token);
                }
            }
         
            sensor.Open();

            if (IsRecordingEnabled == true)
            {
                WriteMetadata();
            }

            StopButton.IsEnabled = true;
            StartButton.IsEnabled = false;

            if (!ContinuousMode & IsRecordingEnabled==true)
            {
                // if we're running in timed mode, fire a SessionCleanup in RecordingTime minutes...

                RecTimer = new DispatcherTimer();
                RecTimer.Interval = TimeSpan.FromMilliseconds(RecordingTime);
                RecTimer.Tick += RecTimer_Tick;
                
                RecStartTime = DateTime.Now;
                RecEndTime = RecStartTime.AddMilliseconds(RecordingTime);
                RecTimer.Start();
            }
            
        }

        /// <summary>
        /// What do do when the timer runs out
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void RecTimer_Tick(object sender, EventArgs e)
        {
            StopButton.IsEnabled = false;
            SessionCleanup();
            (sender as DispatcherTimer).Stop();
        }

        /// <summary>
        /// Performs checks if user requests to close the window
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Closed(object sender, CancelEventArgs e)
        {


            if (!IsSessionClean && IsRecordingEnabled == true)
            {
                MessageBoxResult dr = MessageBox.Show("You have not stopped the session, stop and cleanup now?", "Session stop", MessageBoxButton.YesNo);

                switch (dr)
                {
                    case MessageBoxResult.Yes: 
                        SessionCleanup();
                        e.Cancel = true;
                        return;
                    case MessageBoxResult.No:
                        break;
                }
            }

            if (CompressTask != null)
            {
                if (!CompressTask.IsCompleted) 
                {
                    MessageBoxResult dr = MessageBox.Show("Still compressing, cancel the compression and quit?", "Session stop", MessageBoxButton.YesNo);
                    switch (dr)
                    {
                        case MessageBoxResult.Yes:
                            break;
                        case MessageBoxResult.No:
                            e.Cancel = true;
                            return;
                    }
                }
            }

        }

        /// <summary>
        /// Clean up the session
        /// </summary>
        private void SessionCleanup()
        {
            // Dispose of the Kinect and the readers

            resetEvent = new AutoResetEvent(false);

            // Save the UI settings

            kinect2_nidaq.Properties.Settings.Default.Save();
            
            // Stop the sensor and the image display

            sensor.Close();
            ImageTimer.Stop();
            
            //CheckTimer.Stop();

            // Stop the Nidaq acquisition if it's enabled

            if (runningTask != null)
            {
                runningTask = null;
                AnalogInTask.Stop();
                AnalogInTask.Dispose();
            }

            // If we're recording, shut down all of the queues

            if (IsRecordingEnabled==true)
            {
                if (IsColorStreamEnabled == true)
                {
                    ColorFrameCollection.CompleteAdding();
                    //ColorFrameCollection.Dispose();
                }
                if (IsDepthStreamEnabled == true)
                {
                    DepthFrameCollection.CompleteAdding();
                    //DepthFrameCollection.Dispose();
                }
                if (IsNidaqEnabled == true)
                {
                    NidaqQueue.CompleteAdding();
                }
            }           

            // if the dump task were initiated, deal with them

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
                                Console.WriteLine("Exception finishing task:  {0} {1}\n{2}", x.GetType().ToString(), x.Message, x.StackTrace);
                                return false;
                            });
                    }
                }
            }

            // dispose of the resources to make sure we can cleanly re-initialize 

            if (IsRecordingEnabled == true)
            {
                if (IsColorStreamEnabled == true)
                {
                    ColorFrameCollection.Dispose();
                }
                if (IsDepthStreamEnabled == true)
                {
                    DepthFrameCollection.Dispose();
                }
                if (IsNidaqEnabled == true)
                {
                    NidaqQueue.Dispose();
                }
            }   
   

            // close the open videowriters

            if (VideoWriter.IsOpen)
            {
                VideoWriter.Close();
            }

            // close the readers, files and filewriting tasks


            if (IsColorStreamEnabled == true && IsRecordingEnabled == true)
            {
                ColorReader.Dispose();
                ColorTSStream.Close();
                ColorDumpTask.Dispose();
            }
            else if (IsColorStreamEnabled == true)
            {
                ColorReader.Dispose();
            }
                    
            if (IsDepthStreamEnabled == true && IsRecordingEnabled== true)
            {
                DepthReader.Dispose();
                DepthTSStream.Close();
                DepthVidStream.Close();
                DepthDumpTask.Dispose(); 
            }
            else if (IsDepthStreamEnabled == true)
            {
                DepthReader.Dispose();
            }

            if (IsNidaqEnabled == true && IsRecordingEnabled ==true)
            {
                NidaqStream.Close();
                NidaqFile.Close();
                NidaqDumpTask.Dispose();
            }
                
          
            StatusBarProgress.IsEnabled = true;
            StatusBarProgressETA.IsEnabled = true;
        
            // now enable the statusbars

            ColorLight.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
            DepthLight.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
            NidaqLight.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);

            // If we were recording and the data isn't compressed, compress it!            

            if (!IsDataCompressed && IsRecordingEnabled)
            {
                CompressTask = System.Threading.Tasks.Task.Factory.StartNew(CompressSession, fCancellationTokenSource.Token);
            }
            else if (!IsDataCompressed)
            {
                IsSessionClean = true;
            }
            // now everything has been shut off, set all the flags accordingly

            IsRecordingEnabled = false;
            IsNidaqEnabled = false;
            IsDepthStreamEnabled = false;
            IsColorStreamEnabled = false;
            IsPreviewEnabled = false;

        }


        private void CompressSession()
        {
           
            long totalBytes = 0;
            foreach (string[] FileName in FilePaths)
            {
                if (File.Exists(FileName[0]))
                {
                    FileInfo tmpInfo = new FileInfo(FileName[0]);
                    totalBytes += tmpInfo.Length;
                }
            }

            // start a stopwatch?

            Stopwatch ETATgz = new Stopwatch();
            ETATgz.Start();

            //long oldWritten = 0;
            long totalWritten = 0;
            //long leftToWrite = 0;
            //double oldProgress = 0;
            double totalSeconds = 0;
            double ETAEstimate = 0;

            ETAQueue = new Queue<double>(Constants.etaMaxBuffer);

            using (AGZTar tarball = new AGZTar(FilePath_Tar))
            {
                if (totalBytes != 0)
                {
                    tarball.WriteEvent += (sender, args) =>
                    {

                        totalSeconds = ETATgz.Elapsed.TotalSeconds;

                        totalWritten += args.Written;
                        tarballProgress = 100 - 100 * (totalBytes - totalWritten) / totalBytes;

                        // seconds per percent

                        ETAEstimate = (100 - tarballProgress) * (totalSeconds / (double)(tarballProgress));

                        ETAQueue.Enqueue(ETAEstimate);
                        while (ETAQueue.Count > Constants.etaMaxBuffer)
                        {
                            ETAQueue.Dequeue();
                        }

                        //ETAAve = ETAEstimate;

                        ETAAve = ETAQueue.Average();

                        // time since last update

                    };
                }

                foreach (string[] FileName in FilePaths)
                {
                    Console.WriteLine(String.Format("{0} {1}", FileName[0], FileName[1]));

                    if (File.Exists(FileName[0]))
                    {
                        Console.WriteLine(String.Format("{0} {1}", FileName[0], FileName[1]));
                        tarball.Write(FileName[0], FileName[1]);
                    }
                }
            }

            File.Copy(FilePath_Metadata, FilePath_TarMetadata);

            foreach (string[] FileName in FilePaths)
            {
                if (File.Exists(FileName[0]))
                {
                    File.Delete(FileName[0]);
                }

            }
            

            IsSessionClean = true;
            
        }

        /// <summary>
        /// Clean up when stop button clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            // Stop the recording timer if it's active

            if (RecTimer != null)
            {
               
                if (RecTimer.IsEnabled == true)
                {
                    RecTimer.Stop();
                }
            }


            StopButton.IsEnabled = false;
            SessionCleanup();
   

        }

        /// <summary>
        /// Grab and release color data, update display code and buffers
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ColorReader_FrameArrived(object sender, ColorFrameArrivedEventArgs e)
        {
            
            // grab and dispatch

            using (ColorFrame frame = e.FrameReference.AcquireFrame())
            {
                
                colorEventArgs = new ColorFrameEventArgs();
                colorEventArgs.RelativeTime = e.FrameReference.RelativeTime;
                colorEventArgs.TimeStamp = CurrentNITimeStamp;            

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
                    colorEventArgs.DepthWidth = Constants.kDefaultFrameWidth;
                    colorEventArgs.DepthHeight = Constants.kDefaultFrameHeight;

                    // update to include absolute timestamps with hi-rest stopwatch

                    LastColorFrame = colorEventArgs;

                    // don't add to the queue if we're in preview mode

                    if (IsRecordingEnabled && IsColorStreamEnabled)
                    {
                        ColorFrameCollection.Add(colorEventArgs);
                    }

                }
            }

        }
        

        /// <summary>
        /// Grab and release depth data
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DepthReader_FrameArrived(object sender, DepthFrameArrivedEventArgs e)
        {
            using (DepthFrame frame = e.FrameReference.AcquireFrame())
            {
               

                depthEventArgs = new DepthFrameEventArgs();

                if (frame == null)
                {
                    DepthFramesDropped++;
                }
                else
                {
                    
                    depthEventArgs.RelativeTime = frame.RelativeTime;                   
                    frame.CopyFrameDataToArray(depthData);

                    // Only map if we're also recording RGB, otherwise not reason to care...

                    if (IsColorStreamEnabled == true)
                    {
                        fColorSpacepoints = new ColorSpacePoint[depthData.Length];
                        sensor.CoordinateMapper.MapDepthFrameToColorSpace(depthData, fColorSpacepoints);
                    }

                    depthEventArgs.TimeStamp = CurrentNITimeStamp;
                    depthEventArgs.DepthData = depthData;
                    depthEventArgs.Height = frame.FrameDescription.Height;
                    depthEventArgs.Width = frame.FrameDescription.Width;
                    depthEventArgs.DepthMinReliableDistance = frame.DepthMinReliableDistance;
                    depthEventArgs.DepthMaxReliableDistance = frame.DepthMaxReliableDistance;

                    LastDepthFrame = depthEventArgs;

                    // don't add if we're in preview mode

                    if (IsRecordingEnabled && IsDepthStreamEnabled)
                    {
                        DepthFrameCollection.Add(depthEventArgs);
                    }

                }
            }
        }
      
        /// <summary>
        /// Deal with the National instruments data
        /// </summary>
        /// <param name="ar"></param>
        private void AnalogIn_Callback(IAsyncResult ar)
        {

            if (null != runningTask && runningTask == ar.AsyncState)
            {
                // read in with waveform data type to get timestamp          

                Waveforms = NidaqReader.EndReadWaveform(ar);
    
                // Current NIDAQ timestamp

                TmpTimeStamp= Waveforms[0].GetPrecisionTimeStamps();
                CurrentNITimeStamp = (double)TmpTimeStamp[0].WholeSeconds + TmpTimeStamp[0].FractionalSeconds;
                
                // Guaranteed to be 1

                NidaqQueue.Add(Waveforms);
                NidaqReader.BeginReadWaveform(1, AnalogIn_Callback, AnalogInTask);
            }       
        }
        
        /// <summary>
        /// Updates display
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ImageTimer_Tick(object sender, EventArgs e)
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

            while (!ColorFrameCollection.IsCompleted)
            {
                ColorFrameEventArgs colorData = null;
                while (ColorFrameCollection.TryTake(out colorData, timeout))
                {
                    if (IsColorStreamEnabled && IsRecordingEnabled)
                    {

                        if (!VideoWriter.IsOpen)
                        {
                            VideoWriter.Open(FilePath_ColorVid, Constants.kDefaultFrameWidth, Constants.kDefaultFrameHeight);
                            VideoWriterInitialTimeSpan = colorData.RelativeTime;
                        }
                        ColorTSStream.WriteLine(String.Format("{0} {1}", colorData.RelativeTime.TotalMilliseconds, colorData.TimeStamp));
                        VideoWriter.WriteVideoFrame(colorData.ToBitmap().ToSystemBitmap(), colorData.RelativeTime - VideoWriterInitialTimeSpan);
                    }
                }
            }          
            
        }

        /// <summary>
        /// Chomps on depth data (write out to bin)
        /// </summary>
        private void DepthRunner()
        {
            while (!DepthFrameCollection.IsCompleted)
            {
                DepthFrameEventArgs depthData = null;
                while (DepthFrameCollection.TryTake(out depthData, timeout))
                {

                    if (IsDepthStreamEnabled && IsRecordingEnabled)
                    {
                        DepthTSStream.WriteLine(String.Format("{0} {1}", depthData.RelativeTime.TotalMilliseconds, depthData.TimeStamp));
                        foreach (ushort depthDatum in depthData.DepthData)
                        {
                            DepthVidStream.Write(depthDatum);
                        }
                    }
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

                    double[][] data = new double[nchannels][];

                    // write out nidaq data, etc. etc.

                    for (int i = 0; i < nchannels; i++)
                    {
                        double[] tmp = NIDatum[i].GetScaledData();

                        // do something with each datapoint and timestamp

                        data[i] = new double[nsamples];
                        data[i] = tmp;
                    }

                    NationalInstruments.PrecisionDateTime[] timestamps = NIDatum[0].GetPrecisionTimeStamps();

                    // now we can write out...             
                    // check for multiple samples?

                    for (int i = 0; i < nsamples; i++)
                    {
                        //string writestring = "";
                        for (int ii = 0; ii < nchannels; ii++)
                        {
                            NidaqStream.Write(data[ii][i]);
                            //writestring = String.Format("{0} {1}", writestring, data[ii][i]);
                        }
                        /*NidaqStream.WriteLine(String.Format("{0} {1}",
                            writestring,
                            (double)timestamps[i].WholeSeconds + timestamps[i].FractionalSeconds));*/
                        NidaqStream.Write((double)timestamps[i].WholeSeconds + timestamps[i].FractionalSeconds);

                    }


                }
            }
        }

        /// <summary>
        /// Prepares the National Instruments board for acquisition
        /// </summary>
        private void NidaqPrepare() 
        {
            if (runningTask == null && aiChannelList.SelectedItems.Count > 0 && (SamplingRate > 0 && SamplingRate < MaxRate))
            {

                MyTerminalConfig = (AITerminalConfiguration)Enum.Parse(typeof(AITerminalConfiguration), TerminalConfigBox.SelectedItem.ToString(), true);
                AnalogInTask = new NationalInstruments.DAQmx.Task();
                NidaqDump = new NidaqData();
                string ChannelString = "";
                NidaqQueue = new BlockingCollection<AnalogWaveform<double>[]>(Constants.nMaxBuffer);
                NidaqFile = new FileStream(FilePath_Nidaq, FileMode.Append);
                NidaqStream = new BinaryWriter(NidaqFile);

                var voltageRangeSelect = VoltageRangeBox.SelectedItem;
                string voltageRangeTmp = voltageRangeSelect.ToString();
                string[] voltageRangeParse = voltageRangeTmp.Split(' ');

                Console.WriteLine(voltageRangeParse[0]);
                Console.WriteLine(voltageRangeParse[1]);
                
                foreach (var Channel in aiChannelList.SelectedItems)
                {
                    ChannelString = String.Format("{0} {1},", ChannelString, Channel.ToString());
                }

                ChannelString.Trim(new Char[] { ' ', ',' });

                AnalogInTask.AIChannels.CreateVoltageChannel(
                    ChannelString,
                    "",
                    MyTerminalConfig,
                    Convert.ToDouble(voltageRangeParse[0]),
                    Convert.ToDouble(voltageRangeParse[1]),
                    AIVoltageUnits.Volts);

                AnalogInTask.Timing.ConfigureSampleClock("", SamplingRate, SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, 1);
                AnalogInTask.Control(TaskAction.Verify);

                NidaqReader = new AnalogMultiChannelReader(AnalogInTask.Stream);
                NidaqReader.SynchronizeCallbacks = true;

                AnalogInCallback = new AsyncCallback(AnalogIn_Callback);
                NidaqReader.BeginReadWaveform(1, AnalogInCallback, AnalogInTask);

                runningTask = AnalogInTask;

                //NidaqPrepare.IsEnabled = false;
                //Indicate that we're pulling data from the Nidaq
                IsNidaqEnabled = true;

            }
        }

        /// <summary>
        /// Device selection box callback
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DevBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

            string DevSelection = DevBox.SelectedItem.ToString();
            Console.WriteLine(DevSelection);
            
            string[] ChannelList = DaqSystem.Local.LoadDevice(DevSelection).AIPhysicalChannels;
            string[] TerminalList = Enum.GetNames(typeof(AITerminalConfiguration));
            string[] UnitsList = Enum.GetNames(typeof(AIVoltageUnits));
            double[] RangeList = DaqSystem.Local.LoadDevice(DevSelection).AIVoltageRanges;

            foreach (var Channel in ChannelList)
            {
                Console.WriteLine(Channel);
                aiChannelList.Items.Add(Channel);
            }

            foreach (var TerminalConfig in TerminalList)
            {
                TerminalConfigBox.Items.Add(TerminalConfig);
            }

            for (int i = 0; i < RangeList.Length; i=i+2 )
            {
                VoltageRangeBox.Items.Add(String.Format("{0} {1}",RangeList[i],RangeList[i+1]));
            }

            MaxRate = DaqSystem.Local.LoadDevice(DevSelection).AIMaximumMultiChannelRate;

        }

        /// <summary>
        /// Sampling rate text box callback
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SamplingRateBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SettingsChanged();
        }

        /// <summary>
        /// Directory selection callback
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SelectDirectory_Click(object sender, RoutedEventArgs e)
        {
            CommonOpenFileDialog dialog = new CommonOpenFileDialog();
            dialog.IsFolderPicker = true;
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                kinect2_nidaq.Properties.Settings.Default.FolderName = FolderName.Text;
                FolderName.Text = dialog.FileName;
                SettingsChanged();
            }
        }

        private void FolderName_TextChanged(object sender, TextChangedEventArgs e)
        {
            SettingsChanged();
        }

        private void SubjectName_TextChanged(object sender, TextChangedEventArgs e) 
        {
            SettingsChanged();
        }

        private void SessionName_TextChanged(object sender, TextChangedEventArgs e)
        {
            SettingsChanged();
        }

        /// <summary>
        /// Check if settings are valid
        /// </summary>
        private void SettingsChanged()
        {
            // check sampling rate and recording configuration in general

            // set up the files and folders, if any exist do not give the user the chance to start the session...


            try
            {
                SamplingRate = Convert.ToDouble(SamplingRateBox.Text.ToString());
                SaveFolder = FolderName.Text;
                double tmp = 0;
                bool TimeFlag = true;

                // if preview is checked, then we're previewing depth and color

                // else if notimer is selected, disable time related options
                // else if recording time box is specified correctly, then convert recording time to minutes
                // else everything enabled          
      
                // how do the nidaq options look?

                // disable the nidaq stuff if there's no nidaq device

                if (!IsNidaqDevice)
                {
                    DevBox.IsEnabled = false;
                    SamplingRateBox.IsEnabled = false;
                    aiChannelList.IsEnabled = false;
                    TerminalConfigBox.IsEnabled = false;
                    VoltageRangeBox.IsEnabled = false;
                }

                // if there is a nidaq and settings look good, let the user record nidaq data

                if (IsNidaqDevice && aiChannelList.SelectedItems.Count > 0 && DevBox.Items.Count > 0
                    && (SamplingRate > 0 && SamplingRate < MaxRate))
                {
                    CheckNidaqStream.IsEnabled = true;
                }
                else
                {
                    CheckNidaqStream.IsEnabled = false;
                    CheckNidaqStream.IsChecked = false;
                }
                
                if (PreviewMode.IsChecked == true)
                {
                    // if preview is checked, enabled the camera streams and disable nidaq
                    StartButton.IsEnabled = true;
                    CheckDepthStream.IsChecked = true;
                    CheckColorStream.IsChecked = true;
                    CheckNidaqStream.IsChecked = false;
                    CheckNidaqStream.IsEnabled = false;
                    CheckNoTimer.IsChecked = false;
                    CheckNoTimer.IsEnabled = false;
                    RecordingTimeBox.IsEnabled = false;
                }
                else if (CheckNoTimer.IsChecked == true)
                {
                    // we're in continuous mode in this case
                    ContinuousMode = true;
                    RecordingTimeText.IsEnabled = false;
                    RecordingTimeBox.IsEnabled = false;
                    //StatusBarSessionProgress.IsEnabled = false;
                }
                else if (RecordingTimeBox.Text.Length > 0 & double.TryParse(RecordingTimeBox.Text, out tmp) == true)
                {
                    
                    // if we have anything in the time box, then disable the continuous button
                    CheckNoTimer.IsEnabled = false;
                    ContinuousMode = false;
                    RecordingTimeText.IsEnabled = true;
                    RecordingTimeBox.IsEnabled = true;
                    RecordingTime = Convert.ToDouble(RecordingTimeBox.Text) * 60e3;
                    //StatusBarSessionProgress.IsEnabled = true;
                }
                else
                {
                    // otherwise, leave things enabled
                    TimeFlag=false;
                    //StatusBarSessionProgress.IsEnabled = false;
                    ContinuousMode = true;
                    CheckNoTimer.IsEnabled = true;
                    RecordingTimeText.IsEnabled = true;
                    RecordingTimeBox.IsEnabled = true;
                }

                // if the filepath checks out, then establish our filenames

                if (SessionName.Text.Length > 0 && SubjectName.Text.Length > 0 && SaveFolder != null && TimeFlag==true
                    && PreviewMode.IsChecked == false)
                {

                    // Temporary directory is defined here

                    //string BasePath = Path.GetTempPath();
                    string BasePath = SaveFolder;
                    string now = DateTime.Now.ToString("yyyyMMddHHmmss");

                    FilePath_ColorTs = Path.Combine(BasePath, String.Format("rgb_ts_{0}.txt", now));
                    FilePath_ColorVid = Path.Combine(BasePath, String.Format("rgb_{0}.mp4", now));
                    FilePath_DepthTs = Path.Combine(BasePath, String.Format("depth_ts_{0}.txt", now));
                    FilePath_DepthVid = Path.Combine(BasePath, String.Format("depth_{0}.dat", now));
                    FilePath_Nidaq = Path.Combine(BasePath, String.Format("nidaq_{0}.dat", now));
                    FilePath_Metadata = Path.Combine(BasePath, String.Format("metadata_{0}.json", now));
                    
                    // Paths for the tarball and metadata

                    FilePath_Tar = Path.Combine(SaveFolder, String.Format("session_{0}.tar.gz", now));
                    FilePath_TarMetadata = Path.Combine(SaveFolder, String.Format("session_{0}.json", now));
 
                    FilePaths =  new List<string[]>{
                        new string[] { FilePath_ColorTs, "rgb_ts.txt" },
                        new string[] { FilePath_ColorVid, "rgb.mp4" },
                        new string[] { FilePath_DepthTs, "depth_ts.txt" },
                        new string[] { FilePath_DepthVid, "depth.dat" },
                        new string[] { FilePath_Metadata, "metadata.json" },
                        new string[] { FilePath_Nidaq, "nidaq.dat" }
                      };

                    // we've gotten this far, meaning the preview button was not selected, and we want to record
                    // if any of the files overwrite old data or cannot be created, NO GO 

                    if (FilePaths.All(p => !File.Exists(p[0])) && !File.Exists(FilePath_Tar) &&  Directory.Exists(SaveFolder)
                        && (CheckColorStream.IsChecked==true || CheckDepthStream.IsChecked==true || CheckNidaqStream.IsChecked==true))
                    {
                        //NidaqPrepare.IsEnabled = true;
                        StartButton.IsEnabled = true;
                    }
                    else 
                    {
                        //NidaqPrepare.IsEnabled = false;
                        StartButton.IsEnabled = false;
                    }
                    

                }
                else if (PreviewMode.IsChecked == false)
                {
                    // this means the preview button wasn't checked but one of the other conditions failed
                    StartButton.IsEnabled = false;
                }
                
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private void ChannelSelection_Changed(object sender, SelectionChangedEventArgs e)
        {
            SettingsChanged();
        }

        private void CheckNoTimer_Checked(object sender, RoutedEventArgs e)
        {
            SettingsChanged();
        }


        /// <summary>
        /// Updates status bars
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CheckTimer_Tick(object sender, EventArgs e)
        {
            // FreeMem in GB
            try
            {
                if (Directory.Exists(SaveFolder))
                {
                    FileInfo PathInfo = new FileInfo(SaveFolder);
                    DriveInfo SaveDrive = new DriveInfo(PathInfo.Directory.Root.FullName);
                    Double FreeMem = SaveDrive.AvailableFreeSpace / 1e9;
                    Double AllMem = SaveDrive.TotalSize / 1e9;
                    StatusBarFreeSpace.Text = String.Format("{0} {1:0.##} / {2:0.##} GB Free", SaveDrive.RootDirectory, FreeMem, AllMem);
                }
            }
            catch
            {
                StatusBarFreeSpace.Text = "N/A";
            }
            
            
            // Check Buffers?

            string CPUPercent= (100-CPUPerformance.NextValue()).ToString("F1")+"% Free";
            string RAMUsage = RAMPerformance.NextValue().ToString("F1")+"MB Free";
            double MemUsed = GC.GetTotalMemory(true) / 1e6;

            StatusBarCPU.Text = CPUPercent;
            StatusBarRAM.Text = RAMUsage;

            StatusBarFramesDropped.Text = String.Format("Color {0} Depth {1} ",
                ColorFramesDropped,
                DepthFramesDropped);

            if (IsColorStreamEnabled == true)
            {
                StatusBarColor.Value = ((double)ColorFrameCollection.Count / (double)Constants.kMaxFrames )*100;
                ColorLight.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }

            if (IsDepthStreamEnabled == true) 
            {
                StatusBarDepth.Value = ((double)DepthFrameCollection.Count / (double)Constants.kMaxFrames)*100;
                DepthLight.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }

            if (IsNidaqEnabled == true & IsRecordingEnabled == true)
            {
                StatusBarNidaq.Value = ((double)NidaqQueue.Count / (double)Constants.nMaxBuffer) * 100;
                NidaqLight.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }

            if (!ContinuousMode & RecTimer != null & IsRecordingEnabled)
            {
                // running in timed mode, get the time left

                // total milliseconds 

                if (RecTimer.IsEnabled == true)
                {
                    TimeSpan SessionTSpan = RecEndTime - RecStartTime;
                    TimeSpan LeftTSpan = RecEndTime - DateTime.Now;

                    StatusBarProgress.Value = 100.0 - (LeftTSpan.TotalMilliseconds * 100.0 / SessionTSpan.TotalMilliseconds);
                    StatusBarProgressETA.Text = String.Format("ETA: ({0} mins, {1:F2} secs)", Math.Floor(LeftTSpan.TotalMinutes), LeftTSpan.TotalSeconds % 60);
                }
                else if (CompressTask != null)
                {
                    if (CompressTask.IsCompleted)
                    {
                        StatusBarProgress.Value = 0;
                    }
                    
                }
                
            }
            else if ((ContinuousMode & IsRecordingEnabled) | IsPreviewEnabled)
            {
                StatusBarProgressETA.Text = "ETA: Continuous";   
            }
            
            if (IsRecordingEnabled == true)
            {
                StatusBarSessionText.Text = "Recording";
                //InactivateSettings();
            }
            else if (IsPreviewEnabled)
            {
                StatusBarSessionText.Text = "Preview";
            }
            else if (CompressTask!=null)
            {
                if (!CompressTask.IsCompleted)
                {
                    StatusBarProgress.Value = tarballProgress;

                    if (ETAAve >= 0)
                    {
                        StatusBarProgressETA.Text = String.Format("ETA: ({0} mins, {1:F2} secs)", Math.Floor(ETAAve / 60), ETAAve % 60);
                        StatusBarSessionText.Text = "Compressing";
                    }
                    
                }
                else
                {
                    //ActivateSettings();
                    if (!SubjectName.IsEnabled)
                    {
                        ActivateSettings();
                    }
                    StatusBarProgress.Value = 100;
                    StatusBarProgressETA.Text = "ETA: (0 mins, 0 secs)";
                    StatusBarSessionText.Text = "Done";
                }
            }
            else if (IsSessionClean)
            {
                //ActivateSettings();
                if (!SubjectName.IsEnabled)
                {
                    ActivateSettings();
                }

                StatusBarProgress.Value = 100;
                StatusBarProgressETA.Text = "ETA: (0 mins, 0 secs)";
                StatusBarSessionText.Text = "Done";
            }
            else
            {
                StatusBarSessionText.Text = "";
            }
       
        }

        /// <summary>
        /// Write out the relevant Metadata
        /// </summary>
        private void WriteMetadata()
        {

            fMetadata.ColorResolution = new int[2] { Constants.kDefaultFrameWidth, Constants.kDefaultFrameHeight };
            fMetadata.DepthResolution = fMetadata.ColorResolution;
            
            
            
            fMetadata.SubjectName = SubjectName.Text;
            fMetadata.SessionName = SessionName.Text;
            fMetadata.IsLittleEndian = BitConverter.IsLittleEndian;
            fMetadata.DepthDataType = depthData.GetType().Name;
            fMetadata.ColorDataType = colorData.GetType().Name;
            
            if (IsNidaqEnabled)
            {
                fMetadata.NidaqChannels = aiChannelList.SelectedItems.Count;
                fMetadata.NidaqTerminalConfiguration = TerminalConfigBox.SelectedItem.ToString();
                fMetadata.NidaqChannelNames = new string[aiChannelList.SelectedItems.Count];
                fMetadata.NidaqSamplingRate = SamplingRate;
                fMetadata.NidaqVoltageRange = VoltageRangeBox.SelectedItem.ToString();

                Type t = typeof(NidaqData);
                System.Reflection.PropertyInfo t2 = t.GetProperty("Data");
                fMetadata.NidaqDataType = t2.PropertyType.Name;

                for (int i = 0; i < aiChannelList.SelectedItems.Count; i++)
                {
                    fMetadata.NidaqChannelNames[i] = aiChannelList.SelectedItems[i].ToString();
                }
            }
            
            fMetadata.StartTime = DateTime.Now;

            JsonSerializer serializer = new JsonSerializer();
            serializer.NullValueHandling = NullValueHandling.Ignore;

            using (StreamWriter sw = new StreamWriter(FilePath_Metadata))
            using (JsonWriter writer = new JsonTextWriter(sw))
            {
                serializer.Serialize(writer, fMetadata);
            }
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.MainWindow.Close();
        }

        // Solution from http://www.codeproject.com/Questions/284995/DragMove-problem-help-pls

        private bool inDrag = false;
        private Point anchorPoint;
        private bool iscaptured = false;

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            anchorPoint = PointToScreen(e.GetPosition(this));
            inDrag = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (inDrag)
            {
                if (!iscaptured)
                {
                    CaptureMouse();
                    iscaptured = true;
                }
                Point currentPoint = PointToScreen(e.GetPosition(this));
                this.Left = this.Left + currentPoint.X - anchorPoint.X;
                this.Top = this.Top + currentPoint.Y - anchorPoint.Y;
                anchorPoint = currentPoint;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (inDrag)
            {
                inDrag = false;
                iscaptured = false;
                ReleaseMouseCapture();
            }
        }

        private void RecordingTime_TextChanged(object sender, TextChangedEventArgs e)
        {
            SettingsChanged();
        }

        private void InactivateSettings()
        {
            SubjectName.IsEnabled = false;
            SessionName.IsEnabled = false;
            FolderName.IsEnabled = false;
            SamplingRateBox.IsEnabled = false;
            DevBox.IsEnabled = false;
            TerminalConfigBox.IsEnabled = false;
            aiChannelList.IsEnabled = false;
            VoltageRangeBox.IsEnabled = false;
            SelectDirectory.IsEnabled = false;
            CheckColorStream.IsEnabled = false;
            CheckDepthStream.IsEnabled = false;
            CheckNidaqStream.IsEnabled = false;
            CheckNoTimer.IsEnabled = false;
            RecordingTimeBox.IsEnabled = false;
            RecordingTimeText.IsEnabled = false;
            PreviewMode.IsEnabled = false;
        }

        private void ActivateSettings()
        {
            DevBox.IsEnabled = true;
            aiChannelList.IsEnabled = true;
            SamplingRateBox.IsEnabled = true;
            TerminalConfigBox.IsEnabled = true;
            VoltageRangeBox.IsEnabled = true;
            SubjectName.IsEnabled = true;
            SessionName.IsEnabled = true;
            FolderName.IsEnabled = true;
            SelectDirectory.IsEnabled = true;
            CheckColorStream.IsEnabled = true;
            CheckDepthStream.IsEnabled = true;
            CheckNidaqStream.IsEnabled = true;
            CheckNoTimer.IsEnabled = true;
            RecordingTimeBox.IsEnabled = true;
            RecordingTimeText.IsEnabled = true;
            PreviewMode.IsEnabled = true;
        }

        private void PreviewMode_Checked(object sender, RoutedEventArgs e)
        {
            //StartButton.IsEnabled = true;
            //InactivateSettings();
            SettingsChanged();
        }

        private void PreviewMode_Unchecked(object sender, RoutedEventArgs e)
        {
            SettingsChanged();
            //ActivateSettings();
        }

        private void Stream_Checked(object sender, RoutedEventArgs e)
        {
            SettingsChanged();
        }

        private void DepthBox_TextChanged(object sender, RoutedEventArgs e)
        {
            var textbox = sender as TextBox;
            int value;
            if (int.TryParse(textbox.Text, out value))
            {
                if (value > ushort.MaxValue)
                    textbox.Text = ushort.MaxValue.ToString();
                else if (value < 0)
                    textbox.Text = "0";
            }
        }

        private void FlipDepth_Checked(object sender, RoutedEventArgs e)
        {
            System.Windows.Media.RotateTransform rotateTransform = new System.Windows.Media.RotateTransform(180);
            DepthDisplay.RenderTransform = rotateTransform;
            ColorDisplay.RenderTransform = rotateTransform;
        }

        private void FlipDepth_Unchecked(object sender, RoutedEventArgs e)
        {
            System.Windows.Media.RotateTransform rotateTransform = new System.Windows.Media.RotateTransform(0);
            DepthDisplay.RenderTransform = rotateTransform;
            ColorDisplay.RenderTransform = rotateTransform;
        }
 
    }

}