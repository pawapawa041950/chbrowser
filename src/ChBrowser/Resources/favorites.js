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

    // ---- C# からの setConfig / setShortcutBindings 受信 ----
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (e) {
            var msg = e.data;
            if (!msg || !msg.type) return;
            if (msg.type === 'setConfig') {
                if (typeof msg.openOnSingleClick === 'boolean') {
                    openOnSingleClick = msg.openOnSingleClick;
                }
            }
            // setShortcutBindings は shortcut-bridge.js 内で受信。
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
