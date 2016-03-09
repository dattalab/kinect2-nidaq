using System;
using System.Windows;
using System.IO;
using System.Windows.Threading;
using System.Windows.Controls;
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
using Microsoft.WindowsAPICodePack.Dialogs;

// TODO 1: Add settings for session name/animal name/notes field
// TODO 2: Save UI settings (custom settings?)
// TODO 3: Make sure we exit gracefully if start button has not been clicked (without the use of try/catch)

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

        /// <summary>
        /// Queue for color frames
        /// </summary>
        BlockingCollection<ColorFrameEventArgs> ColorFrameQueue;

        /// <summary>
        /// Queue for depth frames
        /// </summary>
        BlockingCollection<DepthFrameEventArgs> DepthFrameQueue;

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
        /// National Instruments queue dump
        /// </summary>
        System.Threading.Tasks.Task NidaqDumpTask;

        /// <summary>
        /// How many color frames dropped?
        /// </summary>
        private int ColorFramesDropped;
        
        /// <summary>
        /// How many depth frames dropped?
        /// </summary>
        private int DepthFramesDropped;


        /// <summary>
        /// Directory initialization
        /// </summary>
        private string TestPath_ColorTs;
        private string TestPath_ColorVid;
        private string TestPath_DepthTs;
        private string TestPath_Nidaq;

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
        private StreamWriter NidaqStream;

        private FileStream ColorTSFile;
        private FileStream DepthTSFile;

        private StreamWriter ColorTSStream;
        private StreamWriter DepthTSStream;

        private ColorFrameEventArgs colorEventArgs;
        private DepthFrameEventArgs depthEventArgs;

        NationalInstruments.PrecisionDateTime[] CurrentNITimeStamp;
        CancellationTokenSource fCancellationTokenSource = new CancellationTokenSource();

        /// <summary>
        /// Buffer timeout
        /// </summary>
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
               
               
                // get all channels...

                string[] deviceList = DaqSystem.Local.Devices;
                foreach (string currentDevice in deviceList)
                {
                    DevBox.Items.Add(currentDevice.ToString());
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
                ;
            }  

        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            fColorSpacepoints = null;

            ColorFramesDropped = 0;
            DepthFramesDropped = 0;

            ColorFrameQueue = new BlockingCollection<ColorFrameEventArgs>(Constants.kMaxFrames);
            DepthFrameQueue = new BlockingCollection<DepthFrameEventArgs>(Constants.kMaxFrames);
           
            ColorTSFile = new FileStream(TestPath_ColorTs, FileMode.Append);
            ColorTSStream = new StreamWriter(ColorTSFile);

            DepthTSFile = new FileStream(TestPath_DepthTs, FileMode.Append);
            DepthTSStream = new StreamWriter(DepthTSFile);

            // Open the Kinect

            ColorReader = sensor.ColorFrameSource.OpenReader();
            DepthReader = sensor.DepthFrameSource.OpenReader();

            ColorReader.FrameArrived += ColorReader_FrameArrived;
            DepthReader.FrameArrived += DepthReader_FrameArrived;

            // Display code

            
            ImageTimer.Start();

            // HD check

            CheckTimer.Start();

            // Start the tasks that empty each queue

            ColorDumpTask = System.Threading.Tasks.Task.Factory.StartNew(ColorRunner, fCancellationTokenSource.Token);
            DepthDumpTask = System.Threading.Tasks.Task.Factory.StartNew(DepthRunner, fCancellationTokenSource.Token);
            NidaqDumpTask = System.Threading.Tasks.Task.Factory.StartNew(NidaqRunner, fCancellationTokenSource.Token);

            StartButton.IsEnabled = false;           
            sensor.Open();
            StopButton.IsEnabled = true;

        }

        /// <summary>
        /// Clean up when the window closes
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Closed(object sender, EventArgs e)
        {
            // Dispose of the Kinect and the readers

            sensor.Close();
            ImageTimer.Stop();
            CheckTimer.Stop();

            if (runningTask != null)
            {
                runningTask = null;
                AnalogInTask.Stop();
                AnalogInTask.Dispose();
            }

            try
            {

                ColorFrameQueue.CompleteAdding();
                DepthFrameQueue.CompleteAdding();
                NidaqQueue.CompleteAdding();
            }
            catch
            {
                ;
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

            try
            {
                ColorReader.Dispose();
                DepthReader.Dispose();
                NidaqStream.Close();
                NidaqFile.Close();
                ColorTSStream.Close();
                DepthTSStream.Close();
                DepthDumpTask.Dispose();
                NidaqDumpTask.Dispose();
                ColorDumpTask.Dispose();
            }
            catch
            {
                ;
            }
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
                colorEventArgs.TimeStamp = CurrentNITimeStamp[0].WholeSeconds+CurrentNITimeStamp[0].FractionalSeconds;            

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

        /// <summary>
        /// Pass the color frame data
        /// </summary>
        /// <param name="e"></param>
        private void ColorFrameArrived(ColorFrameEventArgs e)
        {
            LastColorFrame=e;
            ColorFrameQueue.Add(e);
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

                    fColorSpacepoints = new ColorSpacePoint[depthData.Length];
                    sensor.CoordinateMapper.MapDepthFrameToColorSpace(depthData, fColorSpacepoints);

                    depthEventArgs.TimeStamp = CurrentNITimeStamp[0].WholeSeconds+CurrentNITimeStamp[0].FractionalSeconds;

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

        /// <summary>
        /// Pass the depth data
        /// </summary>
        /// <param name="e"></param>
        private void DepthFrameArrived(DepthFrameEventArgs e)
        {
            LastDepthFrame=e;
            DepthFrameQueue.Add(e);
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

                AnalogWaveform<double>[] Waveforms = NidaqReader.EndReadWaveform(ar);

                // Current NIDAQ timestamp

                CurrentNITimeStamp = Waveforms[0].GetPrecisionTimeStamps();
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

            TimeSpan timeoutvid = TimeSpan.FromMilliseconds(1000);

            while (!ColorFrameQueue.IsCompleted)
            {
                ColorFrameEventArgs colorData = null;
                while (ColorFrameQueue.TryTake(out colorData, timeoutvid))
                {
                    if (!VideoWriter.IsOpen)
                    {
                        VideoWriter.Open(TestPath_ColorVid, Constants.kDefaultFrameWidth, Constants.kDefaultFrameHeight);
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
            while (!DepthFrameQueue.IsCompleted)
            {
                DepthFrameEventArgs depthData = null;
                while (DepthFrameQueue.TryTake(out depthData, timeout))
                {
                    DepthTSStream.WriteLine(String.Format("{0} {1}", depthData.RelativeTime.TotalMilliseconds, depthData.TimeStamp));
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

                    for (int i = 0; i < nsamples; i++)
                    {
                        string writestring = "";
                        for (int ii = 0; ii < nchannels; ii++)
                        {
                            writestring = String.Format("{0} {1}", writestring, data[ii][i]);
                        }
                        NidaqStream.WriteLine(String.Format("{0} {1}",
                            writestring,
                            (double)timestamps[i].WholeSeconds + timestamps[i].FractionalSeconds));
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

                AnalogInTask = new NationalInstruments.DAQmx.Task();
                NidaqDump = new NidaqData();
                string ChannelString = "";
                NidaqQueue = new BlockingCollection<AnalogWaveform<double>[]>(Constants.nMaxBuffer);
                NidaqFile = new FileStream(TestPath_Nidaq, FileMode.Append);
                NidaqStream = new StreamWriter(NidaqFile);

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
        void SamplingRateBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                SamplingRate = Convert.ToDouble(SamplingRateBox.Text.ToString());
            }
            catch
            {
                ;
            }

            SettingsChanged();
        }

        /// <summary>
        /// Terminal configuration box callback
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void TerminalConfigBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                MyTerminalConfig = (AITerminalConfiguration)Enum.Parse(typeof(AITerminalConfiguration), TerminalConfigBox.SelectedItem.ToString(), true);
            }
            catch
            {
                ;
            }

            // check to see if we've selected dev and channels
        }

        /// <summary>
        /// Directory selection callback
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void SelectDirectory_Click(object sender, RoutedEventArgs e)
        {
            CommonOpenFileDialog dialog = new CommonOpenFileDialog();
            dialog.IsFolderPicker = true;
            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                SaveFolder = dialog.FileName;
                FolderName.Text = SaveFolder;

                TestPath_ColorTs = Path.Combine(SaveFolder,"testing_color_ts.txt");
                TestPath_ColorVid = Path.Combine(SaveFolder, "testing_color_vid.mp4");
                TestPath_DepthTs = Path.Combine(SaveFolder, "testing_depth_ts.txt");
                TestPath_Nidaq = Path.Combine(SaveFolder,"testing_nidaq.txt");

                SettingsChanged();
            }
        }

        /// <summary>
        /// Check if settings are valid
        /// </summary>
        private void SettingsChanged()
        {
            // check sampling rate and recording configuration in general
            Boolean FolderFlag = Directory.Exists(SaveFolder);
            Boolean SamplingFlag = (SamplingRate > 0 && SamplingRate < MaxRate);

            if (FolderFlag && SamplingFlag )
            {
                NidaqPrepare.IsEnabled = true;
                StartButton.IsEnabled = false;
                
            }
            else
            {
                NidaqPrepare.IsEnabled = false;
                StartButton.IsEnabled = false;
            }

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
                StatusBarFreeSpace.Text = String.Format("{0} {1:0.##} / {2:0.##} GB Free", SaveFolder, FreeMem, AllMem);
            }
            
            // Check Buffers?

            string CPUPercent= "CPU "+CPUPerformance.NextValue()+"%";
            string RAMUsage = "RAM "+RAMPerformance.NextValue().ToString()+"MB";
            double MemUsed = GC.GetTotalMemory(true) / 1e9;

            StatusBarCPU.Text = CPUPercent;
            StatusBarRAM.Text = "RAM Usage "+MemUsed.ToString()+"GB";


        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            Window_Closed(sender,e);
            StopButton.IsEnabled = false;
        }
    }

}