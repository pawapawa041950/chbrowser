// ChBrowser ショートカット / マウス操作 / マウスジェスチャー JS ブリッジ (Phase 16)。
// 各ペイン (thread / thread-list / favorites / board-list) のシェル HTML に共通で埋め込まれる。
// 各ペインの主 JS が window.createShortcutBridge({...}) を呼んで初期化する。
//
// 役割:
//   - キーボード / マウス / ホイール / 右ドラッグを capture phase で hook
//   - C# が push する descriptor → actionId のマップを保持し、bind 済入力のみ preventDefault + 通知
//   - actionId が localActions に登録されていれば C# 経由なしで JS 側で実行
//   - 右ドラッグの方向列を ↑↓←→ に量子化してジェスチャー文字列を生成
//   - 右クリック単独 (動かさず離す) はコンテキストメニュー通常表示、ジェスチャー認識時のみ抑止
//
// オプション:
//   - localActions: { actionId: () => void } — 即時実行する JS ローカルアクション
//   - sendPaneActivated: bool (default true) — mousedown のたびに paneActivated を送信するか

(function() {
    'use strict';
    if (window.createShortcutBridge) return;

    // ペイン共通: ページの「実際の」スクロール対象要素を見つけて先頭/末尾にスクロールする。
    // 単純に window.scrollTo すると、CSS で body { height:100%; overflow:auto } のように
    // body が scroll container になっているケース (= thread-list) ではスクロールしない。
    // そのため scrollingElement / documentElement / body すべてに scrollTop を試す。
    window.chScrollPage = function(toBottom) {
        var els = [document.scrollingElement, document.documentElement, document.body];
        var i;
        if (toBottom) {
            var maxH = 0;
            for (i = 0; i < els.length; i++) {
                if (els[i] && els[i].scrollHeight > maxH) maxH = els[i].scrollHeight;
            }
            for (i = 0; i < els.length; i++) {
                if (els[i]) els[i].scrollTop = maxH;
            }
        } else {
            for (i = 0; i < els.length; i++) {
                if (els[i]) els[i].scrollTop = 0;
            }
        }
    };

    window.createShortcutBridge = function(opts) {
        opts = opts || {};
        const localActions      = opts.localActions || {};
        const sendPaneActivated = opts.sendPaneActivated !== false;

        const bindings = new Map();
        // ジェスチャー入力中フラグ。WebView 内で右下げを観測したときだけ true になる。
        // 単純な右クリック / 修飾キー付き右クリック等は対象外 (= まだ down → up 直後に判定する)。
        let rightDragging = false;
        let suppressNextContext = false;
        let lastSampleX = 0, lastSampleY = 0;
        const directions = [];
        const SAMPLE_DISTANCE = 18;

        // ダブルクリック用の自前カウンタ。
        // ブラウザの e.detail を直接使うと、4 連クリックで detail=1,2,3,4 になり、
        // 2 回目だけ「ダブルクリック」が発火して 3 回目以降は拾えない事象が発生する。
        // 自前で数えて、ダブルが dispatch 成功した瞬間にリセットすることで
        // 4 連クリックを 2 回のダブルクリックとして扱えるようにする。
        // (トリプルクリックは「ダブルクリック 2 連発」と原理的に区別不能なため非対応)
        let clickCount        = 0;
        let lastClickAt       = 0;
        let lastClickX        = 0;
        let lastClickY        = 0;
        const CLICK_INTERVAL_MS = 500; // この間隔以内のクリックを連続扱い (Win 標準のダブルクリック相当)
        const CLICK_RADIUS_PX   = 4;   // 位置がこれ以上ずれたら連続でないと見做してカウンタ初期化

        // 右ボタンが現在押下されているかを Pointer Event の buttons フィールドから判定する。
        // ブラウザ環境では Win32 の SetCapture 相当が無いので、WebView の document を出ると
        // mouseup を観測できない場合がある。次に届く mousemove で buttons & 2 = 0 なら
        // 「外で離した」と判断して gesture state をクリーンアップする。
        function isRightHeld(e) { return typeof e.buttons === 'number' && (e.buttons & 2) !== 0; }

        function resetGesture() {
            rightDragging = false;
            directions.length = 0;
        }

        // C# からの setShortcutBindings 受信は各ペインの主 JS 側で扱うのではなく bridge 側で受ける。
        // ペイン JS の早期 return (tbody/root 不在等) 時にも binding 反映が漏れないようにするため。
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.addEventListener('message', function(e) {
                const msg = e.data;
                if (!msg || msg.type !== 'setShortcutBindings') return;
                bindings.clear();
                if (msg.bindings && typeof msg.bindings === 'object') {
                    for (const desc in msg.bindings) {
                        if (Object.prototype.hasOwnProperty.call(msg.bindings, desc)) {
                            bindings.set(desc, msg.bindings[desc]);
                        }
                    }
                }
            });
            // bridge 初期化完了を C# に通知 → C# が当該 WebView に setShortcutBindings を direct push する。
            // これで「PaneShortcutsJson が値変化していない / タブ再生成で property change が発火しない」
            // ケースでも binding を確実に届ける。
            window.chrome.webview.postMessage({ type: 'bridgeReady' });
        }

        function postProgress(value) {
            if (!window.chrome || !window.chrome.webview) return;
            window.chrome.webview.postMessage({ type: 'gestureProgress', value: value });
        }
        function postEnd() {
            if (!window.chrome || !window.chrome.webview) return;
            window.chrome.webview.postMessage({ type: 'gestureEnd' });
        }

        // event.code → WPF Key enum 名 への変換マップ
        const keyNameMap = (function() {
            const m = {};
            'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('').forEach(c => m['Key' + c] = c);
            for (let n = 0; n <= 9; n++) m['Digit' + n]  = 'D'      + n;
            for (let n = 1; n <= 12; n++) m['F' + n]     = 'F'      + n;
            for (let n = 0; n <= 9; n++) m['Numpad' + n] = 'NumPad' + n;
            Object.assign(m, {
                Enter: 'Enter', Escape: 'Escape', Tab: 'Tab', Space: 'Space',
                Backspace: 'Back', Delete: 'Delete', Insert: 'Insert',
                Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown',
                ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
                Minus: 'OemMinus', Equal: 'OemPlus',
                BracketLeft: 'OemOpenBrackets', BracketRight: 'OemCloseBrackets',
                Semicolon: 'OemSemicolon', Quote: 'OemQuotes',
                Comma: 'OemComma', Period: 'OemPeriod', Slash: 'OemQuestion',
                Backslash: 'OemBackslash', Backquote: 'OemTilde',
            });
            return m;
        })();

        function buildModifiers(e) {
            let s = '';
            if (e.ctrlKey)  s += 'Ctrl+';
            if (e.altKey)   s += 'Alt+';
            if (e.shiftKey) s += 'Shift+';
            if (e.metaKey)  s += 'Win+';
            return s;
        }

        function send(type, descriptor) {
            if (!window.chrome || !window.chrome.webview) return;
            window.chrome.webview.postMessage({ type: type, descriptor: descriptor });
        }

        function trySuppressAndDispatch(type, descriptor, e) {
            if (!descriptor) return false;
            const actionId = bindings.get(descriptor);
            if (!actionId) return false;
            e.preventDefault();
            e.stopPropagation();
            if (typeof e.stopImmediatePropagation === 'function') e.stopImmediatePropagation();
            if (localActions[actionId]) {
                // event を渡す → wheel/mouse 由来なら clientX/Y からカーソル位置取得可、keyboard 由来なら null フォールバック
                try { localActions[actionId](e); } catch (err) { console.error('[Shortcut] local handler failed', actionId, err); }
            } else {
                send(type, descriptor);
            }
            return true;
        }

        document.addEventListener('keydown', function(e) {
            const name = keyNameMap[e.code];
            if (!name) return;
            const desc = buildModifiers(e) + name;
            trySuppressAndDispatch('shortcut', desc, e);
        }, true);

        document.addEventListener('mousedown', function(e) {
            if (sendPaneActivated && window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({ type: 'paneActivated' });
            }

            if (e.button === 2) {
                rightDragging       = true;
                suppressNextContext = false;
                lastSampleX         = e.clientX;
                lastSampleY         = e.clientY;
                directions.length   = 0;
                postProgress('');  // ステータスバーに「ジェスチャー開始」を即座に出す
                return;
            }

            // 右ボタン押下中の判定は event.buttons (= MK_RBUTTON フラグ) を直接見る。
            // bridge 内 rightDragging を使わない理由: タブストリップで右下げ → カーソルが
            // WebView に入って中クリック、のように WebView 側で down を観測しないケースがあるため。
            if (isRightHeld(e) && e.button === 1) {
                if (trySuppressAndDispatch('shortcut', '右クリック+中ボタン', e)) return;
            }

            let name = null;
            if (e.button === 0) {
                // 自前のクリックカウンタで連続クリックを判定。
                // - 前回クリックから CLICK_INTERVAL_MS 内 + CLICK_RADIUS_PX 内なら count++
                // - そうでなければ count=1 (= 別の click sequence の開始)
                const now  = Date.now();
                const dt   = now - lastClickAt;
                const dx   = e.clientX - lastClickX;
                const dy   = e.clientY - lastClickY;
                const near = (dx * dx + dy * dy) <= (CLICK_RADIUS_PX * CLICK_RADIUS_PX);
                clickCount   = (dt < CLICK_INTERVAL_MS && near) ? (clickCount + 1) : 1;
                lastClickAt  = now;
                lastClickX   = e.clientX;
                lastClickY   = e.clientY;

                if (clickCount !== 2) return;
                name = 'ダブルクリック';
            } else if (e.button === 1) {
                name = '中クリック';
            } else {
                return;
            }
            const desc = buildModifiers(e) + name;
            // ダブルクリックが dispatch 成功したら次の click sequence をすぐ始められるよう reset。
            // これで 4 連クリック = 2 回のダブルクリックとして扱える。
            // 中クリック等は clickCount を触らない (= リセットされても挙動同じ)。
            if (trySuppressAndDispatch('shortcut', desc, e) && clickCount === 2) {
                clickCount = 0;
            }
        }, true);

        document.addEventListener('mousemove', function(e) {
            if (!rightDragging) return;
            // WebView 外で右ボタンを離されたケース: mouseup を観測できないため、
            // 戻ってきた mousemove で右ボタンが上がっていたら gesture を取消す (state stuck の防止)。
            if (!isRightHeld(e)) {
                resetGesture();
                postEnd();
                return;
            }
            const dx = e.clientX - lastSampleX;
            const dy = e.clientY - lastSampleY;
            const dist = Math.sqrt(dx * dx + dy * dy);
            if (dist < SAMPLE_DISTANCE) return;
            const dir = Math.abs(dx) > Math.abs(dy)
                ? (dx > 0 ? '→' : '←')
                : (dy > 0 ? '↓' : '↑');
            if (directions.length === 0 || directions[directions.length - 1] !== dir) {
                directions.push(dir);
                postProgress(directions.join(''));
            }
            lastSampleX = e.clientX;
            lastSampleY = e.clientY;
        }, true);

        document.addEventListener('mouseup', function(e) {
            if (e.button !== 2) return;
            if (!rightDragging) return;
            const gs = directions.join('');
            resetGesture();
            postEnd();  // ジェスチャー終了通知 (= ステータスバー表示クリア)
            if (gs.length === 0) return;

            suppressNextContext = true;
            e.preventDefault();
            e.stopPropagation();
            const actionId = bindings.get(gs);
            if (actionId) {
                if (localActions[actionId]) {
                    try { localActions[actionId](); } catch (err) { console.error('[Shortcut] gesture local handler failed', actionId, err); }
                } else {
                    send('gesture', gs);
                }
            }
        }, true);

        document.addEventListener('contextmenu', function(e) {
            if (suppressNextContext) {
                suppressNextContext = false;
                e.preventDefault();
                e.stopPropagation();
            }
        }, true);

        document.addEventListener('wheel', function(e) {
            const dir = e.deltaY < 0 ? 'ホイールアップ' : 'ホイールダウン';
            // 右ホールド中なら "右クリック+ホイール..." を優先 (event.buttons で判定: WPF 側で右下げ →
            // JS 側で wheel という流れでもチョードが成立する)。
            if (isRightHeld(e)) {
                if (trySuppressAndDispatch('shortcut', '右クリック+' + dir, e)) return;
            }
            const desc = buildModifiers(e) + dir;
            trySuppressAndDispatch('shortcut', desc, e);
        }, { capture: true, passive: false });

        // 旧 setBindings API は bridge 内部 listener が直接 setShortcutBindings を消費するため不要だが、
        // 既存ペイン JS が呼んでいる場合に備えて空メソッドで残しておく (no-op)。
        return { setBindings: function(_) {} };
    };
})();
