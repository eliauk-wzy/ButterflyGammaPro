using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ButterflyGammaPro.Models;
using ButterflyGammaPro.Services;
using ButterflyGammaPro.Views;

namespace ButterflyGammaPro {
    public partial class MainWindow : Window {
        private AppConfig _config;
        private readonly GammaEngine _gammaEngine;
        private readonly HotKeyManager _hotKeyManager;
        private OsdWindow? _osdWindow;
        private TrayService? _trayService;
        private GammaProfile? _selectedProfile;
        private bool _isUpdatingUi = false;
        private bool _isExplicitExit = false;

        public MainWindow() {
            InitializeComponent();
            try {
                foreach (var p in Process.GetProcessesByName("gapa")) {
                    p.Kill();
                }
            } catch {}

            _config = ConfigManager.Load();
            _gammaEngine = new GammaEngine();
            _hotKeyManager = new HotKeyManager();

            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
            try { _osdWindow = new OsdWindow(); } catch {}

            _isUpdatingUi = true;
            CmbHotKey.Items.Clear();
            foreach (var k in HotKeyManager.KeyMap.Keys) {
                CmbHotKey.Items.Add(k);
            }
            _isUpdatingUi = false;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(hwnd);
            if (source != null) {
                _hotKeyManager.Initialize(hwnd, source);
                source.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) => {
                    _trayService?.HandleMessage(msg, wParam, lParam);
                    return IntPtr.Zero;
                });
            }

            _hotKeyManager.HotKeyPressed += (p) => Dispatcher.Invoke(() => ActivateProfile(p));

            InitTrayService(hwnd);
            ReRegisterHotKeys();

            ChkOsd.IsChecked = _config.EnableOsd;
            ChkTransition.IsChecked = _config.EnableTransition;
            ChkAntiReset.IsChecked = _config.AntiResetLock;

            RefreshProfileList();
            SelectProfile(_config.Profiles.FirstOrDefault(p => p.VirtualKey == 0x77) ?? _config.Profiles.FirstOrDefault());
        }

        private void InitTrayService(IntPtr hwnd) {
            _trayService = new TrayService();
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            _trayService.Initialize(hwnd, "Butterfly Gamma Pro (小蝴蝶滤镜)", iconPath);
            _trayService.LeftClicked += () => Dispatcher.Invoke(ShowWindow);
            _trayService.DoubleClicked += () => Dispatcher.Invoke(ShowWindow);
            UpdateTrayMenu();
        }

        private void UpdateTrayMenu() {
            if (_trayService == null) return;

            var menu = new ContextMenu {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8")),
                BorderThickness = new Thickness(1.2)
            };

            var miShow = new MenuItem { Header = "🖥️ 显示主界面", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
            miShow.Click += (s, e) => ShowWindow();
            menu.Items.Add(miShow);

            menu.Items.Add(new Separator { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")) });

            foreach (var p in _config.Profiles) {
                var prof = p;
                string label = prof.VirtualKey > 0 ? $"{prof.Name} ({prof.KeyName})" : prof.Name;
                var miProf = new MenuItem { Header = label, Foreground = prof.IsActive ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8")) : Brushes.White };
                if (prof.IsActive) miProf.Header = "✓ " + label;
                miProf.Click += (s, e) => ActivateProfile(prof);
                menu.Items.Add(miProf);
            }

            menu.Items.Add(new Separator { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")) });

            var miSettings = new MenuItem { Header = "⚙️ 关闭按钮行为", Foreground = Brushes.White };
            var miAsk = new MenuItem { Header = "每次询问", IsChecked = !_config.RememberCloseChoice || _config.CloseBehavior == 0 };
            miAsk.Click += (s, e) => { _config.RememberCloseChoice = false; _config.CloseBehavior = 0; ConfigManager.Save(_config); UpdateTrayMenu(); };
            var miTray = new MenuItem { Header = "最小化到托盘", IsChecked = _config.RememberCloseChoice && _config.CloseBehavior == 1 };
            miTray.Click += (s, e) => { _config.RememberCloseChoice = true; _config.CloseBehavior = 1; ConfigManager.Save(_config); UpdateTrayMenu(); };
            var miExitChoice = new MenuItem { Header = "彻底退出", IsChecked = _config.RememberCloseChoice && _config.CloseBehavior == 2 };
            miExitChoice.Click += (s, e) => { _config.RememberCloseChoice = true; _config.CloseBehavior = 2; ConfigManager.Save(_config); UpdateTrayMenu(); };

            miSettings.Items.Add(miAsk);
            miSettings.Items.Add(miTray);
            miSettings.Items.Add(miExitChoice);
            menu.Items.Add(miSettings);

            menu.Items.Add(new Separator { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")) });

            var miExit = new MenuItem { Header = "❌ 彻底退出程序", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")) };
            miExit.Click += (s, e) => ExitApplication();
            menu.Items.Add(miExit);

            _trayService.ContextMenu = menu;
        }

        private void ReRegisterHotKeys() {
            var result = _hotKeyManager.RegisterAll(_config.Profiles);
            if (!result.AllSucceeded && result.Conflicts.Count > 0) {
                DotStatus.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                TxtStatus.Text = "⚠️ 热键冲突: " + string.Join("; ", result.Conflicts);
                TxtStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
            } else {
                DotStatus.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                TxtStatus.Text = "状态：就绪，硬件色彩引擎与热键已激活";
                TxtStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
            }
        }

        private void RefreshProfileList() {
            ProfileList.ItemsSource = null;
            ProfileList.ItemsSource = _config.Profiles;
            UpdateTrayMenu();
        }

        private void SelectProfile(GammaProfile? profile) {
            if (profile == null) return;
            _selectedProfile = profile;
            _isUpdatingUi = true;

            TxtProfileName.Text = profile.Name;
            CmbHotKey.SelectedItem = profile.KeyName;

            double g = profile.Red.Gamma / 100.0;
            TxtGammaVal.Text = g.ToString("0.00", CultureInfo.InvariantCulture);
            SliderGamma.Value = Math.Clamp(profile.Red.Gamma, SliderGamma.Minimum, SliderGamma.Maximum);

            TxtContrastVal.Text = profile.Red.Contrast.ToString();
            SliderContrast.Value = Math.Clamp(profile.Red.Contrast, SliderContrast.Minimum, SliderContrast.Maximum);

            TxtBrightnessVal.Text = profile.Red.Brightness.ToString();
            SliderBrightness.Value = Math.Clamp(profile.Red.Brightness, SliderBrightness.Minimum, SliderBrightness.Maximum);

            _isUpdatingUi = false;
        }

        private void ActivateProfile(GammaProfile profile) {
            foreach (var p in _config.Profiles) p.IsActive = (p.Id == profile.Id);
            RefreshProfileList();

            _gammaEngine.ApplyProfile(profile, immediate: !_config.EnableTransition);
            _config.ActiveProfileId = profile.Id;

            if (_config.EnableOsd && _osdWindow != null) {
                _osdWindow.ShowProfile(profile);
            }

            if (_selectedProfile?.Id == profile.Id) SelectProfile(profile);
        }

        private void ProfileCard_Click(object sender, MouseButtonEventArgs e) {
            if ((sender as FrameworkElement)?.DataContext is GammaProfile p) {
                SelectProfile(p);
                ActivateProfile(p);
            }
        }

        private void BtnAddProfile_Click(object sender, RoutedEventArgs e) {
            var np = new GammaProfile {
                Name = $"自定义方案 {_config.Profiles.Count + 1}",
                KeyName = "无",
                VirtualKey = 0,
                IsReadOnly = false,
                Red = new ColorChannel { Gamma = 200, Contrast = 120, Brightness = 0 },
                Green = new ColorChannel { Gamma = 200, Contrast = 120, Brightness = 0 },
                Blue = new ColorChannel { Gamma = 200, Contrast = 120, Brightness = 0 }
            };
            _config.Profiles.Add(np);
            ConfigManager.Save(_config);
            RefreshProfileList();
            SelectProfile(np);
            ActivateProfile(np);
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e) {
            if ((sender as FrameworkElement)?.Tag is GammaProfile p) {
                if (p.IsReadOnly) {
                    MessageBox.Show("此为内置预设方案，无法删除！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (MessageBox.Show($"确定删除滤镜方案「{p.Name}」？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) {
                    _config.Profiles.Remove(p);
                    ConfigManager.Save(_config);
                    ReRegisterHotKeys();
                    RefreshProfileList();
                    SelectProfile(_config.Profiles.FirstOrDefault());
                }
            }
        }

        private void TxtProfileName_TextChanged(object sender, TextChangedEventArgs e) {
            if (!_isUpdatingUi && _selectedProfile != null) {
                _selectedProfile.Name = TxtProfileName.Text.Trim();
                UpdateTrayMenu();
            }
        }

        private void CmbHotKey_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (!_isUpdatingUi && _selectedProfile != null && CmbHotKey.SelectedItem is string k) {
                _selectedProfile.KeyName = k;
                _selectedProfile.VirtualKey = HotKeyManager.KeyMap.TryGetValue(k, out uint vk) ? vk : 0;
                ReRegisterHotKeys();
                UpdateTrayMenu();
            }
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            if (_isUpdatingUi || _selectedProfile == null) return;

            int g = (int)SliderGamma.Value;
            int c = (int)SliderContrast.Value;
            int b = (int)SliderBrightness.Value;

            _selectedProfile.Red.Gamma = g; _selectedProfile.Green.Gamma = g; _selectedProfile.Blue.Gamma = g;
            _selectedProfile.Red.Contrast = c; _selectedProfile.Green.Contrast = c; _selectedProfile.Blue.Contrast = c;
            _selectedProfile.Red.Brightness = b; _selectedProfile.Green.Brightness = b; _selectedProfile.Blue.Brightness = b;

            _isUpdatingUi = true;
            TxtGammaVal.Text = (g / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            TxtContrastVal.Text = c.ToString();
            TxtBrightnessVal.Text = b.ToString();
            _isUpdatingUi = false;

            _gammaEngine.ApplyProfile(_selectedProfile, immediate: true);
        }

        private void TxtGammaVal_TextChanged(object sender, TextChangedEventArgs e) {
            if (_isUpdatingUi || _selectedProfile == null) return;
            string raw = TxtGammaVal.Text.Trim().Replace(",", ".");
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double g)) {
                int gi = (int)Math.Round(g * 100.0);
                if (gi >= 10 && gi <= 400) {
                    _selectedProfile.Red.Gamma = gi;
                    _selectedProfile.Green.Gamma = gi;
                    _selectedProfile.Blue.Gamma = gi;

                    _isUpdatingUi = true;
                    SliderGamma.Value = Math.Clamp(gi, SliderGamma.Minimum, SliderGamma.Maximum);
                    _isUpdatingUi = false;

                    _gammaEngine.ApplyProfile(_selectedProfile, immediate: true);
                }
            }
        }

        private void TxtGammaVal_LostFocus(object sender, RoutedEventArgs e) {
            if (_selectedProfile != null) {
                _isUpdatingUi = true;
                TxtGammaVal.Text = (_selectedProfile.Red.Gamma / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
                _isUpdatingUi = false;
            }
        }

        private void TxtContrastVal_TextChanged(object sender, TextChangedEventArgs e) {
            if (_isUpdatingUi || _selectedProfile == null) return;
            string raw = TxtContrastVal.Text.Trim().Replace("%", "");
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) {
                if (v >= 20 && v <= 300) {
                    _selectedProfile.Red.Contrast = v;
                    _selectedProfile.Green.Contrast = v;
                    _selectedProfile.Blue.Contrast = v;

                    _isUpdatingUi = true;
                    SliderContrast.Value = Math.Clamp(v, SliderContrast.Minimum, SliderContrast.Maximum);
                    _isUpdatingUi = false;

                    _gammaEngine.ApplyProfile(_selectedProfile, immediate: true);
                }
            }
        }

        private void TxtContrastVal_LostFocus(object sender, RoutedEventArgs e) {
            if (_selectedProfile != null) {
                _isUpdatingUi = true;
                TxtContrastVal.Text = _selectedProfile.Red.Contrast.ToString();
                _isUpdatingUi = false;
            }
        }

        private void TxtBrightnessVal_TextChanged(object sender, TextChangedEventArgs e) {
            if (_isUpdatingUi || _selectedProfile == null) return;
            string raw = TxtBrightnessVal.Text.Trim().Replace("%", "");
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) {
                if (v >= -80 && v <= 80) {
                    _selectedProfile.Red.Brightness = v;
                    _selectedProfile.Green.Brightness = v;
                    _selectedProfile.Blue.Brightness = v;

                    _isUpdatingUi = true;
                    SliderBrightness.Value = Math.Clamp(v, SliderBrightness.Minimum, SliderBrightness.Maximum);
                    _isUpdatingUi = false;

                    _gammaEngine.ApplyProfile(_selectedProfile, immediate: true);
                }
            }
        }

        private void TxtBrightnessVal_LostFocus(object sender, RoutedEventArgs e) {
            if (_selectedProfile != null) {
                _isUpdatingUi = true;
                TxtBrightnessVal.Text = _selectedProfile.Red.Brightness.ToString();
                _isUpdatingUi = false;
            }
        }

        private void BtnApplyCurrent_Click(object sender, RoutedEventArgs e) {
            if (_selectedProfile != null) ActivateProfile(_selectedProfile);
        }

        private void BtnResetDefault_Click(object sender, RoutedEventArgs e) {
            var def = _config.Profiles.FirstOrDefault(p => p.VirtualKey == 0x76);
            if (def != null) {
                ActivateProfile(def);
            } else {
                _gammaEngine.ResetToDefault();
            }
        }

        private void BtnSaveConfig_Click(object sender, RoutedEventArgs e) {
            _config.EnableOsd = ChkOsd.IsChecked == true;
            _config.EnableTransition = ChkTransition.IsChecked == true;
            _config.AntiResetLock = ChkAntiReset.IsChecked == true;
            ConfigManager.Save(_config);
            MessageBox.Show("配置已成功保存！", "Butterfly Gamma Pro", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void ShowWindow() {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) {
            this.WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) {
            this.Close();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e) {
            if (!_isExplicitExit) {
                if (_config.RememberCloseChoice && _config.CloseBehavior == 1) {
                    e.Cancel = true;
                    this.Hide();
                    _trayService?.ShowBalloon("Butterfly Gamma Pro", "已最小化至托盘后台运行");
                    return;
                }
                if (_config.RememberCloseChoice && _config.CloseBehavior == 2) {
                    // 彻底退出
                } else {
                    e.Cancel = true;
                    var dlg = new CloseConfirmWindow(_config.CloseBehavior) { Owner = this };
                    dlg.ShowDialog();
                    if (dlg.Confirmed) {
                        int b = dlg.MinimizeToTray ? 1 : 2;
                        if (dlg.RememberChoice) {
                            _config.CloseBehavior = b;
                            _config.RememberCloseChoice = true;
                            ConfigManager.Save(_config);
                            UpdateTrayMenu();
                        }
                        if (b == 1) {
                            this.Hide();
                            _trayService?.ShowBalloon("Butterfly Gamma Pro", "已最小化至托盘后台运行");
                        } else {
                            ExitApplication();
                        }
                    }
                    return;
                }
            }

            _hotKeyManager.Dispose();
            _gammaEngine.Dispose();
            _osdWindow?.Close();
            _trayService?.Dispose();
        }

        private void ExitApplication() {
            _isExplicitExit = true;
            _trayService?.Dispose();
            _gammaEngine.ResetToDefault(true);
            System.Windows.Application.Current.Shutdown();
        }

        private void BtnMinimizeTray_Click(object sender, RoutedEventArgs e) {
            this.Hide();
            _trayService?.ShowBalloon("Butterfly Gamma Pro", "已最小化至托盘后台运行");
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) {
            if (e.LeftButton == MouseButtonState.Pressed) this.DragMove();
        }
    }
}
