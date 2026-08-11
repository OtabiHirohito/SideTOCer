using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Windows.Media;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace SideTOCer
{
    /// <summary>
    /// メインウィンドウ。エディタ、プレビュー、目次表示、および各種管理機能を提供します。
    /// </summary>
    public partial class MainWindow : Window
    {
        // ── フィールド ──
        private string? _markdownBaseDirectory;
        private string? _currentMarkdownPath;
        private bool _isDirty;
        private bool _isSearching;
        private readonly ConcurrentDictionary<string, string> _base64Cache = new();

        // 検索ステート
        private int _currentSearchIndex = -1;
        private string _lastSearchQuery = "";

        // Markdigパイプライン
        private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseSourcePos()
            .Build();

        private readonly MarkdownPipeline _exportPipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        /// <summary>
        /// 目次（TOC）の各項目を表すクラス。
        /// </summary>
        public class TocEntry
        {
            /// <summary>見出しレベル (1-6)</summary>
            public int Level { get; init; }
            /// <summary>見出しのテキスト</summary>
            public string Text { get; init; } = "";
            /// <summary>HTML要素のID（アンカー用）</summary>
            public string Id { get; init; } = "";
            /// <summary>自動採番の接頭辞（例: "1.2. "）</summary>
            public string DisplayPrefix { get; init; } = "";

            /// <summary>表示用のレベル文字列（例: "H1"）</summary>
            public string LevelDisplay => $"H{Level}";
            /// <summary>レベルに応じたインデント幅</summary>
            public Thickness Indent => new Thickness((Level - 1) * 14, 0, 0, 0);
            /// <summary>採番とテキストを合わせたフルテキスト</summary>
            public string FullText => DisplayPrefix + Text;
            /// <summary>H1のみ太字にするためのフォントウェイト</summary>
            public FontWeight FontWeight => Level == 1 ? FontWeights.Bold : FontWeights.Normal;
            /// <summary>レベルに応じたフォントサイズ</summary>
            public double FontSize => Level == 1 ? 13 : 12;
        }
        private List<TocEntry> _toc = new();

        // デバウンス用タイマー
        private System.Windows.Threading.DispatcherTimer? _debounce;
        private bool _isWebViewReady;
        private int _renderVersion;
        private bool _isInitialized;
        private bool _isRendering; // レンダリング中フラグ

        // ── プレビュー用HTMLテンプレート ──
        private const string CodeBlockCss = """
            .code-block-wrapper{margin:20px 0}
            .code-block-toolbar{display:flex;justify-content:flex-end;margin-bottom:6px}
            .code-copy-btn{padding:4px 10px;font-size:12px;background:#f0f0f0;border:1px solid #ccc;border-radius:4px;cursor:pointer;color:#333;line-height:1.4}
            .code-copy-btn:hover{background:#e0e0e0}
            .code-copy-btn:disabled{opacity:.7;cursor:default}
            .dark-mode .code-copy-btn{background:#333;color:#eee;border-color:#444}
            .dark-mode .code-copy-btn:hover{background:#444}
            """;

        private const string ImageLightboxCss = """
            img.lightbox-enabled{cursor:zoom-in}
            .lightbox-overlay{position:fixed;inset:0;background:rgba(0,0,0,.86);display:none;align-items:center;justify-content:center;padding:24px;z-index:2000}
            .lightbox-overlay.is-open{display:flex}
            .lightbox-panel{position:relative;max-width:min(96vw,1400px);max-height:92vh;display:flex;flex-direction:column;gap:10px}
            .lightbox-image{max-width:96vw;max-height:82vh;object-fit:contain;border-radius:8px;box-shadow:0 10px 40px rgba(0,0,0,.45);background:#111}
            .lightbox-caption{color:#f0f0f0;font-size:13px;line-height:1.5;text-align:center;max-width:96vw;overflow-wrap:anywhere}
            .lightbox-close{position:absolute;top:-12px;right:-12px;width:34px;height:34px;border:none;border-radius:999px;background:#fff;color:#111;font-size:20px;line-height:1;cursor:pointer;box-shadow:0 2px 10px rgba(0,0,0,.35)}
            .lightbox-close:hover{background:#e5e5e5}
            """;

        private const string CodeBlockScript = """
            function copyCodeBlock(button) {
                const wrapper = button.closest('.code-block-wrapper');
                const pre = wrapper ? wrapper.querySelector('pre') : null;
                if (!pre) return;

                const code = pre.querySelector('code');
                const text = code ? code.textContent : pre.textContent;
                if (!text) return;

                const flashState = (label, disabled, timeout = 1200) => {
                    const previous = button.textContent;
                    button.textContent = label;
                    button.disabled = disabled;
                    if (timeout > 0) {
                        window.setTimeout(() => {
                            button.textContent = previous;
                            button.disabled = false;
                        }, timeout);
                    }
                };

                const copyWithFallback = () => {
                    const textarea = document.createElement('textarea');
                    textarea.value = text;
                    textarea.setAttribute('readonly', '');
                    textarea.style.position = 'fixed';
                    textarea.style.opacity = '0';
                    textarea.style.left = '-9999px';
                    document.body.appendChild(textarea);
                    textarea.select();
                    textarea.setSelectionRange(0, textarea.value.length);
                    const copied = document.execCommand('copy');
                    document.body.removeChild(textarea);
                    return copied;
                };

                Promise.resolve()
                    .then(async () => {
                        if (navigator.clipboard && window.isSecureContext) {
                            await navigator.clipboard.writeText(text);
                            return true;
                        }
                        return copyWithFallback();
                    })
                    .then(copied => {
                        flashState(copied ? 'コピー済み' : 'コピー失敗', copied, copied ? 1200 : 1600);
                    })
                    .catch(() => {
                        flashState('コピー失敗', false, 1600);
                    });
            }

            function decorateCodeBlocks() {
                const root = document.getElementById('markdown-body') || document.querySelector('main') || document.body;
                if (!root) return;

                root.querySelectorAll('pre').forEach(pre => {
                    if (pre.closest('.code-block-wrapper')) return;
                    if (pre.classList.contains('mermaid') || pre.querySelector('code.language-mermaid')) return;
                    if (pre.closest('.mermaid')) return;

                    const wrapper = document.createElement('div');
                    wrapper.className = 'code-block-wrapper';

                    const toolbar = document.createElement('div');
                    toolbar.className = 'code-block-toolbar';

                    const button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'code-copy-btn';
                    button.textContent = 'コピー';
                    button.addEventListener('click', () => copyCodeBlock(button));

                    toolbar.appendChild(button);
                    pre.parentNode?.insertBefore(wrapper, pre);
                    wrapper.appendChild(toolbar);
                    wrapper.appendChild(pre);
                });
            }
            """;

        private const string ImageLightboxScript = """
            function ensureLightbox() {
                let overlay = document.getElementById('image-lightbox-overlay');
                if (overlay) return overlay;

                overlay = document.createElement('div');
                overlay.id = 'image-lightbox-overlay';
                overlay.className = 'lightbox-overlay';
                overlay.innerHTML = `
                    <div class="lightbox-panel" role="dialog" aria-modal="true" aria-label="画像の拡大表示">
                        <button type="button" class="lightbox-close" aria-label="閉じる">×</button>
                        <img class="lightbox-image" alt="">
                        <div class="lightbox-caption"></div>
                    </div>`;
                document.body.appendChild(overlay);

                const close = () => closeLightbox();
                overlay.addEventListener('click', e => {
                    if (e.target === overlay) close();
                });
                overlay.querySelector('.lightbox-close')?.addEventListener('click', close);
                document.addEventListener('keydown', e => {
                    if (e.key === 'Escape') close();
                });
                return overlay;
            }

            function openLightbox(img) {
                const overlay = ensureLightbox();
                const panelImg = overlay.querySelector('.lightbox-image');
                const caption = overlay.querySelector('.lightbox-caption');
                if (!panelImg || !caption) return;

                panelImg.src = img.currentSrc || img.src;
                panelImg.alt = img.alt || '';
                caption.textContent = img.alt || img.title || '';
                panelImg.style.width = (img.naturalWidth * 2) + 'px';
                panelImg.style.height = (img.naturalHeight * 2) + 'px';
                overlay.classList.add('is-open');
                document.body.style.overflow = 'hidden';
            }

            function closeLightbox() {
                const overlay = document.getElementById('image-lightbox-overlay');
                if (!overlay) return;
                overlay.classList.remove('is-open');
                document.body.style.overflow = '';
                const panelImg = overlay.querySelector('.lightbox-image');
                if (panelImg) {
                    panelImg.style.width = '';
                    panelImg.style.height = '';
                }
            }

            function decorateImages() {
                const root = document.getElementById('markdown-body') || document.querySelector('main') || document.body;
                if (!root) return;

                root.querySelectorAll('img').forEach(img => {
                    let retries = 0;
                    const setHalfWidth = () => {
                        if (img.naturalWidth) {
                            img.style.setProperty('--natural-half-width', (img.naturalWidth / 2) + 'px');
                        } else if (retries < 20) {
                            retries++;
                            setTimeout(setHalfWidth, 50);
                        }
                    };
                    img.addEventListener('load', setHalfWidth);
                    setHalfWidth();

                    if (img.closest('a[href]')) return;

                    img.classList.add('lightbox-enabled');
                    if (img.dataset.lightboxBound === '1') return;
                    img.dataset.lightboxBound = '1';
                    img.addEventListener('click', () => openLightbox(img));
                });

                ensureLightbox();
            }
            """;

        private const string HtmlTemplate = """
            <!DOCTYPE html>
            <html lang="ja">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width,initial-scale=1.0">
            <script src="https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js"></script>
            <style>
            @import url('https://fonts.googleapis.com/css2?family=Noto+Sans+JP:wght@400;700&family=JetBrains+Mono:wght@400;500&display=swap');
            *{box-sizing:border-box;margin:0;padding:0}
            html{height:100%;overflow-y:auto}
            body{height:100%;font-family:'Noto Sans JP',sans-serif;background:#ffffff;color:#1a1a1a;padding:36px 48px;max-width:900px}
            h1,h2,h3,h4,h5,h6{color:#1a1a1a;scroll-margin-top:20px;line-height:1.4}
            h1{font-size:1.9em;font-weight:700;margin:0 0 20px;padding-bottom:10px;border-bottom:2px solid #cccccc}
            h2{font-size:1.4em;font-weight:700;margin:36px 0 14px;padding-left:10px;border-left:4px solid #1a56db}
            h3{font-size:1.15em;font-weight:700;margin:24px 0 10px}
            h4{font-size:1em;font-weight:700;margin:18px 0 8px;color:#595959}
            p{line-height:1.85;margin-bottom:16px}
            code{font-family:'JetBrains Mono',monospace;font-size:.875em;background:#f0f0f0;color:#c0392b;padding:2px 6px;border-radius:3px;border:1px solid #cccccc}
            pre{background:#1a1a1a;color:#f0f0f0;border-radius:6px;padding:20px;overflow-x:auto;margin:0}
            pre code{background:none;border:none;padding:0;color:#f0f0f0;font-size:.9em;line-height:1.7}
            {{CODE_BLOCK_CSS}}
            {{IMAGE_LIGHTBOX_CSS}}
            ul,ol{padding-left:28px;margin-bottom:16px}
            li{line-height:1.8;margin-bottom:4px}
            ul ul,ul ol,ol ul,ol ol{margin:4px 0}
            blockquote{border-left:4px solid #1a56db;padding:10px 18px;margin:20px 0;background:#f0f4ff}
            a{color:#1a56db}
            strong{font-weight:700}em{font-style:italic;color:#595959}
            hr{border:none;border-top:2px solid #cccccc;margin:32px 0}
            .table-scroll{max-width:100%;overflow-x:auto;margin:20px 0}
            .table-scroll table{width:max-content;min-width:100%;border-collapse:collapse;margin:0;font-size:.95em}
            th,td{white-space:nowrap}
            th{background:#e8e8e8;border:1px solid #cccccc;padding:10px 14px;text-align:left;font-weight:700}
            td{border:1px solid #cccccc;padding:9px 14px}
            tr:nth-child(even) td{background:#f8f8f8}
            img{max-width:100%;height:auto}
            body.half-image img:not(.lightbox-image){max-width:100% !important;width:min(var(--natural-half-width, 100%), 50%) !important;height:auto !important}
            details{border:1px solid #cccccc;border-radius:6px;margin:16px 0;background:#fafafa;overflow:hidden}
            details[open]{background:#ffffff}
            summary{padding:12px 16px;font-weight:700;cursor:pointer;color:#1a56db;list-style:none;display:flex;align-items:center;gap:8px;border-bottom:1px solid transparent}
            details[open]>summary{border-bottom-color:#eeeeee;background:#fafafa}
            summary::-webkit-details-marker{display:none}
            summary::before{content:'▶';font-size:10px;color:#595959;transition:transform .2s}
            details[open]>summary::before{transform:rotate(90deg)}
            /* 入れ子のアコーディオンの調整 */
            details details{margin:1px 0 1px 16px}
            details>:not(summary){padding:12px 16px}
            details ul,details ol{padding-left:30px;margin:8px 0}
            .img-missing{display:inline-block;padding:4px 8px;background:#fff3cd;border:1px solid #ffc107;border-radius:4px;font-size:.85em;color:#856404}
            /* 追加機能のスタイル */
            .action-bar{margin-bottom:20px;display:flex;gap:10px;border-bottom:1px solid #eee;padding-bottom:15px}
            .btn-action{padding:6px 12px;font-size:12px;background:#f0f0f0;border:1px solid #ccc;border-radius:4px;cursor:pointer;color:#333}
            .btn-action:hover{background:#e0e0e0}
            #btn-back-to-top{position:fixed;bottom:20px;right:20px;width:44px;height:44px;background:#1a56db;color:white;border:none;border-radius:50%;cursor:pointer;display:none;align-items:center;justify-content:center;box-shadow:0 2px 10px rgba(0,0,0,0.2);font-size:20px;z-index:1000}
            #btn-back-to-top:hover{background:#1547b3}
            
            ::selection { background: #ffeb3b; color: #000; }
            .dark-mode ::selection { background: #fbc02d; color: #000; }
            
            /* 見出し番号振りのスタイル */
            .auto-numbering { counter-reset: h1 h2 h3 h4 h5 h6; }
            
            /* 通常モード (H1が第1章) */
            .auto-numbering:not(.h2-base) h1 { counter-reset: h2; }
            .auto-numbering:not(.h2-base) h1::before { counter-increment: h1; content: counter(h1) ". "; }
            .auto-numbering:not(.h2-base) h2 { counter-reset: h3; }
            .auto-numbering:not(.h2-base) h2::before { counter-increment: h2; content: counter(h1) "." counter(h2) " "; }
            .auto-numbering:not(.h2-base) h3 { counter-reset: h4; }
            .auto-numbering:not(.h2-base) h3::before { counter-increment: h3; content: counter(h1) "." counter(h2) "." counter(h3) " "; }
            
            /* H2ベースモード (H1はタイトル、H2が第1章) */
            .auto-numbering.h2-base h1::before { content: none; }
            .auto-numbering.h2-base h2 { counter-reset: h3; }
            .auto-numbering.h2-base h2::before { counter-increment: h2; content: counter(h2) ". "; }
            .auto-numbering.h2-base h3 { counter-reset: h4; }
            .auto-numbering.h2-base h3::before { counter-increment: h3; content: counter(h2) "." counter(h3) " "; }
            .auto-numbering.h2-base h4 { counter-reset: h5; }
            .auto-numbering.h2-base h4::before { counter-increment: h4; content: counter(h2) "." counter(h3) "." counter(h4) " "; }

            /* Mermaid 描画用の特別スタイル */
            pre.mermaid {
                background: none !important;
                border: none !important;
                padding: 0 !important;
                overflow: visible !important;
            }

            /* ダークモードスタイル */
            body.dark-mode { background: #1a1a1a; color: #f0f0f0; }
            .dark-mode h1, .dark-mode h2, .dark-mode h3, .dark-mode h4 { color: #ffffff; }
            .dark-mode h1 { border-bottom-color: #444; }
            .dark-mode h2 { border-left-color: #3b82f6; }
            .dark-mode code { background: #333; color: #ff79c6; border-color: #444; }
            .dark-mode pre { background: #0d0d0d; border: 1px solid #333; }
            .dark-mode pre code { color: #f8f8f2; }
            .dark-mode blockquote { background: #1e293b; border-left-color: #3b82f6; color: #cbd5e1; }
            .dark-mode a { color: #60a5fa; }
            .dark-mode hr { border-top-color: #444; }
            .dark-mode th { background: #333; border-color: #444; color: #fff; }
            .dark-mode td { border-color: #444; }
            .dark-mode tr:nth-child(even) td { background: #222; }
            .dark-mode details { background: #262626; border-color: #444; }
            .dark-mode details[open] { background: #1a1a1a; }
            .dark-mode summary { color: #60a5fa; }
            .dark-mode .btn-action { background: #333; color: #eee; border-color: #444; }
            .dark-mode .btn-action:hover { background: #444; }
            .dark-mode .action-bar { border-bottom-color: #444; }

            /* 検索ハイライト */
            mark.search-highlight { background-color: #ffeb3b; color: #000; border-radius: 2px; }
            mark.search-highlight.active-highlight { background-color: #ff9800; outline: 2px solid #e65100; }
            .dark-mode mark.search-highlight { background-color: #fbc02d; color: #000; }
            .dark-mode mark.search-highlight.active-highlight { background-color: #fb8c00; }
            </style>
            </head>
            <body class="initial-body-class">
            <div id="action-bar-container"></div>
            <div id="markdown-body">{{BODY}}</div>
            <button id="btn-back-to-top" onclick="window.scrollTo({top:0,behavior:'smooth'})" title="上に戻る">↑</button>
            <script>
            let currentSearchQuery = "";
            let currentMatchIndex = -1;

            function setAllDetails(open) {
                document.querySelectorAll('details').forEach(d => d.open = open);
            }

            function highlightAll(text, force = false) {
                if (!force && text === currentSearchQuery) return;
                clearHighlights();
                currentSearchQuery = text;
                if (!text || text.length < 1) return;

                try {
                    const body = document.getElementById('markdown-body');
                    if (!body) return;

                    const regex = new RegExp(text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'gi');
                    
                    const walk = (node) => {
                        if (node.nodeType === 3) {
                            const data = node.data;
                            if (regex.test(data)) {
                                regex.lastIndex = 0;
                                const fragment = document.createDocumentFragment();
                                let lastIndex = 0;
                                let match;
                                while ((match = regex.exec(data)) !== null) {
                                    fragment.appendChild(document.createTextNode(data.substring(lastIndex, match.index)));
                                    const mark = document.createElement('mark');
                                    mark.textContent = match[0];
                                    mark.className = 'search-highlight';
                                    fragment.appendChild(mark);
                                    lastIndex = regex.lastIndex;
                                    if (regex.lastIndex === match.index) regex.lastIndex++; // 無限ループ防止
                                }
                                fragment.appendChild(document.createTextNode(data.substring(lastIndex)));
                                node.parentNode.replaceChild(fragment, node);
                            }
                        } else if (node.nodeType === 1 && node.childNodes && !/(style|script|mark)/i.test(node.tagName)) {
                            Array.from(node.childNodes).forEach(walk);
                        }
                    };
                    walk(body);
                } catch (e) {
                    console.error("Highlight error:", e);
                }
                currentMatchIndex = -1;
            }

            function clearHighlights() {
                try {
                    const marks = document.querySelectorAll('mark.search-highlight');
                    marks.forEach(mark => {
                        if (mark.parentNode) {
                            const text = document.createTextNode(mark.textContent);
                            mark.parentNode.replaceChild(text, mark);
                        }
                    });
                    const body = document.getElementById('markdown-body');
                    if (body) body.normalize();
                } catch (e) {
                    console.error("Clear highlight error:", e);
                }
                currentSearchQuery = "";
                currentMatchIndex = -1;
            }

            {{CODE_BLOCK_SCRIPT}}
            {{IMAGE_LIGHTBOX_SCRIPT}}

            function initMermaid() {
                if (typeof mermaid !== 'undefined') {
                    const isDark = document.body.classList.contains('dark-mode');
                    mermaid.initialize({
                        startOnLoad: false,
                        theme: isDark ? 'dark' : 'default',
                        securityLevel: 'loose'
                    });
                }
            }

            async function renderMermaidDiagrams() {
                if (typeof mermaid === 'undefined') return;
                try {
                    document.querySelectorAll('pre.mermaid, pre>code.language-mermaid').forEach(el => {
                        let text = "";
                        let target = el;
                        if (el.tagName === "CODE") {
                            text = el.textContent;
                            target = el.parentElement;
                        } else {
                            text = el.textContent;
                        }

                        let replacementTarget = target;
                        if (target.parentNode && target.parentNode.classList.contains('code-block-wrapper')) {
                            replacementTarget = target.parentNode;
                        }

                        const div = document.createElement('div');
                        div.className = 'mermaid';
                        div.textContent = text;
                        replacementTarget.parentNode.replaceChild(div, replacementTarget);
                    });

                    document.querySelectorAll('.mermaid').forEach(m => {
                        const wrapper = m.closest('.code-block-wrapper');
                        if (wrapper) {
                            wrapper.parentNode.replaceChild(m, wrapper);
                        }
                        m.querySelectorAll('.code-block-toolbar, .code-copy-btn').forEach(tb => tb.remove());
                    });

                    initMermaid();
                    await mermaid.run({
                        querySelector: '.mermaid',
                        suppressErrors: true
                    });
                } catch (e) {
                    console.error("Mermaid render error:", e);
                }
            }

            function scrollToMatch(index) {
                try {
                    const marks = document.querySelectorAll('mark.search-highlight');
                    if (marks.length === 0) return -1;
                    
                    if (currentMatchIndex >= 0 && currentMatchIndex < marks.length) {
                        marks[currentMatchIndex].classList.remove('active-highlight');
                    }
                    
                    currentMatchIndex = (index % marks.length + marks.length) % marks.length;
                    const target = marks[currentMatchIndex];
                    target.classList.add('active-highlight');
                    target.scrollIntoView({ behavior: 'smooth', block: 'center' });

                    // 同期メッセージの送信（エディタ側のハイライト用）
                    const sourceElement = target.closest('[data-sourcepos]');
                    if (sourceElement) {
                        const pos = sourceElement.getAttribute('data-sourcepos');
                        // この要素内でのこのマークが何番目の一致かカウント
                        const allInElement = Array.from(sourceElement.querySelectorAll('mark.search-highlight'));
                        const indexInElement = allInElement.indexOf(target);
                        
                        window.chrome.webview.postMessage({ 
                            type: 'selection', 
                            pos: pos,
                            isSearchSync: true,
                            query: currentSearchQuery,
                            matchIndexInBlock: indexInElement
                        });
                    }
                    
                    // アコーディオン内にある場合は開く
                    let p = target.parentElement;
                    while (p && p.id !== 'markdown-body') {
                        if (p.tagName === 'DETAILS') p.open = true;
                        p = p.parentElement;
                    }
                    
                    return currentMatchIndex;
                } catch (e) {
                    console.error("ScrollToMatch error:", e);
                    return -1;
                }
            }

            window.onscroll = function() {
                const btn = document.getElementById('btn-back-to-top');
                if (btn) {
                    if (document.body.scrollTop > 100 || document.documentElement.scrollTop > 100) {
                        btn.style.display = 'flex';
                    } else {
                        btn.style.display = 'none';
                    }
                }
            };
            
            // 選択範囲に連動してエディタをハイライト
            function syncSelection() {
                const selection = window.getSelection();
                if (!selection || selection.isCollapsed || selection.rangeCount === 0) return;

                let node = selection.getRangeAt(0).commonAncestorContainer;
                if (node.nodeType !== 1) node = node.parentElement;
                
                const target = node.closest('[data-sourcepos]');
                if (target) {
                    const pos = target.getAttribute('data-sourcepos');
                    window.chrome.webview.postMessage({ type: 'selection', pos: pos });
                }
            }

            document.addEventListener('mouseup', syncSelection);
            document.onselectionchange = function() {
                if (window.getSelection().isCollapsed) return;
                syncSelection();
            };
            </script>
            </body>
            </html>
            """;

        private class ViewerSettings
        {
            public bool OptAccordion { get; set; } = true;
            public bool OptBackToTop { get; set; } = true;
            public bool OptAutoNumber { get; set; } = false;
            public bool OptH2Base { get; set; } = false;
            public bool OptDarkMode { get; set; } = false;
            public bool OptHtmlLight { get; set; } = false;
            public bool OptHalfImage { get; set; } = false;
            public double WindowWidth { get; set; } = 1280;
            public double WindowHeight { get; set; } = 800;
            public WindowState WindowState { get; set; } = WindowState.Normal;
        }

        private string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideTOCer",
            "settings.json");

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            _isInitialized = true;
            InitWebView();

            // コマンドライン引数をチェック（EXEにファイルをドラッグ＆ドロップした場合など）
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
            {
                LoadMarkdownFile(args[1]);
            }
            else
            {
                Editor.Text = GetSampleMarkdown();
            }

            _isDirty = false;
        }

        /// <summary>
        /// 設定ファイルからアプリケーション設定を読み込み、UIに適用します。
        /// </summary>
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<ViewerSettings>(json);
                    if (settings != null)
                    {
                        OptAccordion.IsChecked = settings.OptAccordion;
                        OptBackToTop.IsChecked = settings.OptBackToTop;
                        OptAutoNumber.IsChecked = settings.OptAutoNumber;
                        OptH2Base.IsChecked = settings.OptH2Base;
                        OptDarkMode.IsChecked = settings.OptDarkMode;
                        OptHtmlLight.IsChecked = settings.OptHtmlLight;
                        OptHalfImage.IsChecked = settings.OptHalfImage;

                        if (settings.WindowWidth >= MinWidth)
                        {
                            Width = settings.WindowWidth;
                        }

                        if (settings.WindowHeight >= MinHeight)
                        {
                            Height = settings.WindowHeight;
                        }

                        WindowState = settings.WindowState == WindowState.Maximized
                            ? WindowState.Maximized
                            : WindowState.Normal;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }
            ApplyTheme();
        }

        /// <summary>
        /// 現在のUI設定に基づいてアプリケーションのテーマ（配色）を切り替えます。
        /// </summary>
        private void ApplyTheme()
        {
            bool isDark = OptDarkMode.IsChecked == true;
            var res = Application.Current.Resources;

            if (isDark)
            {
                // ダークモードの配色設定
                res["BrBg"] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
                res["BrSidebar"] = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
                res["BrBorder"] = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
                res["BrText"] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
                res["BrTextLabel"] = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));
                res["BrTextSub"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                res["BrHover"] = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                res["BrSelected"] = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
                res["BrAccent"] = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
                res["BrHeader"] = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17));
                res["BrSearchBtnBg"] = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                res["BrSearchBtnText"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            }
            else
            {
                // ライトモードの配色設定
                res["BrBg"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                res["BrSidebar"] = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
                res["BrBorder"] = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                res["BrText"] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
                res["BrTextLabel"] = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
                res["BrTextSub"] = new SolidColorBrush(Color.FromRgb(0x59, 0x59, 0x59));
                res["BrHover"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5));
                res["BrSelected"] = new SolidColorBrush(Color.FromRgb(0xDB, 0xEA, 0xFE));
                res["BrAccent"] = new SolidColorBrush(Color.FromRgb(0x1A, 0x56, 0xDB));
                res["BrHeader"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x8A));
                res["BrSearchBtnBg"] = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
                res["BrSearchBtnText"] = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
            }
        }

        /// <summary>
        /// 現在の設定をファイルに保存します。
        /// </summary>
        private void SaveSettings()
        {
            try
            {
                var settings = new ViewerSettings
                {
                    OptAccordion = OptAccordion.IsChecked ?? true,
                    OptBackToTop = OptBackToTop.IsChecked ?? true,
                    OptAutoNumber = OptAutoNumber.IsChecked ?? false,
                    OptH2Base = OptH2Base.IsChecked ?? false,
                    OptDarkMode = OptDarkMode.IsChecked ?? false,
                    OptHtmlLight = OptHtmlLight.IsChecked ?? false,
                    OptHalfImage = OptHalfImage.IsChecked ?? false,
                    WindowWidth = WindowState == WindowState.Maximized ? RestoreBounds.Width : Width,
                    WindowHeight = WindowState == WindowState.Maximized ? RestoreBounds.Height : Height,
                    WindowState = WindowState == WindowState.Maximized
                        ? WindowState.Maximized
                        : WindowState.Normal
                };

                var directory = Path.GetDirectoryName(SettingsPath);
                if (directory != null && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        /// <summary>
        /// オプション設定が変更された際のイベントハンドラ。
        /// </summary>
        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            ApplyTheme();
            SaveSettings();
            if (_isWebViewReady) RenderPreview();
        }

        // ── WebView2 初期化 ──
        /// <summary>
        /// WebView2コントロールを初期化し、イベントハンドラを設定します。
        /// </summary>
        private async void InitWebView()
        {
            await Preview.EnsureCoreWebView2Async();
            Preview.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // 仮想サーバーの設定（画像のオンデマンド読み込み用）
            Preview.CoreWebView2.AddWebResourceRequestedFilter("https://sidetocer.app/*", CoreWebView2WebResourceContext.All);
            Preview.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;

            // 外部リンクを既定のブラウザで開く設定
            Preview.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            Preview.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;

            Preview.WebMessageReceived += Preview_WebMessageReceived;
            await LoadPreviewDocumentAsync();
            _isWebViewReady = true;
            RenderPreview();
        }

        /// <summary>
        /// WebView内でのナビゲーション開始時のイベントハンドラ。
        /// HTTP/HTTPSリンクを外部ブラウザで開きます。
        /// </summary>
        private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // http, https プロトコルのみを外部ブラウザで開く
            if (e.Uri.StartsWith("http://") || e.Uri.StartsWith("https://"))
            {
                e.Cancel = true;
                try
                {
                    Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to open URL: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// WebView内での新しいウィンドウ要求時のイベントハンドラ。
        /// target="_blank" のリンクなどを外部ブラウザで開きます。
        /// </summary>
        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open URL: {ex.Message}");
            }
        }

        /// <summary>
        /// WebViewからのリソースリクエストに対するイベントハンドラ。
        /// 仮想ホスト(sidetocer.app/img)へのリクエストをローカルファイルにマッピングします。
        /// </summary>
        private void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var uri = new Uri(e.Request.Uri);
            if (uri.Host != "sidetocer.app" || uri.AbsolutePath != "/img") return;

            // クエリパラメータからパスを取得
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var relPath = query["p"];
            if (string.IsNullOrEmpty(relPath)) return;

            var fullPath = ResolveFullPath(relPath);
            if (fullPath != null && File.Exists(fullPath))
            {
                try
                {
                    var fs = File.OpenRead(fullPath);
                    var mime = GetMime(fullPath);
                    e.Response = Preview.CoreWebView2.Environment.CreateWebResourceResponse(fs, 200, "OK", $"Content-Type: {mime}");
                }
                catch { }
            }
        }

        /// <summary>
        /// WebViewからのメッセージ受信時のイベントハンドラ。
        /// 選択範囲の同期や検索位置の同期を処理します。
        /// </summary>
        private async void Preview_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var type) && type.GetString() == "selection")
                {
                    bool isSearchSync = root.TryGetProperty("isSearchSync", out var isSearch) && isSearch.GetBoolean();
                    var pos = root.GetProperty("pos").GetString();
                    if (string.IsNullOrEmpty(pos)) return;

                    var match = Regex.Match(pos, @"(\d+)-(\d+)");
                    if (!match.Success) return;

                    int start = int.Parse(match.Groups[1].Value);
                    int end = int.Parse(match.Groups[2].Value);
                    if (start < 0 || start >= Editor.Text.Length) return;

                    int length = Math.Max(1, (end - start) + 1);

                    // --- 確実な同期の実装 ---
                    // 検索時も手動選択時も、対象のブロックを「ユーザーが直接ドラッグしたのと全く同じ状態」で選択する
                    Editor.Focus();
                    Editor.Select(start, length);
                    ScrollEditorToMatch(start);
                    UpdateDocumentStatus();

                    // 自動検索同期の場合のみ、検索入力を続けられるようにフォーカスを戻す
                    if (isSearchSync && _isSearching)
                    {
                        await Task.Delay(30);
                        SearchBox.Focus();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Selection sync error: {ex.Message}");
            }
        }

        /// <summary>
        /// プレビュー用のHTMLドキュメントの枠組みをロードします。
        /// </summary>
        private async Task LoadPreviewDocumentAsync()
        {
            var navigationCompleted = new TaskCompletionSource();

            void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                Preview.NavigationCompleted -= OnNavigationCompleted;
                navigationCompleted.TrySetResult();
            }

            Preview.NavigationCompleted += OnNavigationCompleted;
            Preview.NavigateToString(
                HtmlTemplate
                    .Replace("{{BODY}}", "")
                    .Replace("{{CODE_BLOCK_CSS}}", CodeBlockCss)
                    .Replace("{{CODE_BLOCK_SCRIPT}}", CodeBlockScript)
                    .Replace("{{IMAGE_LIGHTBOX_CSS}}", ImageLightboxCss)
                    .Replace("{{IMAGE_LIGHTBOX_SCRIPT}}", ImageLightboxScript));
            await navigationCompleted.Task;
        }

        /// <summary>
        /// エディタのテキストが変更された際のイベントハンドラ。
        /// デバウンス処理を経てプレビューを更新します。
        /// </summary>
        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            _isDirty = true;
            UpdateDocumentStatus();

            _debounce?.Stop();
            _debounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _debounce.Tick += (_, _) => { _debounce.Stop(); RenderPreview(); };
            _debounce.Start();
        }

        // ── マークダウン → HTML変換・プレビュー更新 ──
        /// <summary>
        /// 現在のエディタのテキストをHTMLに変換し、WebView2のプレビューと目次を更新します。
        /// </summary>
        private async void RenderPreview()
        {
            if (!_isWebViewReady || _isRendering)
            {
                return;
            }

            var renderVersion = ++_renderVersion;
            var md = Editor.Text;

            // エディタが空の場合
            if (string.IsNullOrWhiteSpace(md))
            {
                TocList.ItemsSource = null;
                var emptyHtml = JsonSerializer.Serialize("<div style='font-family:sans-serif;color:#888;padding:4px'>マークダウンを入力してください</div>");
                await Preview.ExecuteScriptAsync($"document.getElementById('markdown-body').innerHTML = {emptyHtml};");
                return;
            }

            _isRendering = true;
            try
            {
                // UIスレッドで現在の設定を取得
                bool optAutoNumber = OptAutoNumber.IsChecked == true;
                bool optH2Base = OptH2Base.IsChecked == true;
                bool optAccordion = OptAccordion.IsChecked == true;
                bool optBackToTop = OptBackToTop.IsChecked == true;
                bool optDarkMode = OptDarkMode.IsChecked == true;
                bool optHalfImage = OptHalfImage.IsChecked == true;

                // 重いレンダリング処理を別タスクで実行
                var resultTuple = await Task.Run(() =>
                {
                    var document = Markdown.Parse(md, _pipeline);
                    var newToc = BuildTocFromDocument(document, optAutoNumber, optH2Base);

                    string renderedHtml;
                    using (var writer = new StringWriter())
                    {
                        var renderer = new HtmlRenderer(writer);
                        _pipeline.Setup(renderer);
                        renderer.Render(document);
                        renderedHtml = writer.ToString();
                    }

                    // 追加のHTML加工
                    renderedHtml = ResolveImages(renderedHtml);
                    renderedHtml = WrapTables(renderedHtml);

                    return (Html: renderedHtml, Toc: newToc);
                });

                // 処理中に新しいレンダリング要求が来ていた場合は、この結果を破棄
                if (renderVersion != _renderVersion) return;

                // 目次の更新
                _toc = resultTuple.Toc;
                TocList.ItemsSource = _toc;
                UpdateDocumentStatus();

                // WebView2内のHTMLを更新
                var encodedHtml = JsonSerializer.Serialize(resultTuple.Html);
                var resultScript = await Preview.ExecuteScriptAsync($$"""
                    (() => {
                        const body = document.getElementById('markdown-body');
                        if (body) {
                            body.innerHTML = {{encodedHtml}};
                            decorateCodeBlocks();
                            decorateImages();
                            
                            // 自動採番クラスの制御
                            if ({{(optAutoNumber ? "true" : "false")}}) {
                                document.body.classList.add('auto-numbering');
                                if ({{(optH2Base ? "true" : "false")}}) {
                                    document.body.classList.add('h2-base');
                                    document.body.style.counterReset = 'h2';
                                } else {
                                    document.body.classList.remove('h2-base');
                                    document.body.style.counterReset = 'h1';
                                }
                            } else {
                                document.body.classList.remove('auto-numbering');
                                document.body.classList.remove('h2-base');
                                document.body.style.counterReset = 'none';
                            }
                            
                            // ダークモードの制御
                            if ({{(optDarkMode ? "true" : "false")}}) {
                                document.body.classList.add('dark-mode');
                            } else {
                                document.body.classList.remove('dark-mode');
                            }

                            if (typeof renderMermaidDiagrams === 'function') {
                                renderMermaidDiagrams();
                            }

                            // 画像サイズ半減モードの制御
                            if ({{(optHalfImage ? "true" : "false")}}) {
                                document.body.classList.add('half-image');
                            } else {
                                document.body.classList.remove('half-image');
                            }

                            const btnBack = document.getElementById('btn-back-to-top');
                            if (btnBack) {
                                btnBack.style.visibility = {{(optBackToTop ? "'visible'" : "'hidden'")}};
                            }

                            // 検索中ならハイライトを再適用
                            if ({{(_isSearching ? "true" : "false")}}) {
                                highlightAll({{JsonSerializer.Serialize(SearchBox.Text)}}, true);
                            }

                            return "ok";
                        }
                        return "missing";
                    })();
                """);

                // body要素が見つからない場合はページ全体を再構築
                if (resultScript == "\"missing\"")
                {
                    var fullHtml = HtmlTemplate
                        .Replace("{{BODY}}", $"<div id=\"markdown-body\">{resultTuple.Html}</div>")
                        .Replace("{{CODE_BLOCK_CSS}}", CodeBlockCss)
                        .Replace("{{CODE_BLOCK_SCRIPT}}", CodeBlockScript)
                        .Replace("{{IMAGE_LIGHTBOX_CSS}}", ImageLightboxCss)
                        .Replace("{{IMAGE_LIGHTBOX_SCRIPT}}", ImageLightboxScript);
                    Preview.NavigateToString(fullHtml);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Render error: {ex.Message}");
            }
            finally
            {
                _isRendering = false;
            }
        }

        /// <summary>
        /// HTML内のtable要素をスクロール可能なdivでラップします。
        /// </summary>
        private string WrapTables(string html)
        {
            return Regex.Replace(
                html,
                @"(?is)(<table\b[^>]*>.*?</table>)",
                "<div class=\"table-scroll\">$1</div>");
        }

        // ── 目次構築 (ASTを使用) ──
        /// <summary>
        /// Markdownの抽象構文木(AST)から目次情報を構築します。
        /// </summary>
        private List<TocEntry> BuildTocFromDocument(MarkdownDocument document, bool optAutoNumber, bool optH2Base)
        {
            var toc = new List<TocEntry>();
            int[] counters = new int[7];

            foreach (var heading in document.Descendants<HeadingBlock>())
            {
                var level = heading.Level;
                var text = GetContainerPlainText(heading.Inline);
                var id = heading.GetAttributes().Id ?? "";

                string displayPrefix = "";
                if (optAutoNumber)
                {
                    // 見出しレベルに応じた採番の生成
                    if (optH2Base)
                    {
                        if (level >= 2)
                        {
                            counters[level]++;
                            for (int i = level + 1; i <= 6; i++) counters[i] = 0;
                            if (level == 2) displayPrefix = $"{counters[2]}. ";
                            else if (level == 3) displayPrefix = $"{counters[2]}.{counters[3]} ";
                            else if (level == 4) displayPrefix = $"{counters[2]}.{counters[3]}.{counters[4]} ";
                        }
                    }
                    else
                    {
                        counters[level]++;
                        for (int i = level + 1; i <= 6; i++) counters[i] = 0;
                        if (level == 1) displayPrefix = $"{counters[1]}. ";
                        else if (level == 2) displayPrefix = $"{counters[1]}.{counters[2]} ";
                        else if (level == 3) displayPrefix = $"{counters[1]}.{counters[2]}.{counters[3]} ";
                    }
                }

                toc.Add(new TocEntry
                {
                    Level = level,
                    Text = text,
                    Id = id,
                    DisplayPrefix = displayPrefix
                });
            }
            return toc;
        }

        /// <summary>
        /// Markdigのインライン要素から純粋なテキストのみを抽出します。
        /// </summary>
        private string GetContainerPlainText(Markdig.Syntax.Inlines.ContainerInline? container)
        {
            if (container == null) return "";
            var sb = new StringBuilder();
            foreach (var inline in container)
            {
                if (inline is Markdig.Syntax.Inlines.LiteralInline literal) sb.Append(literal.Content);
                else if (inline is Markdig.Syntax.Inlines.CodeInline code) sb.Append(code.Content);
                else if (inline is Markdig.Syntax.Inlines.LineBreakInline) sb.Append(" ");
                else if (inline is Markdig.Syntax.Inlines.ContainerInline sub) sb.Append(GetContainerPlainText(sub));
                else
                {
                    // その他のインライン（強調、リンクなど）はToString()または再帰的に取得
                    var subContent = inline.ToString();
                    if (subContent != inline.GetType().FullName) sb.Append(subContent);
                }
            }
            return sb.ToString();
        }

        // ── 画像パスを解決 ──
        /// <summary>
        /// HTML内のimgタグのsrc属性を解決します。
        /// プレビュー時は仮想サーバー(https://sidetocer.app/img)経由、
        /// エクスポート時はBase64埋め込みに変換します。
        /// </summary>
        private string ResolveImages(string html, bool isExport = false)
        {
            return Regex.Replace(html, @"<img([^>]*?)src=""([^""]*)""([^>]*?)>", m =>
            {
                var src = m.Groups[2].Value;
                // インターネット上の画像や既にBase64化されているものはそのまま
                if (Regex.IsMatch(src, @"^(https?://|//|data:)")) return m.Value;

                if (isExport)
                {
                    // HTMLエクスポート時は画像をBase64形式で直接埋め込む
                    return GetBase64ImageTag(m, src);
                }
                else
                {
                    // プレビュー時は仮想サーバー経由で読み込む（CoreWebView2_WebResourceRequested で処理）
                    var virtualUrl = $"https://sidetocer.app/img?p={Uri.EscapeDataString(src)}";
                    return m.Value.Replace(src, virtualUrl);
                }
            });
        }

        /// <summary>
        /// 指定された画像パスを読み込み、Base64データURI形式のimgタグを生成します。
        /// </summary>
        private string GetBase64ImageTag(Match m, string src)
        {
            var fullPath = ResolveFullPath(src);
            if (fullPath == null) return m.Value;

            // キャッシュがあればそれを利用
            if (_base64Cache.TryGetValue(fullPath, out var cached))
            {
                return m.Value.Replace(src, cached);
            }

            try
            {
                var bytes = File.ReadAllBytes(fullPath);
                var mime = GetMime(fullPath);
                var b64 = Convert.ToBase64String(bytes);
                var dataUri = $"data:{mime};base64,{b64}";
                _base64Cache.TryAdd(fullPath, dataUri);
                return m.Value.Replace(src, dataUri);
            }
            catch
            {
                return m.Value; // 読み込み失敗時は元のタグを返す
            }
        }

        /// <summary>
        /// 相対パスまたは絶対パスから、実在するファイルのフルパスを解決します。
        /// </summary>
        private string? ResolveFullPath(string src)
        {
            if (string.IsNullOrWhiteSpace(src)) return null;

            // クエリパラメータやフラグメントを削除し、パスを正規化
            var pathPart = src.Split('#')[0].Split('?')[0].Trim();
            try { pathPart = Uri.UnescapeDataString(pathPart).Replace('/', Path.DirectorySeparatorChar); }
            catch { pathPart = pathPart.Replace('/', Path.DirectorySeparatorChar); }

            // 絶対パスか相対パス（現在のMarkdownファイルのディレクトリ基準）かを判定
            var fullPath = Path.IsPathRooted(pathPart)
                ? pathPart
                : _markdownBaseDirectory == null
                    ? pathPart
                    : Path.Combine(_markdownBaseDirectory, pathPart);

            return File.Exists(fullPath) ? fullPath : null;
        }

        /// <summary>
        /// ファイル拡張子に基づいてMIMEタイプを取得します。
        /// </summary>
        private static string GetMime(string path) => Path.GetExtension(path).ToLower() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            _ => "image/png"
        };

        /// <summary>
        /// 現在編集中のファイル名と保存状態（変更あり/なし）をステータスバーに反映します。
        /// </summary>
        private void UpdateDocumentStatus()
        {
            if (_currentMarkdownPath == null)
            {
                DocumentStatusText.Text = "";
            }
            else
            {
                var fileName = Path.GetFileName(_currentMarkdownPath);
                var dirtyMark = _isDirty ? "*" : ""; // 変更がある場合はアスタリスクを表示
                DocumentStatusText.Text = $"{dirtyMark}{fileName}";
            }
        }

        // ── HTML保存 ──
        /// <summary>
        /// 現在のMarkdownをHTMLファイルとしてエクスポートします。
        /// 画像のBase64埋め込み、目次サイドバーの生成、スタイルの適用を含みます。
        /// </summary>
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var md = Editor.Text;
            if (string.IsNullOrWhiteSpace(md))
            {
                MessageBox.Show("マークダウンを入力してください", "確認", MessageBoxButton.OK,
                MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "HTML ファイル|*.html",
                FileName = _currentMarkdownPath == null
                    ? "document.html"
                    : Path.ChangeExtension(Path.GetFileName(_currentMarkdownPath), ".html"),
                Title = "HTMLファイルを保存"
            };
            if (dlg.ShowDialog() != true) return;

            // HTML本体の生成
            var html = Markdown.ToHtml(md, _exportPipeline);
            html = ResolveImages(html, isExport: true);
            html = WrapTables(html);


            // 目次HTMLの生成
            var tocHtml = new StringBuilder();
            int[] tocCounters = new int[7];
            foreach (var entry in _toc)
            {
                var indent = (entry.Level - 1) * 12;
                string displayPrefix = "";
                if (OptAutoNumber!.IsChecked == true)
                {
                    int level = entry.Level;
                    if (OptH2Base!.IsChecked == true)
                    {
                        if (level >= 2)
                        {
                            tocCounters[level]++;
                            for (int i = level + 1; i <= 6; i++) tocCounters[i] = 0;
                            if (level == 2) displayPrefix = $"{tocCounters[2]}. ";
                            else if (level == 3) displayPrefix = $"{tocCounters[2]}.{tocCounters[3]} ";
                            else if (level == 4) displayPrefix = $"{tocCounters[2]}.{tocCounters[3]}.{tocCounters[4]} ";
                        }
                    }
                    else
                    {
                        tocCounters[level]++;
                        for (int i = level + 1; i <= 6; i++) tocCounters[i] = 0;
                        if (level == 1) displayPrefix = $"{tocCounters[1]}. ";
                        else if (level == 2) displayPrefix = $"{tocCounters[1]}.{tocCounters[2]} ";
                        else if (level == 3) displayPrefix = $"{tocCounters[1]}.{tocCounters[2]}.{tocCounters[3]} ";
                    }
                }

                var encodedId = System.Net.WebUtility.HtmlEncode(entry.Id);
                tocHtml.AppendLine(
                    $"<li style=\"padding-left:{indent}px\"><a href=\"#{encodedId}\" data-toc-id=\"{encodedId}\">{System.Net.WebUtility.HtmlEncode(displayPrefix + entry.Text)}</a></li>");
            }

            // エクスポート用CSS（プレビュー用とほぼ同等だが、サイドバー用のスタイルを追加）
            var exportCss =
                "@import url('https://fonts.googleapis.com/css2?family=Noto+Sans+JP:wght@400;700&family=JetBrains+Mono:wght@400;500&display=swap');" +
                "*{box-sizing:border-box;margin:0;padding:0}" +
                "html{scroll-behavior:smooth}" +
                "body{font-family:'Noto Sans JP',sans-serif;background:#ffffff;color:#1a1a1a;display:flex;min-height:100vh}" +
                "nav{width:240px;flex-shrink:0;background:#f5f5f5;border-right:1px solid #cccccc;position:sticky;top:0;height:100vh;overflow-y:auto}" +
                ".nav-title{font-size:12px;font-weight:700;color:#404040;padding:12px 16px;border-bottom:1px solid #cccccc;letter-spacing:.05em}" +
                "nav ul{list-style:none;padding:6px 0}" +
                "nav a{display:block;padding:5px 14px;font-size:13px;color:#1a1a1a;text-decoration:none;border-left:3px solid transparent;transition:all .12s}" +
                "nav a:hover{color:#1a56db;border-left-color:#1a56db;background:#e5e5e5}" +
                "nav a.toc-active, nav a[aria-current='location']{color:#1a56db;background:#dbeafe;border-left-color:#1a56db;font-weight:700}" +
                "main{flex:1;padding:48px 60px;max-width:1200px}" +
                "h1{font-size:1.9em;font-weight:700;margin:0 0 20px;padding-bottom:10px;border-bottom:2px solid #cccccc;scroll-margin-top:20px}" +
                "h2{font-size:1.4em;font-weight:700;margin:36px 0 14px;padding-left:10px;border-left:4px solid #1a56db;scroll-margin-top:20px}" +
                "h3{font-size:1.15em;font-weight:700;margin:24px 0 10px;scroll-margin-top:20px}" +
                "h4{font-size:1em;font-weight:700;margin:18px 0 8px;color:#595959;scroll-margin-top:20px}" +
                "p{line-height:1.85;margin-bottom:16px}" +
                "code{font-family:'JetBrains Mono',monospace;font-size:.875em;background:#f0f0f0;color:#c0392b;padding:2px 6px;border-radius:3px;border:1px solid #cccccc}" +
                "pre{background:#1a1a1a;color:#f0f0f0;border-radius:6px;padding:20px;overflow-x:auto;margin:0}" +
                "pre code{background:none;border:none;padding:0;color:#f0f0f0;font-size:.9em;line-height:1.7}" +
                CodeBlockCss +
                ImageLightboxCss +
                "ul,ol{padding-left:28px;margin-bottom:16px}" +
                "li{line-height:1.8;margin-bottom:4px}" +
                "ul ul,ul ol,ol ul,ol ol{margin:4px 0}" +
                "blockquote{border-left:4px solid #1a56db;padding:10px 18px;margin:20px 0;background:#f0f4ff}" +
                "a{color:#1a56db}strong{font-weight:700}em{font-style:italic;color:#595959}" +
                "hr{border:none;border-top:2px solid #cccccc;margin:32px 0}" +
                ".table-scroll{max-width:100%;overflow-x:auto;margin:20px 0}" +
                ".table-scroll table{width:max-content;min-width:100%;border-collapse:collapse;margin:0;font-size:.95em}" +
                "th,td{white-space:nowrap}" +
                "th{background:#e8e8e8;border:1px solid #cccccc;padding:10px 14px;text-align:left;font-weight:700}" +
                "td{border:1px solid #cccccc;padding:9px 14px}" +
                "tr:nth-child(even) td{background:#f8f8f8}" +
                "img{max-width:100%;height:auto}" +
                "body.half-image img:not(.lightbox-image){max-width:100% !important;width:min(var(--natural-half-width, 100%), 50%) !important;height:auto !important}" +
                "details{border:1px solid #cccccc;border-radius:6px;margin:16px 0;background:#fafafa;overflow:hidden}" +
                "details[open]{background:#ffffff}" +
                "summary{padding:12px 16px;font-weight:700;cursor:pointer;color:#1a56db;list-style:none;display:flex;align-items:center;gap:8px;border-bottom:1px solid transparent}" +
                "details[open]>summary{border-bottom-color:#eeeeee;background:#fafafa}" +
                "summary::-webkit-details-marker{display:none}" +
                "summary::before{content:'▶';font-size:10px;color:#595959;transition:transform .2s}" +
                "details[open]>summary::before{transform:rotate(90deg)}" +
                "details>:not(summary){padding:16px 30px}" +
                "details ul,details ol{padding-left:24px}" +
                ".action-bar{margin-bottom:20px;display:flex;gap:10px;border-bottom:1px solid #eee;padding-bottom:15px}" +
                ".btn-action{padding:6px 12px;font-size:12px;background:#f0f0f0;border:1px solid #ccc;border-radius:4px;cursor:pointer;color:#333;text-decoration:none}" +
                ".btn-action:hover{background:#e0e0e0}" +
                "#btn-back-to-top{position:fixed;bottom:30px;right:30px;width:48px;height:48px;background:#1a56db;color:white;border:none;border-radius:50%;cursor:pointer;display:none;align-items:center;justify-content:center;box-shadow:0 2px 10px rgba(0,0,0,0.2);font-size:24px;z-index:1000;text-decoration:none}" +
                "#btn-back-to-top:hover{background:#1547b3}" +
                ".auto-numbering { counter-reset: h1 h2 h3 h4 h5 h6; }" +
                ".auto-numbering:not(.h2-base) h1 { counter-reset: h2; }" +
                ".auto-numbering:not(.h2-base) h1::before { counter-increment: h1; content: counter(h1) '. '; }" +
                ".auto-numbering:not(.h2-base) h2 { counter-reset: h3; }" +
                ".auto-numbering:not(.h2-base) h2::before { counter-increment: h2; content: counter(h1) '.' counter(h2) ' '; }" +
                ".auto-numbering:not(.h2-base) h3 { counter-reset: h4; }" +
                ".auto-numbering:not(.h2-base) h3::before { counter-increment: h3; content: counter(h1) '.' counter(h2) '.' counter(h3) ' '; }" +
                ".auto-numbering.h2-base h1::before { content: none; }" +
                ".auto-numbering.h2-base h2 { counter-reset: h3; }" +
                ".auto-numbering.h2-base h2::before { counter-increment: h2; content: counter(h2) '. '; }" +
                ".auto-numbering.h2-base h3 { counter-reset: h4; }" +
                ".auto-numbering.h2-base h3::before { counter-increment: h3; content: counter(h2) '.' counter(h3) ' '; }" +
                ".auto-numbering.h2-base h4 { counter-reset: h5; }" +
                ".auto-numbering.h2-base h4::before { counter-increment: h4; content: counter(h2) '.' counter(h3) '.' counter(h4) ' '; }" +
                "body.dark-mode { background: #1a1a1a; color: #f0f0f0; }" +
                ".dark-mode nav { background: #262626; border-right-color: #444; }" +
                ".dark-mode .nav-title { color: #888; border-bottom-color: #444; }" +
                ".dark-mode nav a { color: #ccc; }" +
                ".dark-mode nav a:hover { background: #333; color: #60a5fa; border-left-color: #3b82f6; }" +
                ".dark-mode nav a.toc-active, .dark-mode nav a[aria-current='location'] { background: #1e293b; color: #bfdbfe; border-left-color: #60a5fa; }" +
                ".dark-mode h1, .dark-mode h2, .dark-mode h3, .dark-mode h4 { color: #ffffff; }" +
                ".dark-mode h1 { border-bottom-color: #444; }" +
                ".dark-mode h2 { border-left-color: #3b82f6; }" +
                ".dark-mode code { background: #333; color: #ff79c6; border-color: #444; }" +
                ".dark-mode pre { background: #0d0d0d; border: 1px solid #333; }" +
                ".dark-mode pre code { color: #f8f8f2; }" +
                ".dark-mode blockquote { background: #1e293b; border-left-color: #3b82f6; color: #cbd5e1; }" +
                ".dark-mode a { color: #60a5fa; }" +
                ".dark-mode hr { border-top-color: #444; }" +
                ".dark-mode th { background: #333; border-color: #444; color: #fff; }" +
                ".dark-mode td { border-color: #444; }" +
                ".dark-mode tr:nth-child(even) td { background: #222; }" +
                ".dark-mode details { background: #262626; border-color: #444; }" +
                ".dark-mode details[open] { background: #1a1a1a; }" +
                ".dark-mode summary { color: #60a5fa; }" +
                ".dark-mode .btn-action { background: #333; color: #eee; border-color: #444; }" +
                ".dark-mode .btn-action:hover { background: #444; }" +
                ".dark-mode .action-bar { border-bottom-color: #444; }" +
                ".lightbox-overlay{position:fixed;inset:0;background:rgba(0,0,0,.86);display:none;align-items:center;justify-content:center;padding:24px;z-index:2000}" +
                ".lightbox-overlay.is-open{display:flex}" +
                ".lightbox-panel{position:relative;max-width:min(96vw,1400px);max-height:92vh;display:flex;flex-direction:column;gap:10px}" +
                ".lightbox-image{max-width:96vw;max-height:82vh;object-fit:contain;border-radius:8px;box-shadow:0 10px 40px rgba(0,0,0,.45);background:#111}" +
                ".lightbox-caption{color:#f0f0f0;font-size:13px;line-height:1.5;text-align:center;max-width:96vw;overflow-wrap:anywhere}" +
                ".lightbox-close{position:absolute;top:-12px;right:-12px;width:34px;height:34px;border:none;border-radius:999px;background:#fff;color:#111;font-size:20px;line-height:1;cursor:pointer;box-shadow:0 2px 10px rgba(0,0,0,.35)}" +
                ".lightbox-close:hover{background:#e5e5e5}" +
                "img.lightbox-enabled{cursor:zoom-in}" +
                "pre.mermaid{background:none!important;border:none!important;padding:0!important;overflow:visible!important;}";

            var title = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            var exportHtml = new StringBuilder();
            exportHtml.AppendLine("<!DOCTYPE html>");
            exportHtml.AppendLine("<html lang=\"ja\">");
            exportHtml.AppendLine("<head>");
            exportHtml.AppendLine("<meta charset=\"UTF-8\">");
            exportHtml.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1.0\">");
            exportHtml.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js\"></script>");
            exportHtml.AppendLine($"<title>{title}</title>");
            exportHtml.AppendLine($"<style>{exportCss}</style>");
            exportHtml.AppendLine("</head>");

            var bodyClassList = new List<string>();
            var counterReset = "none";
            if (OptAutoNumber!.IsChecked == true)
            {
                bodyClassList.Add("auto-numbering");
                counterReset = "h1";
                if (OptH2Base!.IsChecked == true)
                {
                    bodyClassList.Add("h2-base");
                    counterReset = "h2";
                }
            }
            if (OptDarkMode!.IsChecked == true && OptHtmlLight!.IsChecked != true)
            {
                bodyClassList.Add("dark-mode");
            }
            if (OptHalfImage!.IsChecked == true)
            {
                bodyClassList.Add("half-image");
            }

            var bodyClass = bodyClassList.Count > 0 ? $" class=\"{string.Join(" ", bodyClassList)}\"" : "";
            var bodyStyle = bodyClassList.Count > 0 ? $" style=\"counter-reset: {counterReset};\"" : "";
            exportHtml.AppendLine($"<body{bodyClass}{bodyStyle}>");

            // 目次サイドバーの開始
            exportHtml.AppendLine("<nav>");
            exportHtml.AppendLine("<div class=\"nav-title\">目次</div>");
            exportHtml.AppendLine($"<ul>{tocHtml}</ul>");

            // アコーディオン一括操作ボタンを目次の下に配置
            if (OptAccordion!.IsChecked == true)
            {
                exportHtml.AppendLine("<div style=\"margin-top: auto; padding: 10px; border-top: 1px solid #cccccc; display: flex; flex-direction: column; gap: 8px;\">");
                exportHtml.AppendLine("<button class=\"btn-action\" onclick=\"setAllDetails(true)\">すべて開く</button>");
                exportHtml.AppendLine("<button class=\"btn-action\" onclick=\"setAllDetails(false)\">すべて閉じる</button>");
                exportHtml.AppendLine("</div>");
            }
            exportHtml.AppendLine("</nav>");

            exportHtml.AppendLine("<main>");
            exportHtml.AppendLine(html);
            exportHtml.AppendLine("</main>");

            if (OptBackToTop.IsChecked == true)
            {
                exportHtml.AppendLine("<button id=\"btn-back-to-top\" onclick=\"window.scrollTo({top:0,behavior:'smooth'})\" title=\"上に戻る\">↑</button>");
            }

            exportHtml.AppendLine("<script>");
            exportHtml.AppendLine("function setAllDetails(open) { document.querySelectorAll('details').forEach(d => d.open = open); }");
            exportHtml.AppendLine(CodeBlockScript);
            exportHtml.AppendLine(ImageLightboxScript);
            exportHtml.AppendLine("function setupTocScrollSpy() {");
            exportHtml.AppendLine("  const nav = document.querySelector('nav');");
            exportHtml.AppendLine("  if (!nav) return;");
            exportHtml.AppendLine("  const links = Array.from(nav.querySelectorAll('a[data-toc-id]'));");
            exportHtml.AppendLine("  const entries = links.map(link => {");
            exportHtml.AppendLine("    const rawId = link.getAttribute('data-toc-id') || '';");
            exportHtml.AppendLine("    const target = document.getElementById(rawId);");
            exportHtml.AppendLine("    return target ? { id: rawId, link, target } : null;");
            exportHtml.AppendLine("  }).filter(Boolean);");
            exportHtml.AppendLine("  if (entries.length === 0) return;");
            exportHtml.AppendLine("  let activeId = '';");
            exportHtml.AppendLine("  const setActive = (id) => {");
            exportHtml.AppendLine("    if (!id || id === activeId) return;");
            exportHtml.AppendLine("    const prev = links.find(link => (link.getAttribute('data-toc-id') || '') === activeId);");
            exportHtml.AppendLine("    if (prev) { prev.classList.remove('toc-active'); prev.removeAttribute('aria-current'); }");
            exportHtml.AppendLine("    const next = links.find(link => (link.getAttribute('data-toc-id') || '') === id);");
            exportHtml.AppendLine("    if (!next) return;");
            exportHtml.AppendLine("    next.classList.add('toc-active');");
            exportHtml.AppendLine("    next.setAttribute('aria-current', 'location');");
            exportHtml.AppendLine("    activeId = id;");
            exportHtml.AppendLine("    next.scrollIntoView({ block: 'nearest' });");
            exportHtml.AppendLine("  };");
            exportHtml.AppendLine("  let ticking = false;");
            exportHtml.AppendLine("  const update = () => {");
            exportHtml.AppendLine("    ticking = false;");
            exportHtml.AppendLine("    const threshold = 140;");
            exportHtml.AppendLine("    let current = entries[0];");
            exportHtml.AppendLine("    for (const entry of entries) {");
            exportHtml.AppendLine("      const top = entry.target.getBoundingClientRect().top;");
            exportHtml.AppendLine("      if (top <= threshold) current = entry; else break;");
            exportHtml.AppendLine("    }");
            exportHtml.AppendLine("    setActive(current ? current.id : '');");
            exportHtml.AppendLine("  };");
            exportHtml.AppendLine("  const requestUpdate = () => {");
            exportHtml.AppendLine("    if (ticking) return;");
            exportHtml.AppendLine("    ticking = true;");
            exportHtml.AppendLine("    requestAnimationFrame(update);");
            exportHtml.AppendLine("  };");
            exportHtml.AppendLine("  window.addEventListener('scroll', requestUpdate, { passive: true });");
            exportHtml.AppendLine("  window.addEventListener('resize', requestUpdate);");
            exportHtml.AppendLine("  window.addEventListener('hashchange', requestUpdate);");
            exportHtml.AppendLine("  requestUpdate();");
            exportHtml.AppendLine("}");
            if (OptBackToTop.IsChecked == true)
            {
                exportHtml.AppendLine("window.onscroll = function() {");
                exportHtml.AppendLine("  const btn = document.getElementById('btn-back-to-top');");
                exportHtml.AppendLine("  if (btn) {");
                exportHtml.AppendLine("    if (document.body.scrollTop > 100 || document.documentElement.scrollTop > 100) { btn.style.display = 'flex'; }");
                exportHtml.AppendLine("    else { btn.style.display = 'none'; }");
                exportHtml.AppendLine("  }");
                exportHtml.AppendLine("};");
            }
            exportHtml.AppendLine("setupTocScrollSpy();");
            exportHtml.AppendLine("decorateCodeBlocks();");
            exportHtml.AppendLine("decorateImages();");
            exportHtml.AppendLine("function initMermaid() {");
            exportHtml.AppendLine("  if (typeof mermaid !== 'undefined') {");
            exportHtml.AppendLine("    const isDark = document.body.classList.contains('dark-mode');");
            exportHtml.AppendLine("    mermaid.initialize({");
            exportHtml.AppendLine("      startOnLoad: false,");
            exportHtml.AppendLine("      theme: isDark ? 'dark' : 'default',");
            exportHtml.AppendLine("      securityLevel: 'loose'");
            exportHtml.AppendLine("    });");
            exportHtml.AppendLine("  }");
            exportHtml.AppendLine("}");
            exportHtml.AppendLine("async function renderMermaidDiagrams() {");
            exportHtml.AppendLine("  if (typeof mermaid === 'undefined') return;");
            exportHtml.AppendLine("  try {");
            exportHtml.AppendLine("    document.querySelectorAll('pre.mermaid, pre>code.language-mermaid').forEach(el => {");
            exportHtml.AppendLine("      let text = '';");
            exportHtml.AppendLine("      let target = el;");
            exportHtml.AppendLine("      if (el.tagName === 'CODE') {");
            exportHtml.AppendLine("        text = el.textContent;");
            exportHtml.AppendLine("        target = el.parentElement;");
            exportHtml.AppendLine("      } else {");
            exportHtml.AppendLine("        text = el.textContent;");
            exportHtml.AppendLine("      }");
            exportHtml.AppendLine("      let replacementTarget = target;");
            exportHtml.AppendLine("      if (target.parentNode && target.parentNode.classList.contains('code-block-wrapper')) {");
            exportHtml.AppendLine("        replacementTarget = target.parentNode;");
            exportHtml.AppendLine("      }");
            exportHtml.AppendLine("      const div = document.createElement('div');");
            exportHtml.AppendLine("      div.className = 'mermaid';");
            exportHtml.AppendLine("      div.textContent = text;");
            exportHtml.AppendLine("      replacementTarget.parentNode.replaceChild(div, replacementTarget);");
            exportHtml.AppendLine("    });");
            exportHtml.AppendLine("    document.querySelectorAll('.mermaid').forEach(m => {");
            exportHtml.AppendLine("      const wrapper = m.closest('.code-block-wrapper');");
            exportHtml.AppendLine("      if (wrapper) {");
            exportHtml.AppendLine("        wrapper.parentNode.replaceChild(m, wrapper);");
            exportHtml.AppendLine("      }");
            exportHtml.AppendLine("      m.querySelectorAll('.code-block-toolbar, .code-copy-btn').forEach(tb => tb.remove());");
            exportHtml.AppendLine("    });");
            exportHtml.AppendLine("    initMermaid();");
            exportHtml.AppendLine("    await mermaid.run({");
            exportHtml.AppendLine("      querySelector: '.mermaid',");
            exportHtml.AppendLine("      suppressErrors: true");
            exportHtml.AppendLine("    });");
            exportHtml.AppendLine("  } catch (e) {");
            exportHtml.AppendLine("    console.error('Mermaid render error:', e);");
            exportHtml.AppendLine("  }");
            exportHtml.AppendLine("}");
            exportHtml.AppendLine("renderMermaidDiagrams();");
            exportHtml.AppendLine("</script>");

            exportHtml.AppendLine("</body>");
            exportHtml.AppendLine("</html>");

            File.WriteAllText(dlg.FileName, exportHtml.ToString(), Encoding.UTF8);
            MessageBox.Show($"保存しました:\n{dlg.FileName}", "完了", MessageBoxButton.OK,
           MessageBoxImage.Information);
        }

        // ── 目次クリック → プレビュースクロール ──
        /// <summary>
        /// 目次の項目が選択された際のイベントハンドラ。
        /// プレビュー画面内の該当する見出しまでスクロールします。
        /// </summary>
        private async void TocList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isWebViewReady) return;

            if (TocList.SelectedItem is not TocEntry entry) return;
            var id = entry.Id;
            if (string.IsNullOrEmpty(id)) return;

            try
            {
                var encodedId = JsonSerializer.Serialize(id);
                await Preview.ExecuteScriptAsync(
                    $$"""
                    (() => {
                        const target = document.getElementById({{encodedId}});
                        if (!target) return false;
                        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
                        return true;
                    })();
                    """);
            }
            catch { /* WebView2未初期化時は無視 */ }
        }

        /// <summary>
        /// すべてのアコーディオンを展開します。
        /// </summary>
        private async void BtnExpandAll_Click(object sender, RoutedEventArgs e)
        {
            if (!_isWebViewReady) return;
            await Preview.ExecuteScriptAsync("setAllDetails(true);");
        }

        /// <summary>
        /// すべてのアコーディオンを閉じます。
        /// </summary>
        private async void BtnCollapseAll_Click(object sender, RoutedEventArgs e)
        {
            if (!_isWebViewReady) return;
            await Preview.ExecuteScriptAsync("setAllDetails(false);");
        }

        // ── ファイル操作 ──
        /// <summary>
        /// 「ファイルを開く」ダイアログを表示し、選択されたMarkdownファイルを読み込みます。
        /// </summary>
        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Markdown ファイル|*.md;*.markdown|すべてのファイル|*.*",
                Title = "Markdownファイルを開く"
            };
            if (dlg.ShowDialog() != true) return;
            LoadMarkdownFile(dlg.FileName);
        }

        /// <summary>
        /// 指定されたパスからMarkdownファイルを読み込み、エディタとプレビューを更新します。
        /// </summary>
        private void LoadMarkdownFile(string path)
        {
            _base64Cache.Clear();
            _currentMarkdownPath = path;
            _markdownBaseDirectory = Path.GetDirectoryName(path);

            Editor.Text = File.ReadAllText(path, Encoding.UTF8);
            _isDirty = false;
            UpdateDocumentStatus();
            RenderPreview();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            SaveMarkdown();
        }

        private void BtnSaveAs_Click(object sender, RoutedEventArgs e)
        {
            SaveMarkdownAs();
        }

        /// <summary>
        /// 現在編集中のファイルを上書き保存します。ファイルが未指定の場合は「名前を付けて保存」を呼び出します。
        /// </summary>
        private bool SaveMarkdown()
        {
            if (string.IsNullOrWhiteSpace(_currentMarkdownPath))
            {
                return SaveMarkdownAs();
            }

            WriteMarkdownFile(_currentMarkdownPath);
            return true;
        }

        /// <summary>
        /// 「名前を付けて保存」ダイアログを表示し、現在の内容をファイルに保存します。
        /// </summary>
        private bool SaveMarkdownAs()
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Markdown ファイル|*.md|Markdown ファイル|*.markdown|すべてのファイル|*.*",
                FileName = _currentMarkdownPath == null ? "document.md" : Path.GetFileName(_currentMarkdownPath),
                InitialDirectory = _markdownBaseDirectory,
                Title = "Markdownファイルを保存"
            };

            if (dlg.ShowDialog() != true) return false;

            _currentMarkdownPath = dlg.FileName;
            _markdownBaseDirectory = Path.GetDirectoryName(dlg.FileName);
            WriteMarkdownFile(dlg.FileName);
            return true;
        }

        /// <summary>
        /// 実際にファイルへの書き込みを行い、ステータスを更新します。
        /// </summary>
        private void WriteMarkdownFile(string path)
        {
            try
            {
                File.WriteAllText(path, Editor.Text, Encoding.UTF8);
                _isDirty = false;
                UpdateDocumentStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// エディタの内容をクリアします。変更がある場合は確認ダイアログを表示します。
        /// </summary>
        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("エディタの内容をクリアしますか？", "確認",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _currentMarkdownPath = null;
                _markdownBaseDirectory = null;
                _isDirty = false;
                DocumentStatusText.Text = "";
                Editor.Clear();
            }
        }

        /// <summary>
        /// ウィンドウ全体でのキー押下イベント。ショートカットキー（保存、検索など）を処理します。
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SaveMarkdown();
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowSearchBar(false);
                e.Handled = true;
            }
            else if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowSearchBar(true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_isSearching)
                {
                    HideSearchBar();
                    e.Handled = true;
                }
            }
        }

        /// <summary>
        /// 検索バーを表示します。
        /// </summary>
        /// <param name="showReplace">置換コントロールを表示するかどうか</param>
        private void ShowSearchBar(bool showReplace = false)
        {
            _isSearching = true;
            if (_isWebViewReady)
            {
                Preview.ExecuteScriptAsync("window.isSearching = true;");
                UpdateWebViewHighlights(SearchBox.Text);
            }
            if (SearchBar != null) SearchBar.Visibility = Visibility.Visible;
            if (ReplaceControls != null) ReplaceControls.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;
            if (SearchNavigationControls != null) SearchNavigationControls.Visibility = showReplace ? Visibility.Collapsed : Visibility.Visible;

            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        /// <summary>
        /// 検索バーを非表示にし、ハイライトをクリアします。
        /// </summary>
        private void HideSearchBar()
        {
            _isSearching = false;
            _lastSearchQuery = "";
            _currentSearchIndex = -1;
            if (_isWebViewReady)
            {
                Preview.ExecuteScriptAsync("window.isSearching = false; clearHighlights();");
            }
            if (SearchBar != null) SearchBar.Visibility = Visibility.Collapsed;
            Editor.Focus();
        }

        /// <summary>
        /// 検索入力欄でのキー押下イベント。Enterで次を検索、Shift+Enterで前を検索します。
        /// </summary>
        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift) SearchPrev();
                else SearchNext();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                HideSearchBar();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 検索テキストが変更された際の処理。リアルタイムにハイライトを更新します。
        /// </summary>
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitialized) return;
            string query = SearchBox.Text;
            if (query == _lastSearchQuery) return;
            _lastSearchQuery = query;
            _currentSearchIndex = -1;

            if (_isWebViewReady)
            {
                UpdateWebViewHighlights(query);
            }
        }

        /// <summary>
        /// WebView内のハイライト表示を更新します。
        /// </summary>
        private async void UpdateWebViewHighlights(string query)
        {
            var escapedQuery = JsonSerializer.Serialize(query);
            await Preview.ExecuteScriptAsync($"highlightAll({escapedQuery});");
        }

        private void BtnSearchNext_Click(object sender, RoutedEventArgs e) => SearchNext();
        private void BtnSearchPrev_Click(object sender, RoutedEventArgs e) => SearchPrev();
        private void BtnCloseSearch_Click(object sender, RoutedEventArgs e) => HideSearchBar();

        /// <summary>
        /// 検索されたすべてのテキストを指定された文字列で置換します。
        /// </summary>
        private void BtnReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            string find = SearchBox.Text;
            string replace = ReplaceBox.Text;
            if (string.IsNullOrEmpty(find)) return;

            try
            {
                string currentText = Editor.Text;
                // 大文字小文字を区別せず、すべて置換
                string newText = Regex.Replace(currentText, Regex.Escape(find), replace, RegexOptions.IgnoreCase);

                if (currentText != newText)
                {
                    int caret = Editor.CaretIndex;
                    Editor.Text = newText;
                    // 置換後のカーソル位置を調整
                    Editor.CaretIndex = Math.Min(caret, newText.Length);
                    Editor.Focus();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"置換中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 次の一致箇所へスクロールします。
        /// </summary>
        private async void SearchNext()
        {
            string query = SearchBox.Text;
            if (string.IsNullOrEmpty(query)) return;

            // WebView検索をトリガーにする。エディタ側のフォーカス移動は同期メッセージ側に任せる。
            if (_isWebViewReady)
            {
                _currentSearchIndex++;
                await Preview.ExecuteScriptAsync($"scrollToMatch({_currentSearchIndex});");
            }
        }

        /// <summary>
        /// 前の一致箇所へスクロールします。
        /// </summary>
        private async void SearchPrev()
        {
            string query = SearchBox.Text;
            if (string.IsNullOrEmpty(query)) return;

            if (_isWebViewReady)
            {
                _currentSearchIndex--;
                await Preview.ExecuteScriptAsync($"scrollToMatch({_currentSearchIndex});");
            }
        }

        /// <summary>
        /// エディタを指定された位置までスクロールし、その行を画面中央付近に表示します。
        /// </summary>
        private void ScrollEditorToMatch(int index)
        {
            if (index < 0 || index >= Editor.Text.Length) return;
            try
            {
                // まず該当行を可視化
                int lineIndex = Editor.GetLineIndexFromCharacterIndex(index);
                Editor.ScrollToLine(lineIndex);

                // 画面中央付近に寄せるための精密なスクロール計算
                Rect rect = Editor.GetRectFromCharacterIndex(index);
                if (!rect.IsEmpty)
                {
                    double targetOffset = Editor.VerticalOffset + rect.Top - (Editor.ActualHeight / 3);
                    Editor.ScrollToVerticalOffset(Math.Max(0, targetOffset));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Scroll error: {ex.Message}");
            }
        }

        /// <summary>
        /// ウィンドウが閉じられる際の処理。未保存の変更がある場合は確認します。
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isDirty)
            {
                var result = MessageBox.Show("変更を保存しますか？", "確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    if (!SaveMarkdown())
                    {
                        e.Cancel = true;
                    }
                }
                else if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                }
            }
            if (!e.Cancel)
            {
                SaveSettings();
            }
            base.OnClosing(e);
        }

        // ── ドラッグ＆ドロップ ──
        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            ProcessDroppedFiles(files);
        }

        private void Editor_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Editor_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            ProcessDroppedFiles(files);
            e.Handled = true;
        }

        private void BtnCsvTool_Click(object sender, RoutedEventArgs e)
        {
            var win = new CsvToMarkdownWindow
            {
                Owner = this
            };
            win.ShowDialog();
        }

        /// <summary>
        /// ドロップされたファイルを処理します。
        /// Markdownファイルなら開き、画像ファイルならリンクとしてエディタに挿入します。
        /// </summary>
        private void ProcessDroppedFiles(string[] files)
        {
            string? mdFile = null;
            var imageExtensions = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg" };
            var imageFiles = new List<string>();

            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLower();
                if (ext is ".md" or ".markdown")
                {
                    if (mdFile == null) mdFile = f;
                }
                else if (imageExtensions.Contains(ext))
                {
                    imageFiles.Add(f);
                }
            }

            // Markdownファイルがあれば優先的に読み込む
            if (mdFile != null)
            {
                LoadMarkdownFile(mdFile);
            }

            // 画像ファイルがあればエディタに挿入
            if (imageFiles.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var img in imageFiles)
                {
                    var name = Path.GetFileNameWithoutExtension(img);
                    // 現在のファイルからの相対パスに変換を試みる
                    string path = img;
                    if (_currentMarkdownPath != null)
                    {
                        try
                        {
                            var mdDir = Path.GetDirectoryName(_currentMarkdownPath);
                            if (mdDir != null)
                            {
                                path = Path.GetRelativePath(mdDir, img);
                            }
                        }
                        catch { }
                    }
                    sb.AppendLine($"![{name}]({path})");
                }

                if (Editor.SelectionLength > 0)
                {
                    Editor.SelectedText = sb.ToString();
                }
                else
                {
                    int caretIndex = Editor.CaretIndex;
                    Editor.Text = Editor.Text.Insert(caretIndex, sb.ToString());
                    Editor.CaretIndex = caretIndex + sb.Length;
                }
                Editor.Focus();
            }
        }

        /// <summary>
        /// 新規起動時に表示するサンプルMarkdownテキストを取得します。
        /// </summary>
        private static string GetSampleMarkdown()
        {
            return
                "# プロジェクト設計書\n\n" +
                "## 概要\n\n" +
                "このアプリは **Markdown → HTML コンバーター** です。\n" +
                "左のエディタに入力すると、リアルタイムでプレビューが表示されます。\n" +
                "**HTML保存**でサイドバーに目次付きのHTMLを保存できます。\n\n" +
                "## 使い方\n\n" +
                "1. 左のエディタにMarkdownを入力する\n" +
                "2. `開く` でファイルを読み込む\n" +
                "3. `HTML保存` でエクスポート\n\n" +
                "## 対応記法\n\n" +
                "### インライン\n\n" +
                "- **太字** `**text**`\n" +
                "- *斜体* `*text*`\n" +
                "- `コード` `` `code` ``\n" +
                "- ~~取り消し~~ `~~text~~`\n\n" +
                "### コードブロック\n\n" +
                "```csharp\n" +
                "var pipeline = new MarkdownPipelineBuilder()\n" +
                "    .UseAdvancedExtensions()\n" +
                "    .Build();\n" +
                "var html = Markdown.ToHtml(markdown, pipeline);\n" +
                "```\n\n" +
                "### 水平線\n" +
                "---\n" +
                "### テーブル\n\n" +
                " | 場所 | 機能名 | 詳細 |\n" +
                " |---|---|---|\n" +
                " | ツールバー（画面上部） | CSV↔MD変換 | CSVとMDの表を相互に変換できます。 |\n" +
                " | ツールバー（画面上部） | 表示・変換設定 | ツールバー中央で「アコーディオン一括操作」「上に戻るボタン」「見出し番号振り」「ダークモード」の有効/無効を切り替えられます。 |\n" +
                " | サイドバー（中央） | 目次表示 | 見出し(# )が自動的に目次として表示され、クリックするとプレビューの該当箇所へジャンプします。 |\n" +
                " | サイドバー（中央） | アコーディオン一括操作 | 「オプションが有効な場合、目次の下に「すべて開く」「すべて閉じる」ボタンが表示されます。」 |\n" +
                " | プレビュー・エディタ | 上に戻るボタン | プレビューをスクロールすると右下に表示されます。 |\n" +
                " | プレビュー・エディタ | ドラッグ＆ドロップ対応 | .mdファイルを直接ドロップでもファイルを開けます。 |\n" +
                " | プレビュー・エディタ | 検索・置換 | `Ctrl + F` で検索、`Ctrl + H` で置換バーを表示します。 |\n" +
                " | プレビュー・エディタ | ハイライト | プレビュー側の文をドラッグ選択することでエディター側がハイライトされます。 |\n" +
                " | プレビュー・エディタ| 画像挿入 | 画像ファイルをエディタにドロップすると、相対パスのリンクが自動挿入されます。 |\n\n" +
                "### 画像\n\n" +
                "オンライン画像の例:\n\n" +
                "[![GITHUB](https://avatars.githubusercontent.com/u/285259335?v=4)](https://github.com/OtabiHirohito)\n\n" +
                "オフライン画像の例（.exeと同じ階層）:\n\n" +
                "![アイコン](SideTOCer.ico)\n\n" +
                "### 引用\n\n" +
                "> MarkdigライブラリによりGFM準拠のMarkdownを変換します。\n\n" +
                "### アコーディオン\n\n" +
                "#### 通常（閉じている状態）\n\n" +
                "<details>\n" +
                "<summary>クリックで展開</summary>\n\n" +
                "中身が隠れています。\n\n" +
                "</details>\n\n" +
                "#### 最初から開く場合\n\n" +
                "<details open>\n" +
                "<summary>最初から開いています</summary>\n\n" +
                "タグに `open` を付与すると、最初から展開された状態で表示されます。\n" +
                "例: `<details open>`\n\n" +
                "</details>\n\n" +
                "### リンク\n\n" +
                "URLリンクの例: [GitHub - OtabiHirohito](https://github.com/OtabiHirohito \"制作者\")\n\n" +
                "### 注釈\n\n" +
                "注釈[^1]もサポートしています。\n\n" +
                "[^1]: ここに注釈の内容が表示されます。\n\n" +
                "### Mermaid記法\n\n" +
                "Mermaid記法によるダイアグラムの描画に対応しています。以下のように記述します。\n\n" +
                "```mermaid\n" +
                "graph TD\n" +
                "    A[スタート] --> B{選択}\n" +
                "    B -- はい --> C[成功]\n" +
                "    B -- いいえ --> D[失敗]\n" +
                "```\n";
        }
    }
}
