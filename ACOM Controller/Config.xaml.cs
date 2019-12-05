﻿using System.Windows;

namespace ACOM_Controller
{
    public partial class Config : Window
    {
        MainWindow mainwindow;

        public Config(MainWindow mw, string model, string port)
        {
            InitializeComponent();

            Top = mw.Top + 10;
            Left = mw.Left + 50;

            mainwindow = mw;
            modelComboBox.Items.Add("600S");
            modelComboBox.Items.Add("700S");
            modelComboBox.Items.Add("1200S");
            modelComboBox.SelectedItem = model;

            for (int i = 1; i <= 30; i++)
                portComboBox.Items.Add("COM" + i.ToString());
            portComboBox.SelectedItem = port;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            mainwindow.Configuration(portComboBox.Text, modelComboBox.Text);
            Close();
        }
    }
}
