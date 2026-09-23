// ChBrowser スレ一覧 (Phase 14a で WebView 化、Phase 11d で外出し)。
// ページ (シェル + 表の見出し) は 1 回だけ読み込み、行は C# から setItems (JSON) で受け取って組み立てる。
// 一覧を取り直してもページを読み直さないので、列幅・並べ替え・絞り込み・選択行・スクロール位置がそのまま残る。
// JS → C#:
//   { type: 'ready' }                                                    — シェル読み込み完了 (C# は現在の行を送る)
//   { type: 'openThread', host, directoryName, key, title, logState }   — クリック or ダブルクリックで開く
//   { type: 'openBoard', host, directoryName, name }                     — 板行 (data-kind="board") のクリックで板を開く
//   { type: 'threadListRowMenu', kind, host, directoryName, key, title } — 行の右クリック (kind: 'thread' | 'board')
//   { type: 'paneActivated' }                                            — Phase 14: pane 内任意の mousedown (アドレスバー切替用)
// C# → JS:
//   { type: 'setItems', rows: [{ kind, key, host, dir, no, title, board, count, momentum, log, fav }, ...] } — 行の全体
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
            // border-box 値 + delta を content-box の style.width に書き戻すヘルパ。
            // padding を引いた値で設定しないと開始時点で padding 分だけ境界が飛ぶ。
            function applyWidth(snap, deltaBorder) {
                snap.th.style.width = Math.max(0, snap.border + deltaBorder - snap.pad) + 'px';
            }
            // 全列を現在のレンダー幅で固定 (これで table の自動スケールが止まる)
            snapshot.forEach(function(s) { applyWidth(s, 0); });

            var aSnap = snapshot[idx];      // 左側 = ドラッグ中の th
            var bSnap = snapshot[idx + 1];  // 右側 = 隣の th
            grip.classList.add('dragging');

            function onMove(ev) {
                var dx = ev.clientX - startX;
                // 両列とも MIN を割らないようにクランプ
                if (aSnap.border + dx < MIN_COL_WIDTH) dx = MIN_COL_WIDTH - aSnap.border;
                if (bSnap.border - dx < MIN_COL_WIDTH) dx = bSnap.border - MIN_COL_WIDTH;
                applyWidth(aSnap,  dx);
                applyWidth(bSnap, -dx);
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
        // 板行 (「板一覧以外の取得済み板」集約タブ) はスレではなく板を開く。
        if (tr.dataset.kind === 'board') {
            window.chrome.webview.postMessage({
                type:          'openBoard',
                host:          tr.dataset.host,
                directoryName: tr.dataset.dir,
                name:          tr.dataset.title,
            });
            return;
        }
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

    // 行の右クリック → C# 側に「右クリックメニューを出して」と通知。
    // メニュー本体 (お気に入り追加 / コピー / 次スレ候補検索 / ログ削除) は WPF 側で popup する
    // (= スレタブ右クリックメニューと同じ並び。「板を開く」だけ除外)。
    tbody.addEventListener('contextmenu', function(e) {
        var tr = e.target.closest && e.target.closest('tr');
        if (!tr) return;
        e.preventDefault();
        if (!window.chrome || !window.chrome.webview) return;
        // 行をハイライト (= 操作対象が視覚的に分かるよう left-click と同じ "selected" を付ける)。
        if (selected) selected.classList.remove('selected');
        tr.classList.add('selected');
        selected = tr;
        window.chrome.webview.postMessage({
            type:          'threadListRowMenu',
            kind:          tr.dataset.kind || 'thread',   // 'board' なら板用メニュー (C# 側で出し分け)
            host:          tr.dataset.host,
            directoryName: tr.dataset.dir,
            key:           tr.dataset.key,
            title:         tr.dataset.title,
        });
    });

    // ---------- 行の組み立て (setItems) ----------

    function esc(t) {
        return String(t == null ? '' : t)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    var LOG_CLASS = { 1: 'has-log', 2: 'has-update', 3: 'has-dropped', 4: 'has-replied-to-own' };

    /** 1 行分の HTML。スレ行と板行 (data-kind="board"、「板一覧以外の取得済み板」集約タブ) で列構成と data-* は同じ。
     *  td には省略表示 (text-overflow) されても元テキストが見えるよう title 属性を付ける。 */
    function rowHtml(r) {
        var title = esc(r.title), board = esc(r.board);
        if (r.kind === 'board') {
            return '<tr class="row-board" data-kind="board" data-key="" data-host="' + esc(r.host) + '" data-dir="' + esc(r.dir)
                 + '" data-no="0" data-title="' + title + '" data-board="' + board + '" data-count="' + r.count + '" data-momentum="0" data-log="0">'
                 + '<td class="col-log"><span class="log-mark"></span></td><td class="col-no"></td>'
                 + '<td class="col-title" title="' + title + '">' + title + '</td>'
                 + '<td class="col-board" title="' + board + '">' + board + '</td>'
                 + '<td class="col-count" title="' + r.count + '">' + r.count + '</td><td class="col-momentum"></td></tr>';
        }
        var cls = (LOG_CLASS[r.log] ? LOG_CLASS[r.log] + ' ' : '') + (r.fav ? 'is-favorited ' : '');
        var m = esc(r.momentum || '0');
        return '<tr class="' + cls + '" data-key="' + esc(r.key) + '" data-host="' + esc(r.host) + '" data-dir="' + esc(r.dir)
             + '" data-no="' + r.no + '" data-title="' + title + '" data-board="' + board + '" data-count="' + r.count
             + '" data-momentum="' + m + '" data-log="' + (r.log | 0) + '">'
             + '<td class="col-log"><span class="log-mark"></span></td>'
             + '<td class="col-no" title="' + r.no + '">' + r.no + '</td>'
             + '<td class="col-title" title="' + title + '">' + title + '</td>'
             + '<td class="col-board" title="' + board + '">' + board + '</td>'
             + '<td class="col-count" title="' + r.count + '">' + r.count + '</td>'
             + '<td class="col-momentum" title="' + m + '">' + m + '</td></tr>';
    }

    /** 行を全部差し替える。tbody 自体は残すのでクリック等のハンドラ・列幅・見出しの並べ替え状態はそのまま。
     *  並べ替え・絞り込みはかけ直し、選択行は同じスレ (host + dir + key) があれば付け直す。 */
    function setItems(rows) {
        var sel = selected && selected.isConnected
            ? { host: selected.dataset.host, dir: selected.dataset.dir, key: selected.dataset.key, kind: selected.dataset.kind || '' }
            : null;
        var html = '';
        for (var i = 0; i < rows.length; i++) html += rowHtml(rows[i]);
        tbody.innerHTML = html;

        selected = null;
        if (sel) {
            var trs = tbody.querySelectorAll('tr');
            for (var j = 0; j < trs.length; j++) {
                var d = trs[j].dataset;
                if (d.host === sel.host && d.dir === sel.dir && d.key === sel.key && (d.kind || '') === sel.kind) {
                    trs[j].classList.add('selected');
                    selected = trs[j];
                    break;
                }
            }
        }
        // 見出しで選んでいた並べ替えを当て直す (行は subject.txt 順で届く)
        var activeTh = document.querySelector('thead th.sort-asc, thead th.sort-desc');
        if (activeTh && activeTh.dataset && activeTh.dataset.sort && !(activeTh.dataset.sort === 'no' && activeTh.classList.contains('sort-asc'))) {
            sortBy(activeTh.dataset.sort, activeTh.dataset.sortType, activeTh.classList.contains('sort-asc') ? 1 : -1);
        }
        applyListSearch();
    }

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
                    tr.classList.remove('has-log', 'has-update', 'has-dropped', 'has-replied-to-own');
                    var sortVal = 0;
                    if      (change.state === 'cached')        { tr.classList.add('has-log');             sortVal = 1; }
                    else if (change.state === 'updated')       { tr.classList.add('has-update');          sortVal = 2; }
                    else if (change.state === 'dropped')       { tr.classList.add('has-dropped');         sortVal = 3; }
                    else if (change.state === 'repliedToOwn')  { tr.classList.add('has-replied-to-own');  sortVal = 4; }
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
            } else if (msg.type === 'setItems' && Array.isArray(msg.rows)) {
                setItems(msg.rows);
            } else if (msg.type === 'setConfig') {
                if (typeof msg.openOnSingleClick === 'boolean') {
                    openOnSingleClick = msg.openOnSingleClick;
                }
            } else if (msg.type === 'setListSearch') {
                // C# 側 ThreadListTabViewModel.SearchQuery の反映。空文字なら絞り込み解除。
                currentSearchQuery = typeof msg.query === 'string' ? msg.query : '';
                applyListSearch();
            }
            // setShortcutBindings は shortcut-bridge.js 内で直接受信して処理する。
        });
    }

    // ---------- スレ一覧の絞り込み (タイトル文字列) ----------
    // 各タブが独自に保持する状態。タブ切替で C# が setListSearch を送り直すので、
    // ここでは「現在の WebView2 (= 1 タブ分) のクエリ」を保持するだけ。
    var currentSearchQuery = '';

    /** クエリにマッチしない行は filter-hidden で隠し、マッチする行は title セルに <mark.search-highlight> を入れる。
     *  クエリが空なら全行可視 + 既存ハイライト除去。data-title (= 元タイトル) で大文字小文字無視の部分一致判定。 */
    function applyListSearch() {
        if (!tbody) return;
        var rows = tbody.querySelectorAll('tr');
        var query = currentSearchQuery || '';
        var queryLower = query.toLowerCase();
        var queryLen = query.length;

        for (var i = 0; i < rows.length; i++) {
            var tr = rows[i];
            var titleCell = tr.querySelector('td.col-title');

            // 既存ハイライトを unwrap (= 中身のテキストに戻す)
            if (titleCell) {
                var marks = titleCell.querySelectorAll('mark.search-highlight');
                for (var j = 0; j < marks.length; j++) {
                    var m = marks[j];
                    var text = document.createTextNode(m.textContent || '');
                    m.parentNode.replaceChild(text, m);
                }
                if (marks.length > 0) titleCell.normalize();
            }

            if (!query) {
                tr.classList.remove('filter-hidden');
                continue;
            }

            var title = (tr.dataset.title || '');
            if (title.toLowerCase().indexOf(queryLower) < 0) {
                tr.classList.add('filter-hidden');
            } else {
                tr.classList.remove('filter-hidden');
                // タイトルセルの該当箇所を <mark> でラップ
                if (titleCell) highlightInTitleCell(titleCell, queryLower, queryLen);
            }
        }
    }

    /** title セル内のテキストノードを走査し、queryLower (= 既に小文字化済) の出現を <mark.search-highlight> で囲む。
     *  title セルは普通プレーンテキストだが、念のため TreeWalker で nested text にも対応。 */
    function highlightInTitleCell(cellEl, queryLower, queryLen) {
        var texts = [];
        var walker = document.createTreeWalker(cellEl, NodeFilter.SHOW_TEXT, null);
        while (walker.nextNode()) texts.push(walker.currentNode);

        for (var i = 0; i < texts.length; i++) {
            var tn = texts[i];
            if (!tn.parentNode) continue;
            var parent = tn.parentNode;
            if (parent.classList && parent.classList.contains('search-highlight')) continue;

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
            parent.replaceChild(frag, tn);
        }
    }

    // シェルの準備完了 → C# が現在の行 (setItems) を送る。ページを読み直したとき (CSS 変更 / 別ペインへのタブ移動) も同じ経路で復元される
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage({ type: 'ready' });
})();
