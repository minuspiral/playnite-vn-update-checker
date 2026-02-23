using System;

namespace VNUpdateChecker.Models
{
    public class GameUpdateStatus
    {
        /// <summary>批評空間のゲームID</summary>
        public int ErogameScapeId { get; set; }

        /// <summary>最終チェック日時</summary>
        public DateTime? LastChecked { get; set; }

        /// <summary>パッチ情報が存在するか</summary>
        public bool HasPatches { get; set; }

        /// <summary>リモート側の最新バージョン</summary>
        public string LatestKnownVersion { get; set; }

        /// <summary>ローカルexeのバージョン</summary>
        public string LocalExeVersion { get; set; }

        /// <summary>前回チェック時のコメント数</summary>
        public int KnownCommentCount { get; set; }

        /// <summary>最新パッチコメントのタイムスタンプ</summary>
        public DateTime? LatestPatchTimestamp { get; set; }

        /// <summary>ユーザーが「適用済み」にしたバージョン</summary>
        public string AcknowledgedVersion { get; set; }

        /// <summary>前回の判定結果（キャッシュ復元・再チェック時に使用）</summary>
        public CheckOutcome? LastOutcome { get; set; }

        /// <summary>ユーザーが適用済みにした時点のコメント数</summary>
        public int? AcknowledgedCommentCount { get; set; }

        /// <summary>批評空間から取得した公式サイトURL</summary>
        public string OfficialSiteUrl { get; set; }

        /// <summary>前回の修正ファイルインジケータ</summary>
        public bool HasPatchIndicator { get; set; }

        /// <summary>前回のパッチ確認者数</summary>
        public int PatchRegisteredUserCount { get; set; }
    }
}
