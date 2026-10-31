using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        private AppConfig _config = new();
        private GammaProfile? _selectedProfile;
        private readonly GammaEngine _gammaEngine = new();
        private readonly HotKeyManager _hotKeyManager = new();
        private TrayService? _trayService;
        private OsdWindow? _osdWindow;
        private bool _isUpdatingUi = false;
        private bool _isExplicitExit = false;

        // 通道调节模式：0 = 全通道 (All RGB), 1 = 红色 (Red), 2 = 绿色 (Green), 3 = 蓝色 (Blue)
        private int _channelMode = 0;

        public MainWindow() {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
            _config = ConfigManager.Load();
            _gammaEngine.AntiResetEnabled = _config.AntiResetLock;
            _gammaEngine.TargetMonitorId = string.IsNullOrEmpty(_config.TargetMonitor) ? "ALL" : _config.TargetMonitor;

            PopulateHotKeyCombo();

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(hwnd);
            if (source != null) {
                source.AddHook(WndProcHook);
            }

            // 初始化零延迟底层键盘与鼠标钩子专属线程
            _hotKeyManager.Initialize();
            _hotKeyManager.HotKeyPressed += (p) => Dispatcher.Invoke(() => ActivateProfile(p));

            InitTrayService(hwnd);
            ReRegisterHotKeys();

            ChkOsd.IsChecked = _config.EnableOsd;
            ChkTransition.IsChecked = _config.EnableTransition;
            ChkAntiReset.IsChecked = _config.AntiResetLock;

            InitMonitorsList();
            RefreshProfileList();
            SelectProfile(_config.Profiles.FirstOrDefault(p => p.VirtualKey == 0x77) ?? _config.Profiles.FirstOrDefault());

            // 启动 3.5 秒后在后台静默检测更新 (支持国内镜像加速)
            _ = Task.Run(async () => {
                await Task.Delay(3500);
                try {
                    var updateInfo = await UpdateService.CheckForUpdatesAsync();
                    if (updateInfo != null && updateInfo.HasUpdate) {
                        Dispatcher.Invoke(() => ShowUpdateDialog(updateInfo));
                    }
                } catch { }
            });
        }

        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            // 1. 系统级单实例广播唤醒消息处理 (彻底解决隐藏至托盘时无法定位窗口的假死)
            if (msg == App.WM_SHOWME) {
                ShowWindow();
                handled = true;
                return IntPtr.Zero;
            }

            // 2. 硬件显示配置变动监听 (WM_DISPLAYCHANGE: 0x007E - 游戏切全屏/改分辨率时秒级重置滤镜)
            if (msg == 0x007E) {
                _gammaEngine.BackupInitialRamps();
                _gammaEngine.ReapplyActiveProfileImmediately();
                return IntPtr.Zero;
            }

            // 3. 应用程序激活状态切换 (WM_ACTIVATEAPP: 0x001C)
            if (msg == 0x001C && wParam != IntPtr.Zero) {
                _gammaEngine.CheckAndRestoreTargetRamps();
            }

            // 4. 托盘消息传递
            _trayService?.HandleMessage(msg, wParam, lParam);
            return IntPtr.Zero;
        }

        private void PopulateHotKeyCombo() {
            _isUpdatingUi = true;
            CmbHotKey.Items.Clear();
            CmbHotKey.Items.Add("无");
            CmbHotKey.Items.Add("鼠标侧键下 (Mouse 4)");
            CmbHotKey.Items.Add("鼠标侧键上 (Mouse 5)");

            for (int i = 1; i <= 12; i++) {
                CmbHotKey.Items.Add($"F{i}");
            }
            for (int i = 1; i <= 9; i++) {
                CmbHotKey.Items.Add($"数字 {i}");
            }
            CmbHotKey.Items.Add("数字 0");
            _isUpdatingUi = false;
        }

        private void InitMonitorsList() {
            var monitors = DisplayManager.GetActiveMonitors();
            var items = new List<DisplayMonitorInfo> {
                new DisplayMonitorInfo { DisplayId = "ALL", Title = "🌐 全部显示器 (所有屏幕同步)" },
                new DisplayMonitorInfo { DisplayId = "PRIMARY", Title = "🖥️ 仅主显示器 (竞技游戏主屏专用)" }
            };
            items.AddRange(monitors);

            _isUpdatingUi = true;
            CmbMonitors.ItemsSource = items;

            var match = items.FirstOrDefault(i => string.Equals(i.DisplayId, _config.TargetMonitor, StringComparison.OrdinalIgnoreCase));
            if (match != null) {
                CmbMonitors.SelectedValue = match.DisplayId;
            } else {
                CmbMonitors.SelectedValue = "ALL";
            }
            _isUpdatingUi = false;

            UpdateHdrWarningBadge();
        }

        private void UpdateHdrWarningBadge() {
            var monitors = DisplayManager.GetActiveMonitors();
            bool targetHdr = false;

            if (string.Equals(_config.TargetMonitor, "ALL", StringComparison.OrdinalIgnoreCase)) {
                targetHdr = monitors.Any(m => m.IsHdrEnabled);
            } else if (string.Equals(_config.TargetMonitor, "PRIMARY", StringComparison.OrdinalIgnoreCase)) {
                var p = monitors.Find(m => m.IsPrimary);
                targetHdr = p != null && p.IsHdrEnabled;
            } else {
                var match = monitors.Find(m => string.Equals(m.DeviceName, _config.TargetMonitor, StringComparison.OrdinalIgnoreCase));
                targetHdr = match != null && match.IsHdrEnabled;
            }

            BadgeHdrWarning.Visibility = targetHdr ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BadgeHdrWarning_MouseDown(object sender, MouseButtonEventArgs e) {
            try {
                Process.Start(new ProcessStartInfo {
                    FileName = "ms-settings:display",
                    UseShellExecute = true
                });
            } catch { }
        }

        private void CmbMonitors_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (_isUpdatingUi) return;
            string? targetId = CmbMonitors.SelectedValue as string;
            if (!string.IsNullOrEmpty(targetId)) {
                SelectTargetMonitor(targetId);
            }
        }

        private void BtnRefreshMonitors_Click(object sender, RoutedEventArgs e) {
            _gammaEngine.BackupInitialRamps();
            InitMonitorsList();
            var list = DisplayManager.GetActiveMonitors();
            TxtStatus.Text = $"已刷新显示器：检测到 {list.Count} 台物理屏幕";
            UpdateTrayMenu();
        }

        private void SelectTargetMonitor(string id) {
            _config.TargetMonitor = id;
            _gammaEngine.SetTargetMonitor(id);
            ConfigManager.Save(_config);

            _isUpdatingUi = true;
            CmbMonitors.SelectedValue = id;
            _isUpdatingUi = false;

            UpdateHdrWarningBadge();
            UpdateTrayMenu();
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

            // 滤镜方案列表
            foreach (var p in _config.Profiles) {
                var prof = p;
                string label = prof.VirtualKey > 0 ? $"{prof.Name} ({prof.KeyName})" : prof.Name;
                var miProf = new MenuItem { Header = label, Foreground = prof.IsActive ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8")) : Brushes.White };
                if (prof.IsActive) miProf.Header = "✓ " + label;
                miProf.Click += (s, e) => ActivateProfile(prof);
                menu.Items.Add(miProf);
            }

            menu.Items.Add(new Separator { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")) });

            // 目标屏幕子菜单
            var miTargetMon = new MenuItem { Header = "🖥️ 目标屏幕", Foreground = Brushes.White };
            var miAll = new MenuItem { Header = "🌐 全部显示器 (所有屏幕同步)", IsChecked = string.Equals(_config.TargetMonitor, "ALL", StringComparison.OrdinalIgnoreCase) };
            miAll.Click += (s, e) => SelectTargetMonitor("ALL");
            miTargetMon.Items.Add(miAll);

            var miPri = new MenuItem { Header = "🖥️ 仅主显示器 (竞技主屏专用)", IsChecked = string.Equals(_config.TargetMonitor, "PRIMARY", StringComparison.OrdinalIgnoreCase) };
            miPri.Click += (s, e) => SelectTargetMonitor("PRIMARY");
            miTargetMon.Items.Add(miPri);

            var monitors = DisplayManager.GetActiveMonitors();
            foreach (var m in monitors) {
                var mon = m;
                var miMon = new MenuItem { Header = mon.Title, IsChecked = string.Equals(_config.TargetMonitor, mon.DeviceName, StringComparison.OrdinalIgnoreCase) };
                miMon.Click += (s, e) => SelectTargetMonitor(mon.DeviceName);
                miTargetMon.Items.Add(miMon);
            }
            menu.Items.Add(miTargetMon);

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

            var miUpdate = new MenuItem { Header = "🔄 检查更新...", Foreground = Brushes.White };
            miUpdate.Click += async (s, e) => {
                ShowWindow();
                await CheckForUpdatesManually();
            };
            menu.Items.Add(miUpdate);

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
            } else {
                DotStatus.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                TxtStatus.Text = "状态：就绪，零延迟底层钩子已激活";
            }
        }

        private void RefreshProfileList() {
            ListProfiles.ItemsSource = null;
            ListProfiles.ItemsSource = _config.Profiles;
        }

        private void SelectProfile(GammaProfile? profile) {
            if (profile == null) return;
            _selectedProfile = profile;
            _isUpdatingUi = true;

            TxtProfileName.Text = profile.Name;
            CmbHotKey.SelectedItem = string.IsNullOrEmpty(profile.KeyName) ? "无" : profile.KeyName;

            SliderBlackEq.Value = profile.BlackEqualizer;
            TxtBlackEqVal.Text = profile.BlackEqualizer.ToString();

            SliderHighlight.Value = profile.HighlightRolloff;
            TxtHighlightVal.Text = profile.HighlightRolloff.ToString();

            UpdateChannelSlidersFromProfile();

            _isUpdatingUi = false;
        }

        private void UpdateChannelSlidersFromProfile() {
            if (_selectedProfile == null) return;
            ColorChannel ch = _channelMode switch {
                1 => _selectedProfile.Red,
                2 => _selectedProfile.Green,
                3 => _selectedProfile.Blue,
                _ => _selectedProfile.Red
            };

            SliderGamma.Value = ch.Gamma;
            TxtGammaVal.Text = (ch.Gamma / 100.0).ToString("F2");

            SliderContrast.Value = ch.Contrast;
            TxtContrastVal.Text = ch.Contrast.ToString();

            SliderBrightness.Value = ch.Brightness;
            TxtBrightnessVal.Text = ch.Brightness.ToString();
        }

        private void RdoChan_Checked(object sender, RoutedEventArgs e) {
            if (_isUpdatingUi) return;
            if (RdoChanAll.IsChecked == true) _channelMode = 0;
            else if (RdoChanRed.IsChecked == true) _channelMode = 1;
            else if (RdoChanGreen.IsChecked == true) _channelMode = 2;
            else if (RdoChanBlue.IsChecked == true) _channelMode = 3;

            _isUpdatingUi = true;
            UpdateChannelSlidersFromProfile();
            _isUpdatingUi = false;
        }

        private void ProfileItem_MouseDown(object sender, MouseButtonEventArgs e) {
            if (sender is Border b && b.DataContext is GammaProfile p) {
                SelectProfile(p);
                ActivateProfile(p);
            }
        }

        private void ActivateProfile(GammaProfile profile) {
            foreach (var p in _config.Profiles) {
                p.IsActive = (p.Id == profile.Id);
            }

            if (profile.VirtualKey == 0x76 && profile.Name.Contains("恢复")) {
                _gammaEngine.ResetToDefault(!_config.EnableTransition);
            } else {
                _gammaEngine.ApplyProfile(profile, !_config.EnableTransition);
            }

            if (_config.EnableOsd) {
                ShowOsd(profile);
            }

            UpdateTrayMenu();
        }

        private void ShowOsd(GammaProfile profile) {
            if (_osdWindow == null) {
                _osdWindow = new OsdWindow();
            }
            string subtitle = $"暗部平衡:{profile.BlackEqualizer}% | Gamma:{(profile.Red.Gamma / 100.0):F2} | 对比:{profile.Red.Contrast}%";
            _osdWindow.ShowMessage(profile.Name, profile.KeyName, subtitle);
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            if (_isUpdatingUi || _selectedProfile == null) return;

            if (sender == SliderBlackEq) {
                _selectedProfile.BlackEqualizer = (int)SliderBlackEq.Value;
                TxtBlackEqVal.Text = _selectedProfile.BlackEqualizer.ToString();
            } else if (sender == SliderHighlight) {
                _selectedProfile.HighlightRolloff = (int)SliderHighlight.Value;
                TxtHighlightVal.Text = _selectedProfile.HighlightRolloff.ToString();
            } else if (sender == SliderGamma) {
                int val = (int)SliderGamma.Value;
                TxtGammaVal.Text = (val / 100.0).ToString("F2");
                ApplyChannelValue(c => c.Gamma = val);
            } else if (sender == SliderContrast) {
                int val = (int)SliderContrast.Value;
                TxtContrastVal.Text = val.ToString();
                ApplyChannelValue(c => c.Contrast = val);
            } else if (sender == SliderBrightness) {
                int val = (int)SliderBrightness.Value;
                TxtBrightnessVal.Text = val.ToString();
                ApplyChannelValue(c => c.Brightness = val);
            }

            _gammaEngine.ApplyProfile(_selectedProfile, true);
        }

        private void ApplyChannelValue(Action<ColorChannel> updateAction) {
            if (_selectedProfile == null) return;
            if (_channelMode == 0) {
                updateAction(_selectedProfile.Red);
                updateAction(_selectedProfile.Green);
                updateAction(_selectedProfile.Blue);
            } else if (_channelMode == 1) {
                updateAction(_selectedProfile.Red);
            } else if (_channelMode == 2) {
                updateAction(_selectedProfile.Green);
            } else if (_channelMode == 3) {
                updateAction(_selectedProfile.Blue);
            }
        }

        private void TxtBlackEqVal_TextChanged(object sender, TextChangedEventArgs e) {
            if (_isUpdatingUi || _selectedProfile == null) return;
            if (int.TryParse(TxtBlackEqVal.Text.Trim(), out int val)) {
                val = Math.Clamp(val, 0, 100);
                _selectedProfile.BlackEqualizer = val;
                _isUpdatingUi = true;
                SliderBlackEq.Value = val;
                _isUpdatingUi = false;
                _gammaEngine.ApplyProfile(_selectedProfile, true);
            }
        }

        private void TxtBlackEqVal_LostFocus(object sender, RoutedEventArgs e) {
            if (_selectedProfile != null) TxtBlackEqVal.Text = _selectedProfile.BlackEqualizer.ToString();
        }

        private void TxtHighlightVal_TextChanged(object sender, TextChangedEventArgs e) {
            if (_isUpdatingUi || _selectedProfile == null) return;
            if (int.TryParse(TxtHighlightVal.Text.Trim(), out int val)) {
                val = Math.Clamp(val, 0, 100);
                _selectedProfile.HighlightRolloff = val;
                _isUpdatingUi = true;
                SliderHighlight.Value = val;
                _isUpdatingUi = false;
                _gammaEngine.ApplyProfile(_selectedProfile, true);
            }
        }

        private void TxtHighlightVal_LostFocus(object sender, RoutedEventArgs e) {
            if (_selectedProfile != null) TxtHighlightVal.Text = _selectedProfile.HighlightRolloff.ToString();
        }

        private void TxtGammaVal_TextChanged(object sender, TextChangedEventArgs e) {
            if (_isUpdatingUi || _selectedProfile == null) return;
            if (double.TryParse(TxtGammaVal.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val)) {
                int g = (int)Math.Clamp(Math.Round(val * 100.0), 100, 320);
                ApplyChannelValue(c => c.Gamma = g);
                _isUpdatingUi = true;
                SliderGamma.Value = g;
                _isUpdatingUi = false;
                _gammaEngine.ApplyProfile(_selectedProfile, true);
            }
        }

        private void TxtGammaVal_LostFocus(object sender, RoutedEventArgs e) {
            if (_selectedProfile != null) TxtGammaVal.Text = (_selectedProfile.Red.Gamma / 100.0).ToString("F2");
        }

        private void TxtContrastVal_TextChanged(object sender, TextChangedEventArgs e) {
            if (_isUpdatingUi || _selectedProfile == null) return;
            if (int.TryParse(TxtContrastVal.Text.Trim(), out int val)) {
                val = Math.Clamp(val, 80, 180);
                ApplyChannelValue(c => c.Contrast = val);
                _isUpdatingUi = true;
                SliderContrast.Value = val;
                _isUpdatingUi = false;
                _gammaEngine.ApplyProfile(_selectedProfile, true);
            }
        }

        private void TxtContrastVal_LostFocus(object sender, RoutedEventArgs e) {
            if (_selectedProfile != null) TxtContrastVal.Text = _selectedProfile.Red.Contrast.ToString();
        }

        private void TxtBrightnessVal_TextChanged(object sender, TextChangedEventArgs e) {
            if (_isUpdatingUi || _selectedProfile == null) return;
            if (int.TryParse(TxtBrightnessVal.Text.Trim(), out int val)) {
                val = Math.Clamp(val, -30, 30);
                ApplyChannelValue(c => c.Brightness = val);
                _isUpdatingUi = true;
                SliderBrightness.Value = val;
                _isUpdatingUi = false;
                _gammaEngine.ApplyProfile(_selectedProfile, true);
            }
        }

        private void TxtBrightnessVal_LostFocus(object sender, RoutedEventArgs e) {
            if (_selectedProfile != null) TxtBrightnessVal.Text = _selectedProfile.Red.Brightness.ToString();
        }

        private void TxtProfileName_TextChanged(object sender, TextChangedEventArgs e) {
            if (_isUpdatingUi || _selectedProfile == null) return;
            _selectedProfile.Name = TxtProfileName.Text;
        }

        private void CmbHotKey_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (_isUpdatingUi || _selectedProfile == null) return;
            string sel = CmbHotKey.SelectedItem as string ?? "无";
            _selectedProfile.KeyName = sel;

            if (sel == "无") {
                _selectedProfile.VirtualKey = 0;
            } else if (sel.Contains("Mouse 4")) {
                _selectedProfile.VirtualKey = HotKeyManager.VK_XBUTTON1;
            } else if (sel.Contains("Mouse 5")) {
                _selectedProfile.VirtualKey = HotKeyManager.VK_XBUTTON2;
            } else if (sel.StartsWith("F") && int.TryParse(sel.Substring(1), out int fNum)) {
                _selectedProfile.VirtualKey = (uint)(0x6F + fNum);
            } else if (sel.StartsWith("数字 ") && int.TryParse(sel.Substring(3), out int dNum)) {
                _selectedProfile.VirtualKey = (uint)(0x30 + dNum);
            }

            ReRegisterHotKeys();
        }

        private void BtnAddProfile_Click(object sender, RoutedEventArgs e) {
            var newProf = new GammaProfile {
                Name = $"自定义滤镜 {_config.Profiles.Count + 1}",
                KeyName = "无",
                VirtualKey = 0,
                BlackEqualizer = 20,
                HighlightRolloff = 30
            };
            newProf.Red.Gamma = 220; newProf.Red.Contrast = 135; newProf.Red.Brightness = -5;
            newProf.Green.Gamma = 220; newProf.Green.Contrast = 135; newProf.Green.Brightness = -5;
            newProf.Blue.Gamma = 220; newProf.Blue.Contrast = 135; newProf.Blue.Brightness = -5;

            _config.Profiles.Add(newProf);
            RefreshProfileList();
            SelectProfile(newProf);
            ConfigManager.Save(_config);
            UpdateTrayMenu();
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e) {
            if (sender is Button b && b.Tag is GammaProfile p) {
                if (p.IsReadOnly) {
                    MessageBox.Show("系统默认预设不能删除。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _config.Profiles.Remove(p);
                RefreshProfileList();
                SelectProfile(_config.Profiles.FirstOrDefault());
                ConfigManager.Save(_config);
                ReRegisterHotKeys();
                UpdateTrayMenu();
            }
        }

        private void BtnApplyCurrent_Click(object sender, RoutedEventArgs e) {
            if (_selectedProfile != null) ActivateProfile(_selectedProfile);
        }

        private void BtnResetDefault_Click(object sender, RoutedEventArgs e) {
            foreach (var p in _config.Profiles) p.IsActive = false;
            _gammaEngine.ResetToDefault(!_config.EnableTransition);
            if (_config.EnableOsd) {
                _osdWindow ??= new OsdWindow();
                _osdWindow.ShowMessage("屏幕色彩已复原", "默认", "恢复标准显示器硬件设定");
            }
            UpdateTrayMenu();
        }

        private void BtnSaveConfig_Click(object sender, RoutedEventArgs e) {
            _config.EnableOsd = ChkOsd.IsChecked ?? true;
            _config.EnableTransition = ChkTransition.IsChecked ?? true;
            _config.AntiResetLock = ChkAntiReset.IsChecked ?? true;
            _gammaEngine.AntiResetEnabled = _config.AntiResetLock;

            ConfigManager.Save(_config);
            TxtStatus.Text = "配置已保存至本地 config.json";
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void ShowWindow() {
            this.Show();
            if (this.WindowState == WindowState.Minimized) {
                this.WindowState = WindowState.Normal;
            }
            this.Activate();
            this.Topmost = true;
            this.Topmost = false;
            this.Focus();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
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

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e) {
            await CheckForUpdatesManually();
        }

        private async Task CheckForUpdatesManually() {
            BtnCheckUpdate.IsEnabled = false;
            BtnCheckUpdate.Content = "⏳ 检查中...";
            try {
                var updateInfo = await UpdateService.CheckForUpdatesAsync();
                if (updateInfo == null) {
                    MessageBox.Show("未能连接到更新服务器，请检查网络或稍后重试。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (updateInfo.HasUpdate) {
                    ShowUpdateDialog(updateInfo);
                } else {
                    MessageBox.Show($"当前已是最新版本 (v{UpdateService.CurrentVersion})，无需更新！", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            } catch (Exception ex) {
                MessageBox.Show($"检查更新发生异常: {ex.Message}", "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            } finally {
                BtnCheckUpdate.IsEnabled = true;
                BtnCheckUpdate.Content = "🔄 检查更新";
            }
        }

        private void ShowUpdateDialog(UpdateInfo updateInfo) {
            var updateWnd = new Views.UpdateWindow(updateInfo) {
                Owner = this
            };
            updateWnd.ShowDialog();
        }
    }
}
