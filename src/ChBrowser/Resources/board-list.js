// ChBrowser 板一覧 (Phase 14a) renderer.
// 階層は <details>/<summary> なのでブラウザ標準の Ctrl+F で閉じたカテゴリも自動展開される。
// JS はイベントハンドラだけを担当 (postMessage で C# にユーザ操作を通知)。
//
// JS → C# メッセージ:
//   { type: 'openBoard', host, directoryName, name }       — 板クリック or ダブルクリック (設定による)
//   { type: 'setCategoryExpanded', categoryName, expanded } — カテゴリの開閉トグル
//   { type: 'contextMenu', target: 'board', host, directoryName, name } — 板右クリック
// C# → JS:
//   { type: 'setConfig', openOnSingleClick: bool }         — Phase 11b: クリック動作の設定

(function () {
    'use strict';

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

    // C# からの setConfig 受信 (Phase 11b)
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (e) {
            var msg = e.data;
            if (!msg || msg.type !== 'setConfig') return;
            if (typeof msg.openOnSingleClick === 'boolean') {
                openOnSingleClick = msg.openOnSingleClick;
            }
        });
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
