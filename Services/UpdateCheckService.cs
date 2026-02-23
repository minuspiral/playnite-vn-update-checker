using VNUpdateChecker.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VNUpdateChecker.Services
{
    public class UpdateCheckService
    {
        private static readonly Regex ExeVersionRegex = new Regex(
            @"(\d+\.\d+(?:\.\d+)*)", RegexOptions.Compiled);

        private readonly ErogameScapeScraper _scraper;
        private readonly VndbApiClient _vndbClient;
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly Func<VNUpdateCheckerSettings> _getSettings;

        public UpdateCheckService(IPlayniteAPI api, ILogger logger, Func<VNUpdateCheckerSettings> getSettings)
        {
            _api = api;
            _logger = logger;
            _getSettings = getSettings;
            _scraper = new ErogameScapeScraper(logger);
            _vndbClient = new VndbApiClient(logger);
        }

        public async Task<CheckResult> CheckGameAsync(Game game, bool skipCache = false, CancellationToken ct = default)
        {
            var result = new CheckResult
            {
                GameName = game.Name,
                PlayniteGameId = game.Id
            };

            try
            {
                // ローカルexeバージョンは常に取得（ID解決前に）
                result.LocalVersion = GetLocalExeVersion(game);
                result.LocalPatchFileCount = DetectLocalPatchFiles(game);
                result.OfficialSiteUrl = GetOfficialSiteUrl(game);

                var gameId = GetErogameScapeId(game);

                // VNDBリンクから批評空間IDを自動解決
                if (!gameId.HasValue)
                {
                    gameId = await ResolveErogameScapeIdFromVndbAsync(game, ct);
                }

                if (!gameId.HasValue)
                {
                    if (game.Links != null && game.Links.Count > 0)
                    {
                        var linkUrls = string.Join(", ", game.Links.Select(l => l.Url ?? "(null)"));
                        _logger.Info($"{game.Name}: 批評空間ID未検出。登録リンク: {linkUrls}");
                    }
                    else
                    {
                        _logger.Info($"{game.Name}: 批評空間ID未検出。リンク未登録。");
                    }
                    result.Outcome = CheckOutcome.NoErogameScapeId;
                    return result;
                }

                result.ErogameScapeUrl = ErogameScapeScraper.GetGameUrl(gameId.Value);

                var settings = _getSettings();
                var status = GetOrCreateStatus(settings, game.Id, gameId.Value);

                // ステータスにもローカルバージョンを保存
                status.LocalExeVersion = result.LocalVersion;

                // キャッシュチェック（単体チェック時はスキップして常に最新データ取得）
                if (!skipCache)
                {
                    var cachedResult = TryGetCachedResult(game, settings, status, result);
                    if (cachedResult != null)
                    {
                        return cachedResult;
                    }
                }

                // 批評空間スクレイピング
                var scrapeResult = await _scraper.ScrapeGamePageAsync(gameId.Value, ct);

                if (!string.IsNullOrEmpty(scrapeResult.Error))
                {
                    result.Outcome = CheckOutcome.Error;
                    result.ErrorMessage = scrapeResult.Error;
                    return result;
                }

                if (!scrapeResult.HasPatchSection)
                {
                    status.HasPatches = false;
                    status.LastChecked = DateTime.UtcNow;
                    result.Outcome = CheckOutcome.NoPatchSection;
                    return result;
                }

                status.HasPatches = true;
                result.HasPatchIndicator = scrapeResult.HasPatchIndicator;
                result.PatchRegisteredUserCount = scrapeResult.PatchRegisteredUserCount;

                // 批評空間から公式サイトURLを補完・保存
                if (!string.IsNullOrEmpty(scrapeResult.OfficialSiteUrl))
                {
                    status.OfficialSiteUrl = scrapeResult.OfficialSiteUrl;
                    if (string.IsNullOrEmpty(result.OfficialSiteUrl))
                    {
                        result.OfficialSiteUrl = scrapeResult.OfficialSiteUrl;
                    }
                }

                // 最新バージョン抽出（バージョン番号の大きさで判定）
                var latestVersion = scrapeResult.Patches
                    .Where(p => !string.IsNullOrEmpty(p.Version))
                    .Select(p => p.Version)
                    .OrderByDescending(v => v, Comparer<string>.Create((a, b) => CompareVersions(a, b)))
                    .FirstOrDefault();

                result.LatestVersion = latestVersion;

                // VNDBからパッチリリース情報を補完
                await TryEnrichFromVndbAsync(game, result, ct);

                bool isFirstCheck = !status.LastChecked.HasValue;
                int previousCommentCount = status.KnownCommentCount;
                DateTime? previousLatestTimestamp = status.LatestPatchTimestamp;

                // ステータス更新（VNDB補完後のバージョンを使用）
                status.LatestKnownVersion = result.LatestVersion;
                status.KnownCommentCount = scrapeResult.CommentCount;
                status.LatestPatchTimestamp = scrapeResult.Patches
                    .Where(p => p.Timestamp.HasValue)
                    .OrderByDescending(p => p.Timestamp)
                    .Select(p => p.Timestamp)
                    .FirstOrDefault();
                status.LastChecked = DateTime.UtcNow;

                // 判定ロジック
                result.Outcome = DetermineOutcome(
                    isFirstCheck, result.LocalVersion, latestVersion,
                    status, previousCommentCount, previousLatestTimestamp,
                    scrapeResult);
                result.NewPatches = GetNewPatches(result.Outcome, scrapeResult);

                // 判定結果を保存（キャッシュ復元時に使用）
                status.LastOutcome = result.Outcome;
                status.HasPatchIndicator = scrapeResult.HasPatchIndicator;
                status.PatchRegisteredUserCount = scrapeResult.PatchRegisteredUserCount;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"チェックエラー: {game.Name}");
                result.Outcome = CheckOutcome.Error;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private CheckResult TryGetCachedResult(
            Game game, VNUpdateCheckerSettings settings,
            GameUpdateStatus status, CheckResult result)
        {
            if (!status.LastChecked.HasValue)
                return null;

            var cacheAge = DateTime.UtcNow - status.LastChecked.Value;
            if (cacheAge.TotalHours >= settings.CacheExpirationHours)
                return null;

            _logger.Info($"{game.Name}: キャッシュ有効期間内のためスキップ");
            result.Outcome = DetermineOutcomeFromStatus(status);
            result.LatestVersion = status.LatestKnownVersion;
            result.HasPatchIndicator = status.HasPatchIndicator;
            result.PatchRegisteredUserCount = status.PatchRegisteredUserCount;

            // OfficialSiteUrlをstatusから補完
            if (string.IsNullOrEmpty(result.OfficialSiteUrl) &&
                !string.IsNullOrEmpty(status.OfficialSiteUrl))
            {
                result.OfficialSiteUrl = status.OfficialSiteUrl;
            }
            return result;
        }

        private CheckOutcome DetermineOutcome(
            bool isFirstCheck, string localVersion, string latestVersion,
            GameUpdateStatus status, int previousCommentCount,
            DateTime? previousLatestTimestamp, ScrapingResult scrapeResult)
        {
            if (isFirstCheck)
            {
                return CheckOutcome.FirstCheck;
            }

            if (!string.IsNullOrEmpty(localVersion) && !string.IsNullOrEmpty(latestVersion))
            {
                var comparison = CompareVersions(localVersion, latestVersion);
                if (comparison < 0)
                {
                    return IsVersionAcknowledged(status.AcknowledgedVersion, latestVersion)
                        ? CheckOutcome.UpToDate
                        : CheckOutcome.UpdateAvailable;
                }
                return CheckOutcome.UpToDate;
            }

            if (!string.IsNullOrEmpty(latestVersion) && string.IsNullOrEmpty(localVersion))
            {
                if (IsVersionAcknowledged(status.AcknowledgedVersion, latestVersion))
                {
                    return CheckOutcome.UpToDate;
                }
                // ローカルver不明で適用済みでもない → 更新ありとして扱う
                return CheckOutcome.UpdateAvailable;
            }

            // バージョン番号なし → コメント数/タイムスタンプで判定
            if (scrapeResult.CommentCount > previousCommentCount ||
                (status.LatestPatchTimestamp.HasValue && previousLatestTimestamp.HasValue &&
                 status.LatestPatchTimestamp > previousLatestTimestamp))
            {
                return CheckOutcome.NewPatchComments;
            }

            // コメント変化なしの場合、前回の未確認状態を維持する
            if (status.LastOutcome.HasValue &&
                (status.LastOutcome.Value == CheckOutcome.UpdateAvailable ||
                 status.LastOutcome.Value == CheckOutcome.NewPatchComments ||
                 status.LastOutcome.Value == CheckOutcome.FirstCheck))
            {
                // 適用済みコメント数と比較して新着がなければUpToDate
                if (status.AcknowledgedCommentCount.HasValue &&
                    scrapeResult.CommentCount <= status.AcknowledgedCommentCount.Value)
                {
                    return CheckOutcome.UpToDate;
                }
                // FirstCheckは2回目以降はNewPatchCommentsとして扱う
                if (status.LastOutcome.Value == CheckOutcome.FirstCheck)
                    return CheckOutcome.NewPatchComments;
                return status.LastOutcome.Value;
            }

            return CheckOutcome.UpToDate;
        }

        private List<PatchInfo> GetNewPatches(CheckOutcome outcome, ScrapingResult scrapeResult)
        {
            // パッチセクションがある場合は常にコメントを返す（適用済みでも参考情報として有用）
            if (scrapeResult.Patches.Count > 0)
                return scrapeResult.Patches;
            return new List<PatchInfo>();
        }

        public async Task<List<CheckResult>> CheckAllGamesAsync(
            IEnumerable<Game> games,
            Action<int, int> progressCallback,
            CancellationToken ct = default)
        {
            var results = new List<CheckResult>();
            var gameList = games.ToList();
            int total = gameList.Count;
            // 単体チェック時はキャッシュをスキップして常に最新データ取得
            bool skipCache = total == 1;

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                progressCallback?.Invoke(i + 1, total);

                var result = await CheckGameAsync(gameList[i], skipCache, ct);
                results.Add(result);
            }

            return results;
        }

        public int? GetErogameScapeId(Game game)
        {
            // 1. ゲームのリンクから自動検出
            if (game.Links != null)
            {
                foreach (var link in game.Links)
                {
                    if (link.Url != null && link.Url.Contains("erogamescape"))
                    {
                        var id = ErogameScapeScraper.ExtractGameIdFromUrl(link.Url);
                        if (id.HasValue) return id;
                    }
                }
            }

            // 2. 手動マッピングから検索
            var settings = _getSettings();
            if (settings.ManualGameMappings != null &&
                settings.ManualGameMappings.TryGetValue(game.Id.ToString(), out int manualId))
            {
                return manualId;
            }

            return null;
        }

        /// <summary>
        /// VNDBリンクからVNDB APIを使って批評空間IDを解決し、ManualGameMappingsにキャッシュする
        /// </summary>
        private async Task<int?> ResolveErogameScapeIdFromVndbAsync(Game game, CancellationToken ct)
        {
            if (game.Links == null) return null;

            foreach (var link in game.Links)
            {
                if (link.Url == null || !link.Url.Contains("vndb.org/v")) continue;

                var vndbId = VndbApiClient.ExtractVndbIdFromUrl(link.Url);
                if (!vndbId.HasValue) continue;

                var egsId = await _vndbClient.GetErogameScapeIdAsync(vndbId.Value, ct);
                if (egsId.HasValue)
                {
                    // 自動解決結果をキャッシュ
                    var settings = _getSettings();
                    settings.ManualGameMappings[game.Id.ToString()] = egsId.Value;
                    _logger.Info($"{game.Name}: VNDBから批評空間ID {egsId.Value} を自動取得・保存");
                    return egsId;
                }
            }

            return null;
        }

        /// <summary>
        /// VNDBリンクがある場合、パッチリリース情報を取得して結果を補完する。
        /// 批評空間でバージョン取得済みの場合はスキップ（負荷軽減）。
        /// </summary>
        private async Task TryEnrichFromVndbAsync(Game game, CheckResult result, CancellationToken ct)
        {
            // 批評空間で既にバージョン情報が取れていれば追加リクエスト不要
            if (!string.IsNullOrEmpty(result.LatestVersion)) return;
            if (game.Links == null) return;

            foreach (var link in game.Links)
            {
                if (link.Url == null || !link.Url.Contains("vndb.org/v")) continue;

                var vndbId = VndbApiClient.ExtractVndbIdFromUrl(link.Url);
                if (!vndbId.HasValue) continue;

                var patchInfo = await _vndbClient.GetPatchReleasesAsync(vndbId.Value, ct);
                if (patchInfo == null) break;

                result.VndbPatchCount = patchInfo.PatchCount;
                result.VndbPatchVersion = patchInfo.LatestPatchVersion;

                if (!string.IsNullOrEmpty(patchInfo.LatestPatchVersion))
                {
                    result.LatestVersion = patchInfo.LatestPatchVersion;
                    _logger.Info($"{game.Name}: VNDBからバージョン補完: {patchInfo.LatestPatchVersion}");
                }
                break;
            }
        }

        public string GetLocalExeVersion(Game game)
        {
            try
            {
                string exePath = null;

                // GameActionsからexeパスを取得
                if (game.GameActions != null && game.GameActions.Count > 0)
                {
                    var playAction = game.GameActions.FirstOrDefault(a => a.IsPlayAction)
                                  ?? game.GameActions.FirstOrDefault();

                    if (playAction != null && !string.IsNullOrEmpty(playAction.Path))
                    {
                        exePath = playAction.Path;

                        // Playniteのプレースホルダーを置換
                        if (!string.IsNullOrEmpty(game.InstallDirectory))
                        {
                            exePath = exePath.Replace("{InstallDir}", game.InstallDirectory);
                        }

                        // 相対パスの場合、InstallDirectoryと結合
                        if (!Path.IsPathRooted(exePath) && !string.IsNullOrEmpty(game.InstallDirectory))
                        {
                            exePath = Path.Combine(game.InstallDirectory, exePath);
                        }
                    }
                    else
                    {
                        _logger.Debug($"{game.Name}: GameAction Path が空");
                    }
                }
                else
                {
                    _logger.Debug($"{game.Name}: GameActions が未設定 (InstallDir={game.InstallDirectory ?? "null"})");
                }

                if (string.IsNullOrEmpty(exePath))
                {
                    // InstallDirectoryからexeを探す
                    if (!string.IsNullOrEmpty(game.InstallDirectory) && Directory.Exists(game.InstallDirectory))
                    {
                        var exeFiles = Directory.GetFiles(game.InstallDirectory, "*.exe", SearchOption.TopDirectoryOnly);
                        if (exeFiles.Length > 0)
                        {
                            exePath = exeFiles[0];
                            _logger.Debug($"{game.Name}: InstallDirectoryからexe検出: {exePath}");
                        }
                    }
                }

                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    _logger.Debug($"{game.Name}: exeファイル未検出 (path={exePath ?? "null"})");
                    return null;
                }

                var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
                var version = versionInfo.ProductVersion ?? versionInfo.FileVersion;

                if (!string.IsNullOrEmpty(version))
                {
                    version = version.Trim();
                    var match = ExeVersionRegex.Match(version);
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn($"exeバージョン取得エラー ({game.Name}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// インストールディレクトリからパッチファイルを検出する。
        /// KiriKiri: patch*.xp3, CatSystem2: update*.int,
        /// GIGA: Update.pac, Artemis: patch*.fpk, Navel: patch*.noa
        /// </summary>
        public int DetectLocalPatchFiles(Game game)
        {
            var installDir = game.InstallDirectory;
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                return 0;

            int count = 0;
            try
            {
                // KiriKiri: patch*.xp3（ゲームルートとarcサブフォルダ）
                count += Directory.GetFiles(installDir, "patch*.xp3", SearchOption.TopDirectoryOnly).Length;
                var arcDir = Path.Combine(installDir, "arc");
                if (Directory.Exists(arcDir))
                {
                    count += Directory.GetFiles(arcDir, "patch*.xp3", SearchOption.TopDirectoryOnly).Length;
                }

                // CatSystem2: update*.int
                count += Directory.GetFiles(installDir, "update*.int", SearchOption.TopDirectoryOnly).Length;

                // GIGA: Update.pac
                if (File.Exists(Path.Combine(installDir, "Update.pac")))
                {
                    count++;
                }

                // Artemis Engine: patch*.fpk
                count += Directory.GetFiles(installDir, "patch*.fpk", SearchOption.TopDirectoryOnly).Length;

                // Navel/NOA: patch*.noa
                count += Directory.GetFiles(installDir, "patch*.noa", SearchOption.TopDirectoryOnly).Length;

                if (count > 0)
                {
                    _logger.Debug($"{game.Name}: ローカルパッチファイル {count}個検出");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"パッチファイル検出エラー ({game.Name}): {ex.Message}");
            }

            return count;
        }

        // ショップ・情報サイト等、公式サイトではないドメイン
        private static readonly string[] NonOfficialDomains = new[]
        {
            "dlsite.com", "dmm.co.jp", "dmm.com", "fanza.com",
            "getchu.com", "amazon.co.jp", "amazon.com",
            "gyutto.com", "digiket.com", "melonbooks.co.jp",
            "booth.pm", "store.steampowered.com",
            "twitter.com", "x.com", "youtube.com", "nicovideo.jp",
            "wikipedia.org", "wikidata.org", "seesaawiki.jp", "wikiwiki.jp", "wiki.fc2.com",
            "blog.fc2.com", "livedoor.jp",
            "vndb.org", "erogamescape"
        };

        // 公式サイトを示すリンク名キーワード
        private static readonly string[] OfficialLinkKeywords = new[]
        {
            "公式", "official", "hp", "ホームページ", "website", "メーカー", "ブランド"
        };

        /// <summary>
        /// ゲームのリンクから公式サイトURLを取得する。
        /// リンク名で公式サイトを優先し、ショップ・情報サイトを除外する。
        /// </summary>
        public static string GetOfficialSiteUrl(Game game)
        {
            if (game.Links == null) return null;

            // 1. リンク名が公式サイトを示すものを優先
            foreach (var link in game.Links)
            {
                if (!IsValidHttpUrl(link.Url)) continue;
                if (string.IsNullOrEmpty(link.Name)) continue;
                var nameLower = link.Name.ToLowerInvariant();
                if (OfficialLinkKeywords.Any(k => nameLower.Contains(k)))
                {
                    return link.Url;
                }
            }

            // 2. ショップ・情報サイトを除外して最初のリンクを返す
            foreach (var link in game.Links)
            {
                if (!IsValidHttpUrl(link.Url)) continue;
                if (IsNonOfficialDomain(link.Url)) continue;
                return link.Url;
            }

            return null;
        }

        private static bool IsValidHttpUrl(string url)
        {
            return !string.IsNullOrEmpty(url) &&
                   (url.StartsWith("http://") || url.StartsWith("https://"));
        }

        private static bool IsNonOfficialDomain(string url)
        {
            try
            {
                var host = new Uri(url).Host.ToLowerInvariant();
                return NonOfficialDomains.Any(d => host.Contains(d));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>AcknowledgedVersionがlatestVersion以上かどうか（バージョン比較で判定）</summary>
        private static bool IsVersionAcknowledged(string acknowledgedVersion, string latestVersion)
        {
            if (string.IsNullOrEmpty(acknowledgedVersion)) return false;
            return CompareVersions(acknowledgedVersion, latestVersion) >= 0;
        }

        public static int CompareVersions(string v1, string v2)
        {
            var parts1 = NormalizeVersion(v1);
            var parts2 = NormalizeVersion(v2);

            int maxLen = Math.Max(parts1.Length, parts2.Length);
            for (int i = 0; i < maxLen; i++)
            {
                int p1 = i < parts1.Length ? parts1[i] : 0;
                int p2 = i < parts2.Length ? parts2[i] : 0;

                if (p1 != p2) return p1.CompareTo(p2);
            }

            return 0;
        }

        private static int[] NormalizeVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return new[] { 0 };

            // "1.01" → [1, 1], "1.2.0" → [1, 2, 0]
            return version.Split('.')
                .Select(p => int.TryParse(p, out int val) ? val : 0)
                .ToArray();
        }

        private GameUpdateStatus GetOrCreateStatus(VNUpdateCheckerSettings settings, Guid gameId, int egsId)
        {
            var key = gameId.ToString();
            if (!settings.GameStatuses.TryGetValue(key, out var status))
            {
                status = new GameUpdateStatus { ErogameScapeId = egsId };
                settings.GameStatuses[key] = status;
            }
            return status;
        }

        private CheckOutcome DetermineOutcomeFromStatus(GameUpdateStatus status)
        {
            if (!status.HasPatches) return CheckOutcome.NoPatchSection;

            // バージョン比較が可能な場合
            if (!string.IsNullOrEmpty(status.LatestKnownVersion) &&
                !string.IsNullOrEmpty(status.LocalExeVersion))
            {
                var cmp = CompareVersions(status.LocalExeVersion, status.LatestKnownVersion);
                if (cmp < 0 && !IsVersionAcknowledged(status.AcknowledgedVersion, status.LatestKnownVersion))
                    return CheckOutcome.UpdateAvailable;
                return CheckOutcome.UpToDate;
            }

            // バージョン比較不可の場合、前回の判定結果を維持
            if (status.LastOutcome.HasValue &&
                status.LastOutcome.Value != CheckOutcome.Error)
            {
                return status.LastOutcome.Value;
            }

            return CheckOutcome.UpToDate;
        }
    }
}
