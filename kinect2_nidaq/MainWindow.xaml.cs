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
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.GZip;

// TODO 1: Make sure we exit gracefully if start button has not been clicked (without the use of try/catch)
// TODO 2: Control sync signal in software?
// TODO 3: Add option to create a tarball when the user clicks stop or closes window
// TODO 4: Make sure we're not being too clever with pre-allocation...

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

        /// <summary>
        /// Session flags
        /// </summary>
        private bool IsRecordingEnabled = false;
        private bool IsColorStreamEnabled = false;
        private bool IsDepthStreamEnabled = false;
        private bool IsDataCompressed = false;
        private bool IsSessionClean = false;
        private bool IsNidaqEnabled = false;

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
        private BackgroundWorker bgTarball;
        private AutoResetEvent resetEvent;
        private List<string> FilePaths;
        private Queue<double> ETAQueue;

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

                if (DevBox.Items.Count > 0)
                {
                    DevBox.SelectedIndex = 0;
                    TerminalConfigBox.SelectedIndex = 0;
                }

                CPUPerformance = new PerformanceCounter();
                RAMPerformance = new PerformanceCounter("Memory","Available MBytes");

                CPUPerformance.CategoryName = "Processor";
                CPUPerformance.CounterName = "% Processor Time";
                CPUPerformance.InstanceName = "_Total";

                ImageTimer = new DispatcherTimer();
                ImageTimer.Interval = TimeSpan.FromMilliseconds(50.0);
                ImageTimer.Tick += ImageTimer_Tick;

                CheckTimer = new DispatcherTimer();
                CheckTimer.Interval = TimeSpan.FromMilliseconds(1000);
                CheckTimer.Tick += CheckTimer_Tick;
            

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

            if (bgTarball != null)
            {
                if (bgTarball.IsBusy)
                {
                    MessageBox.Show("Cannot start another session until tarball complete");
                    return;

                }
            }

            StatusBarProgress.Value = 0;
            fColorSpacepoints = null;
            IsDataCompressed = false;
            IsSessionClean = false;
            ColorFramesDropped = 0;
            DepthFramesDropped = 0;

            if (CheckColorStream.IsChecked==true)
            {
                //ColorFrameQueue = new ConcurrentQueue<ColorFrameEventArgs>();
                ColorFrameCollection = new BlockingCollection<ColorFrameEventArgs>(Constants.kMaxFrames);
                ColorTSFile = new FileStream(FilePath_ColorTs, FileMode.Append);
                ColorTSStream = new StreamWriter(ColorTSFile);
                ColorReader = sensor.ColorFrameSource.OpenReader();
                ColorReader.FrameArrived += ColorReader_FrameArrived;
                ColorDumpTask = System.Threading.Tasks.Task.Factory.StartNew(ColorRunner, fCancellationTokenSource.Token);
                IsColorStreamEnabled = true;
            }

            if (CheckDepthStream.IsChecked == true)
            {
                //DepthFrameQueue = new ConcurrentQueue<DepthFrameEventArgs>();
                DepthFrameCollection = new BlockingCollection<DepthFrameEventArgs>(Constants.kMaxFrames);
                DepthTSFile = new FileStream(FilePath_DepthTs, FileMode.Append);
                DepthTSStream = new StreamWriter(DepthTSFile);
                DepthVidFile = new FileStream(FilePath_DepthVid, FileMode.Append);
                DepthVidStream = new BinaryWriter(DepthVidFile);
                DepthReader = sensor.DepthFrameSource.OpenReader();
                DepthReader.FrameArrived += DepthReader_FrameArrived;
                DepthDumpTask = System.Threading.Tasks.Task.Factory.StartNew(DepthRunner, fCancellationTokenSource.Token);
                IsDepthStreamEnabled = true;
            }

            
            ImageTimer.Start();

            // HD check

            CheckTimer.Start();

            // Start the tasks that empty each queue
        
            NidaqDumpTask = System.Threading.Tasks.Task.Factory.StartNew(NidaqRunner, fCancellationTokenSource.Token);
         
            sensor.Open();
            WriteMetadata();

            IsRecordingEnabled = true;
            StopButton.IsEnabled = true;
            StartButton.IsEnabled = false;

            if (!ContinuousMode)
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
            if (!IsSessionClean & IsRecordingEnabled == true)
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

            if (bgTarball != null)
            { 
                if (bgTarball.IsBusy)
                {
                    MessageBoxResult dr = MessageBox.Show("Tarball in progress, cancel and exit?", "Tarball exception", MessageBoxButton.OKCancel);

                    switch (dr)
                    {
                        case MessageBoxResult.OK:
                            break;
                        case MessageBoxResult.Cancel:
                            e.Cancel = true;
                            break;
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
            DevBox.IsEnabled = true;
            aiChannelList.IsEnabled = true;
            SamplingRateBox.IsEnabled = true;
            TerminalConfigBox.IsEnabled = true;      

            kinect2_nidaq.Properties.Settings.Default.Save();

            sensor.Close();
            ImageTimer.Stop();
            
            //CheckTimer.Stop();

            if (runningTask != null)
            {
                runningTask = null;
                AnalogInTask.Stop();
                AnalogInTask.Dispose();
            }

            if (IsRecordingEnabled==true)
            {
                if (IsColorStreamEnabled == true)
                {
                    ColorFrameCollection.CompleteAdding();
                }
                if (IsDepthStreamEnabled == true)
                {
                    DepthFrameCollection.CompleteAdding();
                }
                NidaqQueue.CompleteAdding();
            }

           

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

            if (VideoWriter.IsOpen)
            {
                VideoWriter.Close();
            }


            if (IsRecordingEnabled == true)
            {
                if (IsColorStreamEnabled == true)
                {
                    ColorReader.Dispose();
                    ColorTSStream.Close();
                    ColorDumpTask.Dispose();                  
                }
               
                if (IsDepthStreamEnabled == true)
                {
                    DepthReader.Dispose();
                    DepthTSStream.Close();
                    DepthVidStream.Close();
                    DepthDumpTask.Dispose();                
                }

                NidaqStream.Close();
                NidaqFile.Close();
                NidaqDumpTask.Dispose();

            }

            IsRecordingEnabled = false;
            IsNidaqEnabled = false;
            IsDepthStreamEnabled = false;
            IsColorStreamEnabled = false;

            StatusBarProgress.IsEnabled = true;
            StatusBarProgressETA.IsEnabled = true;

            if (!IsDataCompressed)
            {
                bgTarball = new BackgroundWorker();
                bgTarball.ProgressChanged += bgTarball_ProgressChanged;
                bgTarball.DoWork += bgTarball_DoWork;
                bgTarball.WorkerReportsProgress = true;
                bgTarball.RunWorkerCompleted += bgTarball_RunWorkerCompleted;
               

                bgTarball.RunWorkerAsync();
                
          
            }
         
        
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
                    ColorFrameCollection.Add(colorEventArgs);

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
                    DepthFrameCollection.Add(depthEventArgs);

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
                    if (!VideoWriter.IsOpen)
                    {
                        VideoWriter.Open(FilePath_ColorVid, Constants.kDefaultFrameWidth, Constants.kDefaultFrameHeight);
                    }
                    ColorTSStream.WriteLine(String.Format("{0} {1}", colorData.RelativeTime.TotalMilliseconds, colorData.TimeStamp));
                    VideoWriter.WriteVideoFrame(colorData.ToBitmap().ToSystemBitmap());
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
                    
                    DepthTSStream.WriteLine(String.Format("{0} {1}", depthData.RelativeTime.TotalMilliseconds, depthData.TimeStamp));
                    foreach (ushort depthDatum in depthData.DepthData)
                    {
                        DepthVidStream.Write(depthDatum);
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
        /// Queue up the NiDaq board with selected properties
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void NidaqPrepare_Click(object sender, RoutedEventArgs e)
        {
            if (runningTask == null && aiChannelList.SelectedItems.Count>0)
            {
                
                MyTerminalConfig = (AITerminalConfiguration)Enum.Parse(typeof(AITerminalConfiguration), TerminalConfigBox.SelectedItem.ToString(), true);
                AnalogInTask = new NationalInstruments.DAQmx.Task();
                NidaqDump = new NidaqData();
                string ChannelString = "";
                NidaqQueue = new BlockingCollection<AnalogWaveform<double>[]>(Constants.nMaxBuffer);
                NidaqFile = new FileStream(FilePath_Nidaq, FileMode.Append);
                NidaqStream = new BinaryWriter(NidaqFile);
                
                foreach (var Channel in aiChannelList.SelectedItems)
                {
                    ChannelString = String.Format("{0} {1},", ChannelString, Channel.ToString());
                }

                ChannelString.Trim(new Char[] { ' ', ',' });
                
                AnalogInTask.AIChannels.CreateVoltageChannel(
                    ChannelString,
                    "",
                    MyTerminalConfig,
                    0,
                    5,
                    AIVoltageUnits.Volts);

                
                AnalogInTask.Timing.ConfigureSampleClock("", SamplingRate, SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, 1);
                AnalogInTask.Control(TaskAction.Verify);

                NidaqReader = new AnalogMultiChannelReader(AnalogInTask.Stream);
                NidaqReader.SynchronizeCallbacks = true;

                AnalogInCallback = new AsyncCallback(AnalogIn_Callback);
                NidaqReader.BeginReadWaveform(1, AnalogInCallback, AnalogInTask);

                runningTask = AnalogInTask;
                SettingsChanged();
                NidaqPrepare.IsEnabled = false;
                StartButton.IsEnabled = true;
                SamplingRateBox.IsEnabled = false;
                DevBox.IsEnabled = false;
                TerminalConfigBox.IsEnabled = false;
                aiChannelList.IsEnabled = false;

                IsNidaqEnabled = true;
            }
            else
            {
                MessageBox.Show("Need to select at least one NI channel");
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

            foreach (var Channel in ChannelList)
            {
                Console.WriteLine(Channel);
                aiChannelList.Items.Add(Channel);
            }

            foreach (var TerminalConfig in TerminalList)
            {
                TerminalConfigBox.Items.Add(TerminalConfig);
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

                if (CheckNoTimer.IsChecked == true)
                {
                    
                    ContinuousMode = true;
                    RecordingTimeText.IsEnabled = false;
                    RecordingTimeBox.IsEnabled = false;
                    //StatusBarSessionProgress.IsEnabled = false;
                }
                else if (RecordingTimeBox.Text.Length > 0 & double.TryParse(RecordingTimeBox.Text, out tmp) == true)
                {
                    
                    CheckNoTimer.IsEnabled = false;
                    ContinuousMode = false;
                    RecordingTime = Convert.ToDouble(RecordingTimeBox.Text) * 60e3;
                    //StatusBarSessionProgress.IsEnabled = true;
                }
                else
                {
                    TimeFlag=false;
                    //StatusBarSessionProgress.IsEnabled = false;
                    ContinuousMode = true;
                    CheckNoTimer.IsEnabled = true;
                    RecordingTimeText.IsEnabled = true;
                    RecordingTimeBox.IsEnabled = true;
                }

                if (SessionName.Text.Length > 0 && SubjectName.Text.Length > 0 && SaveFolder != null && TimeFlag==true)
                {

                    string prefix = String.Format("{0}_{1}", SessionName.Text, SubjectName.Text);
                    FilePath_ColorTs = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "color_ts.txt"));
                    FilePath_ColorVid = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "color_vid.mp4"));
                    FilePath_DepthTs = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "depth_ts.txt"));
                    FilePath_DepthVid = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "depth_vid.dat"));
                    FilePath_Nidaq = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "nidaq.dat"));
                    FilePath_Metadata = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "metadata.json"));
                    FilePath_Tar = Path.Combine(SaveFolder, String.Format("{0}.tar.gz", prefix));

                    FilePaths =  new List<string>{
                        FilePath_ColorTs,
                        FilePath_ColorVid,
                        FilePath_DepthTs,
                        FilePath_DepthVid,
                        FilePath_Metadata,
                        FilePath_Nidaq
                        };

                    if (FilePaths.All(p => !File.Exists(p)) && !File.Exists(FilePath_Tar) && (SamplingRate > 0 && SamplingRate < MaxRate)
                        && Directory.Exists(SaveFolder) && aiChannelList.SelectedItems.Count > 0 && !IsNidaqEnabled)
                    {
                        NidaqPrepare.IsEnabled = true;
                        StartButton.IsEnabled = false;
                    }
                    else if (!IsNidaqEnabled)
                    {
                        NidaqPrepare.IsEnabled = false;
                        StartButton.IsEnabled = false;
                    }

                }
                else
                {
                    NidaqPrepare.IsEnabled = false;
                    StartButton.IsEnabled = false;
                }
            }
            catch
            {
                ;
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
            if (Directory.Exists(SaveFolder))
            {
                FileInfo PathInfo = new FileInfo(SaveFolder);
                DriveInfo SaveDrive = new DriveInfo(PathInfo.Directory.Root.FullName);
                Double FreeMem = SaveDrive.AvailableFreeSpace / 1e9;
                Double AllMem = SaveDrive.TotalSize / 1e9;
                StatusBarFreeSpace.Text = String.Format("{0} {1:0.##} / {2:0.##} GB Free", SaveDrive.RootDirectory, FreeMem, AllMem);
            }
            
            // Check Buffers?

            string CPUPercent= "CPU "+CPUPerformance.NextValue().ToString("F1")+"%";
            string RAMUsage = "RAM "+RAMPerformance.NextValue().ToString("F1")+"MB";
            double MemUsed = GC.GetTotalMemory(true) / 1e6;

            StatusBarCPU.Text = CPUPercent;
            StatusBarRAM.Text = "RAM Usage "+MemUsed.ToString("F1")+"MB";

            StatusBarFramesDropped.Text = String.Format("Dropped C{0} D{1} ",
                ColorFramesDropped,
                DepthFramesDropped);

            if (IsColorStreamEnabled == true)
            {
                StatusBarColor.Value = ((double)ColorFrameCollection.Count / (double)Constants.kMaxFrames )*100; 
            }

            if (IsDepthStreamEnabled == true)
            {
                StatusBarColor.Value = ((double)DepthFrameCollection.Count / (double)Constants.kMaxFrames)*100;
            }

            StatusBarNidaq.Value = ((double)NidaqQueue.Count / (double)Constants.nMaxBuffer) * 100;

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
                else if (bgTarball == null)
                {
                    StatusBarProgress.Value = 0;
                }
                else if (bgTarball.IsBusy != true)
                {
                    StatusBarProgress.Value = 0;
                }
            }
            else if (ContinuousMode & IsRecordingEnabled)
            {
                if (bgTarball == null)
                {
                    StatusBarProgressETA.Text = "ETA: Continuous";
                }
                else if (bgTarball.IsBusy == false)
                {
                    StatusBarProgressETA.Text = "ETA: Continuous";
                }
                
            }
            

            if (IsRecordingEnabled == true)
            {
                StatusBarSessionText.Text = "Recording";
            }
            else if (bgTarball!=null)
            {
                if (bgTarball.IsBusy == true)
                {
                    StatusBarSessionText.Text = "Compressing";
                }
                else
                {
                    StatusBarSessionText.Text = "Done";
                }
            }
            else if (IsSessionClean)
            {
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
            fMetadata.NidaqChannels = aiChannelList.SelectedItems.Count;
            fMetadata.NidaqTerminalConfiguration = TerminalConfigBox.SelectedItem.ToString();
            fMetadata.NidaqChannelNames = new string[aiChannelList.SelectedItems.Count];
            fMetadata.SubjectName = SubjectName.Text;
            fMetadata.SessionName = SessionName.Text;
            fMetadata.IsLittleEndian = BitConverter.IsLittleEndian;
            fMetadata.DepthDataType = depthData.GetType().Name;
            fMetadata.ColorDataType = colorData.GetType().Name;

            Type t = typeof(NidaqData);
            System.Reflection.PropertyInfo t2 = t.GetProperty("Data");

            if (t2 != null)
            {
                fMetadata.NidaqDataType = t2.PropertyType.Name;
            }
            else
            {
                fMetadata.NidaqDataType = null;
            }


            for (int i = 0; i < aiChannelList.SelectedItems.Count; i++)
            {
                fMetadata.NidaqChannelNames[i] = aiChannelList.SelectedItems[i].ToString();
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

        /// <summary>
        /// Make the tarball
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void bgTarball_DoWork(object sender, DoWorkEventArgs e)
        {

            
            Stream outStream = File.Create(FilePath_Tar);
            GZipOutputStream gzoStream = new GZipOutputStream(outStream);
            gzoStream.SetLevel(3); // need for speed!

            TarOutputStream tarOutputStream = new TarOutputStream(gzoStream);
            double TotalBytes = 0;

            // gzip the tar file

            foreach (string filename in FilePaths)
            {

                // get the total number of bytes for progress
                if (File.Exists(filename) == true)
                {
                    using (Stream inputStream = File.OpenRead(filename))
                    {
                        TotalBytes += inputStream.Length;
                    }
                }
            }         

            ETAQueue = new Queue<double>(new double[Constants.etaMaxBuffer]);
            int counter = 0;
            int counter2 = 0;
            double ETA = 0;
            double TotalRead = 0;
            double Progress = 0;
            double NewProgress = 0;
            double TimeElapsed = 0;
            
            Stopwatch TarballETA = new Stopwatch();
            TarballETA.Start();

            foreach (string filename in FilePaths)
            {

                // get the total number of bytes for progress

                if (File.Exists(filename) == true)
                {
                    using (Stream inputStream = File.OpenRead(filename))
                    {

                        string tarName = filename.Substring(3);
                        long fileSize = inputStream.Length;

                        TarEntry entry = TarEntry.CreateTarEntry(Path.GetFileName(filename));
                        entry.Size = fileSize;
                        tarOutputStream.PutNextEntry(entry);

                        byte[] localBuffer = new byte[32 * 1024];

                        while (true)
                        {
                            int numRead = inputStream.Read(localBuffer, 0, localBuffer.Length);
                            if (numRead <= 0)
                            {
                                break;
                            }

                            tarOutputStream.Write(localBuffer, 0, numRead);
                            TotalRead += numRead;
                            counter++;

                            if (counter >= Constants.tUpdateCount)
                            {

                                counter = 0;
                                counter2++;

                                // Seconds per percent progress

                                TimeElapsed = TarballETA.Elapsed.TotalSeconds;
                                TarballETA.Restart();

                                NewProgress = TotalRead * 100.0 / TotalBytes;
                                ETA = TimeElapsed / (NewProgress - Progress);
                                
                                Progress = NewProgress;
                                
                                ETAQueue.Enqueue(ETA);

                                while (ETAQueue.Count > Constants.etaMaxBuffer)
                                {
                                    ETAQueue.Dequeue();
                                }

                                if (counter2 >= (double)Constants.etaMaxBuffer)
                                {
                                    counter2 = 0;
                                    ((BackgroundWorker)sender).ReportProgress((int)Math.Ceiling(Progress), ETAQueue);
                                }

                            }



                        }
                    }
                }

                tarOutputStream.CloseEntry();

            }

            tarOutputStream.Close();
            

            foreach (string filename in FilePaths)
            {
                File.Delete(filename);
            }

        }

        /// <summary>
        /// Background worker update progress bar
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void bgTarball_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            Queue<double> ReportQueue = (Queue<double>)e.UserState;
            double ETAAve = ReportQueue.Average() * (100 - (double)e.ProgressPercentage);
            int MinsRem = (int)Math.Floor(ETAAve / 60);
            double SecsRem = ETAAve % 60;
            StatusBarProgress.Value = e.ProgressPercentage;
            StatusBarProgressETA.Text = String.Format("ETA: ({0} mins, {1} secs)", MinsRem, SecsRem.ToString("F2"));
        }

        /// <summary>
        /// Tarball cleanup
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void bgTarball_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            IsDataCompressed = true;
            IsSessionClean = true;
            StatusBarProgressETA.Text = "ETA: Completed";
            StatusBarProgress.Value = 100;
            StatusBarProgress.IsEnabled = false;
            StatusBarProgressETA.IsEnabled = false;
            bgTarball.Dispose();
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

 
    }

}