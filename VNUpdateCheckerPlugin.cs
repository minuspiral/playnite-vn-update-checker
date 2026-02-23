using VNUpdateChecker.Models;
using VNUpdateChecker.Services;
using VNUpdateChecker.Views;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace VNUpdateChecker
{
    public class VNUpdateCheckerPlugin : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private const string TagPrefix = "[パッチ] ";
        private const string TagUpdateAvailable = TagPrefix + "更新あり";
        private const string TagUpToDate = TagPrefix + "最新";
        private const string TagPatchExists = TagPrefix + "修正ファイルあり";
        private const string TagNoPatch = TagPrefix + "パッチ情報なし";
        private const string TagFirstCheck = TagPrefix + "初回チェック";

        public override Guid Id { get; } = Guid.Parse("ca82b74c-7220-412a-bce1-94f176482728");

        private VNUpdateCheckerSettings Settings { get; set; }
        private UpdateCheckService _checkService;

        public VNUpdateCheckerPlugin(IPlayniteAPI api) : base(api)
        {
            Settings = new VNUpdateCheckerSettings(this);
            _checkService = new UpdateCheckService(api, Logger, () => Settings);
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                Description = "全ゲームのパッチ情報をチェック",
                MenuSection = "@VN Update Checker",
                Action = _ => CheckAllGames()
            };

            yield return new MainMenuItem
            {
                Description = "キャッシュをクリア",
                MenuSection = "@VN Update Checker",
                Action = _ => ClearCache()
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            yield return new GameMenuItem
            {
                Description = "パッチ情報をチェック",
                MenuSection = "VN Update Checker",
                Action = menuArgs => CheckSelectedGames(menuArgs.Games)
            };

            yield return new GameMenuItem
            {
                Description = "批評空間IDを手動設定",
                MenuSection = "VN Update Checker",
                Action = menuArgs => SetErogameScapeId(menuArgs.Games)
            };

            yield return new GameMenuItem
            {
                Description = "パッチ適用済みにする",
                MenuSection = "VN Update Checker",
                Action = menuArgs => AcknowledgeGames(menuArgs.Games)
            };
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return Settings;
        }

        public override UserControl GetSettingsView(bool firstRunView)
        {
            return new VNUpdateCheckerSettingsView();
        }

        private void CheckAllGames()
        {
            var games = PlayniteApi.Database.Games
                .Where(g => _checkService.GetErogameScapeId(g) != null || HasVndbLink(g))
                .ToList();

            if (games.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "批評空間またはVNDBのリンクが登録されたゲームがありません。\n\nゲームの編集画面で「リンク」にURLを追加してください。",
                    "VN Update Checker");
                return;
            }

            RunCheckWithProgress(games);
        }

        private static bool HasVndbLink(Game game)
        {
            return game.Links != null && game.Links.Any(l => l.Url != null && l.Url.Contains("vndb.org/v"));
        }

        private void CheckSelectedGames(List<Game> games)
        {
            if (games == null || games.Count == 0) return;
            RunCheckWithProgress(games);
        }

        private void RunCheckWithProgress(List<Game> games)
        {
            List<CheckResult> results = null;

            PlayniteApi.Dialogs.ActivateGlobalProgress(args =>
            {
                args.ProgressMaxValue = games.Count;
                args.CurrentProgressValue = 0;

                try
                {
                    results = Task.Run(() => _checkService.CheckAllGamesAsync(
                        games,
                        (current, total) =>
                        {
                            args.CurrentProgressValue = current;
                            args.Text = $"チェック中... ({current}/{total}) {games[current - 1].Name}";
                        },
                        args.CancelToken)).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("チェックがキャンセルされました");
                }
            },
            new GlobalProgressOptions("パッチ情報をチェック中...", true)
            {
                IsIndeterminate = false
            });

            if (results != null && results.Count > 0)
            {
                Settings.Save();
                ApplyResultsToGames(results);
                ShowResults(results);
            }
        }

        #region タグ・ノート更新

        private void ApplyResultsToGames(List<CheckResult> results)
        {
            foreach (var result in results)
            {
                try
                {
                    var game = PlayniteApi.Database.Games.Get(result.PlayniteGameId);
                    if (game == null) continue;

                    UpdateGameTags(game, result);
                    UpdateGameNotes(game, result);
                    PlayniteApi.Database.Games.Update(game);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"メタデータ更新エラー ({result.GameName}): {ex.Message}");
                }
            }
        }

        private void UpdateGameTags(Game game, CheckResult result)
        {
            // 既存のEUCタグを削除
            if (game.TagIds != null)
            {
                var eucTagIds = game.TagIds
                    .Where(id =>
                    {
                        var tag = PlayniteApi.Database.Tags.Get(id);
                        return tag != null && tag.Name.StartsWith(TagPrefix);
                    }).ToList();

                foreach (var tagId in eucTagIds)
                {
                    game.TagIds.Remove(tagId);
                }
            }

            // 新しいタグを付与
            var tagName = GetTagNameForOutcome(result);
            if (tagName == null) return;

            var newTagId = GetOrCreateTag(tagName);
            if (game.TagIds == null)
            {
                game.TagIds = new List<Guid>();
            }
            if (!game.TagIds.Contains(newTagId))
            {
                game.TagIds.Add(newTagId);
            }
        }

        private string GetTagNameForOutcome(CheckResult result)
        {
            switch (result.Outcome)
            {
                case CheckOutcome.UpdateAvailable:
                    return TagUpdateAvailable;
                case CheckOutcome.NewPatchComments:
                    return TagUpdateAvailable;
                case CheckOutcome.UpToDate:
                    return result.HasPatchIndicator ? TagPatchExists : TagUpToDate;
                case CheckOutcome.FirstCheck:
                    return result.HasPatchIndicator ? TagPatchExists : TagFirstCheck;
                case CheckOutcome.NoPatchSection:
                    return TagNoPatch;
                default:
                    return null;
            }
        }

        private Guid GetOrCreateTag(string tagName)
        {
            var existing = PlayniteApi.Database.Tags.FirstOrDefault(t => t.Name == tagName);
            if (existing != null) return existing.Id;

            var newTag = new Tag(tagName);
            PlayniteApi.Database.Tags.Add(newTag);
            return newTag.Id;
        }

        private void UpdateGameNotes(Game game, CheckResult result)
        {
            // EUCセクションを構築
            var sb = new StringBuilder();
            sb.AppendLine("--- VN Update Checker ---");
            sb.AppendLine($"チェック日時: {DateTime.Now:yyyy/MM/dd HH:mm}");

            if (!string.IsNullOrEmpty(result.LocalVersion))
                sb.AppendLine($"ローカルver: {result.LocalVersion}");
            if (result.LocalPatchFileCount > 0)
                sb.AppendLine($"ローカルパッチファイル: {result.LocalPatchFileCount}個");
            if (!string.IsNullOrEmpty(result.LatestVersion))
                sb.AppendLine($"最新ver: {result.LatestVersion}");
            if (result.HasPatchIndicator)
                sb.AppendLine("修正ファイルあり");
            if (result.PatchRegisteredUserCount > 0)
                sb.AppendLine($"パッチ確認者: {result.PatchRegisteredUserCount}人");

            if (result.NewPatches != null && result.NewPatches.Count > 0)
            {
                sb.AppendLine();
                var latest = result.NewPatches
                    .OrderByDescending(p => p.Timestamp ?? DateTime.MinValue)
                    .First();
                sb.AppendLine($"最新パッチ: {latest.Timestamp?.ToString("yyyy/MM/dd") ?? "日時不明"}");
                if (!string.IsNullOrEmpty(latest.Version))
                    sb.AppendLine($"  ver.{latest.Version}");
                if (latest.Urls.Count > 0)
                    sb.AppendLine($"  URL: {latest.Urls[0]}");
            }

            sb.Append("--- /VN Update Checker ---");
            var eucSection = sb.ToString();

            // 既存ノートからEUCセクションを置換
            var notes = game.Notes ?? "";
            var startMarker = "--- VN Update Checker ---";
            var endMarker = "--- /VN Update Checker ---";
            var startIdx = notes.IndexOf(startMarker);
            var endIdx = notes.IndexOf(endMarker);

            if (startIdx >= 0 && endIdx >= 0 && endIdx > startIdx)
            {
                notes = notes.Substring(0, startIdx) + eucSection +
                        notes.Substring(endIdx + endMarker.Length);
            }
            else
            {
                if (!string.IsNullOrEmpty(notes) && !notes.EndsWith("\n"))
                    notes += "\n\n";
                else if (!string.IsNullOrEmpty(notes))
                    notes += "\n";
                notes += eucSection;
            }

            game.Notes = notes.Trim();
        }

        #endregion

        private void ShowResults(List<CheckResult> results)
        {
            var viewModels = results.Select(r => new CheckResultViewModel(r, vm =>
            {
                AcknowledgeGame(vm.PlayniteGameId, vm.Result.LatestVersion);
            })).ToList();

            PlayniteApi.MainView.UIDispatcher.Invoke(() =>
            {
                if (viewModels.Count == 1)
                {
                    ShowSingleGameResult(viewModels[0]);
                }
                else
                {
                    ShowMultiGameResults(viewModels);
                }
            });
        }

        private void ShowSingleGameResult(CheckResultViewModel vm)
        {
            var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false
            });
            window.Title = $"パッチ情報 - {vm.GameName}";
            window.Content = new SingleGameResultView(vm);
            window.SizeToContent = SizeToContent.Height;
            window.Width = 480;
            window.MaxHeight = 600;
            window.Owner = Application.Current.MainWindow;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.ShowDialog();
        }

        private void ShowMultiGameResults(List<CheckResultViewModel> viewModels)
        {
            var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = true
            });
            window.Title = "パッチ情報チェック結果";
            window.Content = new UpdateResultsView(viewModels);
            window.Width = 900;
            window.Height = 650;
            window.Owner = Application.Current.MainWindow;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.ShowDialog();
        }

        private void SetErogameScapeId(List<Game> games)
        {
            if (games == null || games.Count == 0) return;
            var game = games.First();

            var currentId = _checkService.GetErogameScapeId(game);
            var input = PlayniteApi.Dialogs.SelectString(
                $"「{game.Name}」の批評空間ゲームIDを入力してください。\n\n" +
                "例: game.php?game=35825 のURLの場合は「35825」と入力",
                "批評空間ID設定",
                currentId?.ToString() ?? "");

            if (input.Result && !string.IsNullOrWhiteSpace(input.SelectedString))
            {
                if (int.TryParse(input.SelectedString.Trim(), out int id))
                {
                    Settings.ManualGameMappings[game.Id.ToString()] = id;
                    Settings.Save();
                    PlayniteApi.Dialogs.ShowMessage(
                        $"「{game.Name}」に批評空間ID {id} を設定しました。",
                        "VN Update Checker");
                }
                else
                {
                    PlayniteApi.Dialogs.ShowMessage(
                        "無効なIDです。数値を入力してください。",
                        "VN Update Checker");
                }
            }
        }

        private void AcknowledgeGames(List<Game> games)
        {
            if (games == null) return;

            foreach (var game in games)
            {
                var key = game.Id.ToString();
                Settings.GameStatuses.TryGetValue(key, out var status);
                if (status != null)
                {
                    if (!string.IsNullOrEmpty(status.LatestKnownVersion))
                    {
                        status.AcknowledgedVersion = status.LatestKnownVersion;
                    }
                    status.AcknowledgedCommentCount = status.KnownCommentCount;
                    status.LastOutcome = CheckOutcome.UpToDate;
                }

                // タグ・ノートも更新（HasPatchIndicatorをstatusから復元）
                var ackResult = new CheckResult
                {
                    Outcome = CheckOutcome.UpToDate,
                    HasPatchIndicator = status?.HasPatchIndicator ?? false
                };
                UpdateGameTags(game, ackResult);
                UpdateGameNotes(game, ackResult);
                PlayniteApi.Database.Games.Update(game);
            }

            Settings.Save();
            PlayniteApi.Dialogs.ShowMessage("選択したゲームのパッチを適用済みにしました。", "VN Update Checker");
        }

        private void AcknowledgeGame(Guid gameId, string version)
        {
            var key = gameId.ToString();
            Settings.GameStatuses.TryGetValue(key, out var status);
            if (status != null)
            {
                status.AcknowledgedVersion = !string.IsNullOrEmpty(version) ? version : status.LatestKnownVersion;
                status.AcknowledgedCommentCount = status.KnownCommentCount;
                status.LastOutcome = CheckOutcome.UpToDate;
                Settings.Save();
            }

            // タグ・ノートも更新（HasPatchIndicatorをstatusから復元）
            var game = PlayniteApi.Database.Games.Get(gameId);
            if (game != null)
            {
                var ackResult = new CheckResult
                {
                    Outcome = CheckOutcome.UpToDate,
                    HasPatchIndicator = status?.HasPatchIndicator ?? false
                };
                UpdateGameTags(game, ackResult);
                UpdateGameNotes(game, ackResult);
                PlayniteApi.Database.Games.Update(game);
            }
        }

        private void ClearCache()
        {
            var result = PlayniteApi.Dialogs.ShowMessage(
                "すべてのゲームのチェックキャッシュをクリアしますか？\n次回チェック時にすべてのゲームが再チェックされます。",
                "キャッシュクリア",
                MessageBoxButton.YesNo);

            if (result == MessageBoxResult.Yes)
            {
                foreach (var status in Settings.GameStatuses.Values)
                {
                    status.LastChecked = null;
                }
                Settings.Save();
                PlayniteApi.Dialogs.ShowMessage("キャッシュをクリアしました。", "VN Update Checker");
            }
        }
    }
}
