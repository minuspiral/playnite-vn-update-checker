using VNUpdateChecker.Models;
using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;

namespace VNUpdateChecker
{
    public class VNUpdateCheckerSettings : ObservableObject, ISettings
    {
        private readonly VNUpdateCheckerPlugin _plugin;

        /// <summary>キャッシュ有効期間（時間）</summary>
        public int CacheExpirationHours { get; set; } = 24;

        /// <summary>手動ゲームマッピング (PlayniteGameId → ErogameScapeId)</summary>
        public Dictionary<string, int> ManualGameMappings { get; set; } = new Dictionary<string, int>();

        /// <summary>ゲーム毎の更新追跡ステータス</summary>
        public Dictionary<string, GameUpdateStatus> GameStatuses { get; set; } = new Dictionary<string, GameUpdateStatus>();

        // ISettings用の前回値バックアップ
        private VNUpdateCheckerSettings _previousSettings;

        public VNUpdateCheckerSettings() { }

        public VNUpdateCheckerSettings(VNUpdateCheckerPlugin plugin)
        {
            _plugin = plugin;

            try
            {
                var saved = plugin.LoadPluginSettings<VNUpdateCheckerSettings>();
                if (saved != null)
                {
                    CacheExpirationHours = saved.CacheExpirationHours;
                    ManualGameMappings = saved.ManualGameMappings ?? new Dictionary<string, int>();
                    GameStatuses = saved.GameStatuses ?? new Dictionary<string, GameUpdateStatus>();
                }
            }
            catch (Exception)
            {
                // 設定ファイル破損時はデフォルト値で起動（データ全損を防ぐ）
            }
        }

        public void BeginEdit()
        {
            _previousSettings = new VNUpdateCheckerSettings
            {
                CacheExpirationHours = CacheExpirationHours,
                ManualGameMappings = new Dictionary<string, int>(ManualGameMappings)
            };
        }

        public void CancelEdit()
        {
            if (_previousSettings != null)
            {
                CacheExpirationHours = _previousSettings.CacheExpirationHours;
                ManualGameMappings = new Dictionary<string, int>(_previousSettings.ManualGameMappings);
            }
        }

        public void EndEdit()
        {
            _plugin?.SavePluginSettings(this);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            if (CacheExpirationHours < 1)
            {
                errors.Add("キャッシュ有効期間は1時間以上にしてください。");
            }

            return errors.Count == 0;
        }

        public void Save()
        {
            _plugin?.SavePluginSettings(this);
        }
    }
}
