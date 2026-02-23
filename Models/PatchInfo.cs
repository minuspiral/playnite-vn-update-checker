using System;
using System.Collections.Generic;

namespace VNUpdateChecker.Models
{
    public class PatchInfo
    {
        /// <summary>批評空間のコメント評価 (A/B/C等)</summary>
        public string Rating { get; set; }

        /// <summary>コメント本文（生テキスト）</summary>
        public string RawComment { get; set; }

        /// <summary>抽出されたバージョン番号 (例: "1.2.0")</summary>
        public string Version { get; set; }

        /// <summary>コメント投稿日時</summary>
        public DateTime? Timestamp { get; set; }

        /// <summary>コメント内のURL一覧</summary>
        public List<string> Urls { get; set; } = new List<string>();

        /// <summary>投稿者名</summary>
        public string Author { get; set; }
    }
}
