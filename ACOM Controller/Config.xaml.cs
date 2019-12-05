﻿using System.Windows;

namespace ACOM_Controller
{
    public partial class Config : Window
    {
        MainWindow mainwindow;

        public Config(MainWindow mw, string model, string port)
        {
            InitializeComponent();

            Top = mw.Top + 50;
            Left = mw.Left + 100;

            mainwindow = mw;
            modelComboBox.Items.Add("600S");
            modelComboBox.Items.Add("700S");
            modelComboBox.Items.Add("1200S");
            modelComboBox.SelectedItem = model;

            for (int i = 0; i < 30; i++)
                portComboBox.Items.Add("COM" + i.ToString());
            portComboBox.SelectedItem = port;
        }

        private void cancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void okButton_Click(object sender, RoutedEventArgs e)
        {
            mainwindow.Configuration(portComboBox.Text, modelComboBox.Text);
            Close();
        }
    }
}
