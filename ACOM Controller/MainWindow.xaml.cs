using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.IO.Ports;
using ACOM_Controller.Properties;

namespace ACOM_Controller
{
    public partial class MainWindow : Window
    {
        string ComPort; 

        const byte msglen = 72;
        byte[] msg = new byte[msglen];
        byte msgpos = 0;
        bool parsing = false; // true while receiving a message

        readonly byte[] CommandEnableTelemetry = new byte[] { 0x55, 0x92, 0x04, 0x15 };
        readonly byte[] CommandDisableTelemetry = new byte[] { 0x55, 0x91, 0x04, 0x16 };

        readonly byte[] CommandOperate = new byte[] { 0x55, 0x81, 0x08, 0x02, 0x00, 0x06, 0x00, 0x1A };
        readonly byte[] CommandStandby = new byte[] { 0x55, 0x81, 0x08, 0x02, 0x00, 0x05, 0x00, 0x1B };
        readonly byte[] CommandOff = new byte[] { 0x55, 0x81, 0x08, 0x02, 0x00, 0x0A, 0x00, 0x16 };

        readonly string[] BandName = new string[16] { "?m", "160m", "80m", "40/60m", "30m", "20m", "17m", "15m", "12m", "10m", "6m", "?m", "?m", "?m", "?m", "?m"};

        int PAstatus = 0;
        int PAtemp = 0; // PA temperature
        int PAfan = 0; // PAM fan state, 0 = off

        const int PApowerPeakMemory = 10;
        int PApowerPeakIndex = 0; 
        double[] PApower = new double[PApowerPeakMemory]; // Array for filtering PA power reports
        double PApowerCurrent; // Current PA power
        double PApowerDisplay = 0; // Filtered PA output power

        const int DrivePowerPeakMemory = 10;
        int DrivePowerPeakIndex = 0;
        double[] DrivePower = new double[DrivePowerPeakMemory]; // Array for filtering PA power reports
        double DrivePowerCurrent; // Current PA power
        double DrivePowerDisplay = 0; // Filtered PA output power

        const int ReflectedPowerPeakMemory = 10;
        int ReflectedPowerPeakIndex = 0;
        double[] ReflectedPower = new double[ReflectedPowerPeakMemory]; // Array for filtering PA power reports
        double ReflectedPowerCurrent; // Current PA power
        double ReflectedPowerDisplay = 0; // Filtered PA output power

        SerialPort port;
        
        public MainWindow()
        {
            String[] commandLineArguments = Environment.GetCommandLineArgs();

            InitializeComponent();

            // If there is a command line argument, take it as the COM port, else use same as last time
            if (commandLineArguments.Length > 1)
                ComPort = commandLineArguments[1].ToUpper();
            else
                ComPort = Settings.Default.ComPort;

            ProgramWindow.Title = "ACOM Controller (" + ComPort + ")";

            port = new SerialPort(ComPort, 9600, Parity.None, 8, StopBits.One);

            OpenSerial();
            // Send enable telemetry command to PA
            port.Write(CommandEnableTelemetry, 0, CommandEnableTelemetry.Length);

            // Fetch window location from saved settings
            this.Top = Settings.Default.Top;
            this.Left = Settings.Default.Left;

            // Clearing peak detection arrays, just to be safe
            Array.Clear(PApower, 0, PApower.Length);
            Array.Clear(DrivePower, 0, DrivePower.Length);

            // Enable event handler for serial data received
            port.DataReceived += Port_OnReceiveData; // DataReceived Event Handler

            // Since there is no way to know if telemetry is enabled on PA, 
            // use a periodic timer to constantly re-enable telemetry
            System.Windows.Threading.DispatcherTimer dispatcherTimer = new System.Windows.Threading.DispatcherTimer();
            dispatcherTimer.Tick += new EventHandler(OnTimer);
            dispatcherTimer.Interval = new TimeSpan(0, 0, 0, 0, 2000);
            dispatcherTimer.Start();
        }

        // Clean up and housekeeping at program shutdown
        void MainWindow_Closed(object sender, EventArgs e) 
        {
            // Remember window location 
            Settings.Default.Top = this.Top;
            Settings.Default.Left = this.Left;
            Settings.Default.Save();

            // Remember COM port used 
            Settings.Default.ComPort = ComPort;

            // Send disable telemetry command to PA
            port.Write(CommandDisableTelemetry, 0, CommandDisableTelemetry.Length);
        }

        // Safely open serial port and throw a popup if fails
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

        // Event handler for received data
        private void Port_OnReceiveData(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort _port = (SerialPort)sender;
            byte[] buf = new byte[_port.BytesToRead];
            _port.Read(buf, 0, buf.Length);

            foreach (Byte b in buf)
            {
                // Known weakness of solution below:
                // If there are other, non-telemetry, datagrams present in the data stream 
                // there could be false triggers if they contain the sequence 0x55 0x2f
                // Complete safety is only achieved by making parser aware of all 
                // possible datagrams. However, since application does not request 
                // other datagrams from PA, the current implementation should be safe.

                // 0x55 is first byte in telemetry message
                if (!parsing & (b == 0x55)) 
                {
                    msgpos = 0;
                    parsing = true;
                    msg[msgpos++] = b;
                }
                else if (parsing)
                {
                    msg[msgpos++] = b;

                    // 0x2f is second byte in telemetry datagram. 
                    // If not a telemetry message, reset parser
                    if (msg[1] != 0x2f) 
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

                            // checksum zero => a real message - get parameters and update UI
                            if (checksum == 0) 
                            {
                                PAstatus = (msg[3] & 0xF0) >> 4; // extract data from message

                                // Updates to UI needs to be done in main thread
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

                                    if (PAstatus != 10) // PAstatus == 10 means in powering down mode
                                    {
                                        if (PAtemp >= 0 & PAtemp <= 100) // safety for corrupted reads
                                        {
                                            tempLabel.Content = PAtemp.ToString() + "C";
                                            tempBar.Value = PAtemp;
                                        }

                                        // Change color on temp bar on higher fan speeds
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
                                        DrivePowerCurrent = msg[20] + msg[21] * 256.0;
                                        DrivePower[DrivePowerPeakIndex++] = DrivePowerCurrent; // save current power in fifo
                                        DrivePowerDisplay = DrivePower.Max() / 10.0;
                                        if (DrivePowerPeakIndex >= DrivePowerPeakMemory) DrivePowerPeakIndex = 0;  // wrap around
                                        driveLabel.Content = DrivePowerDisplay.ToString("0") + "W";

                                        // Filter reflected power data 
                                        ReflectedPowerCurrent = msg[24] + msg[25] * 256.0;
                                        ReflectedPower[ReflectedPowerPeakIndex++] = ReflectedPowerCurrent; // save current power in fifo
                                        ReflectedPowerDisplay = ReflectedPower.Max();
                                        if (ReflectedPowerPeakIndex >= ReflectedPowerPeakMemory) ReflectedPowerPeakIndex = 0;  // wrap around
                                        reflLabel.Content = ReflectedPowerDisplay.ToString("0") + "R";
                                        
                                        // 0-114W part of the reflected bar in gray
                                        reflBar.Value = (ReflectedPowerDisplay > 122.0) ? 122 : (int)ReflectedPowerDisplay;
                                        reflBar.Foreground = Brushes.Gray;
                                        
                                        // 114-150 part of the reflected bar in red
                                        reflBar_Peak.Value = (ReflectedPowerDisplay > 122.0) ? (int)PApowerDisplay - 122 : 0;
                                        reflBar_Peak.Foreground = Brushes.Crimson;

                                        // Filter output power data 
                                        // Add 2% to align better with PA's own display, unclear why
                                        PApowerCurrent = 1.02f * (msg[22] + msg[23] * 256);
                                        PApower[PApowerPeakIndex++] = PApowerCurrent; // save current power in fifo
                                        PApowerDisplay = PApower.Max();
                                        if (PApowerPeakIndex >= PApowerPeakMemory) PApowerPeakIndex = 0;  // wrap around
                                        pwrLabel.Content = PApowerDisplay.ToString("0") + "W";

                                        // 0-600W part of the bar in blue
                                        pwrBar.Value = (PApowerDisplay > 600f) ? 600 : (int)PApowerDisplay;
                                        pwrBar.Foreground = Brushes.RoyalBlue;

                                        // 600-700W part of the bar in red
                                        pwrBar_Peak.Value = (PApowerDisplay > 600f) ? (int)PApowerDisplay - 600 : 0;
                                        pwrBar_Peak.Foreground = Brushes.Crimson;

                                        // Show active LPF as text
                                        bandLabel.Content = BandName[msg[69] & 0x0F];  
                                    }
                                    else // PA is powering down
                                    {
                                        bandLabel.Content = "--m";
                                        driveLabel.Content = "--W";

                                        reflLabel.Content = "--R";
                                        reflBar.Value = 0;
                                        reflBar_Peak.Value = 0;

                                        tempLabel.Content = "--C";
                                        tempBar.Value = 0;

                                        pwrLabel.Content = "---W";
                                        pwrBar.Value = 0;
                                        pwrBar_Peak.Value = 0;
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

        // At click on standby button
        void StandbyClick(object sender, RoutedEventArgs e)
        {
            // Send Standby command to PA
            port.Write(CommandStandby, 0, CommandStandby.Length); 
        }

        // At click on operate button
        void OperateClick(object sender, RoutedEventArgs e)
        {
            // Send Operate command to PA
            port.Write(CommandOperate, 0, CommandOperate.Length);  
        }

        // At click on off button
        void OffClick(object sender, RoutedEventArgs e)
        {
            // Send Off command to PA
            port.Write(CommandOff, 0, CommandOff.Length);  
        }

        // Executed repeatedly 
        void OnTimer(object sender, EventArgs e)
        {
            // Re-enable PA telemetry on every timer click to ensure status info after startup
            port.Write(CommandEnableTelemetry, 0, CommandEnableTelemetry.Length);
        }
    }
}

