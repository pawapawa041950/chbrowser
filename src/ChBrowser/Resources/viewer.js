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
// メディア種別:
//   .mp4/.webm/.mov の URL なら <video controls autoplay> を rendering、
//   それ以外は <img>。setImage 受信時に URL 種別が現在の要素と違えば DOM ごと差し替える。
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
    var media   = document.getElementById('media'); // 初期は viewer.html の <img id="media">
    var statusEl = document.getElementById('status');

    var zoom = 1.0;
    var panX = 0;
    var panY = 0;
    var rotation = 0; // 度 (90 単位)

    var VIDEO_EXT_RE = /\.(mp4|webm|mov)(?:[?#]|$)/i;
    function isVideoUrl(url) { return VIDEO_EXT_RE.test(url || ''); }

    /** 現在の media 要素の自然サイズ。img なら naturalWidth/Height、video なら videoWidth/Height。
     *  まだ load 中で 0 のことがあるので呼び出し側で 0 ガードする。 */
    function naturalSize() {
        if (media.tagName === 'VIDEO') {
            return { w: media.videoWidth || 0, h: media.videoHeight || 0 };
        }
        return { w: media.naturalWidth || 0, h: media.naturalHeight || 0 };
    }

    function applyTransform() {
        // translate(-50%, -50%) でセンタリング → translate(panX, panY) で移動 → scale でズーム → rotate で回転
        media.style.transform = 'translate(-50%, -50%) translate(' + panX + 'px, ' + panY + 'px) scale(' + zoom + ') rotate(' + rotation + 'deg)';
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

    /** ビューワ側で再生中の <video> からフレームを抽出して ImageCacheService (Kind=VideoThumb) に保存。
     *  thread.js の extractAndCacheVideoThumbnail と同じ通信フロー (videoThumbnailCache メッセージ) で、
     *  保存後に C# 側がタブの ThumbnailPath を更新する。
     *  cross-origin 動画で CORS ヘッダが無い場合は canvas が tainted になり toDataURL が SecurityError を投げる。
     *  この関数は seeked 後に呼ぶ前提 (= 0.5 秒など意味のあるフレームを捕捉する)。 */
    function tryCaptureVideoThumbnail(srcUrl) {
        try {
            if (media.tagName !== 'VIDEO') return;
            var w = media.videoWidth;
            var h = media.videoHeight;
            if (!w || !h) return;
            // 長辺 240px まで縮小 (= thread 側 extractAndCacheVideoThumbnail と同じパラメタ)
            var max = 240;
            var scale = Math.min(1, max / Math.max(w, h));
            var canvas = document.createElement('canvas');
            canvas.width  = Math.max(1, Math.round(w * scale));
            canvas.height = Math.max(1, Math.round(h * scale));
            var ctx = canvas.getContext('2d');
            ctx.drawImage(media, 0, 0, canvas.width, canvas.height);
            var dataUri = canvas.toDataURL('image/jpeg', 0.85);
            post({ type: 'videoThumbnailCache', url: srcUrl, dataUri: dataUri });
        } catch (e) {
            post({ type: 'videoThumbnailCacheFailed', url: srcUrl, error: (e && e.name) || 'unknown', message: (e && e.message) || '' });
        }
    }

    /** URL 種別に応じて #media を <img> / <video> に差し替える (既に正しい型なら何もしない)。
     *  パンや wheel/dblclick/contextmenu のリスナは要素ではなく stage 側にあるので再 attach 不要。 */
    function ensureMediaElement(wantsVideo) {
        var isVideo = media.tagName === 'VIDEO';
        if (isVideo === wantsVideo) return;
        var next;
        if (wantsVideo) {
            next = document.createElement('video');
            next.controls = true;
            next.autoplay = true;
            next.loop     = true;          // 5ch でよくある短尺ループ動画想定
            next.playsInline = true;
        } else {
            next = document.createElement('img');
            next.alt = '';
        }
        next.id = 'media';
        media.replaceWith(next);
        media = next;
    }

    function setImage(url) {
        if (!url) {
            // 空 URL: 現状の要素から src だけ落とす
            if (media.tagName === 'VIDEO') media.removeAttribute('src');
            else                           media.removeAttribute('src');
            return;
        }
        var wantsVideo = isVideoUrl(url);
        ensureMediaElement(wantsVideo);
        resetView();
        // ロード前に媒体を不可視化 (= ビューワー初回オープン時の「原寸 → 縮小」チラつき対策)。
        // fitToStage で transform が適用された後にまとめて visibility: visible に戻す。
        media.style.visibility = 'hidden';
        if (wantsVideo) {
            // <video> は loadedmetadata で size が確定する。再生エラーは error イベント。
            media.onloadedmetadata = function () {
                fitToStage(/*capAtNatural*/ true);
                media.style.visibility = 'visible';
                post({ type: 'imageReady' });
                // サムネ抽出のために 0.5 秒地点へシーク (= time=0 は黒フレームになりがちなので回避)。
                // ユーザの主観上はクリック直後の数百 ms に過ぎず実害なし。
                try {
                    var dur = media.duration || 0;
                    var target = (dur > 0 && dur < 1) ? (dur / 2) : Math.min(0.5, Math.max(0.1, dur - 0.05));
                    if (isFinite(target) && target > 0) {
                        media.currentTime = target;
                    } else {
                        tryCaptureVideoThumbnail(url);
                    }
                } catch (_) { tryCaptureVideoThumbnail(url); }
            };
            // seeked: 上記 currentTime 設定の完了通知。意味のあるフレームを取得できる。
            media.onseeked = function () {
                tryCaptureVideoThumbnail(url);
            };
            // crossOrigin='anonymous' で proxy 経由の CORS-clean レスポンス → canvas 抽出可。
            // proxy 失敗 (CDN 側 CORS 全く受け付けない) の場合のフォールバックは onerror 経路で外す。
            media.crossOrigin = 'anonymous';
            var corsFallbackTried = false;
            media.onerror = function () {
                if (!corsFallbackTried) {
                    corsFallbackTried = true;
                    try { media.removeAttribute('crossorigin'); } catch (_) {}
                    media.onseeked = null;     // 再試行ではサムネ抽出は諦める (tainted canvas 回避)
                    media.src = url;
                    try { media.load(); } catch (_) {}
                    return;
                }
                media.style.visibility = 'visible'; // エラーで止まっても次の遷移で詰まらないように戻す
                post({ type: 'imageError' });
            };
            media.src = url;
            // load() は src 設定で暗黙に起動するが、autoplay が確実に効くよう明示
            try { media.load(); } catch (_) {}
        } else {
            media.onload  = function () {
                fitToStage(/*capAtNatural*/ true);
                media.style.visibility = 'visible';
                post({ type: 'imageReady' });
            };
            media.onerror = function () {
                media.style.visibility = 'visible';
                post({ type: 'imageError' });
            };
            media.src = url;
        }
    }

    /** ウィンドウに合わせる。
     *  capAtNatural = true (初期表示 / リサイズ時): 画像が小さくても原寸 (1.0) より大きくしない。
     *  capAtNatural = false (右クリックメニューからの明示要求): キャップなしで stage いっぱいに広げる。 */
    function fitToStage(capAtNatural) {
        var sw = stage.clientWidth;
        var sh = stage.clientHeight;
        var n  = naturalSize();
        if (!sw || !sh || !n.w || !n.h) return;
        var fit = Math.min(sw / n.w, sh / n.h);
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
    // video 上では pointer-events: auto により video が drag を消費するため
    // 視覚的には「外側の余白でしか pan できない」が、それで十分。controls 操作と両立させる。
    var dragging = false;
    var dragStartX = 0, dragStartY = 0;
    var dragOrigPanX = 0, dragOrigPanY = 0;

    stage.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return; // 左クリックのみ
        // 動画 controls クリックは pan させない (= video 要素自身が target)
        if (e.target && e.target.tagName === 'VIDEO') return;
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

    // ---- ウィンドウサイズ変更でフィット再計算 ----
    // 画像: img.complete && naturalWidth > 0。動画: readyState >= 1 && videoWidth > 0。
    window.addEventListener('resize', function () {
        if (media.tagName === 'VIDEO') {
            if (media.readyState >= 1 && media.videoWidth > 0) fitToStage(/*capAtNatural*/ true);
        } else {
            if (media.complete && media.naturalWidth > 0) fitToStage(/*capAtNatural*/ true);
        }
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
