using System;
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
        public const string CurrentVersion = "1.0.2";
        private const string RepoOwner = "eliauk-wzy";
        private const string RepoName = "ButterflyGammaPro";

        // 多端点网络加速（包含官方 API 与国内加速镜像节点，彻底解决 DNS 污染与 Rate Limit）
        private static readonly string[] Endpoints = new[] {
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest",
            $"https://fastly.jsdelivr.net/gh/{RepoOwner}/{RepoName}@main/release_info.json",
            $"https://mirror.ghproxy.com/https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/main/release_info.json"
        };

        public static async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default) {
            foreach (var url in Endpoints) {
                try {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(8);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("ButterflyGammaPro-AutoUpdater/1.0");

                    var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;

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
                        if (remoteVer > curVer) hasUpdate = true;
                    }

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

                            if (isLightweight && (aName.Contains("极速轻巧版") || aName.Contains("Lightweight"))) {
                                downloadUrl = aUrl;
                                downloadFileName = aName;
                                fileSize = aSize;
                                break;
                            } else if (!isLightweight && (aName.Contains("小蝴蝶滤镜_Pro.exe") || aName.Contains("Standalone"))) {
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
                } catch { }
            }
            return null;
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

            // 支持镜像多节点下载回退
            string[] downloadUrls = new[] {
                update.DownloadUrl,
                $"https://mirror.ghproxy.com/{update.DownloadUrl}"
            };

            bool downloadSuccess = false;
            foreach (var dUrl in downloadUrls) {
                try {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromMinutes(4);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("ButterflyGammaPro-AutoUpdater/1.0");

                    using var response = await client.GetAsync(dUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;

                    long? totalBytes = response.Content.Headers.ContentLength;
                    if (totalBytes == null || totalBytes <= 0) totalBytes = update.FileSize > 0 ? update.FileSize : null;

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
                    downloadSuccess = true;
                    break;
                } catch { }
            }

            if (!downloadSuccess) {
                throw new IOException("无法从任何下载节点拉取更新，请点击【浏览器打开】手动更新。");
            }

            progress.Report(1.0);

            // 彻底告别高危 .bat 脚本与杀毒软件误报！
            // 由新版本 exe 自带的 --replace-old 参数接管安全无损的原地自替换
            if (tempDownloadedFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
                int currentPid = Process.GetCurrentProcess().Id;
                var psi = new ProcessStartInfo {
                    FileName = tempDownloadedFile,
                    Arguments = $"--replace-old \"{currentExePath}\" --wait-pid {currentPid}",
                    UseShellExecute = true
                };

                Process.Start(psi);

                Application.Current.Dispatcher.Invoke(() => {
                    Application.Current.Shutdown();
                });
            } else {
                Process.Start(new ProcessStartInfo {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{tempDownloadedFile}\"",
                    UseShellExecute = true
                });
            }
        }
    }
}
