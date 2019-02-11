using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.IO.Ports;
using ACOM_Controller.Properties;

namespace ACOM_Controller
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public static string ComPort; 

        public static byte msglen = 72;
        public static byte[] msg = new byte[msglen];
        public static byte msgpos = 0;
        public static bool parsing = false; // true while receiving a message

        public static byte[] CommandEnableTelemetry = new byte[] { 0x55, 0x92, 0x04, 0x15 };
        public static byte[] CommandDisableTelemetry = new byte[] { 0x55, 0x91, 0x04, 0x16 };

        public static byte[] CommandOperate = new byte[] { 0x55, 0x81, 0x08, 0x02, 0x00, 0x06, 0x00, 0x1A };
        public static byte[] CommandStandby = new byte[] { 0x55, 0x81, 0x08, 0x02, 0x00, 0x05, 0x00, 0x1B };
        public static byte[] CommandOff = new byte[] { 0x55, 0x81, 0x08, 0x02, 0x00, 0x0A, 0x00, 0x16 };

        public static string[] BandName = new string[16] { "?m", "160m", "80m", "40/60m", "30m", "20m", "17m", "15m", "12m", "10m", "6m", "?m", "?m", "?m", "?m", "?m"};

        public static int PAstatus = 0;
        public static int PAband = 0;
        public static int PALPF = 0;
        public static int PAtemp = 0; // PA temperature
        public static int PAfan = 0; // PAM fan state, 0 = off

        public static int PApowerPeakMemory = 10;
        public static int PApowerPeakIndex = 0; 
        public static float[] PApower = new float[PApowerPeakMemory]; // Array for filtering PA power reports
        public static float PApowerCurrent; // Current PA power
        public static float PApowerDisplay = 0; // Filtered PA output power

        public static int DrivePowerPeakMemory = 10;
        public static int DrivePowerPeakIndex = 0;
        public static float[] DrivePower = new float[DrivePowerPeakMemory]; // Array for filtering PA power reports
        public static float DrivePowerCurrent; // Current PA power
        public static float DrivePowerDisplay = 0; // Filtered PA output power

        SerialPort port;
        

        public MainWindow()
        {
            String[] commandLineArguments = Environment.GetCommandLineArgs();

            InitializeComponent();

            // If there is a command line argument, take it as the COM port 
            if (commandLineArguments.Length > 1)
                ComPort = commandLineArguments[1].ToUpper();
            else
                ComPort = Settings.Default.ComPort;

            ProgramWindow.Title = "ACOM Controller (" + ComPort + ")";

            port = new SerialPort(ComPort, 9600, Parity.None, 8, StopBits.One);

            OpenSerial();
            EnableTelemetry();

            // Fetch window location from saved settings
            this.Top = Settings.Default.Top;
            this.Left = Settings.Default.Left;

            // Clearing peak detection array, just to be on the safe side
            Array.Clear(PApower, 0, PApower.Length);

            // Enable event handler for data received
            port.DataReceived += Port_OnReceiveData; // DataReceived Event Handler

            // Set up timer with 1s period for brute-force re-enabling telemetry mode after power on
            System.Windows.Threading.DispatcherTimer dispatcherTimer = new System.Windows.Threading.DispatcherTimer();
            dispatcherTimer.Tick += new EventHandler(OnTimer);
            dispatcherTimer.Interval = new TimeSpan(0, 0, 0, 0, 1000);
            dispatcherTimer.Start();
        }

        void MainWindow_Closed(object sender, EventArgs e) // Executes at shut down
        {
            // Remember window location 
            Settings.Default.Top = this.Top;
            Settings.Default.Left = this.Left;
            Settings.Default.Save();

            // Remember COM port used 
            Settings.Default.ComPort = ComPort;

            DisableTelemetry();
        }

        void EnableTelemetry()
        {
            port.Write(CommandEnableTelemetry, 0, CommandEnableTelemetry.Length);
        }

        void DisableTelemetry()
        {
            port.Write(CommandDisableTelemetry, 0, CommandDisableTelemetry.Length);
        }

        void OpenSerial()
        {
            try
            {
                port.Open();
            }
            catch
            {
                MessageBoxResult result = MessageBox.Show("Could not open serial port " + ComPort, "ACOM Controller", MessageBoxButton.OK, MessageBoxImage.Question);
                if (result == MessageBoxResult.OK)
                {
                    Application.Current.Shutdown();
                }
            }
        }

        private void Port_OnReceiveData(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort spL = (SerialPort)sender;
            byte[] buf = new byte[spL.BytesToRead];
            spL.Read(buf, 0, buf.Length);
            foreach (Byte b in buf)
            {
                // parse 
                if (!parsing & (b == 0x55)) // 0x55 is start of telemetry message
                {
                    msgpos = 0;
                    parsing = true;
                    msg[msgpos++] = b;
                }
                else if (parsing)
                {
                    msg[msgpos++] = b;

                    if (msg[1] != 0x2f) // Not a telemetry message, reset parser
                    {
                        msgpos = 0;
                        parsing = false;

                    }
                    else // A telemetry message 
                    {
                        if (msgpos == msglen) // done, time to check check sum
                        {
                            // decode
                            byte checksum = 0;
                            foreach (Byte c in msg)
                                checksum += c;

                            if (checksum == 0) // checksum ok means a real message - get parameters and update UI
                            {
                                PAstatus = (msg[3] & 0xF0) >> 4; // extract data from message

                                // Do UI updates in main thread
                                Application.Current.Dispatcher.Invoke(new Action(() =>
                                {
                                    switch (PAstatus)
                                    {
                                        case 1:
                                            statusLabel.Foreground = Brushes.Black;
                                            statusLabel.Content = "RESET";
                                            break;
                                        case 2:
                                            statusLabel.Foreground = Brushes.Black;
                                            statusLabel.Content = "INIT";
                                            break;
                                        case 3:
                                            statusLabel.Foreground = Brushes.Black;
                                            statusLabel.Content = "DEBUG";
                                            break;
                                        case 4:
                                            statusLabel.Foreground = Brushes.Black;
                                            statusLabel.Content = "SERVICE";
                                            break;
                                        case 5:
                                            statusLabel.Foreground = Brushes.Blue;
                                            statusLabel.Content = "STANDBY";
                                            break;
                                        case 6:
                                            statusLabel.Foreground = Brushes.Green;
                                            statusLabel.Content = "RECEIVE";
                                            break;
                                        case 7:
                                            statusLabel.Foreground = Brushes.Red;
                                            statusLabel.Content = "TRANSMIT";
                                            break;
                                        case 9:
                                            statusLabel.Foreground = Brushes.Black;
                                            statusLabel.Content = "SYSTEM";
                                            break;
                                        case 10:
                                            statusLabel.Foreground = Brushes.Gray;
                                            statusLabel.Content = "OFF";
                                            break;
                                        default:
                                            statusLabel.Foreground = Brushes.Gray;
                                            statusLabel.Content = "UNKNOWN";
                                            break;
                                    }

                                    // Temperature bar with fan status 
                                    PAtemp = msg[16] + msg[17] * 256 - 273; // extract data from message
                                    PAfan = (msg[69] & 0xF0) >> 4;

                                    if (PAstatus != 10)
                                    {
                                        if (PAtemp >= 0 & PAtemp <= 100) // safety for corrupted reads
                                        {
                                            tempLabel.Content = PAtemp.ToString() + "C";
                                            tempBar.Value = PAtemp;
                                        }

                                        switch (PAfan)
                                        {
                                            case 1:
                                                tempBar.Foreground = Brushes.Green;
                                                fanLabel.Content = "Fan";
                                                fanLabel.Foreground = Brushes.Gray;
                                                break;
                                            case 2:
                                                tempBar.Foreground = Brushes.Green;
                                                fanLabel.Content = "Fan 2";
                                                fanLabel.Foreground = Brushes.Gray;
                                                break;
                                            case 3:
                                                tempBar.Foreground = Brushes.Yellow;
                                                fanLabel.Content = "Fan 3";
                                                fanLabel.Foreground = Brushes.DimGray;
                                                break;
                                            case 4:
                                                tempBar.Foreground = Brushes.Red;
                                                fanLabel.Content = "FAN 4";
                                                fanLabel.Foreground = Brushes.Black;
                                                break;
                                            default:
                                                tempBar.Foreground = Brushes.Green; // no Fan
                                                fanLabel.Content = "";
                                                break;
                                        }

                                        // Filter and display drive power data 
                                        DrivePowerCurrent = msg[20] + msg[21] * 256;
                                        DrivePower[DrivePowerPeakIndex++] = DrivePowerCurrent; // save current power in fifo
                                        DrivePowerDisplay = DrivePower.Max()/10;
                                        if (DrivePowerPeakIndex >= DrivePowerPeakMemory) DrivePowerPeakIndex = 0;  // wrap around
                                        driveLabel.Content = DrivePowerDisplay.ToString("0") + "W";

                                        // Filter output power data 
                                        PApowerCurrent = 1.02f * (msg[22] + msg[23] * 256);
                                        PApower[PApowerPeakIndex++] = PApowerCurrent; // save current power in fifo
                                        PApowerDisplay = PApower.Max();
                                        if (PApowerPeakIndex >= PApowerPeakMemory) PApowerPeakIndex = 0;  // wrap around
                                        pwrLabel.Content = PApowerDisplay.ToString("0") + "W";
                                        pwrBar.Value = (int)PApowerDisplay;
                                        pwrBar.Foreground = Brushes.RoyalBlue;

                                        // Show active LPF as text
                                        bandLabel.Content = BandName[msg[69] & 0x0F];  
                                    }
                                    else // PA is off
                                    {
                                        bandLabel.Content = "--m";
                                        driveLabel.Content = "--W";
                                        tempLabel.Content = "--C";
                                        tempBar.Value = 0;
                                        pwrLabel.Content = "---W";
                                        pwrBar.Value = 0;
                                    }
                                }));
                            }
                            msgpos = 0; // reset pointer to start of message buffer
                            parsing = false; // Parsing of message body completed, do this last
                        }
                    }
                }
            }
        }

        void StandbyClick(object sender, RoutedEventArgs e)
        {
            port.Write(CommandStandby, 0, CommandStandby.Length); 
        }

        void OperateClick(object sender, RoutedEventArgs e)
        {
            port.Write(CommandOperate, 0, CommandOperate.Length);  
        }

        void OffClick(object sender, RoutedEventArgs e)
        {
            port.Write(CommandOff, 0, CommandOff.Length);  
        }

        void OnTimer(object sender, EventArgs e)
        {
            // Re-enable telemetry every timer click to ensure status info after startup
            EnableTelemetry();
        }
     }
}

