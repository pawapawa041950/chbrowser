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

    // 列リサイザ — 各 <th> の右端に <span class="col-resizer"> を差し込み、
    // ドラッグで列幅を変更する。table-layout: fixed なので th.style.width が
    // そのままその列の確定幅になる。最右の列はリサイザ無し (右に列が無いので)。
    // 幅は localStorage("chbrowser.threadlist.colwidths") に data-sort キーで保存。
    var WIDTH_KEY = 'chbrowser.threadlist.colwidths';
    var MIN_COL_WIDTH = 24;

    function loadSavedWidths() {
        try {
            var raw = localStorage.getItem(WIDTH_KEY);
            if (!raw) return {};
            var obj = JSON.parse(raw);
            return (obj && typeof obj === 'object') ? obj : {};
        } catch (_) { return {}; }
    }
    function saveWidths(map) {
        try { localStorage.setItem(WIDTH_KEY, JSON.stringify(map)); } catch (_) {}
    }

    var savedWidths = loadSavedWidths();
    var ths = Array.prototype.slice.call(document.querySelectorAll('thead th'));

    function paddingX(el) {
        var cs = window.getComputedStyle(el);
        return (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.paddingRight) || 0);
    }

    ths.forEach(function(th, idx) {
        var key = th.dataset.sort;
        if (key && savedWidths[key]) {
            th.style.width = savedWidths[key] + 'px';
        }
        if (idx === ths.length - 1) return; // 最右列にはハンドル不要

        var grip = document.createElement('span');
        grip.className = 'col-resizer';
        th.appendChild(grip);

        grip.addEventListener('mousedown', function(e) {
            e.preventDefault();
            e.stopPropagation();   // ソート発火防止
            //
            // table-layout: fixed; width: 100% では、1 列の幅を変えると table 全体の
            // 比例スケールが走り、他列の境界も動く。そこで全列を現在のレンダー幅で
            // 固定したうえで、ドラッグ境界の左右 2 列だけを ±Δ で動かす方式にする。
            //
            // <th> は box-sizing: content-box (table cell の既定) で padding: 4px 8px。
            // getBoundingClientRect は border-box を返すので、style.width に渡す値は
            // padding を引いた content-box 値にしないと開始時点で padding ぶんずれる。
            //
            var startX = e.clientX;
            var snapshot = ths.map(function(t) {
                return { th: t, border: t.getBoundingClientRect().width, pad: paddingX(t) };
            });
            // 全列を現在のレンダー幅で固定 (これで table の自動スケールが止まる)
            snapshot.forEach(function(s) {
                s.th.style.width = Math.max(0, s.border - s.pad) + 'px';
            });

            var aSnap = snapshot[idx];      // 左側 = ドラッグ中の th
            var bSnap = snapshot[idx + 1];  // 右側 = 隣の th
            grip.classList.add('dragging');

            function onMove(ev) {
                var dx = ev.clientX - startX;
                // 両列とも MIN を割らないようにクランプ
                if (aSnap.border + dx < MIN_COL_WIDTH) dx = MIN_COL_WIDTH - aSnap.border;
                if (bSnap.border - dx < MIN_COL_WIDTH) dx = bSnap.border - MIN_COL_WIDTH;
                aSnap.th.style.width = Math.max(0, aSnap.border + dx - aSnap.pad) + 'px';
                bSnap.th.style.width = Math.max(0, bSnap.border - dx - bSnap.pad) + 'px';
            }
            function onUp() {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup',   onUp);
                grip.classList.remove('dragging');
                // ドラッグ中はスケール抑止のため全列を inline width で固定したが、
                // 操作した 2 列以外はクリアして CSS 既定 / title 吸収モードに戻す
                // (window リサイズ時に title が伸縮するように)。
                snapshot.forEach(function(s) {
                    if (s !== aSnap && s !== bSnap) s.th.style.width = '';
                });
                // 保存対象はドラッグした 2 列のみ。
                var aKey = aSnap.th.dataset.sort;
                var bKey = bSnap.th.dataset.sort;
                var aW   = parseInt(aSnap.th.style.width, 10);
                var bW   = parseInt(bSnap.th.style.width, 10);
                if (aKey && !isNaN(aW)) savedWidths[aKey] = aW;
                if (bKey && !isNaN(bW)) savedWidths[bKey] = bW;
                saveWidths(savedWidths);
            }
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup',   onUp);
        });

        // クリックがソート扱いされないように — mousedown で stopPropagation 済みだが
        // click も念のため抑止 (Chromium 系は mousedown で防いでも click は飛ぶ場合がある)。
        grip.addEventListener('click', function(e) { e.stopPropagation(); });
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
