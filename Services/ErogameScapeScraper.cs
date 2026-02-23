using VNUpdateChecker.Models;
using HtmlAgilityPack;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VNUpdateChecker.Services
{
    public class ErogameScapeScraper
    {
        private static readonly HttpClient HttpClient;
        private static DateTime _lastRequestTime = DateTime.MinValue;
        private static readonly object _rateLimitLock = new object();
        private const int RateLimitMs = 2500;

        private readonly ILogger _logger;

        private static readonly Regex VersionRegex = new Regex(
            @"(?:ver\.?\s*|v)(\d+\.\d+(?:\.\d+)*)|(\d+\.\d+\.\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TimestampRegex = new Regex(
            @"(\d{4})年(\d{2})月(\d{2})日(\d{2})時(\d{2})分(\d{2})秒",
            RegexOptions.Compiled);

        private static readonly Regex UrlRegex = new Regex(
            @"https?://[^\s<>""']+",
            RegexOptions.Compiled);

        private static readonly Regex PatchUserCountRegex = new Regex(
            @"他(\d+)人", RegexOptions.Compiled);

        static ErogameScapeScraper()
        {
            HttpClient = new HttpClient();
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            HttpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        public ErogameScapeScraper(ILogger logger)
        {
            _logger = logger;
        }

        public static string GetGameUrl(int gameId)
        {
            return $"https://erogamescape.dyndns.org/~ap2/ero/toukei_kaiseki/game.php?game={gameId}";
        }

        public static int? ExtractGameIdFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            var match = Regex.Match(url, @"game\.php\?game=(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int id))
            {
                return id;
            }
            return null;
        }

        public async Task<ScrapingResult> ScrapeGamePageAsync(int gameId, CancellationToken ct = default)
        {
            var result = new ScrapingResult { GameId = gameId };

            try
            {
                await RateLimitAsync(ct);

                var url = GetGameUrl(gameId);
                _logger.Info($"批評空間ページ取得中: {url}");

                var response = await HttpClient.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                var html = await response.Content.ReadAsStringAsync();

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // 公式サイトURL抽出（game_OHP → brand_OHP フォールバック）
                // ページ全体から取得するのでパッチセクションの有無に関係なく取得
                var ohpLink = doc.DocumentNode.SelectSingleNode("//a[text()='game_OHP']");
                if (ohpLink != null)
                {
                    result.OfficialSiteUrl = ohpLink.GetAttributeValue("href", null);
                }
                if (string.IsNullOrEmpty(result.OfficialSiteUrl))
                {
                    var brandLink = doc.DocumentNode.SelectSingleNode("//a[text()='brand_OHP']");
                    if (brandLink != null)
                    {
                        result.OfficialSiteUrl = brandLink.GetAttributeValue("href", null);
                    }
                }

                var patchDiv = doc.DocumentNode.SelectSingleNode("//div[@id='patch_information']");
                if (patchDiv == null)
                {
                    result.HasPatchSection = false;
                    return result;
                }

                result.HasPatchSection = true;

                var mainDiv = patchDiv.SelectSingleNode(".//div[@id='patch_information_main']");
                if (mainDiv != null)
                {
                    var boldRedSpan = mainDiv.SelectSingleNode(".//span[contains(@class,'bold') and contains(@class,'red')]");
                    result.HasPatchIndicator = boldRedSpan != null &&
                        boldRedSpan.InnerText.Contains("修正ファイルがあります");

                    // 登録ユーザー数を抽出（"登録ユーザー : name1 , name2 , 他XX人"）
                    var mainText = HtmlEntity.DeEntitize(mainDiv.InnerText);
                    var userCountMatch = PatchUserCountRegex.Match(mainText);
                    if (userCountMatch.Success && int.TryParse(userCountMatch.Groups[1].Value, out int otherCount))
                    {
                        // "他XX人" の前にカンマ区切りで名前が列挙されている
                        var regUserIdx = mainText.IndexOf("登録ユーザー");
                        if (regUserIdx >= 0)
                        {
                            var afterLabel = mainText.Substring(regUserIdx);
                            var commaCount = afterLabel.Split(',').Length - 1;
                            result.PatchRegisteredUserCount = commaCount + otherCount;
                        }
                        else
                        {
                            result.PatchRegisteredUserCount = otherCount;
                        }
                    }
                    else if (mainText.Contains("登録ユーザー"))
                    {
                        // "他XX人"がない場合（少人数）→ カンマ区切りの名前を数える
                        var regUserIdx = mainText.IndexOf("登録ユーザー");
                        if (regUserIdx >= 0)
                        {
                            var afterLabel = mainText.Substring(regUserIdx);
                            var names = afterLabel.Split(',');
                            result.PatchRegisteredUserCount = names.Length;
                        }
                    }
                }

                var commentDivs = patchDiv.SelectNodes(
                    ".//div[contains(concat(' ',normalize-space(@class),' '),' pov_c_comment ')]");
                if (commentDivs != null)
                {
                    foreach (var commentDiv in commentDivs)
                    {
                        var patch = ParseComment(commentDiv);
                        if (patch != null)
                        {
                            result.Patches.Add(patch);
                        }
                    }
                }

                result.CommentCount = result.Patches.Count;
                _logger.Info($"ゲーム {gameId}: パッチセクションあり, コメント数={result.CommentCount}");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Error(ex, $"批評空間スクレイピングエラー (game={gameId})");
                result.Error = ex.Message;
            }

            return result;
        }

        private PatchInfo ParseComment(HtmlNode commentDiv)
        {
            try
            {
                var patch = new PatchInfo();

                var bodyDiv = commentDiv.SelectSingleNode(".//div[contains(@class,'pov_c_body')]");
                if (bodyDiv == null) return null;

                var rawText = HtmlEntity.DeEntitize(bodyDiv.InnerText).Trim();
                var rawHtml = bodyDiv.InnerHtml;
                patch.RawComment = rawText;

                // Rating抽出 (A/B/C等、先頭のspan.red.boldの内容)
                var ratingSpan = bodyDiv.SelectSingleNode(".//span[contains(@class,'red') and contains(@class,'bold')]");
                if (ratingSpan != null)
                {
                    patch.Rating = ratingSpan.InnerText.Trim();
                }

                // バージョン番号抽出
                var versionMatch = VersionRegex.Match(rawText);
                if (versionMatch.Success)
                {
                    patch.Version = versionMatch.Groups[1].Success
                        ? versionMatch.Groups[1].Value
                        : versionMatch.Groups[2].Value;
                }

                // URL抽出
                var urlMatches = UrlRegex.Matches(rawHtml);
                foreach (Match m in urlMatches)
                {
                    patch.Urls.Add(m.Value.TrimEnd(')', '"', '\'', ';'));
                }

                // href属性からもURL抽出
                var links = bodyDiv.SelectNodes(".//a[@href]");
                if (links != null)
                {
                    foreach (var link in links)
                    {
                        var href = link.GetAttributeValue("href", "");
                        if (!string.IsNullOrEmpty(href) && href.StartsWith("http") && !patch.Urls.Contains(href))
                        {
                            patch.Urls.Add(href);
                        }
                    }
                }

                // フッターからタイムスタンプと投稿者を抽出
                var footerDiv = commentDiv.SelectSingleNode(".//div[contains(@class,'pov_c_footer')]");
                if (footerDiv != null)
                {
                    var footerText = HtmlEntity.DeEntitize(footerDiv.InnerText).Trim();
                    var tsMatch = TimestampRegex.Match(footerText);
                    if (tsMatch.Success)
                    {
                        try
                        {
                            patch.Timestamp = new DateTime(
                                int.Parse(tsMatch.Groups[1].Value),
                                int.Parse(tsMatch.Groups[2].Value),
                                int.Parse(tsMatch.Groups[3].Value),
                                int.Parse(tsMatch.Groups[4].Value),
                                int.Parse(tsMatch.Groups[5].Value),
                                int.Parse(tsMatch.Groups[6].Value));
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"タイムスタンプ解析エラー: {ex.Message}");
                        }
                    }

                    // タイムスタンプの後が投稿者名
                    if (tsMatch.Success)
                    {
                        var afterTs = footerText.Substring(tsMatch.Index + tsMatch.Length).Trim();
                        if (!string.IsNullOrEmpty(afterTs))
                        {
                            patch.Author = afterTs;
                        }
                    }
                }

                return patch;
            }
            catch (Exception ex)
            {
                _logger.Warn($"コメント解析エラー: {ex.Message}");
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

    public class ScrapingResult
    {
        public int GameId { get; set; }
        public bool HasPatchSection { get; set; }
        public bool HasPatchIndicator { get; set; }
        public List<PatchInfo> Patches { get; set; } = new List<PatchInfo>();
        public int CommentCount { get; set; }
        public int PatchRegisteredUserCount { get; set; }
        public string OfficialSiteUrl { get; set; }
        public string Error { get; set; }
    }
}
