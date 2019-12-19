using System;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.IO.Ports;
using System.Reflection;
using ACOM_Controller.Properties;

namespace ACOM_Controller
{
    public partial class MainWindow : Window
    {
        AssemblyName _assemblyName = Assembly.GetExecutingAssembly().GetName();
        string Release;

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
        double[] ReflectedPower = new double[ReflectedPowerPeakMemory]; // Array for filtering reflected power reports
        double ReflectedPowerCurrent; // Current reflected power
        double ReflectedPowerDisplay = 0; // Filtered reflected power

        const int swrPeakMemory = 8;
        int swrPeakIndex = 0;
        double[] swrValue = new double[swrPeakMemory]; // Array for filtering SWR reports
        double swrCurrent; // Current SWR
        double swrDisplay = 0; // Filtered SWR 

        double NominalForwardPower; // For scaling display
        double MaxForwardPower;
        double NominalReversePower;
        double MaxReversePower;

        int errorCode; // Code for error message shown on PA's display

        string programTitle;

        bool portIsOpen = false;
        bool linkIsAlive = false;

        SerialPort Port;
        
        public MainWindow()
        {
            InitializeComponent();

            Release = string.Format(" {0}.{1} ", _assemblyName.Version.Major, _assemblyName.Version.Minor);

            if (Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1)
            {
                MessageBox.Show("ACOM Controller is already running on this PC.", "ACOM Controller");
                Application.Current.Shutdown();
            }

            // Hide error pop up
            errorTextButton.Visibility = Visibility.Hidden;

            Configuration(Settings.Default.ComPort, Settings.Default.AmplifierModel, Settings.Default.AlwaysOnTop);

            // Fetch window location from saved settings
            Top = Settings.Default.Top;
            Left = Settings.Default.Left;

            // Clearing peak detection arrays, just to be safe
            Array.Clear(PApower, 0, PApower.Length);
            Array.Clear(DrivePower, 0, DrivePower.Length);

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
            Settings.Default.Top = Top;
            Settings.Default.Left = Left;
            Settings.Default.Save();

            // Send disable telemetry command to PA
            if (portIsOpen)
                Port.Write(CommandDisableTelemetry, 0, CommandDisableTelemetry.Length);
        }

        public void Configuration(string comPort, string ampModel, bool alwaysontop)
        {
            try
            {
                // Make sure port is released and event unsubscribed
                Port.Close();
                Port.DataReceived -= Port_OnReceiveData;
            }
            catch { }

            portIsOpen = false;

            // Default to COM4 if config file has been corrupted
            Port = new SerialPort(comPort == "" ? "COM4" : comPort, 9600, Parity.None, 8, StopBits.One);

            try
            {
                Port.Open();
                portIsOpen = true;
            }
            catch
            {
                portIsOpen = false;
            }

            switch (ampModel)
            {
                case "700S":
                    NominalForwardPower = 700.0;
                    MaxForwardPower = 800.0;
                    NominalReversePower = 129.0;
                    MaxReversePower = 170.0;
                    break;
                case "1200S":
                    NominalForwardPower = 1200.0;
                    MaxForwardPower = 1400.0;
                    NominalReversePower = 228.0;
                    MaxReversePower = 300.0;
                    break;
                default:
                    // Default to ACOM 600S if config file has been corrupted
                    ampModel = "600S";
                    NominalForwardPower = 600.0;
                    MaxForwardPower = 700.0;
                    NominalReversePower = 114.0;
                    MaxReversePower = 150.0;
                    break;
            }

            Settings.Default.AmplifierModel = ampModel;
            Settings.Default.ComPort = comPort;
            Settings.Default.AlwaysOnTop = alwaysontop;
            Settings.Default.Save();

            programTitle = "ACOM " + ampModel + " Controller" + Release + "(" 
                + comPort + (portIsOpen ? ")" : " - failed to open)");

            // UI changes has to be made on main thread
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                ProgramWindow.Title = programTitle;
                ProgramWindow.Topmost = alwaysontop;
                pwrBar.Maximum = NominalForwardPower;
                pwrBar_Peak.Maximum = MaxForwardPower - NominalForwardPower;
                reflBar.Maximum = NominalReversePower;
                reflBar_Peak.Maximum = MaxReversePower - NominalReversePower;
            }));

            // Send enable telemetry command to PA
            if (portIsOpen)
                Port.Write(CommandEnableTelemetry, 0, CommandEnableTelemetry.Length);

            // Enable event handler for serial data received
            Port.DataReceived += Port_OnReceiveData; // DataReceived Event Handler
        }

        // Event handler for received data
        private void Port_OnReceiveData(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort _port = (SerialPort)sender;
            byte[] buf = new byte[_port.BytesToRead];
            _port.Read(buf, 0, buf.Length);

            linkIsAlive = true; // Set flag to indicate telemetry is flowing

            foreach (byte b in buf)
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
                            foreach (byte c in msg)
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
                                        
                                        // Lower part of the reflected bar in gray
                                        reflBar.Value = (ReflectedPowerDisplay > NominalReversePower) ? NominalReversePower : ReflectedPowerDisplay;
                                        reflBar.Foreground = Brushes.Gray;
                                        
                                        // Upper part of the reflected bar in red
                                        reflBar_Peak.Value = (ReflectedPowerDisplay > NominalReversePower) ? PApowerDisplay - NominalReversePower : 0;
                                        reflBar_Peak.Foreground = Brushes.Crimson;

                                        // Filter and display SWR data 
                                        swrCurrent = (msg[26] + msg[27] * 256) / 100.0;
                                        swrValue[swrPeakIndex++] = swrCurrent; // save current power in fifo
                                        swrDisplay= swrValue.Max();
                                        if (swrPeakIndex>= swrPeakMemory) swrPeakIndex = 0;  // wrap around
                                        if (swrDisplay != 0)
                                            swrLabel.Content = swrDisplay.ToString("0.00");
                                        else
                                            swrLabel.Content = "";

                                        // Filter output power data 
                                        // Add 2% to align better with PA's own display, unclear why
                                        PApowerCurrent = 1.02 * (msg[22] + msg[23] * 256);
                                        PApower[PApowerPeakIndex++] = PApowerCurrent; // save current power in fifo
                                        PApowerDisplay = PApower.Max();
                                        if (PApowerPeakIndex >= PApowerPeakMemory) PApowerPeakIndex = 0;  // wrap around
                                        pwrLabel.Content = PApowerDisplay.ToString("0") + "W";

                                        // Lower part of the bar in blue
                                        pwrBar.Value = (PApowerDisplay > NominalForwardPower) ? NominalForwardPower : PApowerDisplay;
                                        pwrBar.Foreground = Brushes.RoyalBlue;

                                        // Upper part of the bar in red
                                        pwrBar_Peak.Value = (PApowerDisplay > NominalForwardPower) ? PApowerDisplay - NominalForwardPower : 0.0;
                                        pwrBar_Peak.Foreground = Brushes.Crimson;

                                        // Show active LPF as text
                                        bandLabel.Content = BandName[msg[69] & 0x0F];

                                        errorCode = msg[66];
                                        //errorTextButton.Content = string.Format("code: {0}\nparameter: {1}", errorCode, errorParameter);
                                        if (errorCode == 0xff)
                                            errorTextButton.Visibility = Visibility.Hidden;
                                        else 
                                        { // We have an error or warning condition
                                            errorTextButton.Visibility = Visibility.Visible;
                                            switch (errorCode)
                                            {
                                                case 0x00:
                                                case 0x08:
                                                    errorTextButton.Content = "Hot switching";
                                                    break;
                                                case 0x03:
                                                    errorTextButton.Content = "Drive power at wrong time";
                                                    break;
                                                case 0x04:
                                                case 0x05:
                                                    errorTextButton.Content = "Reflected power warning";
                                                    break;
                                                case 0x06:
                                                case 0x07:
                                                    errorTextButton.Content = "Drive power too high";
                                                    break;
                                                case 0x0c:
                                                    errorTextButton.Content = "RF power at wrong time";
                                                    break;
                                                case 0x0e:
                                                    errorTextButton.Content = "Stop transmission first";
                                                    break;
                                                case 0x0f:
                                                    errorTextButton.Content = "Remove drive power";
                                                    break;
                                                case 0x24:
                                                case 0x25:
                                                case 0x39:
                                                case 0x44:
                                                case 0x45:
                                                case 0x59:
                                                    errorTextButton.Content = "Excessive PAM current";
                                                    break;
                                                case 0x70:
                                                    errorTextButton.Content = "CAT error";
                                                    break;
                                                default:
                                                    errorTextButton.Content = "ERROR - See display";
                                                    break;
                                            }
                                        }
                                    }
                                    else // PA is powering down
                                    {
                                        bandLabel.Content = "--m";
                                        driveLabel.Content = "--W";

                                        reflLabel.Content = "--R";
                                        reflBar.Value = 0.0;
                                        reflBar_Peak.Value = 0.0;

                                        tempLabel.Content = "--C";
                                        tempBar.Value = 0.0;

                                        pwrLabel.Content = "---W";
                                        pwrBar.Value = 0.0;
                                        pwrBar_Peak.Value = 0.0;
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
            if (portIsOpen)
                Port.Write(CommandStandby, 0, CommandStandby.Length); 
        }

        // At click on operate button
        void OperateClick(object sender, RoutedEventArgs e)
        {
            // Send Operate command to PA
            if (portIsOpen)
                Port.Write(CommandOperate, 0, CommandOperate.Length);  
        }

        // At click on off button
        void OffClick(object sender, RoutedEventArgs e)
        {
            // Send Off command to PA
            if (portIsOpen)
                Port.Write(CommandOff, 0, CommandOff.Length);  
        }

        // Executed repeatedly 
        void OnTimer(object sender, EventArgs e)
        {
            // Re-enable PA telemetry on every timer click to ensure status info after startup
            if (portIsOpen)
                Port.Write(CommandEnableTelemetry, 0, CommandEnableTelemetry.Length);

            if (!linkIsAlive)
                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    bandLabel.Content = "--m";
                    driveLabel.Content = "--W";

                    reflLabel.Content = "--R";
                    reflBar.Value = 0.0;
                    reflBar_Peak.Value = 0.0;

                    tempLabel.Content = "--C";
                    tempBar.Value = 0.0;

                    pwrLabel.Content = "---W";
                    pwrBar.Value = 0.0;
                    pwrBar_Peak.Value = 0.0;

                    statusLabel.Foreground = Brushes.Gray;
                    statusLabel.Content = "OFF";
                }));
            
            linkIsAlive = false;
        }

        private void DismissErrorClick(object sender, RoutedEventArgs e)
        {
            errorTextButton.Visibility = Visibility.Hidden;
            // Send Operate command to PA
            if (portIsOpen)
                Port.Write(CommandOperate, 0, CommandOperate.Length);
        }

        private void standbyButton_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            Config configPanel = new Config(this, Settings.Default.AmplifierModel, Settings.Default.ComPort, Settings.Default.AlwaysOnTop);
            configPanel.ShowDialog();
        }
    }
}

