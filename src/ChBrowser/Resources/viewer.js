// ChBrowser ビューア (Phase 10、Phase 16+ でショートカット/ジェスチャーブリッジ統合)
//
// 操作 (デフォルト):
//   ホイール          → 次/前のタブ
//   Ctrl+ホイール     → 拡大/縮小 (カーソル位置を原点)
//   ドラッグ          → パン
// ショートカット:
//   バインディングは shortcuts.json で管理。zoom_in/out, rotate_right/left は JS ローカル即時実行。
//   close, save, next_image, prev_image は C# 側で処理。
//
// C# → JS:
//   { type: 'setImage', url }
//   { type: 'setShortcutBindings', bindings: {...} } — ブリッジ用 (shortcut-bridge.js が直接受信)
//
// JS → C#:
//   { type: 'wheelTab', direction: 'next'|'prev' }
//   { type: 'imageReady'|'imageError' }
//   { type: 'contextMenu' }
//   { type: 'shortcut'|'gesture'|'gestureProgress'|'gestureEnd'|'bridgeReady' }  — ブリッジ経由

(function () {
    'use strict';

    var stage   = document.getElementById('stage');
    var img     = document.getElementById('image');
    var statusEl = document.getElementById('status');

    var zoom = 1.0;
    var panX = 0;
    var panY = 0;
    var rotation = 0; // 度 (90 単位)

    function applyTransform() {
        // translate(-50%, -50%) でセンタリング → translate(panX, panY) で移動 → scale でズーム → rotate で回転
        img.style.transform = 'translate(-50%, -50%) translate(' + panX + 'px, ' + panY + 'px) scale(' + zoom + ') rotate(' + rotation + 'deg)';
    }

    function resetView() {
        zoom = 1.0;
        panX = 0;
        panY = 0;
        rotation = 0;
        applyTransform();
    }

    // カーソル位置基準のズーム (Ctrl+wheel 等)。
    function zoomAt(clientX, clientY, factor) {
        var rect = stage.getBoundingClientRect();
        var cx   = clientX - rect.left - rect.width  / 2;
        var cy   = clientY - rect.top  - rect.height / 2;
        var prev = zoom;
        zoom = Math.max(0.05, Math.min(20, zoom * factor));
        var ratio = zoom / prev;
        panX = cx - (cx - panX) * ratio;
        panY = cy - (cy - panY) * ratio;
        applyTransform();
        flashStatus(Math.round(zoom * 100) + ' %');
    }
    // 中心基準のズーム (キーボード由来等、カーソル位置不明時)
    function zoomBy(factor) {
        var prev = zoom;
        zoom = Math.max(0.05, Math.min(20, zoom * factor));
        var ratio = zoom / prev;
        panX = panX * ratio;
        panY = panY * ratio;
        applyTransform();
        flashStatus(Math.round(zoom * 100) + ' %');
    }

    function rotateBy(deg) {
        rotation = ((rotation + deg) % 360 + 360) % 360;
        applyTransform();
        flashStatus(rotation + '°');
    }

    // ============================================================
    // Phase 16+: ショートカット / マウス操作 / ジェスチャーブリッジ初期化
    // 共通実装は shortcut-bridge.js。viewer 固有の局所アクションを localActions で渡す。
    // sendPaneActivated はビューア向けなので false (アドレスバー追跡は無関係)。
    // ============================================================
    var Shortcut = window.createShortcutBridge({
        sendPaneActivated: false,
        localActions: {
            // wheel 等で event.clientX/Y が取れるならカーソル位置基準、それ以外 (keyboard) は中心基準
            'viewer.zoom_in':      function(e) {
                if (e && typeof e.clientX === 'number') zoomAt(e.clientX, e.clientY, 1.15);
                else                                    zoomBy(1.15);
            },
            'viewer.zoom_out':     function(e) {
                if (e && typeof e.clientX === 'number') zoomAt(e.clientX, e.clientY, 1 / 1.15);
                else                                    zoomBy(1 / 1.15);
            },
            'viewer.rotate_right': function() { rotateBy( 90); },
            'viewer.rotate_left':  function() { rotateBy(-90); },
            // 右クリックメニューと同じ動作。fit はユーザ明示操作なのでキャップなし。
            'viewer.zoom_actual':  function() { showActualSize(); },
            'viewer.zoom_fit':     function() { fitToStage(false); },
        },
    });

    function flashStatus(text) {
        statusEl.textContent = text;
        statusEl.classList.add('visible');
        clearTimeout(flashStatus._timer);
        flashStatus._timer = setTimeout(function () {
            statusEl.classList.remove('visible');
        }, 800);
    }

    function post(msg) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(msg);
        }
    }

    function setImage(url) {
        if (!url) {
            img.removeAttribute('src');
            return;
        }
        resetView();
        img.onload = function () {
            // 初期表示: window に収まるよう自動フィット (= 小さい画像は原寸キープ、大きい画像は縮小)
            fitToStage(/*capAtNatural*/ true);
            post({ type: 'imageReady' });
        };
        img.onerror = function () {
            post({ type: 'imageError' });
        };
        img.src = url;
    }

    /** ウィンドウに合わせる。
     *  capAtNatural = true (初期表示 / リサイズ時): 画像が小さくても原寸 (1.0) より大きくしない。
     *  capAtNatural = false (右クリックメニューからの明示要求): キャップなしで stage いっぱいに広げる。
     *  メニュー経由時にキャップをかけると「100% から動かない」現象 (= 既に zoom=1.0 のとき何も変わらない) が出るため切り替える。 */
    function fitToStage(capAtNatural) {
        var sw = stage.clientWidth;
        var sh = stage.clientHeight;
        var iw = img.naturalWidth;
        var ih = img.naturalHeight;
        if (!sw || !sh || !iw || !ih) return;
        var fit = Math.min(sw / iw, sh / ih);
        zoom = capAtNatural ? Math.min(1, fit) : fit;
        panX = 0;
        panY = 0;
        applyTransform();
        flashStatus(Math.round(zoom * 100) + ' %');
    }

    /** 原寸 (1:1 ピクセル) 表示。pan もリセットして画像を中央に置く。 */
    function showActualSize() {
        zoom = 1.0;
        panX = 0;
        panY = 0;
        applyTransform();
        flashStatus('100 %');
    }

    // ---- ホイール: 通常はタブ切替、Ctrl 押下中はズーム ----
    stage.addEventListener('wheel', function (e) {
        e.preventDefault();
        if (e.ctrlKey) {
            // ズーム: カーソル位置を原点にする
            var rect = stage.getBoundingClientRect();
            var cx   = e.clientX - rect.left - rect.width  / 2;
            var cy   = e.clientY - rect.top  - rect.height / 2;
            var prev = zoom;
            // ホイール上 (deltaY < 0) で拡大、下で縮小。1 ステップ 1.15 倍
            var step = e.deltaY < 0 ? 1.15 : (1 / 1.15);
            zoom = Math.max(0.05, Math.min(20, zoom * step));
            // カーソル位置基準のズーム: 拡大時はカーソル下の点が動かないように pan を補正
            //   currentImagePoint = (cursor - panOld) / zoomOld
            //   panNew = cursor - currentImagePoint * zoomNew
            var ratio = zoom / prev;
            panX = cx - (cx - panX) * ratio;
            panY = cy - (cy - panY) * ratio;
            applyTransform();
            flashStatus(Math.round(zoom * 100) + ' %');
        } else {
            // タブ切替: 上=前、下=次。連続発火でも 1 イベント 1 切替に絞る
            post({ type: 'wheelTab', direction: e.deltaY < 0 ? 'prev' : 'next' });
        }
    }, { passive: false });

    // ---- ドラッグでパン ----
    var dragging = false;
    var dragStartX = 0, dragStartY = 0;
    var dragOrigPanX = 0, dragOrigPanY = 0;

    stage.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return; // 左クリックのみ
        dragging = true;
        dragStartX = e.clientX;
        dragStartY = e.clientY;
        dragOrigPanX = panX;
        dragOrigPanY = panY;
        stage.classList.add('panning');
        e.preventDefault();
    });

    window.addEventListener('mousemove', function (e) {
        if (!dragging) return;
        panX = dragOrigPanX + (e.clientX - dragStartX);
        panY = dragOrigPanY + (e.clientY - dragStartY);
        applyTransform();
    });

    window.addEventListener('mouseup', function () {
        if (!dragging) return;
        dragging = false;
        stage.classList.remove('panning');
    });

    // ---- ダブルクリックでフィットにリセット ----
    // (= ユーザ明示要求なので、キャップなしで stage いっぱいに広げる)
    stage.addEventListener('dblclick', function () {
        fitToStage(/*capAtNatural*/ false);
    });

    // ---- 右クリックでブラウザ既定メニューを抑制 → C# 側で WPF ContextMenu を popup ----
    stage.addEventListener('contextmenu', function (e) {
        e.preventDefault();
        post({ type: 'contextMenu' });
    });

    // ---- ウィンドウサイズ変更でフィット再計算 (画像未ロードなら何もしない) ----
    // (= 初期表示と同じく、小さい画像は原寸キープで拡大しない)
    window.addEventListener('resize', function () {
        if (img.complete && img.naturalWidth > 0) fitToStage(/*capAtNatural*/ true);
    });

    // ---- C# からのメッセージ受信 ----
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (e) {
            var msg = e.data;
            if (!msg || typeof msg !== 'object' || !msg.type) return;
            if (msg.type === 'setImage') {
                setImage(msg.url || '');
            } else if (msg.type === 'setZoom') {
                // 右クリックメニュー「画像を原寸表示 / ウィンドウに合わせる」からの指示。
                // メニュー経由はユーザ明示要求なので fit はキャップなし (= 小さい画像も stage いっぱいに広げる)。
                if (msg.mode === 'actual')   showActualSize();
                else if (msg.mode === 'fit') fitToStage(/*capAtNatural*/ false);
            }
        });
    }
})();
