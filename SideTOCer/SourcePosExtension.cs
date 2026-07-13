using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace SideTOCer
{
    /// <summary>
    /// Markdig拡張: HTML要素にソースコード内での位置情報 (data-sourcepos) を付与します。
    /// これにより、プレビュー画面とエディタの間で同期スクロールや選択範囲の同期が可能になります。
    /// </summary>
    public class SourcePosExtension : IMarkdownExtension
    {
        /// <summary>
        /// パイプラインのセットアップ。詳細なソース位置情報を有効にします。
        /// </summary>
        /// <param name="pipeline">Markdownパイプラインビルダー</param>
        public void Setup(MarkdownPipelineBuilder pipeline)
        {
            pipeline.PreciseSourceLocation = true;
        }

        /// <summary>
        /// レンダラーのセットアップ。HTML出力時に属性を追加するイベントを登録します。
        /// </summary>
        /// <param name="pipeline">Markdownパイプライン</param>
        /// <param name="renderer">レンダラー</param>
        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is HtmlRenderer htmlRenderer)
            {
                htmlRenderer.ObjectWriteBefore += OnObjectWriteBefore;
            }
        }

        /// <summary>
        /// 各Markdownオブジェクトが書き込まれる前に実行されるイベントハンドラ。
        /// </summary>
        private void OnObjectWriteBefore(IMarkdownRenderer renderer, MarkdownObject obj)
        {
            // LiteralInline以外（タグを生成するもの）に位置情報を付与
            if (obj is Block || (obj is Markdig.Syntax.Inlines.Inline && obj is not Markdig.Syntax.Inlines.LiteralInline))
            {
                var attributes = obj.GetAttributes();
                // 絶対位置 (開始インデックス-終了インデックス) を属性として追加
                // 例: data-sourcepos="10-25"
                attributes.AddProperty("data-sourcepos", $"{obj.Span.Start}-{obj.Span.End}");
            }
        }
    }

    /// <summary>
    /// SourcePosExtension をパイプラインに登録するための拡張メソッドを提供します。
    /// </summary>
    public static class SourcePosExtensionExtensions
    {
        /// <summary>
        /// MarkdownPipeline で SourcePosExtension を使用するように設定します。
        /// </summary>
        /// <param name="pipeline">Markdownパイプラインビルダー</param>
        /// <returns>設定後のパイプラインビルダー</returns>
        public static MarkdownPipelineBuilder UseSourcePos(this MarkdownPipelineBuilder pipeline)
        {
            if (!pipeline.Extensions.Contains<SourcePosExtension>())
            {
                pipeline.Extensions.Add(new SourcePosExtension());
            }
            return pipeline;
        }
    }
}
