using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ButterflyGammaPro.Services {
    public class UpdateInfo {
        public bool HasUpdate { get; set; }
        public string RemoteVersion { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string Title { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public DateTime? PublishedAt { get; set; }
        public string HtmlUrl { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string DownloadFileName { get; set; } = "";
        public long FileSize { get; set; }
    }

    public static class UpdateService {
        public const string CurrentVersion = "1.0.1";
        private const string RepoOwner = "eliauk-wzy";
        private const string RepoName = "ButterflyGammaPro";
        private static readonly string ApiLatestUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

        public static async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default) {
            try {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ButterflyGammaPro-AutoUpdater");

                var response = await client.GetAsync(ApiLatestUrl, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) {
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
                string title = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "" : "";
                string body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";
                string htmlUrl = root.TryGetProperty("html_url", out var htmlElem) ? htmlElem.GetString() ?? "" : "";

                DateTime? publishedAt = null;
                if (root.TryGetProperty("published_at", out var pubElem) && pubElem.TryGetDateTime(out var dt)) {
                    publishedAt = dt.ToLocalTime();
                }

                string remoteVerStr = tagName.TrimStart('v', 'V').Trim();
                bool hasUpdate = false;

                if (Version.TryParse(remoteVerStr, out var remoteVer) && Version.TryParse(CurrentVersion, out var curVer)) {
                    if (remoteVer > curVer) {
                        hasUpdate = true;
                    }
                }

                // 寻找匹配当前运行类型的下载文件
                string currentExePath = Environment.ProcessPath ?? "";
                string currentExeName = Path.GetFileName(currentExePath);
                bool isLightweight = currentExeName.Contains("极速轻巧版", StringComparison.OrdinalIgnoreCase);

                string downloadUrl = "";
                string downloadFileName = "";
                long fileSize = 0;

                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array) {
                    string fallbackUrl = "";
                    string fallbackName = "";
                    long fallbackSize = 0;

                    foreach (var asset in assetsElem.EnumerateArray()) {
                        string aName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        string aUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                        long aSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

                        if (isLightweight && aName.Contains("极速轻巧版", StringComparison.OrdinalIgnoreCase)) {
                            downloadUrl = aUrl;
                            downloadFileName = aName;
                            fileSize = aSize;
                            break;
                        } else if (!isLightweight && aName.Contains("小蝴蝶滤镜_Pro.exe", StringComparison.OrdinalIgnoreCase)) {
                            downloadUrl = aUrl;
                            downloadFileName = aName;
                            fileSize = aSize;
                            break;
                        }

                        if (aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(fallbackUrl)) {
                            fallbackUrl = aUrl;
                            fallbackName = aName;
                            fallbackSize = aSize;
                        } else if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(fallbackUrl)) {
                            fallbackUrl = aUrl;
                            fallbackName = aName;
                            fallbackSize = aSize;
                        }
                    }

                    if (string.IsNullOrEmpty(downloadUrl)) {
                        downloadUrl = fallbackUrl;
                        downloadFileName = fallbackName;
                        fileSize = fallbackSize;
                    }
                }

                return new UpdateInfo {
                    HasUpdate = hasUpdate,
                    RemoteVersion = remoteVerStr,
                    CurrentVersion = CurrentVersion,
                    Title = string.IsNullOrWhiteSpace(title) ? $"Butterfly Gamma Pro v{remoteVerStr}" : title,
                    ReleaseNotes = body,
                    PublishedAt = publishedAt,
                    HtmlUrl = htmlUrl,
                    DownloadUrl = downloadUrl,
                    DownloadFileName = downloadFileName,
                    FileSize = fileSize
                };
            } catch {
                return null;
            }
        }

        public static async Task DownloadAndInstallAsync(UpdateInfo update, IProgress<double> progress, CancellationToken ct) {
            if (string.IsNullOrEmpty(update.DownloadUrl)) {
                throw new InvalidOperationException("未找到可用的更新下载包地址。");
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "ButterflyGammaPro_Update");
            Directory.CreateDirectory(tempDir);

            string currentExePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(currentExePath) || !File.Exists(currentExePath)) {
                currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            }

            string targetFileName = Path.GetFileName(currentExePath);
            string tempDownloadedFile = Path.Combine(tempDir, string.IsNullOrEmpty(update.DownloadFileName) ? targetFileName : update.DownloadFileName);

            // 下载新版本
            using (var client = new HttpClient()) {
                client.Timeout = TimeSpan.FromMinutes(5);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ButterflyGammaPro-AutoUpdater");

                using var response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                if (totalBytes == null || totalBytes <= 0) {
                    totalBytes = update.FileSize > 0 ? update.FileSize : null;
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var fileStream = new FileStream(tempDownloadedFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[16384];
                long totalRead = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0) {
                    await fileStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    totalRead += read;
                    if (totalBytes.HasValue && totalBytes.Value > 0) {
                        double p = (double)totalRead / totalBytes.Value;
                        progress.Report(Math.Clamp(p, 0.0, 1.0));
                    }
                }
            }

            progress.Report(1.0);

            // 如果下载的是 .exe，执行热替换重启
            if (tempDownloadedFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
                ExecuteInPlaceRestart(tempDownloadedFile, currentExePath);
            } else {
                // 如果是 zip，打开所在目录
                Process.Start(new ProcessStartInfo {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{tempDownloadedFile}\"",
                    UseShellExecute = true
                });
            }
        }

        private static void ExecuteInPlaceRestart(string tempExe, string targetExe) {
            int currentPid = Process.GetCurrentProcess().Id;
            string updateScriptPath = Path.Combine(Path.GetTempPath(), "ButterflyGammaPro_Update", "apply_update.bat");

            string scriptContent = $@"@echo off
chcp 65001 >nul
set PID={currentPid}
set NEW_EXE=""{tempExe}""
set TARGET_EXE=""{targetExe}""

:wait_loop
tasklist /fi ""PID eq %PID%"" 2>nul | findstr /i ""%PID%"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_loop
)

timeout /t 1 /nobreak >nul

copy /y %NEW_EXE% %TARGET_EXE% >nul 2>&1
if errorlevel 1 (
    move /y %NEW_EXE% %TARGET_EXE% >nul 2>&1
)

start """" %TARGET_EXE%

del %NEW_EXE% >nul 2>&1
(goto) 2>nul & del ""%~f0""
exit
";

            File.WriteAllText(updateScriptPath, scriptContent, System.Text.Encoding.Default);

            var psi = new ProcessStartInfo {
                FileName = updateScriptPath,
                CreateNoWindow = true,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);

            Application.Current.Dispatcher.Invoke(() => {
                Application.Current.Shutdown();
            });
        }
    }
}
