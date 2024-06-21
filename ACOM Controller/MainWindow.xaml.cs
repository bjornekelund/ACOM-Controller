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
        readonly AssemblyName _assemblyName = Assembly.GetExecutingAssembly().GetName();
        readonly string Release;

        const byte msglen = 72;
        readonly byte[] messageBytes = new byte[msglen];
        byte msgpos = 0;
        bool parsing = false; // true while receiving a message

        readonly byte[] CommandEnableTelemetry = [0x55, 0x92, 0x04, 0x15];
        readonly byte[] CommandDisableTelemetry = [0x55, 0x91, 0x04, 0x16];

        readonly byte[] CommandOperate = [ 0x55, 0x81, 0x08, 0x02, 0x00, 0x06, 0x00, 0x1a ];
        readonly byte[] CommandStandby = [0x55, 0x81, 0x08, 0x02, 0x00, 0x05, 0x00, 0x1b];
        readonly byte[] CommandOff = [0x55, 0x81, 0x08, 0x02, 0x00, 0x0A, 0x00, 0x16];

        readonly string[] BandName = [ "?m", "160m", "80m", "40/60m", "30m", "20m",
            "17m", "15m", "12m", "10m", "6m", "4m", "?m", "?m", "?m", "?m"];

        int PAstatus = 0;
        int PAtemp = 0; // PA temperature
        int PAfan = 0; // PAM fan state, 0 = off

        const int PApowerPeakMemory = 10;
        int PApowerPeakIndex = 0;
        readonly double[] PApower = new double[PApowerPeakMemory]; // Array for filtering PA power 
        double PApowerCurrent; // Current PA power
        double PApowerDisplay = 0.0; // Filtered PA output power
        double PApowerDisplayBar = 0.0; // Filtered PA output power, increased by 2% for graphics

        readonly double[] DCpower = new double[PApowerPeakMemory];
        double DCpowerCurrent;
        double effDisplay = 0.0;

        const int DrivePowerPeakMemory = 10;
        int DrivePowerPeakIndex = 0;
        readonly double[] DrivePower = new double[DrivePowerPeakMemory]; // Array for filtering drive power 
        double DrivePowerCurrent = 0.0; // Current PA power
        double DrivePowerDisplay = 0.0; // Filtered PA output power

        const int ReflectedPowerPeakMemory = 10;
        int ReflectedPowerPeakIndex = 0;
        readonly double[] ReflectedPower = new double[ReflectedPowerPeakMemory]; // Array for filtering reflected power 
        double ReflectedPowerCurrent = 0.0; // Current reflected power
        double ReflectedPowerDisplay = 0.0; // Filtered reflected power

        const int swrMemory = 10;
        int swrIndex = 0;
        readonly double[] swrValue = new double[swrMemory]; // Array for filtering SWR reports
        double swrCurrent = 0.0; // Current SWR
        double swrDisplay = 0.0; // Filtered SWR 

        double NominalForwardPower; // For scaling display
        double MaxForwardPower;
        double NominalReversePower;
        double MaxReversePower;

        int TemperatureOffset; // For calculating real temperature
        bool ShowTemperature; // Whether PA shows temperature in digits
        int WarningTemperature; // Temperature at which bar turns red
        readonly Brush TempLabelColor;

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
                if (!Settings.Default.NoPopup)
                {
                    MessageBox.Show("ACOM Controller is already running on this PC.", "ACOM Controller");
                }

                Application.Current.Shutdown();
            }

            // Hide error pop up
            errorTextButton.Visibility = Visibility.Hidden;

            Configuration(Settings.Default.ComPort, Settings.Default.AmplifierModel, Settings.Default.AlwaysOnTop, 
                Settings.Default.NoPopup, Settings.Default.ShowEfficiency, Settings.Default.ShowGain, Settings.Default.ShowSWR);

            // Fetch window location from saved settings
            Top = Settings.Default.Top;
            Left = Settings.Default.Left;

            // Clearing peak detection arrays, just to be safe
            Array.Clear(PApower, 0, PApower.Length);
            Array.Clear(DrivePower, 0, DrivePower.Length);

            // Since there is no way to know if telemetry is enabled on PA, 
            // use a periodic timer to constantly re-enable telemetry
            System.Windows.Threading.DispatcherTimer dispatcherTimer = new();
            dispatcherTimer.Tick += new EventHandler(OnTimer);
            dispatcherTimer.Interval = new TimeSpan(0, 0, 0, 0, 200);
            dispatcherTimer.Start();

            TempLabelColor = tempLabel.Foreground;
        }

        // Clean up and housekeeping at program shutdown
        private void MainWindow_Closed(object sender, EventArgs e)
        {
            // Remember window location 
            Settings.Default.Top = Top;
            Settings.Default.Left = Left;
            Settings.Default.Save();

            // Send disable telemetry command to PA
            if (portIsOpen)
            {
                Port.Write(CommandDisableTelemetry, 0, CommandDisableTelemetry.Length);
            }
        }

        public void Configuration(string comPort, string ampModel, bool alwaysontop, bool nopopup, bool showEfficiency, bool showGain, bool showSWR)
        {
            try
            {
                // Make sure port is released and event unsubscribed
                Port.Close();
                Port.DataReceived -= Port_OnReceiveData;
            }
            catch { }

            portIsOpen = false;

            // Default to COM1 if config file is invalid
            Port = new SerialPort(comPort == "" ? "COM1" : comPort, 9600, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None, // Disable handshake
                DtrEnable = false,          // Make sure DTR and RTS are low to avoid blocking front panel power button
                RtsEnable = false
            };

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
                case "500S":
                    NominalForwardPower = 500.0;
                    MaxForwardPower = 600.0;
                    NominalReversePower = 99.0;
                    MaxReversePower = 130.0;
                    TemperatureOffset = 282;
                    ShowTemperature = false;
                    WarningTemperature = 65;
                    break;
                case "600S":
                    NominalForwardPower = 600.0;
                    MaxForwardPower = 700.0;
                    NominalReversePower = 114.0;
                    MaxReversePower = 150.0;
                    TemperatureOffset = 273;
                    ShowTemperature = true;
                    WarningTemperature = 65;
                    break;
                default:
                case "700S":
                    // Default to ACOM 700S if config file is invalid or missing
                    ampModel = "700S";
                    NominalForwardPower = 700.0;
                    MaxForwardPower = 800.0;
                    NominalReversePower = 129.0;
                    MaxReversePower = 170.0;
                    TemperatureOffset = 282;
                    ShowTemperature = true;
                    WarningTemperature = 65;
                    break;
                case "1200S":
                    NominalForwardPower = 1200.0;
                    MaxForwardPower = 1400.0;
                    NominalReversePower = 228.0;
                    MaxReversePower = 300.0;
                    TemperatureOffset = 281;
                    ShowTemperature = true;
                    WarningTemperature = 65;
                    break;
                case "2020S":
                    NominalForwardPower = 1800.0;
                    MaxForwardPower = 2000.0;
                    NominalReversePower = 228.0;
                    MaxReversePower = 300.0;
                    TemperatureOffset = 282;
                    ShowTemperature = false;
                    WarningTemperature = 65;
                    break;
            }

            Settings.Default.AmplifierModel = ampModel;
            Settings.Default.ComPort = comPort;
            Settings.Default.AlwaysOnTop = alwaysontop;
            Settings.Default.NoPopup = nopopup;
            Settings.Default.ShowEfficiency = showEfficiency;
            Settings.Default.ShowGain = showGain;
            Settings.Default.ShowSWR = showSWR;
            Settings.Default.Save();

            effLabel.Visibility = showEfficiency ? Visibility.Visible : Visibility.Hidden;
            swrLabel.Visibility = showSWR ? Visibility.Visible : Visibility.Hidden;
            gainLabel.Visibility = showGain ? Visibility.Visible : Visibility.Hidden;

            programTitle = "ACOM " + ampModel + " Controller" + Release + "("
                + comPort + (portIsOpen ? ")" : " - failed to open)");

            // UI changes has to be made on main thread
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                ProgramWindow.Title = programTitle;
                ProgramWindow.Topmost = alwaysontop;

                pwrBar.Maximum = NominalForwardPower + 1;
                pwrBar.Minimum = 0;
                pwrBar_Red.Maximum = MaxForwardPower;
                pwrBar_Red.Minimum = NominalForwardPower;

                reflBar.Maximum = NominalReversePower - 1;
                reflBar.Minimum = 0;
                reflBar_Red.Maximum = MaxReversePower;
                reflBar_Red.Minimum = NominalReversePower;

                tempBar.Minimum = 0;
                tempBar.Maximum = 100;
            }));

            // Send enable telemetry command to PA if port successfully opened
            if (portIsOpen)
            {
                Port.Write(CommandEnableTelemetry, 0, CommandEnableTelemetry.Length);

                // Enable event handler for serial data received
                Port.DataReceived += Port_OnReceiveData; // DataReceived Event Handler
            }
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
                    messageBytes[msgpos++] = b;
                }
                else if (parsing)
                {
                    messageBytes[msgpos++] = b;

                    // 0x2f is second byte in telemetry datagram. 
                    // If not a telemetry message, reset parser
                    if (messageBytes[1] != 0x2f)
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
                            foreach (byte c in messageBytes)
                            {
                                checksum += c;
                            }

                            // checksum zero => a real message - get parameters and update UI
                            if (checksum == 0)
                            {
                                PAstatus = (messageBytes[3] & 0xf0) >> 4; // extract data from message

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
                                    PAtemp = messageBytes[16] + messageBytes[17] * 256 - TemperatureOffset; // extract data from message
                                    PAfan = (messageBytes[69] & 0xf0) >> 4;

                                    if (PAstatus != 10) // PAstatus 10 means in powering down mode
                                    {
                                        tempBar.Value = PAtemp;
                                        tempLabel.Content = ShowTemperature ? PAtemp.ToString() + "C" : "";

                                        tempBar.Foreground = Brushes.Green;

                                        if (PAtemp < WarningTemperature)
                                        {
                                            tempBar.Foreground = Brushes.Green;
                                            tempLabel.Foreground = TempLabelColor;
                                            tempLabel.FontWeight = FontWeights.Normal;
                                        }
                                        else
                                        {
                                            tempBar.Foreground = Brushes.Red;
                                            tempLabel.Foreground = Brushes.Crimson;
                                            tempLabel.FontWeight = FontWeights.Bold;
                                        }

                                        switch (PAfan)
                                        {
                                            case 1:
                                                fanLabel.Content = "Fan";
                                                fanLabel.FontWeight = FontWeights.Normal;
                                                fanLabel.Foreground = Brushes.Gray;
                                                break;
                                            case 2:
                                                fanLabel.Content = "Fan 2";
                                                fanLabel.FontWeight = FontWeights.Bold;
                                                fanLabel.Foreground = Brushes.Gray;
                                                break;
                                            case 3:
                                                fanLabel.Content = "Fan 3";
                                                fanLabel.FontWeight = FontWeights.Bold;
                                                fanLabel.Foreground = Brushes.Black;
                                                break;
                                            case 4:
                                                fanLabel.Content = "FAN 4";
                                                fanLabel.FontWeight = FontWeights.Bold;
                                                fanLabel.Foreground = Brushes.Black;
                                                break;
                                            default:
                                                fanLabel.Content = "";
                                                break;
                                        }

                                        // Filter and display drive power data 
                                        DrivePowerCurrent = messageBytes[20] + messageBytes[21] * 256.0;
                                        DrivePower[DrivePowerPeakIndex] = DrivePowerCurrent; // save current power in fifo
                                        DrivePowerPeakIndex = (DrivePowerPeakIndex + 1) % DrivePowerPeakMemory;
                                        DrivePowerDisplay = DrivePower.Max() / 10.0;

                                        driveLabel.Content = DrivePowerDisplay.ToString("0") + "W";

                                        // Filter reflected power data 
                                        ReflectedPowerCurrent = messageBytes[24] + messageBytes[25] * 256.0;
                                        ReflectedPower[ReflectedPowerPeakIndex] = ReflectedPowerCurrent; // save current power in fifo
                                        ReflectedPowerPeakIndex = (ReflectedPowerPeakIndex + 1) % ReflectedPowerPeakMemory;
                                        ReflectedPowerDisplay = ReflectedPower.Max();

                                        reflLabel.Content = ReflectedPowerDisplay.ToString("0") + "R";

                                        // Reflected power bars
                                        reflBar.Foreground = Brushes.Gray;
                                        reflBar_Red.Foreground = Brushes.Red;
                                        reflBar.Value = ReflectedPowerDisplay;
                                        reflBar_Red.Value = ReflectedPowerDisplay;

                                        // Filter and display SWR data 
                                        swrCurrent = (messageBytes[26] + messageBytes[27] * 256) / 100.0;
                                        swrValue[swrIndex] = swrCurrent;
                                        swrIndex = (swrIndex + 1) % swrMemory;

                                        // Filter output power data 
                                        PApowerCurrent = messageBytes[22] + messageBytes[23] * 256;
                                        PApower[PApowerPeakIndex] = PApowerCurrent;
                                        PApowerDisplay = PApower.Max();
                                        PApowerDisplayBar = PApowerDisplay;
                                        pwrLabel.Content = PApowerDisplay.ToString("0") + "W";

                                        // Filter DC power data
                                        DCpowerCurrent = messageBytes[8] * 0.1 + messageBytes[9] * 25.6;
                                        DCpower[PApowerPeakIndex] = DCpowerCurrent;

                                        PApowerPeakIndex = (PApowerPeakIndex + 1) % PApowerPeakMemory;

                                        // Output power bars
                                        pwrBar.Foreground = Brushes.RoyalBlue;
                                        pwrBar_Red.Foreground = Brushes.Crimson;
                                        pwrBar.Value = PApowerDisplayBar;
                                        pwrBar_Red.Value = PApowerDisplayBar;

                                        effDisplay = 100.0 * PApowerDisplay / DCpower.Max();

                                        if (effDisplay > 20.0 && effDisplay < 80.0 && PApowerDisplay > 50.0)
                                        {
                                            effLabel.Content = effDisplay.ToString("0") + "%";
                                        }
                                        else
                                        {
                                            effLabel.Content = string.Empty;
                                        }

                                        if (DrivePowerDisplay > 1.0 && PApowerDisplay > 100.0)
                                        {
                                            gainLabel.Content = (10.0 * Math.Log10(PApowerDisplay / DrivePowerDisplay)).ToString("0") + "dB";
                                        }
                                        else
                                        {
                                            gainLabel.Content = string.Empty;
                                        }

                                        // Calculate average of recent non-zero SWR reports
                                        double swrAverageSum = 0.0;
                                        int swrNonZeroCount = 0;
                                        for (int i = 0; i < swrMemory; i++)
                                        {
                                            if (swrValue[i] > 0)
                                            {
                                                swrAverageSum += swrValue[i];
                                                swrNonZeroCount++;
                                            }
                                        }
                                        swrDisplay = swrAverageSum / swrNonZeroCount;

                                        if (swrDisplay > 0 && PApowerDisplay > 0)
                                        {
                                            swrLabel.Content = swrDisplay.ToString("0.00");
                                        }
                                        else
                                        {
                                            swrLabel.Content = string.Empty;
                                        }

                                        // Show active LPF as text
                                        bandLabel.Content = BandName[messageBytes[69] & 0x0f];

                                        errorCode = messageBytes[66];
                                        //errorTextButton.Content = string.Format("code: {0}\nparameter: {1}", errorCode, errorParameter);
                                        if (errorCode == 0xff)
                                        {
                                            errorTextButton.Visibility = Visibility.Hidden;
                                        }
                                        else
                                        { // We have an error or warning condition
                                            errorTextButton.Visibility = Visibility.Visible;
                                            errorTextButton.Content = errorCode switch
                                            {
                                                0x00 or 0x08 => "Hot switching",
                                                0x03 => "Drive power at wrong time",
                                                0x04 or 0x05 => "Reflected power warning",
                                                0x06 or 0x07 => "Drive power too high",
                                                0x0c => "RF power at wrong time",
                                                0x0e => "Stop transmission first",
                                                0x0f => "Remove drive power",
                                                0x24 or 0x25 or 0x39 or 0x44 or 0x45 or 0x59 => "Excessive PAM current",
                                                0x70 => "CAT error",
                                                _ => "ERROR - See display",
                                            };
                                        }
                                    }
                                    else // PA is powering down
                                    {
                                        bandLabel.Content = "--m";
                                        driveLabel.Content = "--W";

                                        reflLabel.Content = "--R";
                                        reflBar.Value = 0.0;
                                        reflBar_Red.Value = 0.0;

                                        tempLabel.Content = "--C";
                                        tempBar.Value = 0.0;

                                        pwrLabel.Content = "--W";
                                        pwrBar.Value = 0.0;
                                        pwrBar_Red.Value = 0.0;
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
        private void StandbyClick(object sender, RoutedEventArgs e)
        {
            // Send Standby command to PA
            if (portIsOpen)
            {
                Port.Write(CommandStandby, 0, CommandStandby.Length);
            }
        }

        // At click on operate button
        private void OperateClick(object sender, RoutedEventArgs e)
        {
            // Send Operate command to PA
            if (portIsOpen)
            {
                Port.Write(CommandOperate, 0, CommandOperate.Length);
            }
        }

        // At click on off button
        private void OffClick(object sender, RoutedEventArgs e)
        {
            // Send Off command to PA
            if (portIsOpen)
            {
                Port.Write(CommandOff, 0, CommandOff.Length);
            }
        }

        // Executed repeatedly 
        private void OnTimer(object sender, EventArgs e)
        {
            if (!linkIsAlive)
            {
                // Re-enable PA telemetry on every timer click to ensure status info after startup
                if (portIsOpen)
                {
                    Port.Write(CommandEnableTelemetry, 0, CommandEnableTelemetry.Length);
                }

                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    bandLabel.Content = "--m";
                    driveLabel.Content = "--W";

                    reflLabel.Content = "--R";
                    reflBar.Value = 0.0;
                    reflBar_Red.Value = 0.0;

                    tempLabel.Content = "--C";
                    tempBar.Value = 0.0;

                    pwrLabel.Content = "--W";
                    pwrBar.Value = 0.0;
                    pwrBar_Red.Value = 0.0;

                    statusLabel.Foreground = Brushes.Gray;
                    statusLabel.Content = "OFF";
                }));
            }

            linkIsAlive = false;
        }

        private void DismissErrorClick(object sender, RoutedEventArgs e)
        {
            errorTextButton.Visibility = Visibility.Hidden;
            // Send Operate command to PA
            if (portIsOpen)
            {
                Port.Write(CommandOperate, 0, CommandOperate.Length);
            }
        }

        private void StandbyButton_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            Config configPanel = new(this, Settings.Default.AmplifierModel, Settings.Default.ComPort, Settings.Default.AlwaysOnTop, 
                Settings.Default.NoPopup, Settings.Default.ShowEfficiency, Settings.Default.ShowGain, Settings.Default.ShowSWR);
            configPanel.ShowDialog();
        }
    }
}

