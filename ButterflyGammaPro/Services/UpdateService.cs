using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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
        public bool IsZipArchive { get; set; }
    }

    public static class UpdateService {
        // 动态读取当前程序集版本号，彻底避免版本号硬编码偏差
        public static string CurrentVersion {
            get {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.4";
            }
        }

        private const string RepoOwner = "eliauk-wzy";
        private const string RepoName = "ButterflyGammaPro";

        // 多端点网络加速（包含官方 API 与国内主流加速镜像节点）
        private static readonly string[] Endpoints = new[] {
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest",
            $"https://mirror.ghproxy.com/https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest",
            $"https://ghproxy.net/https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest",
            $"https://fastly.jsdelivr.net/gh/{RepoOwner}/{RepoName}@main/release_info.json"
        };

        // 国内高速下载镜像加速前缀
        private static readonly string[] DownloadMirrors = new[] {
            "https://mirror.ghproxy.com/",
            "https://ghproxy.net/",
            "https://gh-proxy.com/",
            "" // 直连兜底
        };

        public static async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default) {
            foreach (var url in Endpoints) {
                try {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(6);
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
                    bool isLightweight = currentExeName.Contains("极速轻巧版", StringComparison.OrdinalIgnoreCase) ||
                                         currentExeName.Contains("Lightweight", StringComparison.OrdinalIgnoreCase);

                    string downloadUrl = "";
                    string downloadFileName = "";
                    long fileSize = 0;
                    bool isZip = false;

                    if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array) {
                        string zipUrl = "";
                        string zipName = "";
                        long zipSize = 0;

                        string anyExeUrl = "";
                        string anyExeName = "";
                        long anyExeSize = 0;

                        foreach (var asset in assetsElem.EnumerateArray()) {
                            string aName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            string aUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                            long aSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

                            if (isLightweight) {
                                if (aName.Contains("极速轻巧版", StringComparison.OrdinalIgnoreCase) ||
                                    aName.Contains("Lightweight", StringComparison.OrdinalIgnoreCase)) {
                                    downloadUrl = aUrl;
                                    downloadFileName = aName;
                                    fileSize = aSize;
                                    break;
                                }
                            } else {
                                if (aName.Contains("小蝴蝶滤镜_Pro.exe", StringComparison.OrdinalIgnoreCase) ||
                                    aName.Contains("Standalone", StringComparison.OrdinalIgnoreCase) ||
                                    (aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !aName.Contains("Lightweight") && !aName.Contains("极速轻巧版"))) {
                                    downloadUrl = aUrl;
                                    downloadFileName = aName;
                                    fileSize = aSize;
                                    break;
                                }
                            }

                            if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
                                zipUrl = aUrl;
                                zipName = aName;
                                zipSize = aSize;
                            } else if (aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(anyExeUrl)) {
                                anyExeUrl = aUrl;
                                anyExeName = aName;
                                anyExeSize = aSize;
                            }
                        }

                        // 核心突破：若未提供专属直链 exe，但有 zip 压缩包，软件内部自动下载 zip 并在后台静默解压替换！
                        if (string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(zipUrl)) {
                            downloadUrl = zipUrl;
                            downloadFileName = zipName;
                            fileSize = zipSize;
                            isZip = true;
                        } else if (string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(anyExeUrl)) {
                            downloadUrl = anyExeUrl;
                            downloadFileName = anyExeName;
                            fileSize = anyExeSize;
                        }
                    }

                    // 兜底保护：若 release assets 暂缺任何文件，自动采用 GitHub 官方源码/归档 zip
                    if (string.IsNullOrEmpty(downloadUrl)) {
                        downloadUrl = $"https://github.com/{RepoOwner}/{RepoName}/archive/refs/tags/{tagName}.zip";
                        downloadFileName = $"{RepoName}-{tagName}.zip";
                        isZip = true;
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
                        FileSize = fileSize,
                        IsZipArchive = isZip
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
            bool isLightweight = targetFileName.Contains("极速轻巧版", StringComparison.OrdinalIgnoreCase) ||
                                 targetFileName.Contains("Lightweight", StringComparison.OrdinalIgnoreCase);

            string tempDownloadedFile = Path.Combine(tempDir, string.IsNullOrEmpty(update.DownloadFileName) ? targetFileName : update.DownloadFileName);

            // 构建国内多镜像快速下载降级队列
            var candidateUrls = DownloadMirrors
                .Select(m => string.IsNullOrEmpty(m) ? update.DownloadUrl : (m.EndsWith("/") ? m + update.DownloadUrl : m + "/" + update.DownloadUrl))
                .Distinct()
                .ToList();

            bool downloadSuccess = false;
            foreach (var dUrl in candidateUrls) {
                try {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromMinutes(5);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("ButterflyGammaPro-AutoUpdater/1.0");

                    using var response = await client.GetAsync(dUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;

                    long? totalBytes = response.Content.Headers.ContentLength;
                    if (totalBytes == null || totalBytes <= 0) totalBytes = update.FileSize > 0 ? update.FileSize : null;

                    using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var fileStream = new FileStream(tempDownloadedFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                    var buffer = new byte[32768];
                    long totalRead = 0;
                    int read;

                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0) {
                        await fileStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        totalRead += read;
                        if (totalBytes.HasValue && totalBytes.Value > 0) {
                            double p = ((double)totalRead / totalBytes.Value) * 0.90; // 下载占 90%
                            progress.Report(Math.Clamp(p, 0.0, 0.90));
                        }
                    }
                    downloadSuccess = true;
                    break;
                } catch { }
            }

            if (!downloadSuccess) {
                throw new IOException("无法从任何加速镜像节点下载新版，请检查您的网络连接后重试。");
            }

            progress.Report(0.92);

            string finalExeToLaunch = tempDownloadedFile;

            // 核心突破：若下载的是 zip 压缩包，软件在内部自动无缝解压，提取出与当前版本匹配的 exe 进行热替换！
            if (tempDownloadedFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
                progress.Report(0.95);
                string extractDir = Path.Combine(tempDir, "Extracted_" + DateTime.Now.Ticks);
                Directory.CreateDirectory(extractDir);

                await Task.Run(() => {
                    ZipFile.ExtractToDirectory(tempDownloadedFile, extractDir, true);
                }, ct).ConfigureAwait(false);

                var allExes = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories);
                string matchedExe = "";
                if (isLightweight) {
                    matchedExe = allExes.FirstOrDefault(f => Path.GetFileName(f).Contains("极速轻巧版", StringComparison.OrdinalIgnoreCase) ||
                                                             Path.GetFileName(f).Contains("Lightweight", StringComparison.OrdinalIgnoreCase)) ?? "";
                } else {
                    matchedExe = allExes.FirstOrDefault(f => Path.GetFileName(f).Contains("小蝴蝶滤镜_Pro.exe", StringComparison.OrdinalIgnoreCase) ||
                                                             (!Path.GetFileName(f).Contains("轻巧", StringComparison.OrdinalIgnoreCase) &&
                                                              !Path.GetFileName(f).Contains("Lightweight", StringComparison.OrdinalIgnoreCase))) ?? "";
                }

                if (string.IsNullOrEmpty(matchedExe) && allExes.Length > 0) {
                    matchedExe = allExes[0];
                }

                if (string.IsNullOrEmpty(matchedExe)) {
                    throw new FileNotFoundException("更新压缩包中未检测到可执行程序，无法完成自动安装。");
                }

                finalExeToLaunch = matchedExe;
            }

            progress.Report(1.0);

            // 彻底告别高危 .bat 脚本与杀毒软件误报！
            // 由新版本 exe 自带的 --replace-old 参数接管安全无损的原地自替换并自启动
            int currentPid = Process.GetCurrentProcess().Id;
            var psi = new ProcessStartInfo {
                FileName = finalExeToLaunch,
                Arguments = $"--replace-old \"{currentExePath}\" --wait-pid {currentPid}",
                UseShellExecute = true
            };

            Process.Start(psi);

            Application.Current.Dispatcher.Invoke(() => {
                Application.Current.Shutdown();
            });
        }
    }
}
