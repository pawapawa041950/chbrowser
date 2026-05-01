// ChBrowser スレ一覧 (Phase 14a で WebView 化、Phase 11d で外出し)。
// JS → C#:
//   { type: 'openThread', host, directoryName, key, title, logState }   — クリック or ダブルクリックで開く
//   { type: 'paneActivated' }                                            — Phase 14: pane 内任意の mousedown (アドレスバー切替用)
// C# → JS:
//   { type: 'updateLogMarks', value: { changes: [{key, state}, ...] } } — 増分マーク更新
//   { type: 'setConfig', openOnSingleClick: bool }                       — Phase 11b: クリック動作の設定

(function() {
    'use strict';

    // Phase 16: ショートカット / マウス操作 / マウスジェスチャーブリッジを初期化。
    // 共通実装は shortcut-bridge.js の window.createShortcutBridge にある。
    // mousedown 時の paneActivated もここで送信される (Phase 14 アドレスバー対応統合)。
    var Shortcut = window.createShortcutBridge({
        localActions: {
            'thread_list.scroll_top':    function() { window.chScrollPage(false); },
            'thread_list.scroll_bottom': function() { window.chScrollPage(true);  },
        },
    });

    var selected = null;
    var tbody = document.querySelector('tbody');
    if (!tbody) return;
    var collator = new Intl.Collator('ja', { numeric: true, sensitivity: 'base' });

    // Phase 11b: デフォルト ON (= 1 クリックで開く)。setConfig で C# から上書きされる。
    var openOnSingleClick = true;

    function sortBy(key, type, dir) {
        var rows = Array.prototype.slice.call(tbody.querySelectorAll('tr'));
        rows.sort(function(a, b) {
            var av = a.dataset[key];
            var bv = b.dataset[key];
            var cmp;
            if (type === 'num') {
                cmp = parseFloat(av) - parseFloat(bv);
            } else {
                cmp = collator.compare(av || '', bv || '');
            }
            return cmp * dir;
        });
        var frag = document.createDocumentFragment();
        rows.forEach(function(r) { frag.appendChild(r); });
        tbody.appendChild(frag);
    }

    document.querySelectorAll('thead th.sortable').forEach(function(th) {
        th.addEventListener('click', function() {
            var isAsc = th.classList.contains('sort-asc');
            var dir   = isAsc ? -1 : 1;
            document.querySelectorAll('thead th.sortable').forEach(function(o) {
                o.classList.remove('sort-asc', 'sort-desc');
            });
            th.classList.add(dir === 1 ? 'sort-asc' : 'sort-desc');
            sortBy(th.dataset.sort, th.dataset.sortType, dir);
        });
    });

    function openTr(tr) {
        if (!window.chrome || !window.chrome.webview) return;
        // host/dir/key/title を全部送る (C# 側で Board と ThreadInfo を再構築するため、
        // 出元板に依存しないお気に入りディレクトリ表示でも動くようにする)
        window.chrome.webview.postMessage({
            type:          'openThread',
            host:          tr.dataset.host,
            directoryName: tr.dataset.dir,
            key:           tr.dataset.key,
            title:         tr.dataset.title,
            logState:      parseInt(tr.dataset.log, 10) || 0,
        });
    }

    tbody.addEventListener('click', function(e) {
        var tr = e.target.closest && e.target.closest('tr');
        if (!tr) return;
        if (selected) selected.classList.remove('selected');
        tr.classList.add('selected');
        selected = tr;
        if (openOnSingleClick) openTr(tr);
    });

    tbody.addEventListener('dblclick', function(e) {
        var tr = e.target.closest && e.target.closest('tr');
        if (!tr) return;
        if (!openOnSingleClick) openTr(tr);
    });

    // C# からの増分通知 (LogMarkUpdate / setConfig) を受信
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function(e) {
            var msg = e.data;
            if (!msg || !msg.type) return;
            if (msg.type === 'updateLogMarks' && msg.value) {
                var changes = msg.value.changes || [];
                changes.forEach(function(change) {
                    // 集約タブ (全ログ / お気に入り板として開く) では同じ key が異なる板に存在しうるので、
                    // host + dir + key の 3 つで一意な行を特定する。
                    var sel = 'tr[data-host="' + change.host + '"]'
                            + '[data-dir="'  + change.directoryName + '"]'
                            + '[data-key="'  + change.key + '"]';
                    var tr = tbody.querySelector(sel);
                    if (!tr) return;
                    tr.classList.remove('has-log', 'has-update', 'has-dropped');
                    var sortVal = 0;
                    if      (change.state === 'cached')  { tr.classList.add('has-log');     sortVal = 1; }
                    else if (change.state === 'updated') { tr.classList.add('has-update');  sortVal = 2; }
                    else if (change.state === 'dropped') { tr.classList.add('has-dropped'); sortVal = 3; }
                    tr.dataset.log = String(sortVal);
                });
            } else if (msg.type === 'updateFavorited' && msg.value) {
                // お気に入り増分更新: 行の is-favorited クラスを toggle (★ 背景表示)。
                var fchanges = msg.value.changes || [];
                fchanges.forEach(function(change) {
                    var sel = 'tr[data-host="' + change.host + '"]'
                            + '[data-dir="'  + change.directoryName + '"]'
                            + '[data-key="'  + change.key + '"]';
                    var tr = tbody.querySelector(sel);
                    if (!tr) return;
                    if (change.isFavorited) tr.classList.add('is-favorited');
                    else                    tr.classList.remove('is-favorited');
                });
            } else if (msg.type === 'setConfig') {
                if (typeof msg.openOnSingleClick === 'boolean') {
                    openOnSingleClick = msg.openOnSingleClick;
                }
            }
            // setShortcutBindings は shortcut-bridge.js 内で直接受信して処理する。
        });
    }
})();
