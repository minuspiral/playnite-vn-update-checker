using System;
using System.Collections.Generic;

namespace VNUpdateChecker.Models
{
    public enum CheckOutcome
    {
        /// <summary>パッチ情報なし（パッチセクション自体がない）</summary>
        NoPatchSection,

        /// <summary>パッチ情報あり、更新なし（既に最新 or 適用済み）</summary>
        UpToDate,

        /// <summary>新しいバージョンが利用可能</summary>
        UpdateAvailable,

        /// <summary>新しいパッチコメントあり（バージョン比較不可）</summary>
        NewPatchComments,

        /// <summary>初回チェック（ベースライン記録）</summary>
        FirstCheck,

        /// <summary>批評空間IDが不明</summary>
        NoErogameScapeId,

        /// <summary>チェック中にエラー発生</summary>
        Error
    }

    public class CheckResult
    {
        /// <summary>Playnite上のゲーム名</summary>
        public string GameName { get; set; }

        /// <summary>PlayniteのゲームID</summary>
        public Guid PlayniteGameId { get; set; }

        /// <summary>チェック結果</summary>
        public CheckOutcome Outcome { get; set; }

        /// <summary>リモート側の最新バージョン</summary>
        public string LatestVersion { get; set; }

        /// <summary>ローカルexeのバージョン</summary>
        public string LocalVersion { get; set; }

        /// <summary>ローカルパッチファイル検出数（KiriKiri patch.xp3, CatSystem2 update.int等）</summary>
        public int LocalPatchFileCount { get; set; }

        /// <summary>新しいパッチコメント一覧</summary>
        public List<PatchInfo> NewPatches { get; set; } = new List<PatchInfo>();

        /// <summary>批評空間のURL</summary>
        public string ErogameScapeUrl { get; set; }

        /// <summary>公式サイトのURL</summary>
        public string OfficialSiteUrl { get; set; }

        /// <summary>批評空間で「修正ファイルがあります」表示の有無</summary>
        public bool HasPatchIndicator { get; set; }

        /// <summary>批評空間でパッチを登録したユーザー数</summary>
        public int PatchRegisteredUserCount { get; set; }

        /// <summary>VNDBパッチリリースの最新バージョン</summary>
        public string VndbPatchVersion { get; set; }

        /// <summary>VNDBパッチリリース数</summary>
        public int VndbPatchCount { get; set; }

        /// <summary>エラーメッセージ（Outcome=Errorの場合）</summary>
        public string ErrorMessage { get; set; }
    }
}
