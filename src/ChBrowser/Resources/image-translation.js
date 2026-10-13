// ChBrowser 画像の文字の AI 翻訳 (右クリック「AIで翻訳」) の描画。スレ表示 (サムネイルのポップアップ) と
// 画像ビューアの詳細ペインで共通。各シェルの JS の前に連結して読み込まれる (WebView2Helper)。
//
// 訳の状態の形は C# の ImageTranslator.PageState と同じ:
//   { state: 'pending' } / { state: 'error', message } / { state: 'done', items: [{ where, text }], model }
// 置く場所 (ポップアップ / 詳細ペインのセクション) と外枠の見た目は各画面が決める。中身の見た目 (itr-*) はここで入れる。

(function () {
    'use strict';

    var CSS =
        '.itr-item+.itr-item{margin-top:.45em;padding-top:.45em;border-top:1px dashed rgba(127,127,127,.4)}' +
        '.itr-where{display:block;font-size:11px;font-weight:bold;opacity:.75;margin-bottom:.1em}' +
        '.itr-text{white-space:pre-wrap;word-break:break-word;line-height:1.5}' +
        '.itr-status{opacity:.75}' +
        '.itr-pending{animation:itrPulse 1s ease-in-out infinite alternate}' +
        '.itr-error{color:#d05050;opacity:1}' +
        '@keyframes itrPulse{from{opacity:.45}to{opacity:.9}}';

    function injectCss() {
        if (document.getElementById('itr-style')) return;
        var st = document.createElement('style');
        st.id = 'itr-style';
        st.textContent = CSS;
        (document.head || document.documentElement).appendChild(st);
    }
    if (document.head) injectCss(); else document.addEventListener('DOMContentLoaded', injectCss);

    function esc(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    /** 訳の状態 → HTML (中身だけ)。t が無ければ空文字。 */
    function html(t) {
        if (!t) return '';
        if (t.state === 'pending') return '<div class="itr-status itr-pending">画像の文字を翻訳中…</div>';
        if (t.state === 'error')   return '<div class="itr-status itr-error">' + esc(t.message || '翻訳に失敗しました') + '</div>';
        var items = t.items || [];
        if (items.length === 0)    return '<div class="itr-status">(画像に翻訳する文字は見つかりませんでした)</div>';
        return items.map(function (it) {
            return '<div class="itr-item">' + (it.where ? '<span class="itr-where">' + esc(it.where) + '</span>' : '') +
                   '<div class="itr-text">' + esc(it.text || '') + '</div></div>';
        }).join('');
    }

    window.ChImageTranslation = { html: html };
})();
