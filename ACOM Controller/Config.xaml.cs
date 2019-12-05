using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ACOM_Controller
{
    /// <summary>
    /// Interaction logic for Config.xaml
    /// </summary>
    public partial class Config : Window
    {
        MainWindow mainwindow;

        public Config(MainWindow mw, string model, string port)
        {
            InitializeComponent();

            mainwindow = mw;
            modelComboBox.Items.Add("600S");
            modelComboBox.Items.Add("700S");
            modelComboBox.Items.Add("1200S");
            modelComboBox.SelectedItem = model;

            for (int i = 0; i < 40; i++)
                portComboBox.Items.Add("COM" + i.ToString());
            portComboBox.SelectedItem = port;
        }

        private void cancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void okButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
