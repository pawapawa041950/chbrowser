// ChBrowser 板一覧 (Phase 14a) renderer.
// 階層は <details>/<summary> なのでブラウザ標準の Ctrl+F で閉じたカテゴリも自動展開される。
// JS はイベントハンドラだけを担当 (postMessage で C# にユーザ操作を通知)。
//
// JS → C# メッセージ:
//   { type: 'openBoard', host, directoryName, name }       — 板クリック or ダブルクリック (設定による)
//   { type: 'setCategoryExpanded', categoryName, expanded } — カテゴリの開閉トグル
//   { type: 'contextMenu', target: 'board', host, directoryName, name } — 板右クリック
//   { type: 'shortcut'|'gesture', descriptor }              — Phase 16: ブリッジから dispatch 要求
// C# → JS:
//   { type: 'setConfig', openOnSingleClick: bool }         — Phase 11b: クリック動作の設定
//   { type: 'setShortcutBindings', bindings: {...} }        — Phase 16

(function () {
    'use strict';

    // Phase 16: ショートカット / マウス操作 / マウスジェスチャーブリッジを初期化。
    // 左ペインなのでアドレスバー連動は不要 → paneActivated は送らない。
    var Shortcut = window.createShortcutBridge({ localActions: {}, sendPaneActivated: false });

    var root = document.getElementById('board-list');
    if (!root) return;

    var selected = null;

    // Phase 11b: デフォルト ON (= 1 クリックで開く)。setConfig で C# から上書きされる。
    var openOnSingleClick = true;

    function post(msg) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(msg);
        }
    }

    function openLi(li) {
        post({
            type:           'openBoard',
            host:           li.dataset.host,
            directoryName:  li.dataset.dir,
            name:           li.dataset.name,
        });
    }

    // 板クリック → ハイライト + (1 クリック設定 ON なら) 開く
    root.addEventListener('click', function (e) {
        var li = e.target.closest && e.target.closest('li.board');
        if (!li) return;
        if (selected) selected.classList.remove('selected');
        li.classList.add('selected');
        selected = li;
        if (openOnSingleClick) openLi(li);
    });

    // 板ダブルクリック → 1 クリック設定 OFF のときだけ開く
    root.addEventListener('dblclick', function (e) {
        var li = e.target.closest && e.target.closest('li.board');
        if (!li) return;
        if (!openOnSingleClick) openLi(li);
    });

    // C# からの setConfig / setPaneSearch / setShortcutBindings 受信
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (e) {
            var msg = e.data;
            if (!msg || !msg.type) return;
            if (msg.type === 'setConfig') {
                if (typeof msg.openOnSingleClick === 'boolean') {
                    openOnSingleClick = msg.openOnSingleClick;
                }
            } else if (msg.type === 'setPaneSearch') {
                applyPaneSearch(typeof msg.query === 'string' ? msg.query : '');
            }
            // setShortcutBindings は shortcut-bridge.js 内で受信。
        });
    }

    // ---------- 絞り込み (板一覧) ----------
    // 板名 (= li.board の textContent or data-name) でマッチ判定。ヒットした板 + その属する
    // <details class="category"> を表示し、それ以外の板は filter-hidden。空文字でリセット。
    // ハイライトは li.board 内の text node に <mark.search-highlight> を挿入。

    function clearHighlights() {
        var marks = root.querySelectorAll('mark.search-highlight');
        for (var i = 0; i < marks.length; i++) {
            var m = marks[i];
            var t = document.createTextNode(m.textContent || '');
            m.parentNode.replaceChild(t, m);
        }
        if (marks.length > 0) root.normalize();
    }

    function highlightInElement(el, queryLower, queryLen) {
        if (!el) return;
        var texts = [];
        var walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, null);
        while (walker.nextNode()) texts.push(walker.currentNode);
        for (var i = 0; i < texts.length; i++) {
            var tn = texts[i];
            if (!tn.parentNode) continue;
            if (tn.parentNode.classList && tn.parentNode.classList.contains('search-highlight')) continue;
            var text = tn.nodeValue || '';
            var lower = text.toLowerCase();
            var idx = lower.indexOf(queryLower);
            if (idx < 0) continue;
            var frag = document.createDocumentFragment();
            var pos = 0;
            while (idx >= 0) {
                if (idx > pos) frag.appendChild(document.createTextNode(text.slice(pos, idx)));
                var mark = document.createElement('mark');
                mark.className = 'search-highlight';
                mark.textContent = text.slice(idx, idx + queryLen);
                frag.appendChild(mark);
                pos = idx + queryLen;
                idx = lower.indexOf(queryLower, pos);
            }
            if (pos < text.length) frag.appendChild(document.createTextNode(text.slice(pos)));
            tn.parentNode.replaceChild(frag, tn);
        }
    }

    function applyPaneSearch(query) {
        var qLow = (query || '').toLowerCase();
        clearHighlights();

        var boards     = root.querySelectorAll('li.board');
        var categories = root.querySelectorAll('details.category');

        if (!qLow) {
            for (var i = 0; i < boards.length; i++)     boards[i].classList.remove('filter-hidden');
            for (var k = 0; k < categories.length; k++) categories[k].classList.remove('filter-hidden');
            return;
        }
        var qLen = query.length;

        // 各板にマッチ判定 → カテゴリ単位の visible 集計
        var visibleCategories = new Set();
        for (var j = 0; j < boards.length; j++) {
            var li = boards[j];
            var name = (li.dataset.name || li.textContent || '').toLowerCase();
            if (name.indexOf(qLow) >= 0) {
                li.classList.remove('filter-hidden');
                highlightInElement(li, qLow, qLen);
                // 親 details を強制 open + visible
                var p = li.parentElement;
                while (p && p !== root) {
                    if (p.tagName === 'DETAILS') { p.open = true; visibleCategories.add(p); }
                    p = p.parentElement;
                }
            } else {
                li.classList.add('filter-hidden');
            }
        }

        for (var c = 0; c < categories.length; c++) {
            var cat = categories[c];
            if (visibleCategories.has(cat)) cat.classList.remove('filter-hidden');
            else                            cat.classList.add('filter-hidden');
        }
    }

    // カテゴリ開閉 → C# 側 ViewModel に同期
    root.addEventListener('toggle', function (e) {
        var d = e.target;
        if (!d || d.tagName !== 'DETAILS') return;
        if (!d.classList.contains('category')) return;
        post({
            type:         'setCategoryExpanded',
            categoryName: d.dataset.category,
            expanded:     d.open,
        });
    }, true);

    // 板を右クリック → ブラウザ既定メニューを抑制して C# に通知 (WPF ContextMenu を popup させる)
    root.addEventListener('contextmenu', function (e) {
        var li = e.target.closest && e.target.closest('li.board');
        if (!li) return;
        e.preventDefault();
        // 選択ハイライトも合わせる
        if (selected) selected.classList.remove('selected');
        li.classList.add('selected');
        selected = li;
        post({
            type:           'contextMenu',
            target:         'board',
            host:           li.dataset.host,
            directoryName:  li.dataset.dir,
            name:           li.dataset.name,
        });
    });
})();
