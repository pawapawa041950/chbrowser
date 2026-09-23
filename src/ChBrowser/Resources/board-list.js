// ChBrowser 板一覧 (Phase 14a) renderer.
// ページ (シェル) は 1 回だけ読み込み、中身は C# から setBoardTree (JSON) で受け取って組み立てる。
// 1 掲示板分の更新はその掲示板のノードだけ差し替えるので、スクロール位置・選択・絞り込み・他の掲示板の開閉はそのまま。
// 階層は <details>/<summary> なのでブラウザ標準の Ctrl+F で閉じたカテゴリも自動展開される。
//
// JS → C# メッセージ:
//   { type: 'ready' }                                      — シェル読み込み完了 (C# は中身を全部送る)
//   { type: 'openBoard', host, directoryName, name }       — 板クリック or ダブルクリック (設定による)
//   { type: 'setCategoryExpanded', categoryName, expanded } — カテゴリの開閉トグル
//   { type: 'providerAction', providerId, action }         — 板一覧の無い掲示板の「検索」(search) /「表示済み板」(shown)
//   { type: 'contextMenu', target: 'board', host, directoryName, name } — 板右クリック
//   { type: 'shortcut'|'gesture', descriptor }              — Phase 16: ブリッジから dispatch 要求
// C# → JS:
//   { type: 'setBoardTree', full: bool, providers: [...] } — 中身。full なら全体を作り直し、そうでなければ該当掲示板のノードだけ差し替え
//       provider: { id, name, expanded, hasBoardList, supportsSearch, boardCount, emptyMessage, categories: [{ name, expanded, boards: [{ host, dir, name }] }] }
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
    // 現在の絞り込み (setPaneSearch)。中身を差し替えたときに当て直す
    var paneSearchQuery = '';

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

    // 板一覧を持たない掲示板 (したらば / reddit) の「検索」「表示済み板」項目 → C# に処理を依頼 (1 クリックで動く)
    root.addEventListener('click', function (e) {
        var act = e.target.closest && e.target.closest('li.provider-action');
        if (!act) return;
        e.stopImmediatePropagation();
        if (selected) selected.classList.remove('selected');
        act.classList.add('selected');
        selected = act;
        post({ type: 'providerAction', providerId: act.dataset.provider, action: act.dataset.action });
    }, true);

    // 板クリック → ハイライト + (1 クリック設定 ON なら) 開く
    root.addEventListener('click', function (e) {
        var li = e.target.closest && e.target.closest('li.board');
        if (!li || li.classList.contains('provider-action')) return;
        if (selected) selected.classList.remove('selected');
        li.classList.add('selected');
        selected = li;
        if (openOnSingleClick) openLi(li);
    });

    // 板ダブルクリック → 1 クリック設定 OFF のときだけ開く
    root.addEventListener('dblclick', function (e) {
        var li = e.target.closest && e.target.closest('li.board');
        if (!li || li.classList.contains('provider-action')) return;
        if (!openOnSingleClick) openLi(li);
    });

    // ---------- 中身の組み立て (setBoardTree) ----------

    function esc(t) {
        return String(t == null ? '' : t)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    /** 掲示板ノード 1 つ分の HTML (<details class="provider">)。 */
    function providerHtml(pv) {
        var h = '<details class="provider"' + (pv.expanded ? ' open' : '') + ' data-provider="' + esc(pv.id) + '">'
              + '<summary class="provider-name">' + esc(pv.name)
              + (typeof pv.boardCount === 'number' ? ' <span class="provider-count">' + pv.boardCount + '</span>' : '')
              + '</summary>';
        if (!pv.hasBoardList) {
            // 板一覧を持たない掲示板 (したらば / reddit): 「検索」と「表示済み板」。li.board と同じ見た目で、provider-action で見分ける
            h += '<ul class="boards provider-actions">';
            if (pv.supportsSearch)
                h += '<li class="board provider-action" data-provider="' + esc(pv.id) + '" data-action="search">\u{1F50D} 検索</li>';
            h += '<li class="board provider-action" data-provider="' + esc(pv.id) + '" data-action="shown">\u{1F4C1} 表示済み板</li></ul>';
        } else if (pv.emptyMessage) {
            h += '<div class="provider-empty">' + esc(pv.emptyMessage) + '</div>';
        } else {
            var cats = pv.categories || [];
            for (var i = 0; i < cats.length; i++) {
                var c = cats[i];
                h += '<details class="category"' + (c.expanded ? ' open' : '') + ' data-category="' + esc(c.name) + '" data-provider="' + esc(pv.id) + '">'
                   + '<summary class="category-name">' + esc(c.name) + '</summary><ul class="boards">';
                var bs = c.boards || [];
                for (var j = 0; j < bs.length; j++) {
                    var b = bs[j];
                    h += '<li class="board" data-host="' + esc(b.host) + '" data-dir="' + esc(b.dir) + '" data-name="' + esc(b.name) + '">' + esc(b.name) + '</li>';
                }
                h += '</ul></details>';
            }
        }
        return h + '</details>';
    }

    function applyBoardTree(msg) {
        var providers = Array.isArray(msg.providers) ? msg.providers : [];
        // 選択中の板 (または「検索」等の項目) を覚えておき、作り直した後に付け直す
        var sel = selected && selected.isConnected
            ? { host: selected.dataset.host, dir: selected.dataset.dir, provider: selected.dataset.provider, action: selected.dataset.action }
            : null;

        if (msg.full) {
            var html = '';
            for (var i = 0; i < providers.length; i++) html += providerHtml(providers[i]);
            root.innerHTML = html;
        } else {
            for (var k = 0; k < providers.length; k++) {
                var pv  = providers[k];
                var cur = root.querySelector('details.provider[data-provider="' + CSS.escape(pv.id) + '"]');
                if (cur) cur.outerHTML = providerHtml(pv);
                else     root.insertAdjacentHTML('beforeend', providerHtml(pv));
            }
        }

        selected = null;
        if (sel) {
            var q = sel.action
                ? 'li.provider-action[data-provider="' + CSS.escape(sel.provider || '') + '"][data-action="' + CSS.escape(sel.action) + '"]'
                : 'li.board[data-host="' + CSS.escape(sel.host || '') + '"][data-dir="' + CSS.escape(sel.dir || '') + '"]';
            var el = root.querySelector(q);
            if (el) { el.classList.add('selected'); selected = el; }
        }
        // 絞り込み中なら新しい中身にも当てる
        if (paneSearchQuery) applyPaneSearch(paneSearchQuery);
    }

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
                paneSearchQuery = typeof msg.query === 'string' ? msg.query : '';
                applyPaneSearch(paneSearchQuery);
            } else if (msg.type === 'setBoardTree') {
                applyBoardTree(msg);
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

        var boards     = root.querySelectorAll('li.board:not(.provider-action)');
        var categories = root.querySelectorAll('details.category, details.provider');

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

    // カテゴリ / 提供者ノードの開閉 → C# 側 ViewModel に同期
    root.addEventListener('toggle', function (e) {
        var d = e.target;
        if (!d || d.tagName !== 'DETAILS') return;
        if (d.classList.contains('provider')) {
            post({ type: 'setProviderExpanded', providerId: d.dataset.provider, expanded: d.open });
            return;
        }
        if (!d.classList.contains('category')) return;
        post({
            type:         'setCategoryExpanded',
            categoryName: d.dataset.category,
            providerId:   d.dataset.provider || '',
            expanded:     d.open,
        });
    }, true);

    // 板を右クリック → ブラウザ既定メニューを抑制して C# に通知 (WPF ContextMenu を popup させる)
    root.addEventListener('contextmenu', function (e) {
        var li = e.target.closest && e.target.closest('li.board');
        if (!li || li.classList.contains('provider-action')) return;
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

    // シェルの準備完了 → C# が中身 (setBoardTree full) を送る。ページを読み直したとき (CSS 変更等) も同じ経路で復元される
    post({ type: 'ready' });
})();
