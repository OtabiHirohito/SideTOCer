using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace SideTOCer
{
    /// <summary>
    /// CSVとMarkdownテーブルを相互変換するためのツールウィンドウ。
    /// </summary>
    public partial class CsvToMarkdownWindow : Window
    {
        /// <summary>
        /// 双方向更新時の無限ループを防止するためのフラグ。
        /// </summary>
        private bool _isUpdating = false;

        public CsvToMarkdownWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// CSV入力テキストが変更された際のイベントハンドラ。
        /// Markdownテーブルへの変換を実行します。
        /// </summary>
        private void CsvInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            ConvertCsvToMd();
        }

        /// <summary>
        /// Markdown出力テキストが変更された際のイベントハンドラ。
        /// CSVへの逆変換を実行します。
        /// </summary>
        private void MdOutput_TextChanged(object sender, TextChangedEventArgs e)
        {
            ConvertMdToCsv();
        }

        /// <summary>
        /// CSV形式のテキストをMarkdownテーブル形式に変換します。
        /// </summary>
        private void ConvertCsvToMd()
        {
            if (_isUpdating) return;
            _isUpdating = true;
            try
            {
                var csv = CsvInput.Text;
                if (string.IsNullOrWhiteSpace(csv))
                {
                    MdOutput.Text = "";
                    return;
                }

                // 空行を除外して行ごとに分割
                var lines = csv.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                               .Where(l => !string.IsNullOrWhiteSpace(l))
                               .ToList();

                if (lines.Count == 0)
                {
                    MdOutput.Text = "";
                    return;
                }

                var sb = new StringBuilder();
                
                // 各行をカンマで分割し、トリミングしてパース
                var rows = lines.Select(line => line.Split(',').Select(c => c.Trim()).ToList()).ToList();
                
                // 表全体の最大列数を取得
                int maxCols = rows.Max(r => r.Count);

                // ヘッダー行の生成
                sb.Append("|");
                for (int i = 0; i < maxCols; i++)
                {
                    var val = i < rows[0].Count ? rows[0][i] : "";
                    sb.Append($" {val} |");
                }
                sb.AppendLine();

                // 区切り行（セパレーター）の生成
                sb.Append("|");
                for (int i = 0; i < maxCols; i++)
                {
                    sb.Append("---|");
                }
                sb.AppendLine();

                // データ行の生成
                for (int r = 1; r < rows.Count; r++)
                {
                    sb.Append("|");
                    for (int c = 0; c < maxCols; c++)
                    {
                        var val = c < rows[r].Count ? rows[r][c] : "";
                        sb.Append($" {val} |");
                    }
                    sb.AppendLine();
                }

                MdOutput.Text = sb.ToString();
            }
            finally
            {
                _isUpdating = false;
            }
        }

        /// <summary>
        /// Markdownテーブル形式のテキストをCSV形式に変換します。
        /// </summary>
        private void ConvertMdToCsv()
        {
            if (_isUpdating) return;
            _isUpdating = true;
            try
            {
                var md = MdOutput.Text;
                if (string.IsNullOrWhiteSpace(md))
                {
                    CsvInput.Text = "";
                    return;
                }

                // 行ごとに分割し、空行を除外
                var lines = md.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                               .Select(l => l.Trim())
                               .Where(l => !string.IsNullOrWhiteSpace(l))
                               .ToList();

                var csvLines = new List<string>();
                foreach (var line in lines)
                {
                    // 区切り行（---|---）はスキップ
                    if (IsSeparatorLine(line)) continue;

                    var content = line;
                    // 行頭と行末のパイプ記号を削除
                    if (content.StartsWith("|")) content = content.Substring(1);
                    if (content.EndsWith("|")) content = content.Substring(0, content.Length - 1);

                    // パイプで分割してセルを取得し、トリミングして結合
                    var cells = content.Split('|').Select(c => c.Trim());
                    csvLines.Add(string.Join(",", cells));
                }

                CsvInput.Text = string.Join(Environment.NewLine, csvLines);
            }
            finally
            {
                _isUpdating = false;
            }
        }

        /// <summary>
        /// 指定された行がMarkdownテーブルのセパレーター行（例: |---|---|）かどうかを判定します。
        /// </summary>
        /// <param name="line">判定対象の行</param>
        /// <returns>セパレーター行であればtrue</returns>
        private bool IsSeparatorLine(string line)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) return false;
            
            bool hasHyphen = false;
            foreach (char c in trimmed)
            {
                if (c == '-') hasHyphen = true;
                // パイプ、コロン、ハイフン、空白以外の文字が含まれていればセパレーターではない
                else if (c != '|' && c != ':' && !char.IsWhiteSpace(c)) return false;
            }
            return hasHyphen;
        }

        /// <summary>
        /// CSVの内容をクリップボードにコピーします。
        /// </summary>
        private void BtnCopyCsv_Click(object sender, RoutedEventArgs e)
        {
            CopyContent(CsvInput.Text);
        }

        /// <summary>
        /// Markdownの内容をクリップボードにコピーします。
        /// </summary>
        private void BtnCopyMd_Click(object sender, RoutedEventArgs e)
        {
            CopyContent(MdOutput.Text);
        }

        /// <summary>
        /// 指定されたテキストをクリップボードにコピーし、通知を表示します。
        /// </summary>
        private void CopyContent(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Clipboard.SetText(text);
            MessageBox.Show("クリップボードにコピーしました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
