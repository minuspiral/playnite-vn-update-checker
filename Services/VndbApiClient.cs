using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VNUpdateChecker.Services
{
    public class VndbReleaseInfo
    {
        public string LatestPatchVersion { get; set; }
        public DateTime? LatestPatchDate { get; set; }
        public int PatchCount { get; set; }
    }

    public class VndbApiClient
    {
        private static readonly HttpClient HttpClient;
        private static readonly Regex VndbIdRegex = new Regex(@"vndb\.org/v(\d+)", RegexOptions.Compiled);
        private static readonly Regex EgsUrlRegex = new Regex(
            @"erogamescape\.dyndns\.org[^""]*game\.php\?game=(\d+)", RegexOptions.Compiled);
        private static readonly Regex PatchReleaseRegex = new Regex(
            @"""patch""\s*:\s*true", RegexOptions.Compiled);
        private static readonly Regex VersionRegex = new Regex(
            @"""version""\s*:\s*""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex ReleasedRegex = new Regex(
            @"""released""\s*:\s*""(\d{4}-\d{2}-\d{2})""", RegexOptions.Compiled);
        private static DateTime _lastRequestTime = DateTime.MinValue;
        private static readonly object _rateLimitLock = new object();
        private const int RateLimitMs = 1500;

        private readonly ILogger _logger;

        static VndbApiClient()
        {
            HttpClient = new HttpClient();
            HttpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            HttpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        public VndbApiClient(ILogger logger)
        {
            _logger = logger;
        }

        public static int? ExtractVndbIdFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var match = VndbIdRegex.Match(url);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int id))
                return id;
            return null;
        }

        /// <summary>
        /// VNDB release APIからextlinksを取得し、批評空間のゲームIDを返す
        /// </summary>
        public async Task<int?> GetErogameScapeIdAsync(int vndbId, CancellationToken ct = default)
        {
            try
            {
                await RateLimitAsync(ct);

                // release APIでVNに紐づくリリースのextlinksを取得
                var requestBody =
                    $"{{\"filters\":[\"vn\",\"=\",[\"id\",\"=\",\"v{vndbId}\"]],\"fields\":\"extlinks.url\",\"results\":25}}";

                _logger.Info($"VNDB release API呼び出し: v{vndbId}");

                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                var response = await HttpClient.PostAsync("https://api.vndb.org/kana/release", content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    _logger.Warn($"VNDB API エラー ({response.StatusCode}): {errorBody}");
                    return null;
                }

                var responseJson = await response.Content.ReadAsStringAsync();

                // レスポンスからerogamescape URLを正規表現で抽出
                var match = EgsUrlRegex.Match(responseJson);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int egsId))
                {
                    _logger.Info($"VNDB v{vndbId} → 批評空間ID {egsId}");
                    return egsId;
                }

                _logger.Info($"VNDB v{vndbId}: 批評空間リンクなし");
                return null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn($"VNDB API呼び出しエラー (v{vndbId}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// VNDB release APIからパッチリリース情報を取得する
        /// </summary>
        public async Task<VndbReleaseInfo> GetPatchReleasesAsync(int vndbId, CancellationToken ct = default)
        {
            try
            {
                await RateLimitAsync(ct);

                // patch=trueのリリースを取得（バージョンとリリース日付も）
                var requestBody =
                    $"{{\"filters\":[\"and\",[\"vn\",\"=\",[\"id\",\"=\",\"v{vndbId}\"]],[\"patch\",\"=\",true]],\"fields\":\"version,released\",\"sort\":\"released\",\"reverse\":true,\"results\":10}}";

                _logger.Info($"VNDB patch release API呼び出し: v{vndbId}");

                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                var response = await HttpClient.PostAsync("https://api.vndb.org/kana/release", content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    _logger.Warn($"VNDB patch API エラー ({response.StatusCode}): {errorBody}");
                    return null;
                }

                var responseJson = await response.Content.ReadAsStringAsync();

                // "more":false,"results":[] の場合はパッチなし
                if (responseJson.Contains("\"results\":[]") || responseJson.Contains("\"results\": []"))
                {
                    _logger.Info($"VNDB v{vndbId}: パッチリリースなし");
                    return null;
                }

                var info = new VndbReleaseInfo();

                // 各リリースからバージョンとリリース日を抽出
                // レスポンスは released降順なので最初のマッチが最新
                var versionMatches = VersionRegex.Matches(responseJson);
                var releasedMatches = ReleasedRegex.Matches(responseJson);

                // パッチ数をカウント（"id":"rXXXX" の数）
                info.PatchCount = Regex.Matches(responseJson, @"""id""\s*:\s*""r\d+""").Count;

                // 最新のバージョン文字列を取得（空でないもの）
                foreach (Match vm in versionMatches)
                {
                    var ver = vm.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(ver))
                    {
                        info.LatestPatchVersion = ver;
                        break;
                    }
                }

                // 最新のリリース日
                if (releasedMatches.Count > 0)
                {
                    var dateStr = releasedMatches[0].Groups[1].Value;
                    if (DateTime.TryParse(dateStr, out var dt))
                    {
                        info.LatestPatchDate = dt;
                    }
                }

                if (info.PatchCount > 0)
                {
                    _logger.Info($"VNDB v{vndbId}: パッチ{info.PatchCount}件, 最新ver={info.LatestPatchVersion ?? "不明"}, 日付={info.LatestPatchDate?.ToString("yyyy-MM-dd") ?? "不明"}");
                }

                return info.PatchCount > 0 ? info : null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn($"VNDB patch API呼び出しエラー (v{vndbId}): {ex.Message}");
                return null;
            }
        }

        private async Task RateLimitAsync(CancellationToken ct)
        {
            TimeSpan delay;
            lock (_rateLimitLock)
            {
                var elapsed = DateTime.UtcNow - _lastRequestTime;
                var waitMs = elapsed.TotalMilliseconds < RateLimitMs
                    ? RateLimitMs - (int)elapsed.TotalMilliseconds
                    : 0;
                delay = waitMs > 0 ? TimeSpan.FromMilliseconds(waitMs) : TimeSpan.Zero;
                _lastRequestTime = DateTime.UtcNow + delay;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
        }
    }
}
