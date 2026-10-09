using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using System.Windows.Forms;
using SafeMonitor.src.Core;

using System.IO;
using System.IO.Compression;

namespace SafeMonitor
{
    /// <summary>
    /// SafeMonitor 自动更新模块
    /// - 仅从 GitHub Releases (YourKlc/SafeMonitor) 检测与下载
    /// - 使用 GitHub Releases API 获取最新版本，无需任何镜像站
    /// - 使用默认证书校验（不再信任任意证书）
    /// - ZIP 下载完成后，主程序抢先更新 Updater.exe，防止自更新死锁
    /// - CheckAsync() 可被右键菜单直接调用
    /// </summary>
    public static class UpdateChecker
    {
        private const string RepoOwner = "YourKlc";
        private const string RepoName = "SafeMonitor";
        private const string LatestReleaseApi =
            "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";
        private const string RepoPageUrl =
            "https://github.com/" + RepoOwner + "/" + RepoName;

        // 全局 HttpClient（使用默认证书校验）
        private static readonly HttpClient http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
            // GitHub API 要求携带 User-Agent
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"{RepoName}-Updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.Timeout = TimeSpan.FromSeconds(10);
            return client;
        }

        /// <summary>
        /// 缓存最新版本信息，供菜单等处使用
        /// </summary>
        public static (string latest, string changelog, string releaseDate)? LatestVersionInfo { get; private set; }

        /// <summary>
        /// 是否发现了新版本
        /// </summary>
        public static bool IsUpdateFound => LatestVersionInfo != null;

        // ========================================================
        // 主入口：检查更新
        // ========================================================
        public static async Task CheckAsync(bool showMessage = false)
        {
            try
            {
                var info = await GetLatestReleaseAsync();
                if (info == null)
                {
                    if (showMessage)
                    {
                        ShowInfo("无法连接到更新服务器，请稍后重试。", "Unable to connect to update server, please try again later.",
                            "检查更新", "Update Check", MessageBoxIcon.Warning);
                    }
                    return;
                }

                string latest = info.Value.latest;
                string changelog = info.Value.changelog;
                string releaseDate = info.Value.releaseDate;
                string downloadUrl = info.Value.downloadUrl;
                string current = GetCurrentVersion();

                if (IsNewer(latest, current))
                {
                    LatestVersionInfo = (latest, changelog, releaseDate);

                    // 预检查：非手动触发且未开启自动检查时，只记录不弹窗
                    var settings = Settings.Load();
                    if (!showMessage && !settings.AutoCheckUpdate) return;

                    if (string.IsNullOrEmpty(downloadUrl))
                    {
                        // Release 中没有可用的 win-x64 压缩包
                        ShowInfo("未在最新 Release 中找到 Windows 安装包。", "No Windows package found in the latest release.",
                            "检查更新", "Update Check", MessageBoxIcon.Warning);
                        return;
                    }

                    bool isZh = settings?.Language?.ToLower() == "zh";
                    var context = new DownloadContext
                    {
                        Title = isZh ? "发现新版本！" : "New Version!",
                        VersionLabel = $"⚡️{RepoName}_v{latest}",
                        Description = $"更新日志：\n{changelog} \n更新日期：\n{releaseDate}\n\nGitHub：{RepoPageUrl}",
                        Urls = new[] { downloadUrl },
                        SavePath = Path.Combine(AppContext.BaseDirectory, "resources", "update.zip"),
                        ActionButtonText = "Update",
                        AutoExitOnSuccess = true
                    };

                    new UpdateDialog(context, settings).ShowDialog();
                }
                else
                {
                    LatestVersionInfo = null;
                    if (showMessage)
                    {
                        ShowInfo($"当前已是最新版本 ：v{current}\n发布日期：{releaseDate}",
                            $"Already the latest version: v{current}\nRelease date: {releaseDate}",
                            "检查更新", "Update Check", MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[UpdateChecker] Error: " + ex.Message);
                if (showMessage)
                {
                    ShowInfo("检查更新失败，可能是网络问题。", "Update check failed, possibly due to network issues.",
                        "检查更新失败", "Update Check Failed", MessageBoxIcon.Warning);
                }
            }
        }

        // ========================================================
        // 从 GitHub Releases API 获取最新版本
        // ========================================================
        private static async Task<(string latest, string changelog, string releaseDate, string downloadUrl)?> GetLatestReleaseAsync()
        {
            try
            {
                using var resp = await http.GetAsync(LatestReleaseApi);
                if (!resp.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[Update] GitHub API 返回状态码：{(int)resp.StatusCode}");
                    return null;
                }

                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tag = root.GetProperty("tag_name").GetString() ?? "";
                string latest = tag.TrimStart('v', 'V');

                string changelog = root.TryGetProperty("body", out var bodyEl) ? (bodyEl.GetString() ?? "") : "";

                string releaseDate = "";
                if (root.TryGetProperty("published_at", out var dateEl))
                {
                    string raw = dateEl.GetString() ?? "";
                    if (DateTime.TryParse(raw, out var dt)) releaseDate = dt.ToString("yyyy-MM-dd");
                    else releaseDate = raw;
                }

                // 在 assets 中查找 win-x64 压缩包
                string downloadUrl = "";
                if (root.TryGetProperty("assets", out var assetsEl))
                {
                    foreach (var asset in assetsEl.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            name.IndexOf("win-x64", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            break;
                        }
                    }
                }

                return (latest, changelog, releaseDate, downloadUrl);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update] 获取最新 Release 失败 -> {ex.Message}");
                return null;
            }
        }

        private static bool IsNewer(string latest, string current)
        {
            if (Version.TryParse(latest, out var lv) && Version.TryParse(current, out var cv))
                return lv > cv;
            // 版本号解析失败时，按字符串不等视为有新版本
            return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
        }

        private static void ShowInfo(string zh, string en, string zhTitle, string enTitle, MessageBoxIcon icon)
        {
            bool isZh = LanguageManager.CurrentLang == "zh";
            MessageBox.Show(isZh ? zh : en, isZh ? zhTitle : enTitle, MessageBoxButtons.OK, icon);
        }

        // ========================================================
        // 获取当前版本号
        // ========================================================
        public static string GetCurrentVersion()
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (string.IsNullOrWhiteSpace(version))
                version = Application.ProductVersion;

            int plusIndex = version.IndexOf('+');
            if (plusIndex > 0)
                version = version.Substring(0, plusIndex);

            return version;
        }

        // ========================================================
        // 主程序抢先更新 Updater.exe (解决自更新死锁)
        // ========================================================
        public static string? PreUpdateUpdater(string zipPath)
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string resourcesDir = Path.Combine(baseDir, "resources");

                if (!Directory.Exists(resourcesDir)) Directory.CreateDirectory(resourcesDir);

                // 杀掉所有残留的 Updater 进程 (防止占用)
                string[] updaterNames = { "Updater", "SafeMonitor.Updater" };
                foreach (var name in updaterNames)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            if (p.MainModule != null &&
                                p.MainModule.FileName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                            {
                                p.Kill();
                            }
                        }
                        catch { }
                    }
                }

                System.Threading.Thread.Sleep(200);

                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    var entry = archive.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("SafeMonitor.Updater.exe", StringComparison.OrdinalIgnoreCase));

                    if (entry == null)
                    {
                        entry = archive.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith("Updater.exe", StringComparison.OrdinalIgnoreCase));
                    }

                    if (entry != null)
                    {
                        string fileName = Path.GetFileName(entry.FullName);
                        string targetPath = Path.Combine(resourcesDir, fileName);
                        entry.ExtractToFile(targetPath, true);
                        Debug.WriteLine($"[UpdateChecker] Updater 预更新成功: {targetPath}");
                        return targetPath;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateChecker] Updater 预更新失败: {ex.Message}");
            }

            return null;
        }
    }
}
