// ChBrowser お気に入りペイン (Phase 14b) renderer.
// 階層は <details>/<summary> なのでブラウザ標準の Ctrl+F で閉じたフォルダも自動展開される。
// JS はイベント送信だけ担当。状態の正本は C# 側 (FavoritesViewModel)。
//
// JS → C# メッセージ:
//   { type: 'openFavorite',     id }                                — クリック or ダブルクリックで開く
//   { type: 'setFolderExpanded', id, expanded }                     — フォルダ <details> トグル
//   { type: 'moveFavorite',     sourceId, targetId, position }      — D&D で移動
//   { type: 'contextMenu',      target, id }
//   { type: 'shortcut'|'gesture', descriptor }                      — Phase 16: ブリッジから dispatch 要求
// C# → JS:
//   { type: 'setConfig', openOnSingleClick: bool }                  — Phase 11b: クリック動作の設定
//   { type: 'setShortcutBindings', bindings: {...} }                 — Phase 16

(function () {
    'use strict';

    // Phase 16: ショートカット / マウス操作 / マウスジェスチャーブリッジを初期化。
    // 左ペインなのでアドレスバー連動は不要 → paneActivated は送らない。
    var Shortcut = window.createShortcutBridge({ localActions: {}, sendPaneActivated: false });

    var root = document.getElementById('favorites-root');
    if (!root) return;

    // Phase 11b: 設定で「1 クリックで開く」を切替。デフォルトは ON (= 1 クリックで開く)。
    var openOnSingleClick = true;

    function post(msg) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(msg);
        }
    }

    function findItem(target) {
        return target.closest && target.closest('li.fav-item');
    }

    var selected = null;
    function setSelected(li) {
        if (selected) selected.classList.remove('selected');
        if (li) li.classList.add('selected');
        selected = li;
    }

    // ---- クリック: 1 クリック設定なら開く、そうでなければ選択のみ。
    //      フォルダ / 仮想ルート / 機能フォルダは「ツリー開閉のみ」(= summary の details 標準動作に任せる) ----
    root.addEventListener('click', function (e) {
        var li = findItem(e.target);
        if (!li) return;
        setSelected(li);
        var t = li.dataset.type;
        if (t === 'folder' || t === 'virtual-root' || t === 'function-folder') return;
        if (t === 'all-logs') {
            // クリック設定に関わらず単一クリックで開く (機能項目)
            post({ type: 'openAllLogs' });
            return;
        }
        if (openOnSingleClick) {
            post({ type: 'openFavorite', id: li.dataset.id });
        }
    });

    // ---- ダブルクリック: 1 クリック設定 OFF のときだけ開く (フォルダ / 仮想ルート / 機能は抑止) ----
    root.addEventListener('dblclick', function (e) {
        var li = findItem(e.target);
        if (!li) return;
        var t = li.dataset.type;
        if (t === 'folder' || t === 'virtual-root' || t === 'function-folder') {
            // フォルダ <details> 自動トグルは preventDefault しないと dblclick で展開動作が起きる
            e.preventDefault();
            return;
        }
        if (t === 'all-logs') {
            e.preventDefault();
            return; // single-click 経路で処理済
        }
        if (!openOnSingleClick) {
            post({ type: 'openFavorite', id: li.dataset.id });
        }
        e.preventDefault();
    });

    // ---- C# からの setConfig / setPaneSearch / setShortcutBindings 受信 ----
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

    // ---------- 絞り込み (お気に入りツリー) ----------
    // 各 .fav-item の .label テキストでマッチ判定。ヒットしたエントリ + その全祖先を表示し、
    // それ以外は filter-hidden で隠す。フォルダがマッチした場合は配下も全部見せる (= 文脈を温存)。
    // ハイライトはマッチしたエントリの .label 内に <mark.search-highlight> を挿入。

    /** 既存ハイライトを全 unwrap → text node を結合。 */
    function clearHighlights() {
        var marks = root.querySelectorAll('mark.search-highlight');
        for (var i = 0; i < marks.length; i++) {
            var m = marks[i];
            var t = document.createTextNode(m.textContent || '');
            m.parentNode.replaceChild(t, m);
        }
        if (marks.length > 0) root.normalize();
    }

    /** label の text node を走査してクエリ一致箇所を <mark> で囲む。 */
    function highlightLabel(labelEl, queryLower, queryLen) {
        if (!labelEl) return;
        var texts = [];
        var walker = document.createTreeWalker(labelEl, NodeFilter.SHOW_TEXT, null);
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

        // 全 fav-item を一旦可視に戻す + <details> open 状態をユーザのまま保持。
        var items = root.querySelectorAll('li.fav-item');
        if (!qLow) {
            for (var i = 0; i < items.length; i++) items[i].classList.remove('filter-hidden');
            return;
        }
        var qLen = query.length;

        // Step 1: マッチしたエントリの集合を作る (label の直接テキストを比較)。
        var matched = new Set();
        for (var j = 0; j < items.length; j++) {
            var li = items[j];
            // label は li 直下の summary > .label (フォルダ) または li 直下の .label (board/thread)。
            var label = li.querySelector(':scope > details > summary > .label, :scope > .label');
            if (!label) continue;
            if ((label.textContent || '').toLowerCase().indexOf(qLow) >= 0) matched.add(li);
        }

        // Step 2: keep セット = マッチ + その全祖先 + マッチ folder の全子孫。
        var keep = new Set();
        matched.forEach(function (li) {
            keep.add(li);
            // 全祖先 (= 親方向の li.fav-item)
            var p = li.parentElement;
            while (p && p !== root.parentElement) {
                if (p.classList && p.classList.contains('fav-item')) keep.add(p);
                p = p.parentElement;
            }
            // フォルダがヒットした場合は子孫も全部見せる
            li.querySelectorAll('li.fav-item').forEach(function (d) { keep.add(d); });
        });

        // Step 3: items を keep 判定で表示 / 非表示。
        for (var k = 0; k < items.length; k++) {
            var it = items[k];
            if (keep.has(it)) {
                it.classList.remove('filter-hidden');
                // 祖先 <details> を強制 open しないとマッチが見えない
                var d = it.querySelector(':scope > details');
                if (d) d.open = true;
                var ancestor = it.parentElement;
                while (ancestor && ancestor !== root.parentElement) {
                    if (ancestor.tagName === 'DETAILS') ancestor.open = true;
                    ancestor = ancestor.parentElement;
                }
            } else {
                it.classList.add('filter-hidden');
            }
        }

        // Step 4: マッチ label をハイライト
        matched.forEach(function (li) {
            var lbl = li.querySelector(':scope > details > summary > .label, :scope > .label');
            highlightLabel(lbl, qLow, qLen);
        });
    }

    // ---- フォルダ <details> 開閉 → C# に同期 ----
    root.addEventListener('toggle', function (e) {
        var d = e.target;
        if (!d || d.tagName !== 'DETAILS') return;
        if (!d.classList.contains('folder')) return;
        var li = d.closest('li.fav-item');
        if (!li) return;
        post({
            type:     'setFolderExpanded',
            id:       li.dataset.id,
            expanded: d.open,
        });
    }, true);

    // ---- 右クリック → C# に通知して WPF ContextMenu を popup ----
    root.addEventListener('contextmenu', function (e) {
        e.preventDefault();
        var li = findItem(e.target);
        if (!li) {
            // 空エリア (= 通常は仮想ルート li が画面を埋めるので発火しない fallback パス)
            post({ type: 'contextMenu', target: 'empty' });
            return;
        }
        var t = li.dataset.type;
        // 機能フォルダ / 全ログ には現状コンテキストメニューを出さない
        if (t === 'function-folder' || t === 'all-logs') return;
        setSelected(li);
        post({
            type:   'contextMenu',
            target: t,                 // 'folder' | 'board' | 'thread' | 'virtual-root'
            id:     li.dataset.id || null,
        });
    });

    // ---- HTML5 D&D ----
    var draggingId = null;
    var lastDropTarget = null;
    var lastDropPosition = null;

    function clearDropIndicators() {
        if (lastDropTarget) {
            lastDropTarget.classList.remove('drop-target-into', 'drop-target-before', 'drop-target-after');
        }
        lastDropTarget = null;
        lastDropPosition = null;
    }

    root.addEventListener('dragstart', function (e) {
        var li = findItem(e.target);
        // 仮想ルート / 機能フォルダ / 全ログ は drag 不可 (= 永続化対象でないので動かしようがない)
        if (!li) { e.preventDefault(); return; }
        var t = li.dataset.type;
        if (t === 'virtual-root' || t === 'function-folder' || t === 'all-logs') {
            e.preventDefault();
            return;
        }
        draggingId = li.dataset.id;
        li.classList.add('drag-source');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', draggingId);
    });

    root.addEventListener('dragend', function (e) {
        var li = findItem(e.target);
        if (li) li.classList.remove('drag-source');
        clearDropIndicators();
        draggingId = null;
    });

    /** target li 内のどこに落ちようとしているかを座標から判定。
     *  フォルダ: 中央 50% を 'inside'、上 25% を 'before'、下 25% を 'after'。
     *  仮想ルート: 'inside' のみ (兄弟挿入は不可)。
     *  非フォルダ: 上半分 'before'、下半分 'after' (= フォルダではないので inside 不可)。 */
    function pickPosition(li, e) {
        if (li.dataset.type === 'virtual-root') return 'inside';
        var rect = li.getBoundingClientRect();
        var y    = e.clientY - rect.top;
        var h    = rect.height || 1;
        var ratio = y / h;
        if (li.dataset.type === 'folder') {
            if (ratio < 0.25) return 'before';
            if (ratio > 0.75) return 'after';
            return 'inside';
        } else {
            return ratio < 0.5 ? 'before' : 'after';
        }
    }

    root.addEventListener('dragover', function (e) {
        if (!draggingId) return;
        var li = findItem(e.target);
        if (!li) {
            // 空エリアは「ルート末尾に追加」として 'after' on root の最終子として扱う
            clearDropIndicators();
            e.preventDefault(); // drop 受け入れ
            e.dataTransfer.dropEffect = 'move';
            return;
        }
        // 機能フォルダ / 全ログ は drop 受け付け不可
        if (li.dataset.type === 'function-folder' || li.dataset.type === 'all-logs') {
            clearDropIndicators();
            e.dataTransfer.dropEffect = 'none';
            return;
        }
        if (li.dataset.id === draggingId) {
            // 自分自身には落とせない
            clearDropIndicators();
            e.dataTransfer.dropEffect = 'none';
            return;
        }
        var pos = pickPosition(li, e);
        if (lastDropTarget && (lastDropTarget !== li || lastDropPosition !== pos)) {
            clearDropIndicators();
        }
        lastDropTarget   = li;
        lastDropPosition = pos;
        li.classList.remove('drop-target-into', 'drop-target-before', 'drop-target-after');
        if      (pos === 'inside') li.classList.add('drop-target-into');
        else if (pos === 'before') li.classList.add('drop-target-before');
        else                       li.classList.add('drop-target-after');
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
    });

    root.addEventListener('dragleave', function (e) {
        // li 内の子要素間移動でもチラつかないよう、target が li の外にいったときだけクリア
        if (!e.relatedTarget || !root.contains(e.relatedTarget)) clearDropIndicators();
    });

    root.addEventListener('drop', function (e) {
        e.preventDefault();
        if (!draggingId) return;
        var li = findItem(e.target);
        // 機能フォルダ / 全ログへの drop は無視
        if (li && (li.dataset.type === 'function-folder' || li.dataset.type === 'all-logs')) {
            clearDropIndicators();
            draggingId = null;
            return;
        }
        var targetId = li ? (li.dataset.id || null) : null;
        var position = li ? (lastDropPosition || pickPosition(li, e)) : 'rootEnd';
        // 仮想ルートへの drop は「ルート末尾へ移動」と等価
        if (li && li.dataset.type === 'virtual-root') {
            targetId = null;
            position = 'rootEnd';
        }
        clearDropIndicators();
        post({
            type:     'moveFavorite',
            sourceId: draggingId,
            targetId: targetId,
            position: position,
        });
        draggingId = null;
    });
})();
