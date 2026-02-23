using VNUpdateChecker.Models;
using VNUpdateChecker.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VNUpdateChecker.Views
{
    public class PatchInfoViewModel
    {
        public string TimestampText { get; set; }
        public string Author { get; set; }
        public string VersionText { get; set; }
        public bool HasVersion { get; set; }
        public string Comment { get; set; }

        public PatchInfoViewModel(PatchInfo patch)
        {
            TimestampText = patch.Timestamp?.ToString("yyyy/MM/dd HH:mm") ?? "日時不明";
            Author = patch.Author ?? "";
            VersionText = !string.IsNullOrEmpty(patch.Version) ? $"ver.{patch.Version}" : "";
            HasVersion = !string.IsNullOrEmpty(patch.Version);
            Comment = patch.RawComment ?? "";
        }
    }

    public class CheckResultViewModel
    {
        private readonly CheckResult _result;
        private readonly Action<CheckResultViewModel> _acknowledgeAction;

        public CheckResultViewModel(CheckResult result, Action<CheckResultViewModel> acknowledgeAction)
        {
            _result = result;
            _acknowledgeAction = acknowledgeAction;
        }

        public CheckResult Result => _result;
        public string GameName => _result.GameName;
        public Guid PlayniteGameId => _result.PlayniteGameId;
        public string LocalVersion
        {
            get
            {
                if (!string.IsNullOrEmpty(_result.LocalVersion))
                {
                    if (_result.LocalPatchFileCount > 0)
                        return $"{_result.LocalVersion} (パッチ{_result.LocalPatchFileCount}個)";
                    return _result.LocalVersion;
                }
                if (_result.LocalPatchFileCount > 0)
                    return $"パッチ適用済({_result.LocalPatchFileCount}個)";
                return "-";
            }
        }
        public string LatestVersion
        {
            get
            {
                var ver = _result.LatestVersion ?? "-";
                if (!string.IsNullOrEmpty(_result.VndbPatchVersion) &&
                    _result.VndbPatchVersion != _result.LatestVersion)
                {
                    return $"{ver} (VNDB: {_result.VndbPatchVersion})";
                }
                return ver;
            }
        }
        public string ErogameScapeUrl => _result.ErogameScapeUrl;
        public string OfficialSiteUrl => _result.OfficialSiteUrl;
        public CheckOutcome Outcome => _result.Outcome;
        public List<PatchInfo> NewPatches => _result.NewPatches;

        public string OutcomeText
        {
            get
            {
                var patchNote = _result.HasPatchIndicator ? " (修正ファイルあり)" : "";
                switch (_result.Outcome)
                {
                    case CheckOutcome.NoPatchSection: return "パッチ情報なし";
                    case CheckOutcome.UpToDate: return "最新" + patchNote;
                    case CheckOutcome.UpdateAvailable: return "★ 更新あり";
                    case CheckOutcome.NewPatchComments: return "★ 新コメントあり";
                    case CheckOutcome.FirstCheck: return "初回チェック" + patchNote;
                    case CheckOutcome.NoErogameScapeId: return "批評空間ID未設定";
                    case CheckOutcome.Error:
                        return string.IsNullOrEmpty(_result.ErrorMessage)
                            ? "エラー"
                            : $"エラー: {_result.ErrorMessage}";
                    default: return "不明";
                }
            }
        }

        public string PatchUserCountText =>
            _result.PatchRegisteredUserCount > 0 ? $"{_result.PatchRegisteredUserCount}人" : "-";

        public bool HasUrl => !string.IsNullOrEmpty(_result.ErogameScapeUrl);
        public bool HasOfficialSiteUrl => !string.IsNullOrEmpty(_result.OfficialSiteUrl);

        /// <summary>状態に応じたテーマリソースキー</summary>
        public string OutcomeCategory
        {
            get
            {
                switch (_result.Outcome)
                {
                    case CheckOutcome.UpdateAvailable:
                    case CheckOutcome.NewPatchComments:
                    case CheckOutcome.FirstCheck:
                        return "warning";
                    case CheckOutcome.UpToDate:
                        return "positive";
                    case CheckOutcome.Error:
                        return "error";
                    default:
                        return "normal";
                }
            }
        }

        /// <summary>最新verがローカルより新しいか</summary>
        public bool IsNewerVersionAvailable
        {
            get
            {
                if (string.IsNullOrEmpty(_result.LocalVersion) || string.IsNullOrEmpty(_result.LatestVersion))
                    return false;
                return UpdateCheckService.CompareVersions(_result.LocalVersion, _result.LatestVersion) < 0;
            }
        }

        public bool CanAcknowledge =>
            _result.Outcome == CheckOutcome.UpdateAvailable ||
            _result.Outcome == CheckOutcome.NewPatchComments ||
            _result.Outcome == CheckOutcome.FirstCheck;

        public int SortOrder
        {
            get
            {
                switch (_result.Outcome)
                {
                    case CheckOutcome.UpdateAvailable: return 0;
                    case CheckOutcome.NewPatchComments: return 1;
                    case CheckOutcome.FirstCheck: return 2;
                    case CheckOutcome.Error: return 3;
                    case CheckOutcome.UpToDate: return 4;
                    case CheckOutcome.NoPatchSection: return 5;
                    case CheckOutcome.NoErogameScapeId: return 6;
                    default: return 7;
                }
            }
        }

        public void Acknowledge()
        {
            _acknowledgeAction?.Invoke(this);
        }
    }

    public partial class UpdateResultsView : UserControl
    {
        public UpdateResultsView(List<CheckResultViewModel> viewModels)
        {
            InitializeComponent();

            var sorted = viewModels.OrderBy(vm => vm.SortOrder).ThenBy(vm => vm.GameName).ToList();
            ResultsGrid.ItemsSource = sorted;
        }

        private void OpenErogameScape_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is CheckResultViewModel vm && vm.HasUrl)
            {
                try
                {
                    var uri = new Uri(vm.ErogameScapeUrl);
                    if (!uri.Host.EndsWith("erogamescape.dyndns.org"))
                        return;
                    Process.Start(new ProcessStartInfo(vm.ErogameScapeUrl) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // URL起動失敗は無視
                }
            }
        }

        private void OpenOfficialSite_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is CheckResultViewModel vm && vm.HasOfficialSiteUrl)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(vm.OfficialSiteUrl) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // URL起動失敗は無視
                }
            }
        }

        private void Acknowledge_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is CheckResultViewModel vm)
            {
                vm.Acknowledge();
                btn.IsEnabled = false;
                btn.Content = "適用済み ✓";
            }
        }

        private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is CheckResultViewModel vm &&
                vm.NewPatches != null && vm.NewPatches.Count > 0)
            {
                var patchViewModels = vm.NewPatches
                    .OrderByDescending(p => p.Timestamp ?? DateTime.MinValue)
                    .Select(p => new PatchInfoViewModel(p))
                    .ToList();
                PatchDetailList.ItemsSource = patchViewModels;
                PatchDetailPanel.Visibility = Visibility.Visible;
            }
            else
            {
                PatchDetailList.ItemsSource = null;
                PatchDetailPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Window.GetWindow(this)?.Close();
        }
    }
}
