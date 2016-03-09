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
using Newtonsoft.Json;
using Metadata;
// TODO 1: Add settings for session name/animal name/notes field
// TODO 2: Save UI settings (custom settings?)
// TODO 3: Make sure we exit gracefully if start button has not been clicked (without the use of try/catch)
// TODO 4: Control sync signal in software?

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

        private FileStream DepthVidFile;
        private BinaryWriter DepthVidStream;

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

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            fColorSpacepoints = null;

            ColorFramesDropped = 0;
            DepthFramesDropped = 0;

            ColorFrameQueue = new BlockingCollection<ColorFrameEventArgs>(Constants.kMaxFrames);
            DepthFrameQueue = new BlockingCollection<DepthFrameEventArgs>(Constants.kMaxFrames);
           
            ColorTSFile = new FileStream(FilePath_ColorTs, FileMode.Append);
            ColorTSStream = new StreamWriter(ColorTSFile);

            DepthTSFile = new FileStream(FilePath_DepthTs, FileMode.Append);
            DepthTSStream = new StreamWriter(DepthTSFile);

            DepthVidFile = new FileStream(FilePath_DepthVid, FileMode.Append);
            DepthVidStream = new BinaryWriter(DepthVidFile);

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

            WriteMetadata();

            
        }

        private void WriteMetadata()
        {
            fMetadata.ColorResolution = new int[2] { Constants.kDefaultFrameWidth, Constants.kDefaultFrameHeight };
            fMetadata.DepthResolution = fMetadata.ColorResolution;
            fMetadata.NidaqChannels = aiChannelList.SelectedItems.Count;
            fMetadata.NidaqTerminalConfiguration = TerminalConfigBox.SelectedItem.ToString();
            fMetadata.SubjectName = SubjectName.Text;
            fMetadata.SessionName = SessionName.Text;
            fMetadata.NidaqChannelNames = new string[aiChannelList.SelectedItems.Count];

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
                serializer.Serialize(writer,fMetadata);
            }
        }

        /// <summary>
        /// Clean up when the window closes
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Closed(object sender, EventArgs e)
        {
            // Dispose of the Kinect and the readers
            
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
            while (!DepthFrameQueue.IsCompleted)
            {
                DepthFrameEventArgs depthData = null;
                while (DepthFrameQueue.TryTake(out depthData, timeout))
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
                
                MyTerminalConfig = (AITerminalConfiguration)Enum.Parse(typeof(AITerminalConfiguration), TerminalConfigBox.SelectedItem.ToString(), true);
                AnalogInTask = new NationalInstruments.DAQmx.Task();
                NidaqDump = new NidaqData();
                string ChannelString = "";
                NidaqQueue = new BlockingCollection<AnalogWaveform<double>[]>(Constants.nMaxBuffer);
                NidaqFile = new FileStream(FilePath_Nidaq, FileMode.Append);
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

                if (SessionName.Text.Length > 0 && SubjectName.Text.Length > 0 && SaveFolder != null)
                {

                    string prefix = String.Format("{0}_{1}", SessionName.Text, SubjectName.Text);
                    FilePath_ColorTs = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "color_ts.txt"));
                    FilePath_ColorVid = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "color_vid.mp4"));
                    FilePath_DepthTs = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "depth_ts.txt"));
                    FilePath_DepthVid = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "depth_vid.dat"));
                    FilePath_Nidaq = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "nidaq.txt"));
                    FilePath_Metadata = Path.Combine(SaveFolder, String.Format("{0}_{1}", prefix, "metadata.json"));

                    if (!File.Exists(FilePath_ColorTs) && !File.Exists(FilePath_ColorVid) && !File.Exists(FilePath_DepthTs)
                        && !File.Exists(FilePath_DepthVid) && !File.Exists(FilePath_Nidaq) && (SamplingRate > 0 && SamplingRate < MaxRate)
                        && Directory.Exists(SaveFolder) && aiChannelList.SelectedItems.Count > 0)
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
            double MemUsed = GC.GetTotalMemory(true) / 1e6;

            StatusBarCPU.Text = CPUPercent;
            StatusBarRAM.Text = "RAM Usage "+MemUsed.ToString()+"MB";

        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            Window_Closed(sender,e);
            StopButton.IsEnabled = false;
        }
    }

}