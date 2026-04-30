// ChBrowser お気に入りペイン (Phase 14b) renderer.
// 階層は <details>/<summary> なのでブラウザ標準の Ctrl+F で閉じたフォルダも自動展開される。
// JS はイベント送信だけ担当。状態の正本は C# 側 (FavoritesViewModel)。
//
// JS → C# メッセージ:
//   { type: 'openFavorite',     id }                                — クリック or ダブルクリックで開く
//   { type: 'setFolderExpanded', id, expanded }                     — フォルダ <details> トグル
//   { type: 'moveFavorite',     sourceId, targetId, position }      — D&D で移動
//   { type: 'contextMenu',      target, id }
// C# → JS:
//   { type: 'setConfig', openOnSingleClick: bool }                  — Phase 11b: クリック動作の設定

(function () {
    'use strict';

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

    // ---- クリック: 1 クリック設定なら開く、そうでなければ選択のみ ----
    root.addEventListener('click', function (e) {
        var li = findItem(e.target);
        if (!li) return;
        setSelected(li);
        if (openOnSingleClick) {
            post({ type: 'openFavorite', id: li.dataset.id });
        }
    });

    // ---- ダブルクリック: 1 クリック設定 OFF のときだけ開く ----
    root.addEventListener('dblclick', function (e) {
        var li = findItem(e.target);
        if (!li) return;
        if (!openOnSingleClick) {
            post({ type: 'openFavorite', id: li.dataset.id });
        }
        // フォルダ <details> 自動トグルは preventDefault しないと dblclick で展開動作が起きる
        e.preventDefault();
    });

    // ---- C# からの setConfig 受信 (Phase 11b) ----
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (e) {
            var msg = e.data;
            if (!msg || msg.type !== 'setConfig') return;
            if (typeof msg.openOnSingleClick === 'boolean') {
                openOnSingleClick = msg.openOnSingleClick;
            }
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
            // 空エリア (ルートのコンテキストメニュー)
            post({ type: 'contextMenu', target: 'empty' });
            return;
        }
        setSelected(li);
        post({
            type:   'contextMenu',
            target: li.dataset.type,   // 'folder' | 'board' | 'thread'
            id:     li.dataset.id,
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
        if (!li) { e.preventDefault(); return; }
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
     *  非フォルダ: 上半分 'before'、下半分 'after' (= フォルダではないので inside 不可)。 */
    function pickPosition(li, e) {
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
        var targetId = li ? li.dataset.id : null;
        var position = li ? (lastDropPosition || pickPosition(li, e)) : 'rootEnd';
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
