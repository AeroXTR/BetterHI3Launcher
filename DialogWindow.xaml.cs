﻿using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace BetterHI3Launcher
{
    public partial class DialogWindow : Window
    {
        public DialogWindow(string title, string message, bool question = false)
        {
            Owner = Application.Current.MainWindow;
            Left = Application.Current.MainWindow.Left;
            Top = Application.Current.MainWindow.Top;
            InitializeComponent();
            DialogTitle.Text = title;
            DialogMessage.Text = message;
            if(!question)
            {
                ConfirmButton.Content = MainWindow.textStrings["button_confirm"];
                CancelButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ConfirmButton.Margin = new Thickness(0, 0, 25, 0);
                ConfirmButton.Content = MainWindow.textStrings["button_yes"];
                CancelButton.Content = MainWindow.textStrings["button_no"];
            }
            if(MainWindow.LauncherLanguage != "en")
            {
                Resources["Font"] = new FontFamily("Segoe UI Bold");
            }
            Application.Current.MainWindow.WindowState = WindowState.Normal;
            BpUtility.PlaySound(Properties.Resources.Window_Open);
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            BpUtility.PlaySound(Properties.Resources.Click);
            DialogResult = true;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            BpUtility.PlaySound(Properties.Resources.Click);
            Close();
        }

        private void DialogWindow_Closing(object sender, CancelEventArgs e)
        {
            BpUtility.PlaySound(Properties.Resources.Window_Close);   
        }
    }
}
