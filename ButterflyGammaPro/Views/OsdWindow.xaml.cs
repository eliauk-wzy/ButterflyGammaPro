using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ButterflyGammaPro.Models;

namespace ButterflyGammaPro.Views
{
    public partial class OsdWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private readonly DispatcherTimer _hideTimer;

        public OsdWindow()
        {
            InitializeComponent();

            _hideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1600)
            };
            _hideTimer.Tick += (s, e) =>
            {
                _hideTimer.Stop();
                FadeOut();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);

            UpdatePosition();
        }

        public void UpdatePosition()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            this.Left = (screenWidth - 420) / 2.0;
            this.Top = 36;
        }

        public void ShowProfile(GammaProfile? profile)
        {
            if (profile == null)
            {
                TxtTitle.Text = "已恢复屏幕标准色调";
                TxtKey.Text = "默认";
                TxtDetails.Text = "Gamma: 1.00 | 亮度: 0% | 对比度: 100%";
            }
            else
            {
                TxtTitle.Text = profile.Name;
                TxtKey.Text = string.IsNullOrEmpty(profile.KeyName) ? "激活" : profile.KeyName;
                TxtDetails.Text = $"Gamma: {(profile.Red.Gamma / 100.0):0.00} | 亮度: {profile.Red.Brightness}% | 对比度: {profile.Red.Contrast}%";
            }

            UpdatePosition();
            if (!this.IsVisible)
            {
                this.Show();
            }

            FadeIn();
        }

        private void FadeIn()
        {
            _hideTimer.Stop();

            var opacityAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var slideAnim = new DoubleAnimation(-15, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            MainContainer.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);

            _hideTimer.Start();
        }

        private void FadeOut()
        {
            var opacityAnim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var slideAnim = new DoubleAnimation(0, -10, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            opacityAnim.Completed += (s, e) =>
            {
                this.Hide();
            };

            MainContainer.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
    }
}
