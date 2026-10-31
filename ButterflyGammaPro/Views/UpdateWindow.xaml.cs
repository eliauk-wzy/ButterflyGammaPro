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
            if (string.IsNullOrEmpty(_updateInfo.DownloadUrl)) {
                MessageBox.Show("当前更新包尚未提供直接下载资产，请前往 GitHub 网页下载最新版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                BtnBrowser_Click(sender, e);
                return;
            }

            BtnUpdateNow.IsEnabled = false;
            BtnCancel.IsEnabled = false;
            PanelProgress.Visibility = Visibility.Visible;
            TxtProgressStatus.Text = "正在连接更新服务器并下载...";
            PbDownload.Value = 0;

            _downloadCts = new CancellationTokenSource();
            var progress = new Progress<double>(val => {
                Dispatcher.Invoke(() => {
                    PbDownload.Value = Math.Round(val * 100);
                    TxtProgressPercent.Text = $"{(int)PbDownload.Value}%";
                    if (_updateInfo.FileSize > 0) {
                        double currentMb = (val * _updateInfo.FileSize) / (1024.0 * 1024.0);
                        double totalMb = _updateInfo.FileSize / (1024.0 * 1024.0);
                        TxtProgressStatus.Text = $"正在下载新版本... {currentMb:F1}MB / {totalMb:F1}MB";
                    } else {
                        TxtProgressStatus.Text = $"正在下载新版本... ({(int)PbDownload.Value}%)";
                    }
                });
            });

            try {
                await UpdateService.DownloadAndInstallAsync(_updateInfo, progress, _downloadCts.Token);
                TxtProgressStatus.Text = "下载完成，正在自动重启切换至最新版本...";
            } catch (OperationCanceledException) {
                TxtProgressStatus.Text = "下载已取消。";
                BtnUpdateNow.IsEnabled = true;
                BtnCancel.IsEnabled = true;
            } catch (Exception ex) {
                TxtProgressStatus.Text = "更新下载失败，请点击左侧按钮前往 GitHub 手动下载。";
                MessageBox.Show($"自动更新下载遇到问题: {ex.Message}\n\n已为您保留前往 GitHub 网页下载的选项。", "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                BtnUpdateNow.IsEnabled = true;
                BtnCancel.IsEnabled = true;
            }
        }
    }
}
