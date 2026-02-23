using VNUpdateChecker.Models;
using VNUpdateChecker.Services;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VNUpdateChecker.Views
{
    public partial class SingleGameResultView : UserControl
    {
        private readonly CheckResultViewModel _vm;

        public SingleGameResultView(CheckResultViewModel vm)
        {
            InitializeComponent();
            _vm = vm;

            OutcomeText.Text = vm.OutcomeText;
            LocalVersionText.Text = vm.LocalVersion;
            LatestVersionText.Text = vm.LatestVersion;

            // 状態の色分け
            switch (vm.Outcome)
            {
                case CheckOutcome.UpdateAvailable:
                case CheckOutcome.NewPatchComments:
                case CheckOutcome.FirstCheck:
                    OutcomeText.SetResourceReference(ForegroundProperty, "WarningBrush");
                    break;
                case CheckOutcome.UpToDate:
                    OutcomeText.SetResourceReference(ForegroundProperty, "PositiveRatingBrush");
                    break;
                case CheckOutcome.Error:
                    OutcomeText.SetResourceReference(ForegroundProperty, "WarningBrush");
                    break;
            }

            // バージョン比較の可視化
            if (vm.IsNewerVersionAvailable)
            {
                LatestVersionText.SetResourceReference(ForegroundProperty, "GlyphBrush");
                LatestVersionText.FontWeight = FontWeights.Bold;
            }

            if (vm.Result.PatchRegisteredUserCount > 0)
            {
                UserCountText.Text = $"{vm.Result.PatchRegisteredUserCount}人";
            }
            else
            {
                UserCountLabel.Visibility = Visibility.Collapsed;
                UserCountText.Visibility = Visibility.Collapsed;
            }

            if (vm.HasUrl)
            {
                OpenUrlButton.Visibility = Visibility.Visible;
            }

            if (vm.HasOfficialSiteUrl)
            {
                OpenOfficialButton.Visibility = Visibility.Visible;
            }

            if (vm.CanAcknowledge)
            {
                AcknowledgeButton.Visibility = Visibility.Visible;
            }

            // パッチ詳細
            if (vm.NewPatches != null && vm.NewPatches.Count > 0)
            {
                var patchViewModels = vm.NewPatches
                    .OrderByDescending(p => p.Timestamp ?? DateTime.MinValue)
                    .Select(p => new PatchInfoViewModel(p))
                    .ToList();
                PatchDetailList.ItemsSource = patchViewModels;
                PatchDetailList.Visibility = Visibility.Visible;
                PatchDetailSeparator.Visibility = Visibility.Visible;
            }
        }

        private void OpenErogameScape_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var uri = new Uri(_vm.ErogameScapeUrl);
                if (!uri.Host.EndsWith("erogamescape.dyndns.org"))
                    return;
                Process.Start(new ProcessStartInfo(_vm.ErogameScapeUrl) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // URL起動失敗は無視
            }
        }

        private void OpenOfficialSite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_vm.OfficialSiteUrl) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // URL起動失敗は無視
            }
        }

        private void Acknowledge_Click(object sender, RoutedEventArgs e)
        {
            _vm.Acknowledge();
            AcknowledgeButton.IsEnabled = false;
            AcknowledgeButton.Content = "適用済み";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Window.GetWindow(this)?.Close();
        }
    }
}
