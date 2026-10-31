using System;
using System.Windows;

namespace ButterflyGammaPro.Views
{
    public partial class CloseConfirmWindow : Window
    {
        public bool MinimizeToTray => RdoMinimize.IsChecked == true;
        public bool RememberChoice => ChkRemember.IsChecked == true;
        public bool Confirmed { get; private set; } = false;

        public CloseConfirmWindow(int defaultBehavior)
        {
            InitializeComponent();

            if (defaultBehavior == 2)
            {
                RdoExit.IsChecked = true;
            }
            else
            {
                RdoMinimize.IsChecked = true;
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            this.Close();
        }
    }
}
