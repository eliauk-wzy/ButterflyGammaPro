using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ButterflyGammaPro.Services;

namespace ButterflyGammaPro.Views {
    public partial class UpdateWindow : Window {
        private readonly UpdateInfo _updateInfo;
        private CancellationTokenSource? _downloadCts;

        public UpdateWindow(UpdateInfo updateInfo) {
            InitializeComponent();
            _updateInfo = updateInfo;

            TxtCurrentVer.Text = $"v{updateInfo.CurrentVersion}";
            TxtRemoteVer.Text = $"v{updateInfo.RemoteVersion}";
            TxtReleaseNotes.Text = string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes) ? "优化程序稳定性与性能体验。" : updateInfo.ReleaseNotes;

            if (updateInfo.PublishedAt.HasValue) {
                TxtPublishDate.Text = updateInfo.PublishedAt.Value.ToString("yyyy-MM-dd HH:mm");
            } else {
                TxtPublishDate.Text = "";
            }
        }

        private void Border_MouseDown(object sender, MouseButtonEventArgs e) {
            if (e.ChangedButton == MouseButton.Left) {
                DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) {
            _downloadCts?.Cancel();
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) {
            _downloadCts?.Cancel();
            Close();
        }

        private void BtnBrowser_Click(object sender, RoutedEventArgs e) {
            string url = !string.IsNullOrEmpty(_updateInfo.HtmlUrl)
                ? _updateInfo.HtmlUrl
                : "https://github.com/eliauk-wzy/ButterflyGammaPro/releases";

            try {
                Process.Start(new ProcessStartInfo {
                    FileName = url,
                    UseShellExecute = true
                });
            } catch (Exception ex) {
                MessageBox.Show($"无法打开网页浏览器: {ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnUpdateNow_Click(object sender, RoutedEventArgs e) {
            BtnUpdateNow.IsEnabled = false;
            BtnCancel.IsEnabled = false;
            PanelProgress.Visibility = Visibility.Visible;
            TxtProgressStatus.Text = "正在连接国内加速节点并拉取新版...";
            PbDownload.Value = 0;

            _downloadCts = new CancellationTokenSource();
            var progress = new Progress<double>(val => {
                Dispatcher.Invoke(() => {
                    PbDownload.Value = Math.Round(val * 100);
                    TxtProgressPercent.Text = $"{(int)PbDownload.Value}%";
                    if (val < 0.90) {
                        if (_updateInfo.FileSize > 0) {
                            double currentMb = (val * _updateInfo.FileSize) / (1024.0 * 1024.0);
                            double totalMb = _updateInfo.FileSize / (1024.0 * 1024.0);
                            TxtProgressStatus.Text = $"正在极速下载... {currentMb:F1}MB / {totalMb:F1}MB";
                        } else {
                            TxtProgressStatus.Text = $"正在极速下载... ({(int)PbDownload.Value}%)";
                        }
                    } else if (val < 0.98) {
                        TxtProgressStatus.Text = "下载完成，正在自动校验解压更新包...";
                    } else {
                        TxtProgressStatus.Text = "解压校验成功，正在原地热替换并自启动...";
                    }
                });
            });

            try {
                await UpdateService.DownloadAndInstallAsync(_updateInfo, progress, _downloadCts.Token);
                TxtProgressStatus.Text = "更新完成，正在自动重启切换至最新版本...";
            } catch (OperationCanceledException) {
                TxtProgressStatus.Text = "更新已取消。";
                BtnUpdateNow.IsEnabled = true;
                BtnCancel.IsEnabled = true;
            } catch (Exception ex) {
                TxtProgressStatus.Text = "自动更新重试失败。";
                MessageBox.Show($"自动更新过程遇到异常: {ex.Message}\n\n若因特定网络阻断，可点击左下方浏览器链接手动获取最新版。", "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                BtnUpdateNow.IsEnabled = true;
                BtnCancel.IsEnabled = true;
            }
        }
    }
}
