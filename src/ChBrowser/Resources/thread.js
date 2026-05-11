// ChBrowser thread view renderer.
// Public API exposed on window (C# → JS):
//   window.appendPosts(batch, scrollTarget?) — レスを末尾に追加。スレ表示の唯一の描画チャネル。
//                                              optional scrollTarget で初回スクロール位置を渡す。
//   window.setViewMode(mode)                 — 'flat' | 'tree' | 'dedupTree'
// 受信メッセージ:
//   { type: 'setConfig', popularThreshold?, imageSizeThresholdMb? } — Phase 11 設定の即時反映
//   { type: 'setShortcutBindings', bindings: [...] }                 — Phase 16 ショートカット bind 一覧の同期
// Messages sent to host (C#) via window.chrome.webview.postMessage:
//   { type: 'ready' }                       — JS が初期化完了したことを通知
//   { type: 'openUrl', url }                — 外部 URL クリック
//   { type: 'scrollPosition', postNumber }  — viewport 上端のレス番号 (debounced)
//   { type: 'paneActivated' }               — Phase 14: pane 内任意の mousedown (アドレスバー切替用)
//   { type: 'shortcut', descriptor }        — Phase 16: ショートカット/マウス操作のディスパッチ要求
//   { type: 'gesture',  descriptor }        — Phase 16: マウスジェスチャー認識結果のディスパッチ要求
//
// 各タブが専属 WebView2 を持つので、タブ切替で DOM が再構築されることはない。
// scroll target の同梱と tryScrollToTarget は「初回ロード時 (idx.json からの位置復元)」用。
// scrollY 状態は WebView2 自身が保持するので、scrollTo(0,0) や save 抑制は不要。

(function () {
    'use strict';

    // ============================================================
    // Phase 16: ショートカット / マウス操作 / マウスジェスチャー JS ブリッジ
    // 共通実装は shortcut-bridge.js (window.createShortcutBridge) にあり。
    // 当ペイン用の localActions のみ渡して factory で初期化する。
    // ============================================================
    const Shortcut = window.createShortcutBridge({
        localActions: {
            'thread.scroll_top':    function() { window.chScrollPage(false); },
            'thread.scroll_bottom': function() { window.chScrollPage(true);  },
        },
    });

    let allPosts = [];
    let postsByNumber = new Map();
    /** 「自分の書き込み」としてマークされているレス番号集合。
     *  appendPosts のペイロード ownPostNumbers で初期化 / 上書き、
     *  updateOwnPosts メッセージで増分更新される。renderPost のレンダ判定に使う。 */
    let ownPostNumbers = new Set();
    /** num → 当該レスを >>参照しているレス番号配列。renderCurrentViewMode で全再構築、
     *  appendPosts (flat) で増分更新。返信数バッジ生成のために常に最新を保つ。 */
    let currentReverseIndex = new Map();
    let viewMode = 'flat';

    // スクロール対象レス番号。setPosts / appendPosts のメッセージから受け取り、
    // 対象レスが DOM に現れたタイミングで scrollIntoView してクリアする。
    // 「次回オープン時にこのレスを viewport 下端に揃えたい」レス番号 (= scroll restore target)。
    // 値の意味は「読了 prefix の最大番号」(= 「先頭から連番が途切れず読み終えた」最後の番号)。
    // findReadProgressMaxNumber が算定し scheduleSendScrollPosition が C# に保存させる。
    // 復元時は el.scrollIntoView({ block: 'end' }) で対象レスの下端を viewport 下端に揃える。
    let pendingScrollTarget = null;

    // 設定 (Phase 11) で動的変更可能。setConfig メッセージで上書きされる。
    let POPULAR_THRESHOLD       = 3;
    let ID_HIGHLIGHT_THRESHOLD  = 5; // 同 ID が >= 5 件で「ID」文字列を赤化 (Phase 22)

    // ID / ワッチョイ (xxxx-yyyy) の出現一覧。renderCurrentViewMode / appendPosts 後に再計算する。
    //   id      → [postNumber, postNumber, ...]
    //   wacchoi → [postNumber, postNumber, ...]
    let currentIdMap      = new Map();
    let currentWatchoiMap = new Map();
    // ワッチョイ抽出用の正規表現。`xxxx-yyyy` (4+1+4 = 9 chars) パターン。
    //   - 文字種は alphanumeric + `+/` (5ch のワッチョイ末尾は base64 風で `+/` を含むことがある)。
    //   - 名前文字列に複数候補があった場合は最初の 1 件を採用する。
    const WATCHOI_RE = /[A-Za-z0-9+/]{4}-[A-Za-z0-9+/]{4}/;

    // 「以降新レス」ラベル。
    //   markPostNumber: ラベルを出すレス番号 (= リフレッシュで新たに来たレスの先頭番号)。
    //                   毎 appendPosts のペイロードで C# から最新値が届く (= リフレッシュ完了で更新される)。
    //                   null = ラベルなし (= まだリフレッシュで新着が来ていない初回オープン状態)。
    //
    //   ※ 旧「ここまで読んだ」帯 (= ユーザスクロールで C# に位置を通知し帯を更新する仕組み) は撤去。
    //     ユーザ操作と auto-scroll の境界が曖昧で bug を生みやすかったため。
    //     dedup-tree の「親ごと描写」境界 (= 旧 incrementalPivotIndex) もこのラベルに統一。
    let markPostNumber = null;

    // 「最新リフレッシュ (= 直近の delta batch group) で届いたレス番号」の集合。
    // section B (= 「以降新レス」ラベル以降) の描画コンテンツと、is-new (= レス No 太字) の判定の両方に使う。
    // 新しい refresh の境界 (= mark 値変化) で本 Set はリセットされ、前 refresh のレスは section A に昇格する。
    // ⇒ 結果、section B には「今 refresh の新着レス + その祖先 chain」だけが残り、太字も今 refresh 分だけになる。
    const sessionNewPostNumbers = new Set();

    // appendPosts 経路だけが見る「最後に観測した delta refresh の mark 値」。
    // 新 refresh 検出 (= newMark !== lastDeltaMark) に使う。
    // setMarkPostNumber メッセージは appendPosts より先に届いて markPostNumber を上書きしてしまうため、
    // markPostNumber を比較に使うとタイミング問題で検出が漏れる。それを避けるための独立トラッカー。
    let lastDeltaMark = null;

    // 現在のフィルタ条件 (= C# 側 ThreadFilter record の JSON 反映)。setFilter メッセージで上書きされる。
    // 評価ルール:
    //   - textQuery (= AND 条件): 本文に含まれていれば match (= 大文字小文字無視)。空なら本文条件なし。
    //   - popularOnly + mediaOnly (= 互いに OR): どちらかでも ON なら、その条件に match するレスだけ表示。
    //     両方 OFF なら toggle 条件なし (textQuery のみで判定)。
    let currentFilter = { textQuery: '', popularOnly: false, mediaOnly: false };

    // popularOnly フィルタの「展開済み include 集合」キャッシュ。
    // tree / dedupTree モードでは popular レス自身 + その配下 (= 返信チェイン) を全て表示する仕様で、
    // この集合を毎レス毎に計算するのは無駄なので、setFilter / appendPosts 時に一括計算してキャッシュする。
    let popularIncludeCache = null;

    /** 現在のフィルタが「全レス可視」(= フィルタなし) と等価か。短絡判定用。 */
    function isFilterEmpty() {
        return !currentFilter.textQuery && !currentFilter.popularOnly && !currentFilter.mediaOnly;
    }

    /** popularOnly フィルタ用の include 集合を組む。
     *   - flat モード: popular なレス番号 (= 返信数 >= POPULAR_THRESHOLD) のみ
     *   - tree / dedupTree モード: popular なレスとその配下 (= 返信チェイン全て) を BFS で展開して含める */
    function rebuildPopularIncludeSet() {
        const set = new Set();
        if (!currentFilter.popularOnly) { popularIncludeCache = set; return; }

        const reverseIdx = currentReverseIndex && currentReverseIndex.size > 0
            ? currentReverseIndex
            : buildReverseIndex();
        // popular レスを列挙
        const popularNums = [];
        for (const p of allPosts) {
            const refs = reverseIdx.get(p.number);
            if (refs && refs.length >= POPULAR_THRESHOLD) popularNums.push(p.number);
        }
        for (const n of popularNums) set.add(n);

        // tree 系では配下 (= 返信チェイン) を BFS で展開 (戦略の expandsPopularChain で切替)
        if (vm().expandsPopularChain()) {
            const queue = popularNums.slice();
            while (queue.length > 0) {
                const cur = queue.shift();
                const replies = reverseIdx.get(cur) || [];
                for (const r of replies) {
                    if (!set.has(r)) { set.add(r); queue.push(r); }
                }
            }
        }
        popularIncludeCache = set;
    }

    /** 1 レス (= JS の post オブジェクト = postsByNumber の値) がフィルタ条件に match するか。
     *  textQuery は AND、popularOnly / mediaOnly は互いに OR。 */
    function postMatchesFilter(post) {
        if (!post) return true;

        // (1) textQuery (AND): 検索対象は本文 + 名前行のすべての要素
        //     (name = ワッチョイ含む / mail / dateText / id ユーザ ID)。
        //     post-no (レス番号) は対象外 (= 数字を打つと意図せずレス番号が引っかかるのを避ける)。
        if (currentFilter.textQuery) {
            const q = currentFilter.textQuery.toLowerCase();
            const haystack = (
                (post.body || '')     + '\n' +
                (post.name || '')     + '\n' +
                (post.mail || '')     + '\n' +
                (post.dateText || '') + '\n' +
                (post.id || '')
            ).toLowerCase();
            if (haystack.indexOf(q) < 0) return false;
        }

        // (2) popularOnly / mediaOnly (互いに OR、どちらか ON なら match 必須)
        const popOn = currentFilter.popularOnly === true;
        const medOn = currentFilter.mediaOnly === true;
        if (popOn || medOn) {
            const popMatch = popOn && popularIncludeCache && popularIncludeCache.has(post.number);
            const medMatch = medOn && (bodyContainsImage(post.body) || bodyContainsVideo(post.body));
            if (!popMatch && !medMatch) return false;
        }
        return true;
    }

    /** 現状の DOM 内全 .post に対して filter-hidden クラスを付け外しする。
     *  primary レス (id="rN") を見て、その body で評価。embedded duplicate (= ツリー埋め込み) は
     *  自分自身の所属 number で判定する (= 同じ post オブジェクトを参照するので結果は一致)。
     *  filter 条件が空ならまとめて class を剥がして早期 return。
     *  最後に <see cref="applySearchHighlightToAll"/> を呼んでマッチ箇所のハイライトも更新する。 */
    function applyFilterToAllPosts() {
        const root = document.getElementById('posts');
        if (!root) return;
        // popularOnly トグル用の include 集合をここで毎回再計算 (= allPosts や viewMode の変化に追従)。
        rebuildPopularIncludeSet();
        if (isFilterEmpty()) {
            root.querySelectorAll('.filter-hidden').forEach(function (el) { el.classList.remove('filter-hidden'); });
            applySearchHighlightToAll();
            return;
        }
        root.querySelectorAll('.post').forEach(function (el) {
            const id = el.id || '';
            // primary レスは id="rN"。embedded は id 無し or 別 — その場合は親要素の data-from 等から番号を引きたいが
            // 簡単のためコメント本文をテキスト走査で評価する。
            let post = null;
            if (id.startsWith('r')) {
                const n = parseInt(id.slice(1), 10);
                if (!isNaN(n)) post = postsByNumber.get(n);
            }
            // post オブジェクトが取れなければ DOM テキストで判定 (embedded などのフォールバック)。
            const matched = post
                ? postMatchesFilter(post)
                : (currentFilter.textQuery
                    ? (el.textContent || '').toLowerCase().indexOf(currentFilter.textQuery.toLowerCase()) >= 0
                    : true);
            if (matched) el.classList.remove('filter-hidden');
            else         el.classList.add('filter-hidden');
        });
        // dedupTree モードの incremental-block (= 「以降新レス」ラベル以下の各ブロック) は
        // トップレベル要素が祖先レス (id 無し) で、新着デルタ本体は内部の embedded で配置されている。
        // 内部の ID 付きレス (= 新着デルタ) が全て filter-hidden ならブロック自体も非表示にする
        // (= さもないと祖先 wrapper だけ残ってフィルタが効いていないように見える)。
        root.querySelectorAll('.incremental-block').forEach(function (block) {
            const inner = block.querySelectorAll('.post[id^="r"]');
            let anyVisible = false;
            for (let i = 0; i < inner.length; i++) {
                if (!inner[i].classList.contains('filter-hidden')) { anyVisible = true; break; }
            }
            if (anyVisible || inner.length === 0) block.classList.remove('filter-hidden');
            else                                  block.classList.add('filter-hidden');
        });
        applySearchHighlightToAll();
    }

    /** スレッド内の全 .post-body 配下のテキストノードを走査し、textQuery に一致する箇所を
     *  <mark class="search-highlight"> でラップしてハイライトする。
     *  既存ハイライトは毎回剥がしてから掛け直すので、クエリ変更や差分追加にも追従する。
     *  primary / embedded どちらの .post-body でも同じ処理。 */
    function applySearchHighlightToAll() {
        const root = document.getElementById('posts');
        if (!root) return;

        // Step 1: 既存ハイライトを unwrap (= mark を中身のテキストに置換) → normalize で隣接テキスト統合
        const olds = root.querySelectorAll('mark.search-highlight');
        olds.forEach(function (m) {
            const text = document.createTextNode(m.textContent || '');
            m.parentNode.replaceChild(text, m);
        });
        if (olds.length > 0) root.normalize();

        if (isFilterEmpty()) return;
        const query = currentFilter.textQuery || '';
        if (!query) return;
        const queryLower = query.toLowerCase();
        const queryLen   = query.length;

        // Step 2: 検索対象領域のテキストノードを走査して該当箇所を <mark> で囲む。
        // フィルタ判定 (postMatchesFilter) と整合させるため、対象は本文 + 名前行 (= 名前 / メール /
        // メタ = 日付・ID)。post-no (レス番号) はフィルタ対象外なのでハイライトもしない。
        root.querySelectorAll('.post-body, .post-name, .post-mail, .post-meta').forEach(function (el) {
            highlightTextNodesIn(el, queryLower, queryLen);
        });
    }

    /** 指定要素配下の全テキストノードを走査し、queryLower (= 既に小文字化済) を含むものを
     *  <mark class="search-highlight"> 入りの DocumentFragment に置換する。
     *  ミューテーションを避けるため事前にテキストノード一覧を集めてから処理。
     *  既に <mark.search-highlight> 内のテキストは対象外 (= 二重ラップ防止)。 */
    function highlightTextNodesIn(rootEl, queryLower, queryLen) {
        const texts = [];
        const walker = document.createTreeWalker(rootEl, NodeFilter.SHOW_TEXT, null);
        while (walker.nextNode()) texts.push(walker.currentNode);

        for (var i = 0; i < texts.length; i++) {
            const tn = texts[i];
            if (!tn.parentNode) continue;
            const parent = tn.parentNode;
            if (parent.classList && parent.classList.contains('search-highlight')) continue;

            const text = tn.nodeValue || '';
            const lower = text.toLowerCase();
            let idx = lower.indexOf(queryLower);
            if (idx < 0) continue;

            const frag = document.createDocumentFragment();
            let pos = 0;
            while (idx >= 0) {
                if (idx > pos) frag.appendChild(document.createTextNode(text.slice(pos, idx)));
                const mark = document.createElement('mark');
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

    /** allPosts における「ラベル位置」(= markPostNumber 以上の最初のレスの index)。
     *  null = ラベルなし or ラベル以降のレスがまだ存在しない。
     *  dedup-tree の Section A/B 分割と markNewPosts の境界判定に使う。 */
    function computeMarkIndex() {
        if (markPostNumber == null) return null;
        for (let i = 0; i < allPosts.length; i++) {
            if (allPosts[i].number >= markPostNumber) return i;
        }
        return null;
    }

    // ユーザーが手動でスクロールしたか — 「ホイール / ドラッグ / 矢印キー / PageUp-Down / 触れる」
    // のいずれかが起きた時点で true にする。一度 true になったら以降は元に戻さない。
    //
    // 用途:
    //   - tryScrollToTarget は user input 前は batch ごとに繰り返し target を追従し続ける
    //     (= ストリーミング中に layout shift が起きても最終的に target に着地)
    //   - scrollPosition の送信は user input 後だけ行う
    //     (= scrollIntoView 由来の auto-fire を吸収して idx.json と ScrollTargetPostNumber の
    //        フィードバックループで scroll target がドリフトするのを防ぐ)
    //
    // capture フェーズで購読することで子要素の preventDefault 等の影響を受けない。
    let userHasScrolled = false;
    ['wheel', 'touchstart', 'mousedown', 'keydown'].forEach(function(ev) {
        window.addEventListener(ev, function() { userHasScrolled = true; }, { passive: true, capture: true });
    });

    // ---------- C# 側 LogService への汎用デバッグ出力 ----------
    // 用途: リリースビルド (= F12 DevTools が無い環境) でも JS の挙動を追えるようにする一時的な仕掛け。
    // 必要なくなったら debugLog 呼び出し + WebMessageBridge.cs の "debugLog" case + 本ヘルパを削除する。
    function debugLog(msg) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({ type: 'debugLog', message: String(msg) });
            }
        } catch (_) { /* never throw */ }
    }


    // ---------- escape helpers ----------
    function escapeHtml(s) {
        if (s === null || s === undefined) return '';
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    // ---------- regexes (本文/名前テキストを安全な HTML に変換) ----------
    const TAG_BLOCK_RE     = /<(a|b|small)\b([^>]*)>([\s\S]*?)<\/\1>|<\/?[a-z][^>]*>/gi;
    const HREF_RE          = /href\s*=\s*["']([^"']+)["']/i;
    const INNER_ANCHOR_RE  = /^\s*>>\s*(\d+)(?:\s*-\s*(\d+))?\s*$/;
    const HREF_ANCHOR_RE   = /read\.cgi\/[^/]+\/[^/]+\/(\d+)(?:-(\d+))?\/?$/i;
    const STRIP_TAGS_RE    = /<[^>]+>/g;
    const URL_OR_ANCHOR_RE = /(https?:\/\/[^\s<>"'、。()\[\]{}]+)|(>>\s*(\d+)(?:\s*-\s*(\d+))?)/g;

    // 5ch.io / 5ch.net / bbspink.com の <c>/test/read.cgi/&lt;dir&gt;/&lt;key&gt;(/&lt;postSpec&gt;)?</c> URL を検出。
    // postSpec は \d+ または \d+-\d+ (範囲)。指定が無いときは postNo=0 を返す (= 「1 レス目を見せる」の合図)。
    const FIVECH_THREAD_RE = /^https?:\/\/([A-Za-z0-9.-]+)\/test\/read\.cgi\/([A-Za-z0-9]+)\/(\d+)(?:\/([^/?#]*))?/i;
    function parseFiveChThreadUrl(href) {
        if (!href) return null;
        const m = FIVECH_THREAD_RE.exec(href);
        if (!m) return null;
        const host = m[1].toLowerCase();
        const ok = host === '5ch.io'      || host.endsWith('.5ch.io')
                || host === '5ch.net'     || host.endsWith('.5ch.net')
                || host === 'bbspink.com' || host.endsWith('.bbspink.com');
        if (!ok) return null;
        let postNo = 0;
        if (m[4]) {
            const pm = /^(\d+)/.exec(m[4]);
            if (pm) postNo = parseInt(pm[1], 10);
        }
        return { host: host, dir: m[2], key: m[3], postNo: postNo };
    }

    function buildBodyHtml(rawText) {
        if (!rawText) return '';
        // Defensive <br> → \n
        let text = rawText.replace(/<br\s*\/?>/gi, '\n');

        let html = '';
        let pos = 0;
        let m;
        TAG_BLOCK_RE.lastIndex = 0;
        while ((m = TAG_BLOCK_RE.exec(text)) !== null) {
            if (m.index > pos) html += processPlain(text.slice(pos, m.index));
            const tag = m[1] && m[1].toLowerCase();
            if (tag === 'a') {
                html += renderAnchorElement(m[2] || '', m[3] || '');
            } else if (tag === 'b') {
                html += '<b>' + escapeHtml(stripTags(m[3] || '')) + '</b>';
            } else if (tag === 'small') {
                html += '<small>' + escapeHtml(stripTags(m[3] || '')) + '</small>';
            }
            // 不明タグはタグだけ落として中身を捨てる (中身は次イテレーションで処理されない点は既知の挙動)
            pos = m.index + m[0].length;
        }
        if (pos < text.length) html += processPlain(text.slice(pos));
        return html;
    }

    function stripTags(s) { return String(s).replace(STRIP_TAGS_RE, ''); }

    function processPlain(text) {
        // 改行 → <br>、URL とレスアンカーを auto-link
        const lines = text.split('\n');
        let html = '';
        for (let i = 0; i < lines.length; i++) {
            if (i > 0) html += '<br>';
            html += processLine(lines[i]);
        }
        return html;
    }

    function processLine(line) {
        let html = '';
        let pos = 0;
        let m;
        URL_OR_ANCHOR_RE.lastIndex = 0;
        while ((m = URL_OR_ANCHOR_RE.exec(line)) !== null) {
            if (m.index > pos) html += escapeHtml(line.slice(pos, m.index));
            if (m[1]) {
                html += renderExternalLink(m[1], m[1]);
            } else {
                const from = parseInt(m[3], 10);
                const to = m[4] ? parseInt(m[4], 10) : from;
                html += renderPostAnchor(from, to, m[2]);
            }
            pos = m.index + m[0].length;
        }
        if (pos < line.length) html += escapeHtml(line.slice(pos));
        return html;
    }

    function renderAnchorElement(attrs, innerRaw) {
        const inner = stripTags(innerRaw);
        const innerM = INNER_ANCHOR_RE.exec(inner);
        if (innerM) {
            const f = parseInt(innerM[1], 10);
            const t = innerM[2] ? parseInt(innerM[2], 10) : f;
            return renderPostAnchor(f, t, inner);
        }
        const hrefM = HREF_RE.exec(attrs);
        const href = hrefM ? hrefM[1] : '';
        // 5ch.io / bbspink.com スレ URL は同スレ内アンカー (= 旧 HREF_ANCHOR_RE 経路) ではなく、
        // ホバーでスレタイトル + 対象レス本文を出す thread-link として扱う。
        // (URL に board / key が含まれており、現スレと違う可能性がある以上、postNo だけ取って同スレ扱いするのは不正確)
        const fiveCh = parseFiveChThreadUrl(href);
        if (fiveCh) {
            return renderThreadLink(href, inner, fiveCh);
        }
        const hrefA = HREF_ANCHOR_RE.exec(href);
        if (hrefA) {
            const f = parseInt(hrefA[1], 10);
            const t = hrefA[2] ? parseInt(hrefA[2], 10) : f;
            return renderPostAnchor(f, t, inner);
        }
        return renderExternalLink(href, inner);
    }

    function renderPostAnchor(from, to, visible) {
        // href は意図的に付けない。<a> が href を持つと focusable になり、
        // Chromium が DOM 再構築後にそれへ auto-focus → auto-scroll してしまうため。
        return '<a class="anchor" data-from="' + from + '" data-to="' + to + '">' +
               escapeHtml(visible) + '</a>';
    }

    // ---------- 画像 / 動画 URL 検出 + 外部サービスの展開 ----------
    // 直リンクの拡張子判定。? や # の手前で見る。
    const IMAGE_EXT_RE = /\.(jpe?g|png|gif|webp)(?:[?#]|$)/i;
    function isImageUrl(url) {
        return IMAGE_EXT_RE.test(url || '');
    }

    const VIDEO_EXT_RE = /\.(mp4|webm|mov)(?:[?#]|$)/i;
    function isVideoUrl(url) {
        return VIDEO_EXT_RE.test(url || '');
    }

    // YouTube — watch / youtu.be / shorts / embed の各形を 11 桁 ID で受ける。
    const YOUTUBE_RES = [
        /^https?:\/\/(?:www\.|m\.)?youtube\.com\/watch\?(?:[^#]*&)?v=([A-Za-z0-9_-]{11})/i,
        /^https?:\/\/youtu\.be\/([A-Za-z0-9_-]{11})/i,
        /^https?:\/\/(?:www\.|m\.)?youtube\.com\/shorts\/([A-Za-z0-9_-]{11})/i,
        /^https?:\/\/(?:www\.)?youtube\.com\/embed\/([A-Za-z0-9_-]{11})/i,
    ];
    function extractYouTubeId(url) {
        if (!url) return null;
        for (const re of YOUTUBE_RES) {
            const m = re.exec(url);
            if (m) return m[1];
        }
        return null;
    }

    /**
     * ページ URL を実体画像 URL に展開する変換器の配列。順に試して最初にマッチしたものを返す。
     * 将来 x.com / pixiv 等を追加する場合はここに entry を増やす (それぞれ HTML スクレイピング
     * や外部 API が必要なため、現状は imgur のみ JS だけで完結する単純展開を実装)。
     */
    const URL_EXPANDERS = [
        {
            // imgur のシングル画像ページ: imgur.com/<id> → i.imgur.com/<id>.jpg
            // a/ (album), gallery/, t/ (tag) などは複数画像を持つので対象外。
            // i.imgur.com/<id>.<ext> は IMAGE_EXT_RE 側で先に拾われる。
            pattern: /^https?:\/\/(?:www\.|m\.)?imgur\.com\/([a-zA-Z0-9]+)(?:[/?#].*)?$/i,
            expand: function (m) {
                const id = m[1];
                if (id === 'a' || id === 'gallery' || id === 't') return null;
                return 'https://i.imgur.com/' + id + '.jpg';
            }
        }
    ];

    function expandToImageUrl(url) {
        for (const e of URL_EXPANDERS) {
            const m = e.pattern.exec(url);
            if (!m) continue;
            const expanded = e.expand(m);
            if (expanded) return expanded;
        }
        return null;
    }

    /** インラインサムネイルとして表示すべき画像 URL を返す。直リンクならそのまま、ページ URL なら展開結果を返す。 */
    function getInlineImageSrc(url) {
        if (isImageUrl(url)) return url;
        return expandToImageUrl(url);
    }

    /**
     * 同期では決められないが、非同期 (C# 側の HTML スクレイピング / API 呼び出し) で
     * 実体画像 URL を解決できる可能性のある URL のパターン。
     * C# の <see cref="ChBrowser.Services.Image.UrlExpander"/> と同期して維持する。
     */
    const ASYNC_EXPANDER_RES = [
        // x.com (Twitter) — fxtwitter API で展開
        /^https?:\/\/(?:www\.|m\.|mobile\.)?(?:twitter|x|fxtwitter|vxtwitter)\.com\/[A-Za-z0-9_]+\/status\/\d+/i,
        // pixiv — ajax/illust API で展開
        /^https?:\/\/(?:www\.)?pixiv\.net\/(?:en\/)?artworks\/\d+/i,
    ];

    function isExpandableAsync(url) {
        for (const re of ASYNC_EXPANDER_RES) if (re.test(url)) return true;
        return false;
    }

    /** URL に対応するメディアスロット HTML を返す。マッチしないなら空文字。
     *  画像 (直リンク / imgur 等)、直リンク動画、YouTube、x.com/pixiv 非同期展開対象を扱う。 */
    function buildMediaSlotForUrl(href) {
        // 1) YouTube — サムネイル (i.ytimg.com) を画像スロットとしてロードし、クリックでデフォルトブラウザに開く
        const ytId = extractYouTubeId(href);
        if (ytId) {
            const thumb = 'https://i.ytimg.com/vi/' + ytId + '/hqdefault.jpg';
            return '<span class="image-slot youtube deferred" data-src="' + escapeHtml(thumb) +
                   '" data-url="' + escapeHtml(href) + '" data-video-id="' + escapeHtml(ytId) +
                   '" data-media-type="youtube"></span>';
        }
        // 2) 直リンク動画 (.mp4 .webm .mov)
        if (isVideoUrl(href)) {
            return '<span class="image-slot video" data-src="' + escapeHtml(href) +
                   '" data-url="' + escapeHtml(href) + '" data-media-type="video">' +
                   '<span class="media-play-icon"></span>' +
                   '<span class="image-placeholder-text">動画 - クリックで再生</span>' +
                   '</span>';
        }
        // 3) 同期で決まる画像 URL (直リンク / imgur 単純展開)
        const imgSrc = getInlineImageSrc(href);
        if (imgSrc) {
            return '<span class="image-slot deferred" data-src="' + escapeHtml(imgSrc) +
                   '" data-url="' + escapeHtml(href) + '"></span>';
        }
        // 4) 非同期で展開する URL (x.com / pixiv 等)
        if (isExpandableAsync(href)) {
            // 既に「画像なし」と確定している URL (= 過去の imageMetaRequest で noMedia 返却済) はスロットを作らない。
            // モード切替や再描画でこの関数が同じ URL に対して何度も呼ばれるが、サムネ枠を毎回作って毎回消すのは無駄。
            const cached = imageMetaCache.get(href);
            if (cached && cached.noMedia) return '';
            return '<span class="image-slot deferred async" data-src="' + escapeHtml(href) +
                   '" data-url="' + escapeHtml(href) + '" data-async-expand="1"></span>';
        }
        return '';
    }

    /** 指定 URL を data-url に持つ全 .image-slot を DOM から取り除く (= ツイートに画像なし等で
     *  サムネ枠を出す価値が無いと確定したときに使う)。 */
    function removeMediaSlotsForUrl(url) {
        if (!url) return;
        document.querySelectorAll('.image-slot').forEach(function (slot) {
            if (slot.dataset.url === url) slot.remove();
        });
    }

    /** 本文処理中にメディアスロットを集める buffer。null のときは収集しない (post-name など)。 */
    let _currentBodyMediaSlots = null;

    /** 5ch.io / bbspink.com スレ URL を、ホバーでタイトル + 対象レス本文をプレビューする
     *  リンクに置換する HTML を返す。click は本アプリの新タブで開く既存 openUrl 経路を再利用。 */
    function renderThreadLink(href, visible, fiveCh) {
        return '<a class="thread-link"'
             + ' data-url="'         + escapeHtml(href)              + '"'
             + ' data-thread-host="' + escapeHtml(fiveCh.host)       + '"'
             + ' data-thread-dir="'  + escapeHtml(fiveCh.dir)        + '"'
             + ' data-thread-key="'  + escapeHtml(fiveCh.key)        + '"'
             + ' data-thread-post="' + fiveCh.postNo                 + '">'
             + escapeHtml(visible) + '</a>';
    }

    function renderExternalLink(href, visible) {
        const lower = (href || '').toLowerCase();
        if (lower.indexOf('http://') !== 0 && lower.indexOf('https://') !== 0) {
            return '<span class="link-disabled">' + escapeHtml(visible) + '</span>';
        }
        // 5ch.io / bbspink.com のスレ URL はホバーでタイトル + レス本文をポップアップする専用リンクにする。
        // クリック動作 (= 本アプリ新タブで開く) は openUrl で従来通り処理されるので click 側は変更不要。
        const fiveCh = parseFiveChThreadUrl(href);
        if (fiveCh) {
            // スレ URL はサムネ枠 (= media slot) を作らない (= 画像 URL ではないため)。
            return renderThreadLink(href, visible, fiveCh);
        }
        // href は意図的に付けない (focus 由来 auto-scroll を防ぐため)。click は data-url で識別。
        const linkHtml = '<a data-url="' + escapeHtml(href) + '">' + escapeHtml(visible) + '</a>';

        // メディアスロットは post-body から分離して post-media (={{media}} スロット) に集める。
        // 収集 buffer がアクティブな時 (= body 処理中) のみ push。post-name など他の場面では null。
        if (_currentBodyMediaSlots !== null) {
            const slot = buildMediaSlotForUrl(href);
            if (slot) _currentBodyMediaSlots.push(slot);
        }
        return linkHtml;
    }

    /** body テキストを HTML 化しつつ、本文中のメディアスロットを別途集める。
     *  post-body と post-media に分けて表示するために使う。テンプレで {{media}} スロットに入る。 */
    function buildBodyAndMedia(rawText) {
        const prev = _currentBodyMediaSlots;
        _currentBodyMediaSlots = [];
        const body = buildBodyHtml(rawText);
        const media = _currentBodyMediaSlots.join('');
        _currentBodyMediaSlots = prev;
        return { body: body, media: media };
    }

    // ---------- post HTML builders ----------
    // body 直下にインライン展開する範囲アンカー (>>N-M) の最大幅。例: 5 だと >>1-5 までは展開、>>1-100 はしない。
    const INLINE_EXPAND_RANGE_LIMIT = 5;

    // ---- テンプレートエンジン (post.html を読んで {{var}} / {{#if}}{{/if}} を解釈) ----

    // raw 挿入する変数名 (HTML 既処理済)。それ以外は HTML エスケープして挿入。
    const RAW_TEMPLATE_VARS = new Set(['name', 'body', 'media', 'children']);

    function _isTemplateTruthy(v) {
        if (v === undefined || v === null) return false;
        if (v === false || v === 0 || v === '') return false;
        return true;
    }

    function _parseTemplate(tpl) {
        const tokens = [];
        let i = 0;
        while (i < tpl.length) {
            // HTML コメント (<!-- ... -->) は内部の {{...}} も含めて opaque な text チャンクとして扱う。
            // テンプレ作成者がコメント内に `{{#if foo}}` を「説明文」として書いても、構文として解釈されない
            // ようにするため。これがないと未対応の {{#if}} がスタックに溜まってパース失敗する。
            const commentOpen = tpl.indexOf('<!--', i);
            const tagOpen     = tpl.indexOf('{{',  i);

            if (commentOpen < 0 && tagOpen < 0) {
                if (i < tpl.length) tokens.push({ t: 'text', v: tpl.slice(i) });
                break;
            }

            // コメントが先に来るなら、コメント末尾までを丸ごと text に。
            if (commentOpen >= 0 && (tagOpen < 0 || commentOpen < tagOpen)) {
                const commentClose = tpl.indexOf('-->', commentOpen + 4);
                if (commentClose < 0) {
                    // コメント未閉じ — そのまま末尾までテキスト
                    tokens.push({ t: 'text', v: tpl.slice(i) });
                    break;
                }
                const end = commentClose + 3;
                tokens.push({ t: 'text', v: tpl.slice(i, end) });
                i = end;
                continue;
            }

            // {{ が先に来る通常のケース
            if (tagOpen > i) tokens.push({ t: 'text', v: tpl.slice(i, tagOpen) });
            const close = tpl.indexOf('}}', tagOpen + 2);
            if (close < 0) throw new Error('post template: unclosed "{{"');
            const inner = tpl.slice(tagOpen + 2, close).trim();
            if (inner.startsWith('#if ')) tokens.push({ t: 'ifStart', name: inner.slice(4).trim() });
            else if (inner === '/if')    tokens.push({ t: 'ifEnd' });
            else                         tokens.push({ t: 'var', name: inner });
            i = close + 2;
        }
        // build tree (#if … /if は 1 段だけネスト可)
        const root = [];
        const stack = [root];
        for (const tok of tokens) {
            const top = stack[stack.length - 1];
            if (tok.t === 'ifStart') {
                const node = { t: 'if', name: tok.name, children: [] };
                top.push(node);
                stack.push(node.children);
            } else if (tok.t === 'ifEnd') {
                if (stack.length === 1) throw new Error('post template: unmatched {{/if}}');
                stack.pop();
            } else {
                top.push(tok);
            }
        }
        if (stack.length !== 1) throw new Error('post template: unclosed {{#if}}');
        return root;
    }

    function _renderTemplate(chunks, data) {
        let out = '';
        for (const c of chunks) {
            if (c.t === 'text') {
                out += c.v;
            } else if (c.t === 'var') {
                const v = data[c.name];
                if (v === undefined || v === null) continue;
                out += RAW_TEMPLATE_VARS.has(c.name) ? String(v) : escapeHtml(String(v));
            } else if (c.t === 'if') {
                if (_isTemplateTruthy(data[c.name])) out += _renderTemplate(c.children, data);
            }
        }
        return out;
    }

    let _postTemplateChunks = null;
    function _ensurePostTemplate() {
        if (_postTemplateChunks !== null) return;
        const el = document.getElementById('post-template');
        const raw = (el && el.textContent) ? el.textContent.trim() : '';
        if (!raw) {
            // テーマ未注入: 空配列 → renderPost が組み込み fallback を使う
            _postTemplateChunks = [];
            return;
        }
        try { _postTemplateChunks = _parseTemplate(raw); }
        catch (e) {
            console.error('[chbrowser] post template parse error:', e);
            _postTemplateChunks = [];
        }
    }

    // ---- 返信数による post-no の色階層 (ピンク / 赤) しきい値 ----
    // テンプレの post-no class (= has-replies-few / has-replies-many) と JS 差分更新
    // (updateReplyCountBadge) の両方から replyTierClass で参照され、しきい値は 1 箇所に集約。
    const REPLY_TIER_PINK = 1; // 1 件以上 (かつ赤未満) で post-no をピンク
    const REPLY_TIER_RED  = 3; // 3 件以上で post-no を赤 (= 排他的に上書き)

    /** 返信数 count に対応する post-no クラス名 ('' / 'has-replies-few' / 'has-replies-many') を返す。 */
    function replyTierClass(count) {
        if (count >= REPLY_TIER_RED)  return 'has-replies-many';
        if (count >= REPLY_TIER_PINK) return 'has-replies-few';
        return '';
    }

    /** post 1 件の view-data を作る (テンプレに渡す変数たち)。 */
    function postDataFor(p, isEmbedded, omitId, children) {
        const num     = p.number;
        const replies = currentReverseIndex.get(num) || [];
        const count   = replies.length;
        // body は文字本文、media は末尾に並べる画像/動画/YouTube スロット HTML。
        const built = buildBodyAndMedia(p.body || '');
        return {
            number:         num,
            name:           buildBodyHtml(p.name || ''),
            mail:           p.mail || '',            // メール欄 (sage 等)。RAW_TEMPLATE_VARS に含めないので escape 適用。
            date:           p.dateText || '',
            id:             p.id || '',
            body:           built.body,
            media:          built.media,
            replyCount:     count,
            replyNumbers:   replies.join(','),       // バッジの data-replies 用 (ホバーポップアップで使う)
            hasFewReplies:  count >= REPLY_TIER_PINK && count < REPLY_TIER_RED,
            hasManyReplies: count >= REPLY_TIER_RED,
            isOwn:          ownPostNumbers.has(num), // 「自分の書き込み」バッジ表示用
            isEmbedded:     !!isEmbedded,
            domId:          !omitId,
            children:       children || '',
        };
    }

    /** テンプレートに data を当てて 1 レス分の HTML を返す。
     *  C# 側 LoadThreadShellHtml が必ずテンプレ (ユーザ編集 or 埋め込み既定) を thread.html に注入するため、
     *  通常テンプレ未読 / parse 失敗には到達しない。万一起きた場合は構造を JS 側で再実装せず
     *  エラー placeholder だけ返す (= テンプレが唯一のレス DOM 構造定義になることを保証)。 */
    function renderPost(data) {
        _ensurePostTemplate();
        if (_postTemplateChunks && _postTemplateChunks.length > 0) {
            return _renderTemplate(_postTemplateChunks, data);
        }
        return '<div class="post post-template-error">post template missing (data.number=' + data.number + ')</div>';
    }

    /** flat (レス順) モードの 1 レス分。インライン展開なし。 */
    function buildPostHtml(p) {
        return renderPost(postDataFor(p, /*isEmbedded*/ false, /*omitId*/ false, ''));
    }

    // ---------- ツリー表示 (重複あり / 重複なし) 用の補助 ----------

    /** 本文中の >>N / >>N-M 参照をすべて抽出 (タグは事前ストリップ)。 */
    const ANCHOR_REF_RE = />>\s*(\d+)(?:\s*-\s*(\d+))?/g;
    function extractAnchorRefs(body) {
        if (!body) return [];
        const stripped = body.replace(/<[^>]+>/g, ' ');
        const refs = [];
        ANCHOR_REF_RE.lastIndex = 0;
        let m;
        while ((m = ANCHOR_REF_RE.exec(stripped)) !== null) {
            let from = parseInt(m[1], 10);
            let to   = m[2] ? parseInt(m[2], 10) : from;
            if (to < from) { const tmp = from; from = to; to = tmp; }
            refs.push({ from: from, to: to });
        }
        return refs;
    }

    /** レス番号 N → N を >>参照しているレス番号配列、を返す逆引き。
     *  <code>posts</code> が省略されたら全レス (allPosts) を対象にする (= 既存挙動と同じ)。
     *  Section A (= dedup-tree の pre-pivot) 描画時は subset を渡してインクリメンタル分を除外する。 */
    function buildReverseIndexFrom(posts) {
        const rev = new Map();
        for (const post of posts) {
            const seen = new Set();
            for (const r of extractAnchorRefs(post.body || '')) {
                if (r.to - r.from + 1 > INLINE_EXPAND_RANGE_LIMIT) continue;
                for (let n = r.from; n <= r.to; n++) {
                    if (n >= post.number) continue;       // 後方参照のみ親候補
                    if (!postsByNumber.has(n)) continue;
                    if (seen.has(n)) continue;
                    seen.add(n);
                    if (!rev.has(n)) rev.set(n, []);
                    rev.get(n).push(post.number);
                }
            }
        }
        return rev;
    }
    function buildReverseIndex() { return buildReverseIndexFrom(allPosts); }

    /** p の本文中の有効な forward anchor (= postsByNumber に存在 & p.number 未満 & 範囲 limit 内) を distinct で返す。
     *  ツリー描画における「アンカー数」(0 / 1 / 多) の判定基準に使う共通ヘルパ。 */
    function getValidForwardAnchors(p) {
        if (!p) return [];
        const seen = new Set();
        const result = [];
        for (const r of extractAnchorRefs(p.body || '')) {
            if (r.to - r.from + 1 > INLINE_EXPAND_RANGE_LIMIT) continue;
            for (let n = r.from; n <= r.to; n++) {
                if (n >= p.number) continue;
                if (!postsByNumber.has(n)) continue;
                if (seen.has(n)) continue;
                seen.add(n);
                result.push(n);
            }
        }
        return result;
    }
    function countValidForwardAnchors(p) { return getValidForwardAnchors(p).length; }

    // ---- Phase 20: incremental セクションの chain forest 構築 ----
    /** num の祖先を anchor で 1 段ずつ遡って [oldest, ..., num] の配列にする。
     *  各レスから最初の有効 anchor (= 自分より小さい番号で postsByNumber に居る) を辿る。
     *  循環や行き止まりで終端。 */
    function buildAncestorChain(num) {
        const chain = [num];
        const seen  = new Set([num]);
        let cur     = num;
        while (true) {
            const post = postsByNumber.get(cur);
            if (!post) break;
            let next = null;
            for (const r of extractAnchorRefs(post.body || '')) {
                if (r.to - r.from + 1 > INLINE_EXPAND_RANGE_LIMIT) continue;
                for (let n = r.from; n <= r.to; n++) {
                    if (n >= cur) continue;
                    if (!postsByNumber.has(n)) continue;
                    if (seen.has(n)) continue;
                    next = n; break;
                }
                if (next != null) break;
            }
            if (next == null) break;
            seen.add(next);
            chain.unshift(next);
            cur = next;
        }
        return chain;
    }

    /** 各 incremental レスの祖先 chain を merge して forest (= Map&lt;number, node&gt;) を返す。
     *  共通祖先を持つ chain は同じノード配下に集約される (= 同じ親が複数回描画されないように)。
     *
     *  仕様: 単独 anchor のレスのみ祖先 chain を辿る (= 「アンカーをたどれる限りすべて描写」)。
     *  0 anchor / 多 anchor のレスは chain を作らず単独 root として末尾に並べる
     *  (= 仕様: 末尾に primary 追加するだけ)。 */
    function buildIncrementalForest(incrementalNumbers) {
        const rootMap = new Map(); // number -> node{number, childMap}
        for (const num of incrementalNumbers) {
            const post = postsByNumber.get(num);
            const anchors = post ? countValidForwardAnchors(post) : 0;
            const chain = (anchors === 1)
                ? buildAncestorChain(num)
                : [num];
            let curMap = rootMap;
            for (const n of chain) {
                let node = curMap.get(n);
                if (!node) {
                    node = { number: n, childMap: new Map() };
                    curMap.set(n, node);
                }
                curMap = node.childMap;
            }
        }
        return rootMap;
    }

    /** forest ノードを再帰的に HTML 化。
     *  isEmbedded は「inline-expansion 配下で描画されているか (= 深さ)」を示す:
     *    forest root (Section B の直下) は false、その配下の子は true で渡す。
     *    これにより Section A の通常の dedup tree と同じ見た目 (root=枠線なし / 子=枠線+色) になる。
     *  omitId は「Section A 側に primary instance を持つレス (= 既存の祖先) か」で決める:
     *    incrementalSet に含まれる (= 新規分) なら id 付き、それ以外 (= Section A にも存在) なら id 無し。 */
    function renderIncrementalForestNode(node, incrementalSet, isEmbedded) {
        const post = postsByNumber.get(node.number);
        if (!post) return '';
        const isInc = incrementalSet.has(node.number);
        let childrenHtml = '';
        for (const child of node.childMap.values()) {
            childrenHtml += '<div class="inline-expansion reverse">';
            childrenHtml += renderIncrementalForestNode(child, incrementalSet, /*isEmbedded*/ true);
            childrenHtml += '</div>';
        }
        return renderPost(postDataFor(post, isEmbedded, /*omitId*/ !isInc, childrenHtml));
    }

    /** ツリー (重複あり) の 1 レス分 HTML。
     *  - !isEmbedded (= primary): forward 親を inline で 1 段だけ embed する (= 「自分宛のアンカー先を本文の上に添える」見た目)。
     *    reverse-expansion (= 自分への返信を直下に展開) はもう作らない。返信が来たときに embedUnderParentReverse が
     *    DOM 直接操作で `<div class="inline-expansion reverse">` を後付けする。
     *  - isEmbedded: 中身は付けず leaf として返す (= forward / reverse 展開は呼び出し側が個別にやる)。
     *  primary 出力は id を付ける、embedded 出力は id を付けない (= 重複ありモードは同じレスが 2 か所に出るため)。 */
    function buildTreePostHtml(p, isEmbedded) {
        let children = '';
        if (!isEmbedded) {
            const emitted = new Set();
            for (const r of extractAnchorRefs(p.body || '')) {
                if (r.to - r.from + 1 > INLINE_EXPAND_RANGE_LIMIT) continue;
                for (let n = r.from; n <= r.to; n++) {
                    if (n >= p.number) continue;
                    if (emitted.has(n)) continue;
                    emitted.add(n);
                    const parent = postsByNumber.get(n);
                    if (!parent) continue;
                    children += '<div class="inline-expansion forward">';
                    children += buildTreePostHtml(parent, true);
                    children += '</div>';
                }
            }
        }
        return renderPost(postDataFor(p, isEmbedded, /*omitId*/ isEmbedded, children));
    }

    // ---------- top-level renderer (viewMode に基づき分岐) ----------
    /** allPosts を全て破棄→ per-post 経路で再挿入することで、現在の viewMode に整合した DOM を作り直す。
     *  setViewMode (= ユーザのモード切替) と setPreviewPost (= 書き込みプレビューのリセット直後) から呼ばれる。
     *  挿入ロジックは appendPosts の内側ループと同一 (= replayPostIntoDom 経由で共通化)。
     *
     *  dedupTree で「以降新レス」ラベルが立っている場合のみ 2 段構成:
     *    1) section A (= ラベル前) を section A 内に閉じた reverseIndex で per-post 挿入。
     *    2) reverseIndex を全レス基準に戻し、section A 各 primary の「返信 N 件」バッジを書き直す。
     *    3) section B (= 「以降新レス」ラベル + 祖先 chain forest) を末尾に再構築。 */
    function renderCurrentViewMode() {
        const root = document.getElementById('posts');
        if (!root) return;

        root.innerHTML = '';
        currentReverseIndex = new Map();

        const markIdx           = computeMarkIndex();
        const isSplitBySection  = (vm().splitBySectionMark()
                                   && markIdx != null && markIdx < allPosts.length);

        if (isSplitBySection) {
            const sectionA = allPosts.slice(0, markIdx);
            // section A の per-post 挿入。reverseIndex は p ごとに親側に p を加算して成長させる
            // (= streaming 経路と同じ)。section B のレスは加算しないので reverseIndex は section A 内に閉じる。
            for (const p of sectionA) replayPostIntoDom(p);
            // 描画後、reverseIndex を全レス基準に戻して section A 各 primary の「返信 N 件」バッジを正しい件数にする。
            currentReverseIndex = buildReverseIndex();
            for (const p of sectionA) updateReplyCountBadge(p.number);
            rebuildSectionB();
        } else {
            for (const p of allPosts) replayPostIntoDom(p);
        }

        // 描画後の汎用フック群 (= per-post 経路の appendPosts と同じセット)
        document.querySelectorAll('a.anchor').forEach(function (a) {
            const from = parseInt(a.dataset.from, 10);
            if (!postsByNumber.has(from)) a.classList.add('missing');
        });
        observeImageSlots(document);
        tryScrollToTarget();
        updateRichScrollbar();
        updateNewPostsMarkBand();
        updateThreadEndMarkBand();
        updateMarkScrollbarMarker();
        markNewPosts();
        recomputeMetaMaps();
        decorateMeta();
        applyFilterToAllPosts();
    }

    /** appendPosts と renderCurrentViewMode の両方が使う共通の per-post 復元ロジック。
     *  順序: 親側 reverseIndex を p で加算 → DOM 挿入 → 親の返信バッジ更新。
     *  reverseIndex を p の挿入「前」に加算しておくのは embedUnderParentReverse の overflow 計算の母数として参照されるため。 */
    function replayPostIntoDom(p) {
        const seen = new Set();
        for (const r of extractAnchorRefs(p.body || '')) {
            if (r.to - r.from + 1 > INLINE_EXPAND_RANGE_LIMIT) continue;
            for (let n = r.from; n <= r.to; n++) {
                if (n >= p.number) continue;
                if (!postsByNumber.has(n)) continue;
                if (seen.has(n)) continue;
                seen.add(n);
                if (!currentReverseIndex.has(n)) currentReverseIndex.set(n, []);
                currentReverseIndex.get(n).push(p.number);
            }
        }
        insertPostIncremental(p);
        for (const n of seen) updateReplyCountBadge(n);
    }

    /** 「最新リフレッシュ (= 直近の delta batch group) で届いたレス」を is-new クラスでマークする。
     *  post.css 側で .post.is-new .post-no を太字強調。
     *
     *  sessionNewPostNumbers は per-refresh で運用される:
     *    - 新しい refresh の境界 (= mark 値変化) で appendPosts 側が本 Set をリセットしてくれる前提。
     *    - 旧 delta レスは section A に昇格しているので、本 Set には今 refresh のレスだけが残る。
     *    - JS リロード (= 別タブ切替・再オープン) でも空に戻るので、再オープン直後で 0 件取得なら太字レスも 0 件。 */
    function markNewPosts() {
        if (sessionNewPostNumbers.size === 0) {
            debugLog('markNewPosts: sessionNewPostNumbers is empty → no-op');
            return;
        }
        let added = 0;
        let missing = 0;
        for (const num of sessionNewPostNumbers) {
            const el = document.getElementById('r' + num);
            if (el) {
                el.classList.add('is-new');
                added++;
            } else {
                missing++;
            }
        }
        debugLog('markNewPosts: sessionNewPostNumbers.size=' + sessionNewPostNumbers.size
            + ', applied is-new to ' + added + ' element(s), missing=' + missing
            + ' (sample: ' + Array.from(sessionNewPostNumbers).slice(0, 5).join(',') + ')');
    }

    // ---------- ID / ワッチョイの集計と装飾 (Phase 22) ----------

    /** allPosts を走査して currentIdMap / currentWatchoiMap を再構築する。
     *  ID は p.id をそのまま、ワッチョイは p.name から WATCHOI_RE で最初にマッチした 9 文字を採用。 */
    function recomputeMetaMaps() {
        currentIdMap.clear();
        currentWatchoiMap.clear();
        for (const p of allPosts) {
            if (p.id) {
                if (!currentIdMap.has(p.id)) currentIdMap.set(p.id, []);
                currentIdMap.get(p.id).push(p.number);
            }
            const w = extractWatchoi(p.name);
            if (w) {
                if (!currentWatchoiMap.has(w)) currentWatchoiMap.set(w, []);
                currentWatchoiMap.get(w).push(p.number);
            }
        }
    }

    /** name HTML から最初の `xxxx-yyyy` 形式部分を抽出 (タグは事前ストリップ)。無ければ null。 */
    function extractWatchoi(nameHtml) {
        if (!nameHtml) return null;
        const text = String(nameHtml).replace(STRIP_TAGS_RE, '');
        const m = text.match(WATCHOI_RE);
        return m ? m[0] : null;
    }

    /** scopeRoot 内の全 .post[id^="r"] を走査して、複数件ある ID / ワッチョイの該当文字列に
     *  リンク風 span を被せる。装飾済みは `data-meta-decorated` 属性で判定して二重装飾を防ぐ。
     *  scopeRoot 省略時は document.getElementById('posts')。 */
    function decorateMeta(scopeRoot) {
        const root = scopeRoot || document.getElementById('posts');
        if (!root) return;
        const posts = root.querySelectorAll('.post[id^="r"]');
        posts.forEach(function (postEl) {
            if (postEl.dataset.metaDecorated === '1') return;
            const num = parseInt(postEl.id.slice(1), 10);
            const p = postsByNumber.get(num);
            if (!p) return;

            // ---- ワッチョイ装飾 (post-name 内) ----
            const wacchoi = extractWatchoi(p.name);
            if (wacchoi) {
                const list = currentWatchoiMap.get(wacchoi);
                if (list && list.length > 1) {
                    const nameEl = postEl.querySelector(':scope > .post-header > .post-name');
                    if (nameEl) wrapTextMatches(nameEl, WATCHOI_RE, function (matched) {
                        const span = document.createElement('span');
                        span.className = 'watchoi-link';
                        span.dataset.watchoi = matched;
                        span.dataset.watchoiList = list.join(',');
                        span.textContent = matched;
                        return span;
                    }, /*onlyFirst*/ true);
                }
            }

            // ---- ID 装飾 (post-meta 内の "ID:" 直前の "ID") ----
            if (p.id) {
                const list = currentIdMap.get(p.id);
                if (list && list.length > 1) {
                    const metaEl = postEl.querySelector(':scope > .post-header > .post-meta');
                    if (metaEl) {
                        wrapTextMatches(metaEl, /\bID(?=:)/, function (matched) {
                            const span = document.createElement('span');
                            span.className = 'id-link';
                            if (list.length >= ID_HIGHLIGHT_THRESHOLD) span.classList.add('id-many');
                            span.dataset.id = p.id;
                            span.dataset.idList = list.join(',');
                            span.textContent = matched;
                            return span;
                        }, /*onlyFirst*/ true);

                        // [N/M] 表示: このレスが同 ID 中で何番目かと、総数。N は list 内の position+1。
                        // list は recomputeMetaMaps が allPosts 順 (= レス番号昇順) で push しているので、
                        // indexOf(p.number) は「何番目の書き込みか」を返す。
                        const indexInList = list.indexOf(p.number) + 1;
                        if (indexInList > 0) {
                            const counter = document.createElement('span');
                            counter.className = 'id-count';
                            counter.textContent = ' [' + indexInList + '/' + list.length + ']';
                            metaEl.appendChild(counter);
                        }
                    }
                }
            }
            postEl.dataset.metaDecorated = '1';
        });
        // ホバーハンドラを取り付け (level 0 = 最上位ペイン直下のレス)。
        attachMetaHoverHandlers(root, 0);
    }

    /** decorateMeta が付けた `.watchoi-link` / `.id-link` を全て解除して text node に戻す。
     *  併せて post の data-meta-decorated 属性もクリアし、再度 decorateMeta を走らせられる状態にする。
     *  flat appendPosts で件数しきい値の境界を跨いだケース (例: 4 件 → 5 件で赤化) を反映するために使う。 */
    function clearMetaDecorations(scopeRoot) {
        const root = scopeRoot || document.getElementById('posts');
        if (!root) return;
        root.querySelectorAll('.watchoi-link, .id-link').forEach(function (el) {
            el.parentNode.replaceChild(document.createTextNode(el.textContent), el);
        });
        // .id-count は装飾の付帯要素なので、再装飾前にまるごと取り除く。
        root.querySelectorAll('.id-count').forEach(function (el) {
            if (el.parentNode) el.parentNode.removeChild(el);
        });
        root.querySelectorAll('.post[data-meta-decorated="1"]').forEach(function (postEl) {
            delete postEl.dataset.metaDecorated;
        });
    }

    /** TreeWalker で text node を走査し、regex マッチ部分を element で包む。
     *  onlyFirst=true なら最初のマッチを処理した時点で抜ける (各 post-name のワッチョイは 1 つのみ)。 */
    function wrapTextMatches(scope, regex, makeReplacement, onlyFirst) {
        const walker = document.createTreeWalker(scope, NodeFilter.SHOW_TEXT, null);
        const targets = [];
        let n;
        while ((n = walker.nextNode())) {
            if (regex.test(n.nodeValue)) {
                targets.push(n);
                if (onlyFirst) break;
            }
        }
        if (targets.length === 0) return;

        const r = regex.global ? regex : new RegExp(regex.source, regex.flags + 'g');
        for (const node of targets) {
            const text = node.nodeValue;
            r.lastIndex = 0;
            const parent = node.parentNode;
            let last = 0;
            let m;
            const inserts = [];
            while ((m = r.exec(text)) !== null) {
                if (m.index > last) inserts.push(document.createTextNode(text.slice(last, m.index)));
                inserts.push(makeReplacement(m[0], m));
                last = m.index + m[0].length;
                if (onlyFirst) break;
            }
            if (last < text.length) inserts.push(document.createTextNode(text.slice(last)));
            for (const piece of inserts) parent.insertBefore(piece, node);
            parent.removeChild(node);
        }
    }

    /** scopeRoot 内の .watchoi-link / .id-link にホバーハンドラを取付ける。
     *  ポップアップは anchor / 返信件数バッジ と同じ仕組み (.anchor-popup) を使い、入れ子表示も対応。 */
    function attachMetaHoverHandlers(scopeRoot, level) {
        scopeRoot.querySelectorAll('.watchoi-link[data-watchoi-list]').forEach(function (el) {
            el.addEventListener('mouseenter', function () {
                cancelCloseAt(level);
                openMetaListPopup(el, el.dataset.watchoiList, level);
            });
            el.addEventListener('mouseleave', function () { scheduleCloseAt(level); });
        });
        scopeRoot.querySelectorAll('.id-link[data-id-list]').forEach(function (el) {
            el.addEventListener('mouseenter', function () {
                cancelCloseAt(level);
                openMetaListPopup(el, el.dataset.idList, level);
            });
            el.addEventListener('mouseleave', function () { scheduleCloseAt(level); });
        });
    }

    /** ID / ワッチョイホバー用ポップアップ。data-*-list の "3,7,12" を読んでレスを並べるだけ。
     *  既存 buildPopupContentFromList を再利用する (= anchor/返信ホバーと見た目が揃う)。 */
    function openMetaListPopup(target, listStr, level) {
        closeFrom(level);
        const list = (listStr || '').split(',')
            .map(function (s) { return parseInt(s, 10); })
            .filter(function (n) { return !isNaN(n); });
        if (list.length === 0) return;
        const el = document.createElement('div');
        el.className = 'anchor-popup';
        el.appendChild(buildPopupContentFromList(list));
        document.body.appendChild(el);
        positionPopup(el, target);
        el.addEventListener('mouseenter', function () { cancelCloseAtOrBelow(level); });
        el.addEventListener('mouseleave', function () { scheduleCloseAt(level); });
        // 入れ子で popup 内のレスにも meta 装飾とアンカー hover を付ける
        attachAnchorHandlers(el, level + 1);
        attachMetaHoverHandlers(el, level + 1);
        popups.push({ el: el, anchor: target, level: level });
    }


    // ---------- リッチスクロールバー ----------
    /** 本文中に「インライン画像化される URL」(直リンク / 同期展開 / 非同期展開 すべて含む) が 1 つでもあるか。 */
    const BODY_URL_RE = /https?:\/\/[^\s<>"'、。()\[\]{}]+/gi;
    function bodyContainsImage(body) {
        if (!body) return false;
        BODY_URL_RE.lastIndex = 0;
        let m;
        while ((m = BODY_URL_RE.exec(body)) !== null) {
            const u = m[0];
            if (getInlineImageSrc(u)) return true;
            if (isExpandableAsync(u)) return true;
        }
        return false;
    }

    /** 本文中にインライン再生対象の動画 URL (mp4/webm/mov 直リンク or YouTube) があるか。 */
    function bodyContainsVideo(body) {
        if (!body) return false;
        BODY_URL_RE.lastIndex = 0;
        let m;
        while ((m = BODY_URL_RE.exec(body)) !== null) {
            const u = m[0];
            if (isVideoUrl(u)) return true;
            if (extractYouTubeId(u)) return true;
        }
        return false;
    }

    /** 全トラックのマーカーを再計算。setPosts/appendPosts/setViewMode の各 render 後に呼ぶ。 */
    function updateRichScrollbar() {
        const sb = document.getElementById('richScrollbar');
        if (!sb) return;
        const scrollHeight = document.documentElement.scrollHeight;
        if (scrollHeight === 0) return;

        const popularTrack = sb.querySelector('.track-popular');
        const urlTrack     = sb.querySelector('.track-url');
        const imageTrack   = sb.querySelector('.track-image');
        const videoTrack   = sb.querySelector('.track-video');
        if (!popularTrack || !urlTrack || !imageTrack || !videoTrack) return;
        popularTrack.innerHTML = '';
        urlTrack.innerHTML     = '';
        imageTrack.innerHTML   = '';
        videoTrack.innerHTML   = '';

        const reverseIdx = buildReverseIndex();
        for (const post of allPosts) {
            const refs     = reverseIdx.get(post.number);
            const popular  = refs && refs.length >= POPULAR_THRESHOLD;
            const hasUrl   = /https?:\/\/\S/.test(post.body || '');
            const hasImage = bodyContainsImage(post.body);
            const hasVideo = bodyContainsVideo(post.body);
            if (!popular && !hasUrl && !hasImage && !hasVideo) continue;

            const el = document.getElementById('r' + post.number);
            if (!el) continue;
            const topPercent = (el.offsetTop / scrollHeight * 100) + '%';

            if (popular) {
                const m = document.createElement('div');
                m.className = 'marker';
                m.style.top = topPercent;
                popularTrack.appendChild(m);
            }
            if (hasUrl) {
                const m = document.createElement('div');
                m.className = 'marker';
                m.style.top = topPercent;
                urlTrack.appendChild(m);
            }
            if (hasImage) {
                const m = document.createElement('div');
                m.className = 'marker';
                m.style.top = topPercent;
                imageTrack.appendChild(m);
            }
            if (hasVideo) {
                const m = document.createElement('div');
                m.className = 'marker';
                m.style.top = topPercent;
                videoTrack.appendChild(m);
            }
        }
        updateScrollThumb(); // DOM 高さが変わった可能性があるので thumb も再計算
    }

    /** ビューポート thumb の位置/サイズを scrollY と scrollHeight から計算して反映。 */
    function updateScrollThumb() {
        const sb = document.getElementById('richScrollbar');
        const thumb = document.getElementById('scrollThumb');
        if (!sb || !thumb) return;

        const scrollHeight = document.documentElement.scrollHeight;
        const clientHeight = document.documentElement.clientHeight;
        const scrollY      = window.scrollY;

        if (scrollHeight <= clientHeight) {
            // スクロール不要 (本文が viewport 以下) なら thumb を隠す
            thumb.style.display = 'none';
            return;
        }
        thumb.style.display = '';

        const sbHeight    = sb.clientHeight;
        const thumbHeight = Math.max(20, (clientHeight / scrollHeight) * sbHeight);
        const thumbTop    = (scrollY / scrollHeight) * sbHeight;
        thumb.style.top    = thumbTop + 'px';
        thumb.style.height = thumbHeight + 'px';
    }

    /** scrollbar 全体のクリックは「フラクションの位置にジャンプ」。 */
    function setupRichScrollbarClick() {
        const sb = document.getElementById('richScrollbar');
        if (!sb) return;
        sb.addEventListener('click', function (e) {
            const rect     = sb.getBoundingClientRect();
            const fraction = (e.clientY - rect.top) / rect.height;
            const targetY  = Math.max(0, fraction * document.documentElement.scrollHeight);
            window.scrollTo({ top: targetY, behavior: 'auto' });
        });
    }
    setupRichScrollbarClick();

    /** thumb のドラッグでスクロールを動かす。 */
    function setupScrollThumbDrag() {
        const sb = document.getElementById('richScrollbar');
        const thumb = document.getElementById('scrollThumb');
        if (!sb || !thumb) return;

        // thumb 上のクリックは scrollbar へ伝播しない (jump-to-fraction を抑止)
        thumb.addEventListener('click', function (e) { e.stopPropagation(); });

        let drag = null;
        thumb.addEventListener('mousedown', function (e) {
            e.preventDefault();
            e.stopPropagation();
            drag = {
                startClientY: e.clientY,
                startScrollY: window.scrollY,
                sbHeight:     sb.clientHeight,
                scrollHeight: document.documentElement.scrollHeight,
            };
            thumb.classList.add('dragging');
            document.body.style.userSelect = 'none';
        });
        window.addEventListener('mousemove', function (e) {
            if (!drag) return;
            const dy        = e.clientY - drag.startClientY;
            const newScroll = drag.startScrollY + (dy * drag.scrollHeight / drag.sbHeight);
            window.scrollTo({ top: Math.max(0, newScroll), behavior: 'auto' });
        });
        window.addEventListener('mouseup', function () {
            if (!drag) return;
            drag = null;
            thumb.classList.remove('dragging');
            document.body.style.userSelect = '';
        });
    }
    setupScrollThumbDrag();

    // scroll / resize の度に thumb 位置を更新
    window.addEventListener('scroll', updateScrollThumb, { passive: true });
    window.addEventListener('resize', updateScrollThumb);

    // インライン画像の遅延読み込み完了で後続レスの offsetTop が変化するため、
    // marker と thumb を再計算する。capture phase で load イベントを掴む (img の load は bubble しない)。
    let scrollbarUpdateTimer = null;
    function scheduleScrollbarUpdate() {
        if (scrollbarUpdateTimer) clearTimeout(scrollbarUpdateTimer);
        scrollbarUpdateTimer = setTimeout(function () {
            scrollbarUpdateTimer = null;
            updateRichScrollbar();
            // mark トラック (= 「以降新レス」/ 「自分の書き込み」) の位置も
            // scrollHeight 変化に追従させる。画像 lazy load で本文高が伸びると
            // 比率位置が古いままだとマーカーがズレるため。
            updateMarkScrollbarMarker();
        }, 200);
    }
    document.addEventListener('load', function (e) {
        if (e.target && e.target.tagName === 'IMG') scheduleScrollbarUpdate();
    }, true);

    // ---------- 画像サイズしきい値 (HEAD で size 確認 → 自動ロード or プレースホルダ) ----------
    // 5MB 超のインライン画像はユーザークリックまで取得しない。
    // (Phase 11 で設定値化予定。それまでは固定値)。
    // 設定 (Phase 11) で動的変更可能。setConfig メッセージで上書きされる。
    let IMAGE_SIZE_THRESHOLD = 5 * 1024 * 1024;

    // url → { ok, size } の HEAD 結果キャッシュ (セッション内)。
    const imageMetaCache = new Map();

    function postImageMetaRequest(url) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'imageMetaRequest', url: url });
        }
    }

    /** 全 .image-slot.deferred を IntersectionObserver で監視し、近接時に HEAD 要求を送る。
     *  rootMargin はビューポート前後に余白を持たせて「viewport 外でも前後数枚分はあらかじめ
     *  HEAD → loadSlotImage が走り、ブラウザがデコード済みになった状態で viewport に入るようにする」設計。
     *  値 1500px は概ねサムネイル 8–10 枚分。スクロールがだいぶ速くてもサムネ描画が追いつくよう、
     *  HEAD のみ (= 帯域は数 KB / 枚) を先んじて打つ。実画像の取得帯域はキャッシュやしきい値で抑制。 */
    const imageSlotObserver = new IntersectionObserver(function (entries) {
        for (const ent of entries) {
            if (!ent.isIntersecting) continue;
            const slot = ent.target;
            imageSlotObserver.unobserve(slot);

            // 直リンク動画スロット: HEAD ではなく <video preload=metadata> を materialize して
            // first frame をサムネ代わりに表示する。クリックで実際に再生開始。
            if (slot.dataset.mediaType === 'video') {
                materializeVideoSlot(slot);
                continue;
            }

            if (!slot.classList.contains('deferred')) continue;
            if (slot.dataset.metaState === 'requested' || slot.dataset.metaState === 'resolved') continue;
            const url = slot.dataset.src;
            if (!url) continue;

            // 既に HEAD 済みならその場で適用 (再描画後の再観測など)
            const cached = imageMetaCache.get(url);
            if (cached) { applyMetaToSlot(slot, cached); continue; }

            slot.dataset.metaState = 'requested';
            postImageMetaRequest(url);
        }
    }, { rootMargin: '1500px 0px' });

    function observeImageSlots(root) {
        const scope = root || document;
        // image-slot.deferred (画像 HEAD) と image-slot.video (動画 materialize) の両方を監視する。
        // video スロットは初期 class が 'image-slot video' (deferred なし) で出力されるので、
        // セレクタは「deferred クラス OR video クラス」でカバーする。
        scope.querySelectorAll('.image-slot.deferred, .image-slot.video').forEach(function (s) {
            if (s.dataset.observed === '1') return;
            s.dataset.observed = '1';
            imageSlotObserver.observe(s);
        });
    }

    /** HEAD / 非同期展開の結果を 1 枚のスロットに適用。 */
    function applyMetaToSlot(slot, meta) {
        if (!slot.classList.contains('deferred')) return;
        slot.dataset.metaState = 'resolved';

        // 非同期展開対象の slot で resolvedUrl が無い → 展開失敗 (媒体無し / login 要 / API ダウン)。
        // 「クリックで再試行」プレースホルダにする (= 自動再試行はしない、ユーザクリック時のみ再要求)。
        const isAsyncSlot = slot.classList.contains('async');
        if (isAsyncSlot && !meta.resolvedUrl) {
            slot.classList.remove('deferred');
            slot.classList.add('expand-failed');
            const text = document.createElement('span');
            text.className = 'image-placeholder-text';
            text.textContent = '画像取得失敗 — クリックで再試行';
            slot.appendChild(text);
            return;
        }

        // resolvedUrl が来ていれば実際のロード対象を更新。
        if (meta.resolvedUrl) slot.dataset.src = meta.resolvedUrl;

        // ローカルキャッシュ済みの URL は帯域消費がないので、しきい値を無視して即ロード。
        if (meta.cached) { loadSlotImage(slot); return; }

        if (meta.ok && typeof meta.size === 'number' && meta.size > IMAGE_SIZE_THRESHOLD) {
            slot.classList.remove('deferred');
            slot.classList.add('over-threshold');
            const text = document.createElement('span');
            text.className = 'image-placeholder-text';
            const mb = (meta.size / 1024 / 1024).toFixed(1);
            text.textContent = mb + ' MB - クリックで読み込む';
            slot.appendChild(text);
        } else {
            // size <= しきい値 / HEAD 失敗 (size 不明) どちらの場合もそのまま読み込む。
            // HEAD 失敗時に「念のため止める」を選ぶと、HEAD 非対応サーバーで全画像が出なくなって
            // ユーザビリティが大きく落ちるため、「不明 → 読み込む」を採用。
            loadSlotImage(slot);
        }
    }

    /** ユーザクリックで失敗スロットを再要求する。expand-failed (= 非同期 URL の展開失敗) と
     *  load-failed (= 画像本体の取得失敗) の両方に対応。
     *
     *  C# 側 (UrlExpander / ImageMetaService) は失敗結果をキャッシュから外す設計になっているため、
     *  この再要求は前回失敗時とは独立に新しい HTTP 呼び出しが走る。JS 側の imageMetaCache も
     *  この URL に関しては破棄する (= でないと imageMetaRequest で C# まで届かない)。 */
    function retrySlot(slot) {
        // 非同期展開スロットでは applyMetaToSlot が data-src を resolvedUrl に書き換えていることが
        // ある。再展開させたいので元 URL に戻す (= data-url にオリジナルが保持されている)。
        if (slot.classList.contains('async') && slot.dataset.url) {
            slot.dataset.src = slot.dataset.url;
        }
        const url = slot.dataset.src;
        if (!url) return;

        // JS 側のメタキャッシュをクリア (= でないと applyImageMeta 経由で同じ失敗結果が返ってきて
        // C# まで届かない)。
        imageMetaCache.delete(url);

        // 状態を初期 deferred に戻し、プレースホルダを除去。
        slot.classList.remove('expand-failed', 'load-failed', 'loaded', 'over-threshold');
        slot.classList.add('deferred');
        slot.dataset.metaState = '';
        slot.innerHTML = '';

        // 再リクエスト (C# 側 UrlExpander は失敗 entry を evict 済みなので新規 API を叩く)。
        slot.dataset.metaState = 'requested';
        postImageMetaRequest(url);
    }

    /** スロット内に <img> を生成して実際に画像を読み込む。
     *  画像のロードに失敗した (= 404 / network error / DNS 失敗 等) 場合は load-failed クラスを付け、
     *  「クリックで再試行」プレースホルダを表示する (= 自動リトライはしない、ユーザがクリックで明示再要求)。 */
    function loadSlotImage(slot) {
        const url = slot.dataset.src;
        if (!url) return;
        slot.classList.remove('deferred', 'over-threshold', 'load-failed');
        slot.classList.add('loaded');
        slot.innerHTML = '';
        const img = document.createElement('img');
        img.className = 'inline-image';
        // loading="lazy" は付けない (= ブラウザが二重にビューポート判定して fetch/decode を
        // 遅らせるのを避ける)。本コードではゲートは IntersectionObserver (rootMargin 1500px)
        // の側で既に実施しており、loadSlotImage が呼ばれた時点で「描画して欲しい近接スロット」と
        // 確定しているため、ここでは即時に fetch + decode を開始させる方が高速 (= スクロール
        // 速度に対する追従性が上がる)。
        img.alt       = '';
        img.src       = url;
        img.addEventListener('error', function () {
            slot.classList.remove('loaded');
            slot.classList.add('load-failed');
            slot.innerHTML = '';
            const text = document.createElement('span');
            text.className = 'image-placeholder-text';
            text.textContent = '画像読み込み失敗 — クリックで再試行';
            slot.appendChild(text);
        }, { once: true });
        slot.appendChild(img);

        // YouTube サムネイルには再生アイコンを重ねる (クリックで iframe に置換)
        if (slot.classList.contains('youtube')) {
            const icon = document.createElement('span');
            icon.className = 'media-play-icon';
            slot.appendChild(icon);
        }

        // 画像ロード完了後、AI 生成メタを取りにいって generator バッジを overlay する。
        // load イベントのタイミングではキャッシュ書き込み (WebResourceResponseReceived → SaveAsync)
        // がまだ間に合わないことがあるので 500ms 遅延でリクエストする。
        // 既にメタが in-memory cache にあれば即座にバッジを当てる (= 再ロード時の高速パス)。
        const cached = aiMetaCache.get(url);
        if (cached && cached !== 'pending' && cached !== 'no-data') {
            applyGeneratorBadge(slot, cached);
        } else if (cached !== 'no-data' && cached !== 'pending') {
            img.addEventListener('load', function () {
                setTimeout(function () { requestAiMetaForBadge(url); }, 500);
            }, { once: true });
        }
    }

    /** generator バッジを slot 左上に overlay。既に貼ってあるなら何もしない (idempotent)。 */
    function applyGeneratorBadge(slot, meta) {
        if (!meta || !meta.generator) return;
        if (slot.querySelector(':scope > .image-slot-generator-badge')) return;
        const badge = document.createElement('span');
        badge.className   = 'image-slot-generator-badge';
        badge.textContent = meta.generator;
        slot.appendChild(badge);
    }

    /** バッジ用に AI メタデータを要求 (まだ pending/取得済みでないなら)。
     *  通常の hover popup と同じ aiMetadataRequest を投げ、レスポンスは onAiMetadataResponse 内で
     *  該当 slot を全 querySelectorAll で見つけてバッジを当てる。 */
    function requestAiMetaForBadge(url) {
        if (!url) return;
        const cached = aiMetaCache.get(url);
        if (cached === 'pending' || cached === 'no-data') return;
        if (cached) return; // 既にデータあり → 既に applyGeneratorBadge 済 (or 該当 slot 描画時に当てる)
        aiMetaCache.set(url, 'pending');
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'aiMetadataRequest', url: url });
        }
    }

    /**
     * 直リンク動画スロットに <video preload=metadata muted> を埋め込み、first frame を
     * サムネ代わりに表示する。クリック前なので controls は付けず、再生アイコン (▶) を上に重ねる。
     * IntersectionObserver の viewport 近接時に呼ぶ。playMedia からも (クリック先行ケースで) 呼ばれる。
     * 何度呼んでも安全 (idempotent)。
     */
    function materializeVideoSlot(slot) {
        if (slot.classList.contains('materialized')) return;
        const url = slot.dataset.src;
        if (!url) return;
        slot.classList.add('materialized');
        slot.innerHTML = '';

        const video = document.createElement('video');
        video.src     = url;
        video.preload = 'metadata';
        video.muted   = true;       // クリック前は無音 (auto play policy 対策、ユーザーは見るだけ)
        video.playsInline = true;
        // preload=metadata だけでは first frame が出ない実装が多いので、メタデータが
        // 揃ったら 0.1s seek して 1 フレーム強制描画させる (key frame まで取りに行く)。
        video.addEventListener('loadedmetadata', function () {
            try {
                const dur = video.duration || 0;
                video.currentTime = (dur > 0 && dur < 0.2) ? dur / 2 : 0.1;
            } catch (e) { /* seek 失敗は黒コマで fallback */ }
        }, { once: true });
        slot.appendChild(video);

        // ▶ オーバーレイを再追加 (renderExternalLink で出していたものは innerHTML='' で消えた)
        const icon = document.createElement('span');
        icon.className = 'media-play-icon';
        slot.appendChild(icon);
    }

    /** 直リンク動画スロットを実際の <video> 再生要素に置換する。
     *  YouTube はインライン埋め込みすると Error 153 が出るためここでは扱わず、
     *  クリックハンドラ側でデフォルトブラウザに飛ばす。 */
    function playMedia(slot) {
        if (slot.classList.contains('playing')) return;
        const mediaType = slot.dataset.mediaType;

        if (mediaType === 'video') {
            // viewport 外でクリックされた場合に備えて idempotent materialize
            if (!slot.classList.contains('materialized')) materializeVideoSlot(slot);
            const video = slot.querySelector('video');
            if (!video) return;

            // ▶ オーバーレイを除去、controls を付けて unmute、再生開始
            const icon = slot.querySelector('.media-play-icon');
            if (icon) icon.remove();
            video.controls = true;
            video.muted    = false;
            slot.classList.add('playing');

            // play() は metadata 未ロード時に reject されることがある。
            // その場合は canplay を待ってリトライ。
            video.play().catch(function () {
                video.addEventListener('canplay', function () {
                    video.play().catch(function () { /* それでもダメなら諦める */ });
                }, { once: true });
            });
        }
    }

    /** 同じ URL を持つ全 deferred スロットに HEAD 結果を一括適用。 */
    function applyImageMeta(url, meta) {
        imageMetaCache.set(url, meta);
        document.querySelectorAll('.image-slot.deferred').forEach(function (slot) {
            if (slot.dataset.src === url) applyMetaToSlot(slot, meta);
        });
    }

    // ============================================================
    // AI 生成画像メタ (Stable Diffusion WebUI infotext) のホバーポップアップ
    //
    // 動作概要:
    //   - インライン画像 (.image-slot.loaded > img.inline-image) にマウスが乗る
    //   - JS が { type:'aiMetadataRequest', url } を C# に送信
    //   - C# がキャッシュ済み画像から PNG/JPEG/WebP のメタを抽出し
    //     { type:'aiMetadata', url, hasData, model, positive, negative } で返す
    //   - hasData=true なら画像の近くにポップアップを表示。データ無しなら何もしない。
    //
    // 結果は url -> ('pending'|'no-data'|{model,positive,negative}) で in-memory キャッシュ。
    // 同じ画像に何度ホバーしても要求は 1 回。
    // ============================================================
    const aiMetaCache  = new Map();      // url → 'pending' | 'no-data' | {model, positive, negative}
    let   aiPopupEl    = null;
    let   aiHoverUrl   = null;           // 現在ホバー中の画像 URL (= レスポンス到着時に当該URLなら表示)
    let   aiHoverImg   = null;           // 現在ホバー中の <img> 要素 (= ポップアップ位置決め用)
    let   aiPopupHideTimer = null;       // 「画像離脱 → ポップアップ離脱」を許容するための遅延 hide タイマー

    /** N ミリ秒後に hide を予約 (= 画像から離れた瞬間に hide すると、ポップアップに乗り移る前に消えてしまう)。
     *  既にタイマーが走っていれば張り直す。 */
    function scheduleHideAiPopup(delayMs) {
        if (aiPopupHideTimer) clearTimeout(aiPopupHideTimer);
        aiPopupHideTimer = setTimeout(function () {
            aiPopupHideTimer = null;
            hideAiPopup();
        }, delayMs);
    }

    /** 予約済の hide をキャンセル (= ポップアップに乗り移った / 別画像にホバーし直した)。 */
    function cancelHideAiPopup() {
        if (aiPopupHideTimer) {
            clearTimeout(aiPopupHideTimer);
            aiPopupHideTimer = null;
        }
    }

    function ensureAiPopup() {
        if (aiPopupEl) return aiPopupEl;
        const el = document.createElement('div');
        el.className = 'ai-meta-popup';
        el.style.display = 'none';
        // ポップアップ自身に乗り移ったら hide をキャンセル → 内部テキストを選択してコピペできる。
        // ポップアップから離れたら 150ms 遅延で hide (画像へ戻る経路に対するヒステリシス)。
        el.addEventListener('mouseenter', cancelHideAiPopup);
        el.addEventListener('mouseleave', function () { scheduleHideAiPopup(150); });
        document.body.appendChild(el);
        aiPopupEl = el;
        return el;
    }

    function showAiPopup(targetImg, meta) {
        if (!meta || (!meta.generator && !meta.model && !meta.positive && !meta.negative)) return;
        const popup = ensureAiPopup();

        let html = '';
        // 生成元 (ComfyUI / SD WebUI Forge 等) を最上段に小さくバッジ風で表示。
        if (meta.generator) {
            html += '<div class="ai-meta-popup-generator">' + escapeHtml(meta.generator) + '</div>';
        }
        if (meta.model) {
            html += '<div class="ai-meta-popup-section">'
                  + '<div class="ai-meta-popup-label">モデル</div>'
                  + '<div class="ai-meta-popup-value">' + escapeHtml(meta.model) + '</div></div>';
        }
        if (meta.positive) {
            html += '<div class="ai-meta-popup-section">'
                  + '<div class="ai-meta-popup-label">プロンプト</div>'
                  + '<div class="ai-meta-popup-value ai-meta-popup-prompt">' + escapeHtml(meta.positive) + '</div></div>';
        }
        if (meta.negative) {
            html += '<div class="ai-meta-popup-section">'
                  + '<div class="ai-meta-popup-label">ネガティブプロンプト</div>'
                  + '<div class="ai-meta-popup-value ai-meta-popup-negative">' + escapeHtml(meta.negative) + '</div></div>';
        }
        if (!html) return;

        popup.innerHTML = html;
        popup.style.display = 'block';

        // 画像の真下に出すのが基本。下に出るとビューポート下端を超える場合は上に。
        // 左右ははみ出さないように 8px マージンでクランプ。
        const r  = targetImg.getBoundingClientRect();
        const pr = popup.getBoundingClientRect();
        const vw = window.innerWidth;
        const vh = window.innerHeight;
        const margin = 8;

        let left = r.left;
        if (left + pr.width > vw - margin) left = vw - pr.width - margin;
        if (left < margin) left = margin;

        let top;
        if (r.bottom + pr.height + 4 < vh - margin)         top = r.bottom + 4;
        else if (r.top - pr.height - 4 > margin)            top = r.top - pr.height - 4;
        else                                                top = Math.max(margin, vh - pr.height - margin);

        popup.style.left = Math.round(left) + 'px';
        popup.style.top  = Math.round(top)  + 'px';
    }

    function hideAiPopup() {
        if (aiPopupEl) aiPopupEl.style.display = 'none';
        aiHoverUrl = null;
        aiHoverImg = null;
    }

    /** mouseover (delegation) で .inline-image に到達したら呼ばれる。
     *  キャッシュ命中なら即座にポップアップ表示、未取得なら C# に問い合わせる。 */
    function onImageHoverEnter(img) {
        // ポップアップから画像に戻ってきた経路で残存 hide タイマーを必ず止める。
        // 初回ホバーや別画像へ移った場合でも cancel するだけなら無害なので無条件で呼ぶ。
        cancelHideAiPopup();

        const slot = img.closest && img.closest('.image-slot.loaded');
        if (!slot) return;
        // dataset.src は (resolvedUrl で上書き済の) 実体画像 URL。これがローカルキャッシュのキー。
        const url = slot.dataset.src;
        if (!url) return;

        aiHoverUrl = url;
        aiHoverImg = img;

        const cached = aiMetaCache.get(url);
        if (cached === 'no-data' || cached === 'pending') return;
        if (cached) { showAiPopup(img, cached); return; }

        aiMetaCache.set(url, 'pending');
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'aiMetadataRequest', url: url });
        }
    }

    /** mouseout (delegation) で .inline-image を離れたら 200ms 後に hide を予約。
     *  ポップアップに乗り移ればその間に cancel される (= 表示継続)。 */
    function onImageHoverLeave(img) {
        if (aiHoverImg === img) scheduleHideAiPopup(200);
    }

    /** C# からの aiMetadata レスポンスを処理。 */
    function onAiMetadataResponse(msg) {
        if (typeof msg.url !== 'string') return;
        if (!msg.hasData) {
            // hasData=false かつ cached=true のみ「この画像は AI 情報なし」を確定キャッシュ。
            // cached=false (= 画像保存とリクエストが競合してキャッシュ未到着) は再試行を許す。
            if (msg.cached) {
                aiMetaCache.set(msg.url, 'no-data');
            } else {
                aiMetaCache.delete(msg.url);
                // 表示中のサムネがある間は 1.5s 後に 1 度だけ自動リトライ (= バッジを諦めず取りに行く)。
                // hover popup は元から「次のホバーで再試行」設計なので、ここではバッジ専用の retry を入れる。
                setTimeout(function () { requestAiMetaForBadge(msg.url); }, 1500);
            }
            return;
        }
        const meta = {
            generator: msg.generator || '',
            model:     msg.model     || '',
            positive:  msg.positive  || '',
            negative:  msg.negative  || '',
        };
        aiMetaCache.set(msg.url, meta);

        // まだ同じ画像にホバー中なら即時表示 (= 既に別の画像へ移っていれば何もしない、
        // そちらは別途自分のリクエストを発行している)。
        if (aiHoverUrl === msg.url && aiHoverImg) showAiPopup(aiHoverImg, meta);

        // 該当 URL の slot 全件に generator バッジを overlay (同 URL が複数貼られる流用に対応)。
        if (meta.generator) {
            document.querySelectorAll('.image-slot.loaded').forEach(function (slot) {
                if (slot.dataset.src === msg.url) applyGeneratorBadge(slot, meta);
            });
        }
    }

    document.addEventListener('mouseover', function (e) {
        const t = e.target;
        if (!t || t.tagName !== 'IMG' || !t.classList.contains('inline-image')) return;
        onImageHoverEnter(t);
    });
    document.addEventListener('mouseout', function (e) {
        const t = e.target;
        if (!t || t.tagName !== 'IMG' || !t.classList.contains('inline-image')) return;
        onImageHoverLeave(t);
    });
    // スクロール / ウィンドウサイズ変更でもポップアップは消す (位置がずれるため)。
    window.addEventListener('scroll', hideAiPopup, { passive: true });
    window.addEventListener('resize', hideAiPopup);

    // ---------- scroll target (initial restore + tab-switch restore) ----------
    // pendingScrollTarget は setPosts/appendPosts のメッセージから受け取って保持。
    // tryScrollToTarget は対象レスが DOM に出現したら scrollIntoView する。
    //
    // 設計メモ:
    //   - ユーザーが手動でスクロール (userHasScrolled) する前は、batch 到着のたびに繰り返し追従する。
    //     ツリーモードの appendPosts は innerHTML 全置換で scrollY=0 にリセットされるため、
    //     都度再 anchor しないと target から離れたまま固まる。
    //   - 一度 userHasScrolled が立ったら追従終了 (= ユーザの位置を尊重)。
    //   - pendingScrollTarget はクリアしない (= 後続の batch でも再追従するため)。
    //     visible 判定により、target が既に viewport 内に居れば scroll は発火しないので jolt は出ない。
    /** {r1..rN} の primary instance (id="rN") のうち、DOM 上で最も下にある要素を返す。
     *  途中の番号が DOM に存在しない (= 通常はないが防御的) ものはスキップ。
     *  すべて存在しない場合は null。 */
    function findBottommostPrimaryInRange(N) {
        let best = null;
        let bestY = -Infinity;
        const sy = window.scrollY || window.pageYOffset || 0;
        for (let n = 1; n <= N; n++) {
            const el = document.getElementById('r' + n);
            if (!el) continue;
            const absY = el.getBoundingClientRect().top + sy;
            if (absY > bestY) {
                bestY = absY;
                best = el;
            }
        }
        return best;
    }

    /** スレ開いた直後に保存値 N にあわせて復元スクロールを行う。
     *
     *  方針 (= ユーザ仕様):
     *    1..N のレス番号それぞれの primary instance (id="rN") のうち、DOM 上で最も下に位置するものを選び、
     *    その要素の下端を viewport 下端に揃える (= scrollIntoView({block:'end'}))。
     *
     *  なぜ rN 自体ではないのか:
     *    tree モードで N が他レスへの返信として nest されている場合、N の primary instance は DOM の
     *    途中に置かれることがあり、そこに揃えると「読了した位置より大きく戻った」場所に着地する。
     *    {1..N} の中で DOM 上の最後尾 (= 「読了済集合の DOM 上の境界」) に揃えれば visually 連続感が出る。
     *    flat モードでは番号順 = DOM 順なので bottommost = rN となり、結果は従来同等。
     *
     *  streaming 中: allPosts.length < N の段階では繰り延べ (= 続く batch 到着で再度呼ばれる)。 */
    function tryScrollToTarget() {
        if (userHasScrolled) return;
        if (pendingScrollTarget == null) return;
        const N = pendingScrollTarget;
        // 1..N が全部届くまでは繰り延べ。途中で部分的にスクロールすると、後続 batch で再度動いて jitter になる。
        if (allPosts.length < N) return;

        const bottomMost = findBottommostPrimaryInRange(N);
        if (!bottomMost) return;

        // jolt 防止: 既に bottommost の下端が viewport 下端 ±4px AND 上端も viewport 内なら no-op
        const rect = bottomMost.getBoundingClientRect();
        const vh = document.documentElement.clientHeight;
        if (Math.abs(rect.bottom - vh) <= 4 && rect.top >= 0 && rect.top < vh) return;
        bottomMost.scrollIntoView({ block: 'end' });
    }

    // ---------- scroll position save (debounced) ----------
    // 各タブが専属 WebView2 なので、ユーザーがスクロールした時にだけイベントが発火する。
    // 過渡的な scrollY 値の汚染対策 (suppress) は不要。シンプルな debounce のみ。
    let scrollSaveTimer = null;
    let scrollSaveLastSent = null;       // 直近送信した「読了 prefix」の最大番号
    const SCROLL_SAVE_DEBOUNCE_MS = 200;

    /** 「読了 prefix」の最大レス番号を算出する (= 次回オープン時の scroll restore target)。
     *
     *  方針:
     *    1. 各レス番号 N (= 1, 2, 3, ...) の primary instance (id="rN") の下端が viewport 下端以下にあるか調べる。
     *       条件 (rect.bottom <= clientHeight) を満たすレスは「下端まで見終えた」と扱う。
     *       これは「画面内で全部見えている」と「上にスクロールして去った」の両方をカバーする。
     *    2. 1, 2, 3, ... と連番でこの条件を満たし続ける限り進み、初めて満たさなくなった瞬間で停止して、
     *       直前の番号 N を返す。
     *
     *  ツリー表示でレスが番号順に並んでいない場合でも、「先頭から連番が途切れず読了した最大番号」が安定して取れる。
     *  例: ツリーモードで DOM 順 1,2,3,10,12,4,5,13,6,7,8,9 のスレで「13 まで見えて 6 が下端切れ」だと、
     *  rendered = {1,2,3,10,12,4,5,13} (6 は下端切れで除外)、1 から連番は {1,2,3,4,5} → N=5。 */
    function findReadProgressMaxNumber() {
        if (!allPosts || allPosts.length === 0) return null;
        const vh   = document.documentElement.clientHeight;
        const maxN = allPosts[allPosts.length - 1].number;
        let lastValid = 0;
        for (let n = 1; n <= maxN; n++) {
            const el = document.getElementById('r' + n);
            // DOM に居ない番号は防御的に飛ばす (= 何らかの理由で primary id が無いケースの保険)。
            if (!el) continue;
            if (el.getBoundingClientRect().bottom > vh) break; // 連番が途切れた → 確定
            lastValid = n;
        }
        return lastValid > 0 ? lastValid : null;
    }

    function scheduleSendScrollPosition() {
        if (scrollSaveTimer != null) return; // 既に飛んでいる timer に統合
        scrollSaveTimer = setTimeout(function () {
            scrollSaveTimer = null;
            // user input 前 (= scrollIntoView の auto-fire 経路) は送信抑止。
            // ここを抑えないと: 自動スクロール → scroll event → scrollPosition 送信 →
            // C# が ScrollTargetPostNumber を上書き → 次 batch で別の値が pending に入る、
            // という feedback loop で target がドリフトする。
            if (!userHasScrolled) return;
            const num = findReadProgressMaxNumber();
            if (num == null) return;
            if (num === scrollSaveLastSent) return; // 変化なしは送らない
            scrollSaveLastSent = num;
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({ type: 'scrollPosition', postNumber: num });
            }
        }, SCROLL_SAVE_DEBOUNCE_MS);
    }

    // ---------- 「以降新レス」ラベル ----------
    // markPostNumber は appendPosts のペイロードから受信して保持。
    // ラベル位置はリフレッシュで新着が来たときだけ動く (= 旧 readMark のように scroll で動かない)。

    /** 「以降新レス」ラベル DOM を再配置する。
     *
     *  配置ロジック (優先度順):
     *    1. r{markPostNumber} が #posts の直接の子なら、その前に挿入。
     *    2. r{markPostNumber} が nested (= dedupTree section B のように forest 内に居る) なら、
     *       そのレスを含む top-level 子要素の前に挿入する。
     *       (※ section B の forest root は ancestor (= mark 未満の親) のことがあり id 無し。
     *           したがって「root.children を id で検索」では届かないので必ず target から walk up する。)
     *    3. 上記いずれも見つからなければ、root.children を頭から走査して
     *       「番号 >= mark の最初の primary レス」または「番号 >= mark のレスを内包する top-level」を探す。 */
    function updateNewPostsMarkBand() {
        // 既存ラベルを一旦取り除く (= 配置場所の更新も再挿入で行う)
        const existing = document.getElementById('new-posts-mark-band');
        if (existing && existing.parentNode) existing.parentNode.removeChild(existing);

        if (markPostNumber == null) {
            debugLog('updateNewPostsMarkBand: markPostNumber=null → no band');
            return;
        }

        const root = document.getElementById('posts');
        if (!root) return;

        let target = null;
        let route  = '';

        // ① / ② r{markPostNumber} が DOM 上にあれば、それを内包する top-level 要素を target に
        const direct = document.getElementById('r' + markPostNumber);
        if (direct) {
            let topLevel = direct;
            while (topLevel && topLevel.parentNode !== root) topLevel = topLevel.parentNode;
            if (topLevel && topLevel.parentNode === root) { target = topLevel; route = 'direct'; }
        }

        // ③ それでも見つからない (= 該当レスが DOM に存在しない) 場合は番号 >= mark を探す
        if (!target) {
            for (const post of root.children) {
                const id = post.id;
                // id 付きの primary レスならその番号で判定
                if (id && id.startsWith('r')) {
                    const n = parseInt(id.slice(1), 10);
                    if (n >= markPostNumber) { target = post; route = 'fallback-id'; break; }
                    continue;
                }
                // id 無しの top-level (= dedupTree section B の forest root が ancestor のとき) は
                // 内部に r{>=mark} が居るかを後方互換的にチェックする
                const inner = post.querySelector('[id^="r"]');
                if (inner && inner.id) {
                    const n = parseInt(inner.id.slice(1), 10);
                    if (!isNaN(n) && n >= markPostNumber) { target = post; route = 'fallback-inner'; break; }
                }
            }
        }
        if (!target) {
            debugLog('updateNewPostsMarkBand: target NOT found for mark=' + markPostNumber
                + ' (allPosts=' + allPosts.length + ', root.children=' + root.children.length
                + ', getElementById(r' + markPostNumber + ')=' + (direct ? 'exists-but-orphan' : 'null') + ')');
            return; // ラベルを出す場所がない
        }

        const band = document.createElement('div');
        band.id        = 'new-posts-mark-band';
        band.className = 'new-posts-mark-band';
        target.parentNode.insertBefore(band, target);
        debugLog('updateNewPostsMarkBand: placed for mark=' + markPostNumber + ' via ' + route + ' (target.id=' + (target.id || '<none>') + ')');
    }

    /** スレッド末尾に「最後尾」ラベル (= 最終レスの直後) を 1 つだけ常時配置する。
     *  デザインは new-posts-mark-band と同形 (細い横帯)、色だけ暗いスレート系で別 CSS クラスを使う。
     *  flat / tree / dedupTree いずれの表示モードでも `#posts` の最後の子として末尾に出る。
     *  毎回 remove → append し直すので、appendPosts や setViewMode の後に呼べば追従する。
     *  posts が 0 件のときは出さない。 */
    function updateThreadEndMarkBand() {
        const root = document.getElementById('posts');
        if (!root) return;
        const existing = document.getElementById('thread-end-mark-band');
        if (existing && existing.parentNode) existing.parentNode.removeChild(existing);
        if (allPosts.length === 0) return;

        const band = document.createElement('div');
        band.id        = 'thread-end-mark-band';
        band.className = 'thread-end-mark-band';
        root.appendChild(band);
    }

    /** リッチスクロールバーの mark トラック (= 一番右のトラック) に位置マーカーを描画。
     *  以下の 2 種類を同じトラックに「色違い」で並べる:
     *    1) 「以降新レス」境界の青ライン (= markPostNumber 位置、最大 1 本)
     *    2) 「自分の書き込み」レスの位置マーカー (= ownPostNumbers の各位置、色は CSS の .marker-own で別)
     *  対象レスの DOM 上の縦位置 (scrollHeight に対する比率) に細い横線を出すだけ。 */
    function updateMarkScrollbarMarker() {
        const sb = document.getElementById('richScrollbar');
        if (!sb) return;
        const track = sb.querySelector('.track-mark');
        if (!track) return;
        track.innerHTML = '';
        const scrollHeight = document.documentElement.scrollHeight;
        if (!scrollHeight) return;

        // (1) 「以降新レス」境界 (1 本だけ)
        if (markPostNumber != null) {
            const band = document.getElementById('new-posts-mark-band');
            const ref  = band || document.getElementById('r' + markPostNumber);
            if (ref) {
                const m = document.createElement('div');
                m.className = 'marker';
                m.style.top = (ref.offsetTop / scrollHeight * 100) + '%';
                track.appendChild(m);
            }
        }

        // (2) 「自分の書き込み」マーカー (= 各レス位置、色違い)
        if (ownPostNumbers && ownPostNumbers.size > 0) {
            for (const num of ownPostNumbers) {
                const el = document.getElementById('r' + num);
                if (!el) continue;
                const m = document.createElement('div');
                m.className = 'marker marker-own';
                m.style.top = (el.offsetTop / scrollHeight * 100) + '%';
                track.appendChild(m);
            }
        }
    }

    window.addEventListener('scroll', scheduleSendScrollPosition, { passive: true });

    // ---------- click handling (anchor scroll / external URL / inline image) ----------
    // 注意: スレ表示内の <a> は href 属性を持たず非 focusable にしている (Chromium が
    // 「先頭の focusable」へ auto scroll する経路を断つため)。よってクリック判定は
    // dataset (data-url, data-from) ベース。
    function postOpenUrl(url) {
        if (!url) return;
        if (url.indexOf('http://') !== 0 && url.indexOf('https://') !== 0) return;
        debugLog('postOpenUrl: sending url=' + url);
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'openUrl', url: url });
        }
    }

    /** ロード済み画像のクリックでビューアウィンドウに送る (Phase 10)。 */
    function postOpenInViewer(url) {
        if (!url) return;
        if (url.indexOf('http://') !== 0 && url.indexOf('https://') !== 0) return;
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'openInViewer', url: url });
        }
    }

    // ---------- post-no コンテキストメニュー (返信 / 自分の書き込み / NG 登録) ----------
    // .post-no を「左クリック または 右クリック」した時に C# 側にメッセージを送り、
    // ネイティブ (= WPF ContextMenu) のメニューを表示してもらう。
    // メニューの組み立ては全て C# 側 (ThreadDisplayPane.xaml の PostNoContextMenu リソース)。
    // ペイロードに必要な値 (name / id / watchoi / isOwn) は JS 側で post から抽出して渡す。

    function extractWatchoiFromName(name) {
        if (!name) return '';
        // post.name は HTML を含むことがあるのでタグを剥がしてから検索
        const stripped = String(name).replace(/<[^>]+>/g, '');
        const m = stripped.match(/[A-Za-z0-9]{4}-[A-Za-z0-9]{4}/);
        return m ? m[0] : '';
    }

    function plainName(name) {
        if (!name) return '';
        return String(name).replace(/<[^>]+>/g, '').trim();
    }

    /** updateOwnPosts メッセージで届いた変更を ownPostNumbers Set + DOM の「自分」バッジに反映する。
     *  primary レス (id="rN") のみ更新。tree モード等で重複表示されている embedded レスは
     *  次回再レンダで反映される (= 全件 DOM 走査でコストを払うほどの優先度ではない)。 */
    function applyOwnPostsChanges(changes) {
        for (const c of changes) {
            const n = c && c.number;
            const isOwn = !!(c && c.isOwn);
            if (typeof n !== 'number') continue;
            if (isOwn) ownPostNumbers.add(n);
            else       ownPostNumbers.delete(n);

            const el = document.getElementById('r' + n);
            if (!el) continue;
            const header = el.querySelector(':scope > .post-header');
            if (!header) continue;
            let badge = header.querySelector(':scope > .post-own');
            if (isOwn) {
                if (!badge) {
                    badge = document.createElement('span');
                    badge.className = 'post-own';
                    badge.textContent = '自分';
                    // post-name の直前に挿入 (= テンプレで指定されている挿入位置)
                    const nameEl = header.querySelector(':scope > .post-name');
                    if (nameEl) header.insertBefore(badge, nameEl);
                    else        header.appendChild(badge);
                }
            } else if (badge) {
                badge.remove();
            }
        }
        // 自分マーカーは scrollbar の mark トラックにも色違いで出している。
        // toggle 直後に必ず再描画 (= 即時反映、追加/解除の両方)。
        updateMarkScrollbarMarker();
    }

    /** post-no クリック / 右クリック時に C# にコンテキストメニュー表示を依頼する。
     *  メニュー本体 (返信 / 自分の書き込み / NG …) は C# (ThreadDisplayPane) の WPF ContextMenu。 */
    function postPostNoContextMenu(postNo) {
        const post = postsByNumber.get(postNo);
        if (!post) return;
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({
                type:    'postNoContextMenu',
                number:  postNo,
                name:    plainName(post.name),
                id:      post.id || '',
                watchoi: extractWatchoiFromName(post.name),
                isOwn:   ownPostNumbers.has(postNo),
            });
        }
    }

    /** URL (テキストリンク or 画像サムネ) の右クリック時に C# にコンテキストメニュー表示を依頼。
     *  「リンクをコピー」等のメニューは C# (ThreadDisplayPane) 側 UrlContextMenu リソースで定義。
     *  data-url にはオリジナルのページ URL (= サムネ生成用に解決した data-src ではなく、ユーザが
     *  共有したい元 URL) が入っているのでそれをそのまま渡す。 */
    function postUrlContextMenu(url) {
        if (!url) return;
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'urlContextMenu', url: url });
        }
    }

    // 右クリックは contextmenu イベントで拾い、ブラウザ既定メニューを抑制してから C# に送る。
    // 優先順位: post-no が一番強い → URL リンク (テキスト or 画像) → それ以外は何もしない (= 既定挙動)。
    document.addEventListener('contextmenu', function(e) {
        // 1) post-no
        const postNo = e.target.closest && e.target.closest('.post-no');
        if (postNo && postNo.dataset && postNo.dataset.number) {
            e.preventDefault();
            e.stopPropagation();
            const n = parseInt(postNo.dataset.number, 10);
            if (!isNaN(n)) postPostNoContextMenu(n);
            return;
        }

        // 2) URL: テキストリンク <a[data-url]> または画像サムネ .image-slot[data-url]
        const urlEl = e.target.closest && e.target.closest('a[data-url], .image-slot[data-url]');
        if (urlEl && urlEl.dataset && urlEl.dataset.url) {
            e.preventDefault();
            e.stopPropagation();
            postUrlContextMenu(urlEl.dataset.url);
            return;
        }
    });

    document.addEventListener('click', function (e) {
        // .post-no 左クリック → C# にコンテキストメニュー表示要求。anchor/URL ロジックより先に拾って早期 return。
        // 右クリックは別 listener (contextmenu) で同じ要求を出している (= どちらでもメニューが出る)。
        const postNo = e.target.closest && e.target.closest('.post-no');
        if (postNo && postNo.dataset && postNo.dataset.number) {
            e.preventDefault();
            e.stopPropagation();
            const n = parseInt(postNo.dataset.number, 10);
            if (!isNaN(n)) postPostNoContextMenu(n);
            return;
        }

        // メディアスロットのクリックを mediaType ごとに分岐:
        //   image    → .loaded なら URL を開く / それ以外は強制 <img> 化
        //   video    → 常に <video controls autoplay> に置換 (インライン再生)
        //   youtube  → デフォルトブラウザで開く (インライン iframe は YouTube の embed 制限で
        //              Error 153 が出るため、サムネ表示のみに留める方針)
        const slot = e.target.closest && e.target.closest('.image-slot');
        if (slot) {
            // 失敗スロット (展開失敗 / 画像読み込み失敗) は明示クリックで再試行。
            if (slot.classList.contains('expand-failed') || slot.classList.contains('load-failed')) {
                retrySlot(slot);
                return;
            }
            if (slot.classList.contains('playing')) {
                return; // 再生中は何もしない (内部要素が click を消費)
            }
            const mediaType = slot.dataset.mediaType || 'image';
            if (mediaType === 'video') {
                playMedia(slot);
                return;
            }
            if (mediaType === 'youtube') {
                if (slot.dataset.url) postOpenUrl(slot.dataset.url);
                return;
            }
            // image
            if (slot.classList.contains('loaded')) {
                // Phase 10: ロード済みなら外部ブラウザではなくビューアウィンドウに送る。
                // ビューアにはサムネ生成に使った実画像 URL (= data-src) を送る。元のページ URL
                // (= data-url; x.com のステータス URL や imgur の HTML ページ URL 等) ではビューアが
                // 画像を取得できない。x.com → pbs.twimg、imgur (HTML) → 直 .jpg、のように
                // applyMetaToSlot / buildMediaSlotForUrl が data-src を実画像 URL に解決済み。
                if (slot.dataset.src) postOpenInViewer(slot.dataset.src);
            } else {
                loadSlotImage(slot);
            }
            return;
        }

        const a = e.target.closest && e.target.closest('a');
        if (!a) return;
        if (a.classList.contains('anchor')) {
            const from = parseInt(a.dataset.from, 10);
            const el = document.getElementById('r' + from);
            if (el) el.scrollIntoView({ block: 'start' });
            return;
        }
        if (a.dataset.url) postOpenUrl(a.dataset.url);
    });

    // ---------- multi-level anchor popup ----------
    // popups[i].level === i (level は配列インデックスと一致)
    let popups = [];
    // level → setTimeout id。close は level 単位で扱い、上位/下位は独立。
    const closeTimers = new Map();
    const MAX_RANGE = 50;
    const CLOSE_DELAY_MS = 250;

    function scheduleCloseAt(level) {
        cancelCloseAt(level);
        const id = setTimeout(function () {
            closeFrom(level);
            closeTimers.delete(level);
        }, CLOSE_DELAY_MS);
        closeTimers.set(level, id);
    }

    function cancelCloseAt(level) {
        const id = closeTimers.get(level);
        if (id !== undefined) {
            clearTimeout(id);
            closeTimers.delete(level);
        }
    }

    /** mouse がポップアップ階層の level N にいる間は、それより浅い (<= N) close をすべて取り消す。 */
    function cancelCloseAtOrBelow(maxLevel) {
        for (const lvl of Array.from(closeTimers.keys())) {
            if (lvl <= maxLevel) cancelCloseAt(lvl);
        }
    }

    function closeFrom(level) {
        while (popups.length > level) {
            const p = popups.pop();
            p.el.remove();
        }
        // 閉じた階層に紐付いていた timer は無効
        for (const lvl of Array.from(closeTimers.keys())) {
            if (lvl >= level) cancelCloseAt(lvl);
        }
    }

    /** ポップアップ表示用にレスをクローンする際の共通処理。
     *  - 「返信N件」バッジを除去 → そこからさらに返信ポップアップが連鎖しない
     *  - ツリー / 重複なしツリー表示で対象レスに inline 埋め込まれている子レス (= .post.embedded) を除去
     *    → popup は対象レス本体だけを見せ、それへの返信は出さない
     *  - ワッチョイリンク (.watchoi-link) は wrapper を外してテキスト化 → ポップアップ内ではリンク装飾なし &
     *    hover ポップアップが連鎖しない (ワッチョイ字列が長くマウス通過で誤発火しやすいため)
     *  バッジや子レスは元レス側にはそのまま残るので、通常スレ表示でのホバー / ツリー表示は引き続き機能する。 */
    function clonePostForPopup(src) {
        const clone = src.cloneNode(true);
        clone.querySelectorAll('.post-reply-count').forEach(function (el) { el.remove(); });
        clone.querySelectorAll('.post.embedded').forEach(function (el) { el.remove(); });
        clone.querySelectorAll('.watchoi-link').forEach(function (el) {
            if (el.parentNode) el.parentNode.replaceChild(document.createTextNode(el.textContent), el);
        });
        return clone;
    }

    function buildPopupContent(from, to) {
        const frag = document.createDocumentFragment();
        const span = Math.min(to - from + 1, MAX_RANGE);
        let any = false;
        for (let i = 0; i < span; i++) {
            const n = from + i;
            const src = document.getElementById('r' + n);
            if (src) {
                frag.appendChild(clonePostForPopup(src));
                any = true;
            }
        }
        if (!any) {
            const miss = document.createElement('div');
            miss.className = 'anchor-missing';
            miss.textContent = '>>' + from + (to !== from ? ('-' + to) : '') + ' (見つかりません)';
            frag.appendChild(miss);
        }
        return frag;
    }

    /** 任意のレス番号配列からポップアップ内容を作る (返信件数バッジのホバー用)。 */
    function buildPopupContentFromList(numbers) {
        const frag = document.createDocumentFragment();
        const limit = Math.min(numbers.length, MAX_RANGE);
        let any = false;
        for (let i = 0; i < limit; i++) {
            const src = document.getElementById('r' + numbers[i]);
            if (src) {
                frag.appendChild(clonePostForPopup(src));
                any = true;
            }
        }
        if (!any) {
            const miss = document.createElement('div');
            miss.className = 'anchor-missing';
            miss.textContent = '(返信元レスが見つかりません)';
            frag.appendChild(miss);
        }
        return frag;
    }

    /** ポップアップを表示位置に配置。下に収まらないなら上に出す。viewport (= ペイン) 内に収める。 */
    function positionPopup(el, anchor) {
        const rect = anchor.getBoundingClientRect();
        const vw = document.documentElement.clientWidth;
        const vh = document.documentElement.clientHeight;
        const preferLeft = rect.left + window.scrollX;

        // 一旦下に置いて offsetWidth/Height を測る
        el.style.visibility = 'hidden';
        el.style.display = 'block';
        el.style.left = preferLeft + 'px';
        el.style.top = (rect.bottom + window.scrollY + 2) + 'px';

        const w = el.offsetWidth;
        const h = el.offsetHeight;

        // 横位置: viewport 右端をはみ出すなら左に寄せる
        let left = preferLeft;
        if (left + w > vw - 4) left = Math.max(4, vw - w - 4);
        el.style.left = left + 'px';

        // 縦位置: 下に収まれば下、収まらず上に収まるなら上、どちらも無理ならスペースの広い側
        const spaceBelow = vh - rect.bottom;
        const spaceAbove = rect.top;
        let top;
        if (h <= spaceBelow - 4) {
            top = rect.bottom + window.scrollY + 2;
        } else if (h <= spaceAbove - 4) {
            top = rect.top + window.scrollY - h - 2;
        } else if (spaceAbove > spaceBelow) {
            // 上にもギリギリ。viewport 上端から表示 (はみ出した分は popup 内スクロール)
            top = window.scrollY + 4;
        } else {
            top = rect.bottom + window.scrollY + 2;
        }
        el.style.top = top + 'px';

        el.style.visibility = '';
    }

    function openPopup(anchor, level) {
        closeFrom(level);
        const from = parseInt(anchor.dataset.from, 10);
        const to = parseInt(anchor.dataset.to, 10) || from;
        const el = document.createElement('div');
        el.className = 'anchor-popup';
        el.appendChild(buildPopupContent(from, to));
        document.body.appendChild(el);
        positionPopup(el, anchor);
        // popup level N の hover は <= N の close をすべてキャンセル (チェーン上)
        el.addEventListener('mouseenter', function () { cancelCloseAtOrBelow(level); });
        el.addEventListener('mouseleave', function () { scheduleCloseAt(level); });
        attachAnchorHandlers(el, level + 1);
        popups.push({ el: el, anchor: anchor, level: level });
    }

    /** post-no をホバーした時に開く「返信元レスをまとめて見せる」ポップアップ。
     *  - anchor: ポップアップの位置基準 (= post-no 要素)。
     *  - dataSource: data-replies (カンマ区切りレス番号) を持つ要素 (= 兄弟の .post-reply-count)。
     *  返信数バッジ (.post-reply-count) は hover 反応しない plain テキストになっており、
     *  クリックメニュー (= 返信 / NG 登録) は post-no の click ハンドラ側で別途処理される。 */
    function openReplyPopup(anchor, dataSource, level) {
        closeFrom(level);
        const list = (dataSource.dataset.replies || '')
            .split(',')
            .map(function (s) { return parseInt(s, 10); })
            .filter(function (n) { return !isNaN(n); });
        if (list.length === 0) return;
        const el = document.createElement('div');
        el.className = 'anchor-popup';
        el.appendChild(buildPopupContentFromList(list));
        document.body.appendChild(el);
        positionPopup(el, anchor);
        el.addEventListener('mouseenter', function () { cancelCloseAtOrBelow(level); });
        el.addEventListener('mouseleave', function () { scheduleCloseAt(level); });
        attachAnchorHandlers(el, level + 1);
        popups.push({ el: el, anchor: anchor, level: level });
    }

    // ---- スレ URL ホバーポップアップ (= a.thread-link) ----
    // 同一スレッド (host+dir+key+postNo) は 1 度 fetch すれば再ホバーで即時表示できるよう cache。
    // 値: { ok, title, body, name, dateText, postNumber, error }
    const threadPreviewCache = new Map();
    function threadPreviewCacheKey(host, dir, key, postNo) {
        return host + '|' + dir + '|' + key + '|' + (postNo > 0 ? postNo : 0);
    }
    // requestId → 当該ポップアップを構成する DOM への参照 ({ el, title, body, host, dir, key, postNo })。
    // C# からの threadPreview レスポンスが返ったときに、ユーザがマウスを離して popup が消えていても
    // 静かに drop できるよう、popup が close された時点で entry も削除する。
    const pendingThreadPreviews = new Map();
    let nextThreadPreviewReqId = 1;

    function buildThreadPreviewLoadingNode(href) {
        const wrap = document.createElement('div');
        wrap.className = 'thread-preview-loading';
        wrap.textContent = '読み込み中… ' + href;
        return wrap;
    }

    function buildThreadPreviewContentNode(data, fallbackUrl) {
        const frag = document.createDocumentFragment();
        const titleEl = document.createElement('div');
        titleEl.className = 'thread-preview-title';
        titleEl.textContent = data.title && data.title.length > 0 ? data.title : (fallbackUrl || '(タイトル不明)');
        frag.appendChild(titleEl);

        const bodyEl = document.createElement('div');
        bodyEl.className = 'thread-preview-body';
        if (data.ok) {
            const meta = document.createElement('div');
            meta.className = 'thread-preview-postno';
            meta.textContent = '>>' + (data.postNumber || 1);
            bodyEl.appendChild(meta);
            const txt = document.createElement('div');
            txt.className = 'thread-preview-text';
            // body は dat デコード済み + 改行は \n。buildBodyHtml で URL/アンカー auto-link しつつ HTML 化。
            txt.innerHTML = buildBodyHtml(data.body || '');
            bodyEl.appendChild(txt);
        } else {
            const err = document.createElement('div');
            err.className = 'thread-preview-error';
            err.textContent = data.error ? ('取得失敗: ' + data.error) : '取得失敗';
            bodyEl.appendChild(err);
        }
        frag.appendChild(bodyEl);
        return frag;
    }

    function openThreadPreviewPopup(anchor, level) {
        closeFrom(level);
        const host   = anchor.dataset.threadHost || '';
        const dir    = anchor.dataset.threadDir  || '';
        const key    = anchor.dataset.threadKey  || '';
        const postNo = parseInt(anchor.dataset.threadPost || '0', 10) || 0;
        if (!host || !dir || !key) return;

        const el = document.createElement('div');
        el.className = 'anchor-popup thread-preview';
        document.body.appendChild(el);

        const cacheKey = threadPreviewCacheKey(host, dir, key, postNo);
        const cached = threadPreviewCache.get(cacheKey);
        if (cached) {
            el.appendChild(buildThreadPreviewContentNode(cached, anchor.dataset.url));
        } else {
            el.appendChild(buildThreadPreviewLoadingNode(anchor.dataset.url || ''));
            const reqId = 'tp' + (nextThreadPreviewReqId++);
            pendingThreadPreviews.set(reqId, { el: el, anchor: anchor, host: host, dir: dir, key: key, postNo: postNo });
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({
                    type:       'threadPreviewRequest',
                    requestId:  reqId,
                    host:       host,
                    dir:        dir,
                    key:        key,
                    postNumber: postNo,
                });
            }
        }

        positionPopup(el, anchor);
        el.addEventListener('mouseenter', function () { cancelCloseAtOrBelow(level); });
        el.addEventListener('mouseleave', function () { scheduleCloseAt(level); });
        attachAnchorHandlers(el, level + 1);
        popups.push({ el: el, anchor: anchor, level: level });
    }

    /** C# からの threadPreview レスポンス処理。pending entry を見つけて popup 中身を差し替え、cache に積む。 */
    function onThreadPreviewResponse(msg) {
        if (!msg || typeof msg.requestId !== 'string') return;
        const data = {
            ok:         !!msg.ok,
            title:      typeof msg.title === 'string'    ? msg.title    : '',
            body:       typeof msg.body  === 'string'    ? msg.body     : '',
            name:       typeof msg.name  === 'string'    ? msg.name     : '',
            dateText:   typeof msg.dateText === 'string' ? msg.dateText : '',
            postNumber: typeof msg.postNumber === 'number' ? msg.postNumber : 0,
            error:      typeof msg.error === 'string'    ? msg.error    : null,
        };
        const host = typeof msg.host === 'string' ? msg.host : '';
        const dir  = typeof msg.dir  === 'string' ? msg.dir  : '';
        const key  = typeof msg.key  === 'string' ? msg.key  : '';
        // 投げた時点の postNo はレスポンスにも含まれている (= C# が即値で返すため)。
        // cache キーは投げた postNo (= リクエストの payload.postNumber) を使うべきだが、
        // 0 (= 「指定なし → 1 を表示」) は意味的に同じなので postNumber>0 のとき key も同じになる。
        // ここでは pending entry から元の postNo を取り出して使う。
        const pending = pendingThreadPreviews.get(msg.requestId);
        if (pending) {
            const cacheKey = threadPreviewCacheKey(pending.host, pending.dir, pending.key, pending.postNo);
            threadPreviewCache.set(cacheKey, data);
            // popup がまだ生きているなら DOM 差し替え (= 消えていれば何もしない = ユーザがマウス離している)。
            if (pending.el && pending.el.isConnected) {
                pending.el.innerHTML = '';
                pending.el.appendChild(buildThreadPreviewContentNode(data, pending.anchor.dataset.url));
                positionPopup(pending.el, pending.anchor);
            }
            pendingThreadPreviews.delete(msg.requestId);
        } else if (host && dir && key) {
            // 元 pending entry が close で消されていてもキャッシュには積む (= 次回ホバーで即時表示)。
            const cacheKey = threadPreviewCacheKey(host, dir, key, data.postNumber > 0 ? data.postNumber : 0);
            threadPreviewCache.set(cacheKey, data);
        }
    }

    function attachAnchorHandlers(root, level) {
        root.querySelectorAll('a.anchor').forEach(function (a) {
            if (a.classList.contains('missing')) return;
            a.addEventListener('mouseenter', function () {
                cancelCloseAt(level);
                openPopup(a, level);
            });
            a.addEventListener('mouseleave', function () { scheduleCloseAt(level); });
        });
        // post-no ホバー → 返信元レスをポップアップ (data は兄弟 .post-reply-count[data-replies] から取る)。
        // ポップアップ内クローンは clonePostForPopup が .post-reply-count を剥がすので、ポップアップ中の
        // post-no をホバーしても兄弟バッジが見つからず再帰ポップアップは開かない (= 連鎖防止)。
        root.querySelectorAll('.post-no').forEach(function (postNo) {
            postNo.addEventListener('mouseenter', function () {
                const header = postNo.parentElement;
                if (!header) return;
                const badge = header.querySelector(':scope > .post-reply-count');
                if (!badge || !badge.dataset.replies) return;
                cancelCloseAt(level);
                openReplyPopup(postNo, badge, level);
            });
            postNo.addEventListener('mouseleave', function () { scheduleCloseAt(level); });
        });
        // 5ch.io / bbspink.com スレ URL: ホバーでスレタイトル + 対象レス (or 1) をポップアップ。
        root.querySelectorAll('a.thread-link').forEach(function (a) {
            a.addEventListener('mouseenter', function () {
                cancelCloseAt(level);
                openThreadPreviewPopup(a, level);
            });
            a.addEventListener('mouseleave', function () { scheduleCloseAt(level); });
        });
    }

    /** カーソルが矩形のいずれかに入っているか (= 1 つでも該当すれば true)。
     *  境界はクライアント座標 (e.clientX/Y) と getBoundingClientRect の比較。 */
    function isPointInsideAnyRect(x, y, els) {
        for (var i = 0; i < els.length; i++) {
            var el = els[i];
            if (!el) continue;
            var r = el.getBoundingClientRect();
            if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) return true;
        }
        return false;
    }

    /** ポップアップが消え残るバグ回避用のグローバル mousemove セーフティネット。
     *
     *  典型的な漏れ: 深いポップアップ N からカーソルが直線的に外側へ抜けると、
     *    - mouseleave は popup N でしか発火せず、popup 0..N-1 は閉じ予約が入らない
     *    - その結果、popup N は 250ms 後に消えるが上位は残り続ける
     *  これは mouseenter 時に cancelCloseAtOrBelow(level) で上位の close timer が
     *  毎回キャンセルされるのが原因 (= 中間 popup には自身の close 予約がそもそも残らない)。
     *
     *  対策: mousemove で常時カーソル位置を見て、開いているポップアップ (および
     *  そのアンカー) のいずれにも乗っていなければ level 0 から閉じるよう予約する。
     *  既に level 0 の timer が走っているなら触らない (= 周期的に reset して
     *  「永遠に消えない」状態にしないため)。
     *
     *  passive: true で hot path を維持。短時間に大量に飛んでくるが、
     *  popups.length === 0 で即 return するので通常時はほぼノーコスト。 */
    document.addEventListener('mousemove', function (e) {
        if (popups.length === 0) return;
        var x = e.clientX, y = e.clientY;
        // 深い方から見て「ポップアップ or そのアンカー」にカーソルが乗っているレベルを探す。
        // 見つかったら、その level 以下の close は全部キャンセル (= キープ)。
        for (var i = popups.length - 1; i >= 0; i--) {
            var p = popups[i];
            if (isPointInsideAnyRect(x, y, [p.el, p.anchor])) {
                cancelCloseAtOrBelow(i);
                return;
            }
        }
        // どのポップアップにも乗っていない → level 0 の close 予約が無いなら入れる。
        if (!closeTimers.has(0)) scheduleCloseAt(0);
    }, { passive: true });

    /** 既存レス DOM の返信数バッジ + post-no の色クラスを差分更新する (flat モード appendPosts 用)。
     *  テンプレで `<span class="post-reply-count" data-count="" data-replies="">返<span class="reply-num"></span> </span>`
     *  が常に出ている前提で、data-count / data-replies / .reply-num の textContent だけ書き換える。
     *  位置が動かないのでテンプレで指定した位置を保てる。バッジの可視テキスト「返」はテンプレ側 (post.html)
     *  に置かれており、JS 側にはテキスト literal を持たない。
     *  ホバーリスナは初期描画時に attachAnchorHandlers が data-replies 属性ベースで取り付けているので
     *  この関数では触らない。 */
    function updateReplyCountBadge(num) {
        const post = document.getElementById('r' + num);
        if (!post) return;
        const header = post.querySelector(':scope > .post-header');
        if (!header) return;

        const replies = currentReverseIndex.get(num) || [];
        const count = replies.length;

        // 1) post-no の色クラス (しきい値は replyTierClass に集約)
        const postNo = header.querySelector(':scope > .post-no');
        if (postNo) {
            postNo.classList.remove('has-replies-few', 'has-replies-many');
            const tier = replyTierClass(count);
            if (tier) postNo.classList.add(tier);
        }

        // 2) バッジの数値を data-count + .reply-num に書く。CSS の `[data-count="0"]` で 0 件時は非表示。
        //    テンプレが post-reply-count を出していないテーマでは何もしない (互換)。
        const badge = header.querySelector(':scope > .post-reply-count');
        if (!badge) return;
        badge.dataset.count   = String(count);
        badge.dataset.replies = replies.join(',');
        const numEl = badge.querySelector(':scope > .reply-num');
        if (numEl) numEl.textContent = String(count);
    }

    /** 指定レス番号集合を「即時に DOM から消す + 内部状態を整合させる」(Phase 25)。
     *  C# 側で NG ルールが追加された直後に呼ばれる経路。スレを閉じて開き直さなくても、
     *  追加した瞬間に該当レスが画面から消える。
     *
     *  - DOM: primary レス (id="rN") と、ツリーモード等で他レス配下に出ている embedded copy
     *    (= post-no の data-number で識別) を全て remove。
     *  - 内部状態: allPosts / postsByNumber / currentReverseIndex を更新。
     *  - 残レスの「返信 N 件」バッジは消したレスへの参照分だけ減算が必要なので
     *    primary レス全件に対して updateReplyCountBadge を再実行 (= O(残全件) で軽い)。
     *  - リッチスクロールバーのマーカーも再計算 (= 人気/メディア/URL/mark 全部)。
     *
     *  scroll 位置はブラウザの DOM 維持のままなので、消えた分だけ後ろのレスが上に詰まる挙動。
     *  これは「スレを再描画した時の挙動」よりもユーザの視線位置が保たれて望ましい。 */
    function hidePostsByNumber(numbers) {
        if (!Array.isArray(numbers) || numbers.length === 0) return;
        const set = new Set();
        for (const n of numbers) {
            const v = (typeof n === 'number') ? n : parseInt(n, 10);
            if (!isNaN(v)) set.add(v);
        }
        if (set.size === 0) return;

        // 1) DOM: primary + embedded copy をまとめて取り除く。post-header > .post-no の data-number で判別。
        document.querySelectorAll('.post').forEach(function (p) {
            const noEl = p.querySelector(':scope > .post-header > .post-no');
            if (!noEl) return;
            const n = parseInt(noEl.dataset.number, 10);
            if (!isNaN(n) && set.has(n)) p.remove();
        });

        // 2) 内部状態を整合
        allPosts      = allPosts.filter(function (p) { return !set.has(p.number); });
        for (const n of set) postsByNumber.delete(n);
        currentReverseIndex = buildReverseIndex();

        // 3) 残 primary レスの返信バッジを総再計算 (消したレスへの ref が他のバッジから減るため)
        document.querySelectorAll('#posts > .post[id^="r"]').forEach(function (el) {
            const n = parseInt(el.id.slice(1), 10);
            if (!isNaN(n)) updateReplyCountBadge(n);
        });

        // 4) スクロールバー / セクションマーク等の再計算
        if (typeof updateRichScrollbar       === 'function') updateRichScrollbar();
        if (typeof updateMarkScrollbarMarker === 'function') updateMarkScrollbarMarker();
    }

    // ---------- per-post 増分 DOM 挿入 (Phase 24) ----------
    // ストリーミング受信中に「1 batch ごとに renderCurrentViewMode で全再描画」する旧経路は
    // chunk 数 ×「全レスのテンプレ展開」で O(N²) になり、特に dedupTree モードで重かった。
    // 代わりに 1 レスごとにそのレスを「あるべき場所」だけ DOM に差し込む方式に切り替える。
    // 仕様 (= 各モード × 状況 × アンカー数の組合せ) は以下:
    //   flat   : 末尾 primary
    //   tree   : 末尾 primary + (anchor=1 のとき親の reverse-expansion に duplicate コピーも)
    //   dedupTree (新規/ストリーミング):
    //            anchor=0 / 多 → 末尾 primary (leaf)
    //            anchor=1     → 親の reverse-expansion 配下に embed (primary は出さない)
    //   dedupTree (差分=incremental=true):
    //            insertPostIncremental は通らず、batch 末で rebuildSectionB() が section B を組み直す。
    // どの経路でも「全レス処理後の最終 DOM」は renderCurrentViewMode の出力と一致する設計。

    /** root (= #posts) の最後 (or before 指定要素) に html を流し込み、anchor の missing クラス付与と
     *  attachAnchorHandlers をまとめて実施する共通入口。
     *  各 build*PostHtml の出力を DOM に投入する全箇所で本関数を経由させる。 */
    function insertHtmlIntoContainer(container, html, before) {
        const tmp = document.createElement('div');
        tmp.innerHTML = html;
        tmp.querySelectorAll('a.anchor').forEach(function (a) {
            const from = parseInt(a.dataset.from, 10);
            if (!postsByNumber.has(from)) a.classList.add('missing');
        });
        attachAnchorHandlers(tmp, 0);
        while (tmp.firstChild) {
            if (before) container.insertBefore(tmp.firstChild, before);
            else        container.appendChild(tmp.firstChild);
        }
    }

    /** p の primary を、現モード戦略の HTML 生成関数で作って root 末尾に append。
     *  モード固有の HTML 形は ViewModeStrategy.buildPrimaryHtml に委譲。 */
    function appendPrimaryAtEnd(p, root) {
        insertHtmlIntoContainer(root, vm().buildPrimaryHtml(p), /*before*/ null);
    }

    /** parent (= reverseIndex の親レス) の reverse-expansion 配下に p を embed として追加する。
     *  全件展開する (= 旧実装の「上限超過 → "…他 N 件" に集約」抑制は撤去)。
     *
     *  embed HTML の作り方は呼び出し側 (= ViewModeStrategy.buildEmbedHtml) が決める。これにより
     *  「tree=id 無しの duplicate / dedupTree=id 付きの canonical」のような mode 固有の差を
     *  ここに条件分岐で持たずに済み、新モード追加で本関数を触る必要がなくなる。
     *  既存 DOM に親 primary が無い (= まれ) 場合は false を返し caller 側 fallback に任せる。 */
    function embedUnderParentReverse(parentNum, p, buildEmbedHtml) {
        const parentEl = document.getElementById('r' + parentNum);
        if (!parentEl) return false;

        const wrapper = document.createElement('div');
        wrapper.className = 'inline-expansion reverse';
        wrapper.innerHTML = buildEmbedHtml(p);

        wrapper.querySelectorAll('a.anchor').forEach(function (a) {
            const from = parseInt(a.dataset.from, 10);
            if (!postsByNumber.has(from)) a.classList.add('missing');
        });
        attachAnchorHandlers(wrapper, 0);

        parentEl.appendChild(wrapper);
        return true;
    }

    /** レス 1 件の DOM 挿入 — モード戦略の <c>insertOnArrival</c> に dispatch するだけ。
     *  dedupTree-delta は本関数経由ではなく rebuildSectionB() で一括描画 (caller 側で振り分け)。 */
    function insertPostIncremental(p) {
        const root = document.getElementById('posts');
        if (!root) return;
        vm().insertOnArrival(p, root);
    }

    // ============================================================
    // ViewMode 戦略テーブル — 表示モードごとに変動するロジックを 1 オブジェクトに集約。
    //
    // 新モード追加手順:
    //   1) 下記テーブルに 1 エントリを追加 (= 6 メソッドを実装)
    //   2) 必要なら mode 固有の HTML ビルダ関数 (buildXxxPostHtml 等) を別途追加
    //   3) C# 側の <c>ThreadViewMode</c> enum と XAML の DataTrigger に対応値を追加
    // 既存ロジック側 (insertPostIncremental / appendPrimaryAtEnd / rebuildPopularIncludeSet /
    // renderCurrentViewMode / appendPosts) は分岐を持たず、すべて <c>vm()</c> 経由で戦略を呼ぶ。
    //
    // メソッド契約:
    //   insertOnArrival(p, root)    1 レス到着時の DOM 配置 (primary / 親 embed のどちらにするかを決める)
    //   buildPrimaryHtml(p)         primary instance の HTML 生成 (forward 展開などのモード固有の整形)
    //   expandsPopularChain()       popularOnly フィルタで「人気レスとその子孫」を展開するか (tree 系のみ true)
    //   splitBySectionMark()        フル再描画で「以降新レス」ラベルで section A/B 分割するか (dedupTree のみ true)
    //   usesBulkDeltaRebuild()      isDelta バッチで section B を末尾に bulk rebuild するか (dedupTree のみ true)
    //   promoteOnNewRefresh(root)   新リフレッシュ境界で旧 delta を section A に「昇格」する処理。
    //                               flat / tree は nop。共通の sessionNewPostNumbers.clear と is-new 解除は呼び側で行う。
    // ============================================================
    const VIEW_MODE_STRATEGIES = {
        flat: {
            insertOnArrival(p, root)  { appendPrimaryAtEnd(p, root); },
            buildPrimaryHtml(p)       { return buildPostHtml(p); },
            expandsPopularChain()     { return false; },
            splitBySectionMark()      { return false; },
            usesBulkDeltaRebuild()    { return false; },
            promoteOnNewRefresh(_root) { /* nop */ },
        },
        tree: {
            insertOnArrival(p, root) {
                // tree: 末尾 primary は常に置く。anchor=1 のときだけ親の reverse 配下にも duplicate を置く。
                appendPrimaryAtEnd(p, root);
                const anchors = getValidForwardAnchors(p);
                if (anchors.length === 1) {
                    embedUnderParentReverse(anchors[0], p,
                        function (q) { return buildTreePostHtml(q, /*isEmbedded*/ true); });
                }
            },
            buildPrimaryHtml(p)       { return buildTreePostHtml(p, /*isEmbedded*/ false); },
            expandsPopularChain()     { return true; },
            splitBySectionMark()      { return false; },
            usesBulkDeltaRebuild()    { return false; },
            promoteOnNewRefresh(_root) { /* nop */ },
        },
        dedupTree: {
            insertOnArrival(p, root) {
                // dedupTree (非 delta = ストリーミング初回 / 全表示):
                //   anchor=1 → 親の reverse 配下に canonical embed (primary は出さない、id はここに残す)
                //   anchor=0 / 多 → 末尾に primary leaf
                const anchors = getValidForwardAnchors(p);
                if (anchors.length === 1) {
                    const ok = embedUnderParentReverse(anchors[0], p,
                        function (q) { return renderPost(postDataFor(q, /*isEmbedded*/ true, /*omitId*/ false, '')); });
                    if (!ok) appendPrimaryAtEnd(p, root);
                } else {
                    appendPrimaryAtEnd(p, root);
                }
            },
            // dedupTree primary は forward 親展開を持たない leaf 形式 (= forward は dedup により別所で primary として
            // 既出のため不要)。結果として flat と同じ buildPostHtml(p) で生成できる。
            buildPrimaryHtml(p)       { return buildPostHtml(p); },
            expandsPopularChain()     { return true; },
            splitBySectionMark()      { return true; },
            usesBulkDeltaRebuild()    { return true; },
            promoteOnNewRefresh(root) {
                // 旧 section B (= .incremental-section) を撤去 → sessionNewPostNumbers の各レスを通常の per-post
                // 配置で再挿入することで section A に「昇格」させる。sessionNewPostNumbers の clear は呼び側で実施。
                const existingSection = root.querySelector(':scope > .incremental-section');
                if (existingSection) existingSection.remove();
                const promoteList = [...sessionNewPostNumbers].sort(function (a, b) { return a - b; });
                for (const n of promoteList) {
                    const p = postsByNumber.get(n);
                    if (p) insertPostIncremental(p);
                }
            },
        },
    };
    function vm() { return VIEW_MODE_STRATEGIES[viewMode] || VIEW_MODE_STRATEGIES.flat; }

    /** dedupTree 差分 (= incremental=true) batch 終了後、section B (= 「以降新レス」ラベル以降) を
     *  sessionNewPostNumbers から再構築する。section A (= ラベル以前) の DOM はいじらない (= 仕様)。
     *  既存の incremental-section があれば破棄して新規生成 (= 過去 batch との forest 統合は明示的にやる)。 */
    function rebuildSectionB() {
        const root = document.getElementById('posts');
        if (!root) return;

        const existing = root.querySelector(':scope > .incremental-section');
        if (existing) existing.remove();

        if (sessionNewPostNumbers.size === 0) return;
        const incNumbers = [];
        for (const p of allPosts) {
            if (sessionNewPostNumbers.has(p.number)) incNumbers.push(p.number);
        }
        if (incNumbers.length === 0) return;
        const incSet = new Set(incNumbers);
        const forest = buildIncrementalForest(incNumbers);
        if (forest.size === 0) return;

        let html = '<div class="incremental-section">';
        for (const node of forest.values()) {
            html += '<div class="incremental-block">';
            html += renderIncrementalForestNode(node, incSet, /*isEmbedded*/ false);
            html += '</div>';
        }
        html += '</div>';
        insertHtmlIntoContainer(root, html, /*before*/ null);
    }

    // ---------- public API ----------
    /** streaming で逐次追加。スレ表示の唯一の描画 API。各 WebView2 はライフタイム中ずっと
     *  この関数だけで posts が積み上がる前提で動作する (旧 setPosts チャネルは撤去)。
     *
     *  挿入経路:
     *    - flat / tree / dedupTree-非delta: 1 レスごとに insertPostIncremental で DOM 挿入。
     *    - dedupTree-差分 (= incremental=true): batch 末に rebuildSectionB で section B 全体を再構築。
     *
     *  signature: (batch, scrollTarget, mark, incremental)
     *   - mark: 「以降新レス」ラベルの対象レス番号 (session-local; 永続化されない)
     *           毎 batch ペイロードで C# から最新値が届く (= リフレッシュで新着が来た瞬間に値が立ち、
     *           タブ閉じ / アプリ再起動でリセットされる)。
     *   - incremental: dat 差分追加フラグ。session-new (= is-new 太字対象) の積算 + dedupTree の section B
     *                  再構築経路の振り分けに使う。 */
    window.appendPosts = function (batch, scrollTarget, mark, incremental) {
        if (!Array.isArray(batch) || batch.length === 0) return;
        const root = document.getElementById('posts');
        if (!root) return;

        debugLog('appendPosts: batch=' + batch.length
            + ' (numbers ' + batch[0].number + '..' + batch[batch.length-1].number + ')'
            + ', incremental=' + (incremental === true)
            + ', mark=' + mark
            + ', scrollTarget=' + scrollTarget
            + ', allPostsBefore=' + allPosts.length
            + ', viewMode=' + viewMode);

        // 同梱された scrollTarget があれば保留中ターゲットを更新
        if (typeof scrollTarget === 'number') pendingScrollTarget = scrollTarget;
        const newMark = (typeof mark === 'number') ? mark : null;
        const isDelta = (incremental === true);

        // 新しい delta refresh の境界 (= mark 値が変わった = 別の refresh が始まった) を検出。
        // 比較に markPostNumber を使うと setMarkPostNumber メッセージで先行更新されているので使えず、
        // appendPosts 経路だけが書き込む lastDeltaMark を見る。
        const isNewRefresh = isDelta && newMark != null && newMark !== lastDeltaMark;

        // 新 refresh 境界処理:
        //   1. 前 refresh で太字 (= is-new) になっていたレスを細字に戻す (全モード共通)。
        //   2. モード戦略の promoteOnNewRefresh に dispatch (dedupTree は section B 撤去 + 昇格、他は nop)。
        //   3. sessionNewPostNumbers をリセット (= 後続の per-post loop で current batch が積まれる)。
        if (isNewRefresh) {
            for (const n of sessionNewPostNumbers) {
                const el = document.getElementById('r' + n);
                if (el) el.classList.remove('is-new');
            }
            vm().promoteOnNewRefresh(root);
            sessionNewPostNumbers.clear();
        }
        if (isDelta && newMark != null) lastDeltaMark = newMark;

        // 「以降新レス」ラベルは毎 batch で C# から最新値を受け取り上書き。null なら据え置きせずクリア
        // (= リフレッシュ後 C# 側で値が確定 → 全 batch でその値が届く / 一度設定されたら値は減らない)。
        markPostNumber = newMark;

        // 差分取得 (= isDelta) batch を、per-post 経路 ではなく末尾の section B 一括再構築経路にするか。
        // dedupTree のみ true (= 「ラベル以前 DOM 不可侵 / ラベル以降は祖先 chain forest」の仕様を表現するため)。
        const useDedupBulkRebuild = (vm().usesBulkDeltaRebuild() && isDelta);

        // 1 レスずつ: 内部状態を更新して replayPostIntoDom で「reverseIndex → DOM 挿入 → 親バッジ更新」を実施。
        // dedupTree-delta だけは DOM 挿入を rebuildSectionB に任せるので、ここでは reverseIndex とバッジだけ更新する。
        for (const p of batch) {
            allPosts.push(p);
            postsByNumber.set(p.number, p);
            if (isDelta) sessionNewPostNumbers.add(p.number);

            if (useDedupBulkRebuild) {
                // dedupTree-delta: DOM は batch 末の rebuildSectionB が一括描画する。
                // reverseIndex は section A 既存 primary の返信バッジを最新化するためここで更新する。
                const seen = new Set();
                for (const r of extractAnchorRefs(p.body || '')) {
                    if (r.to - r.from + 1 > INLINE_EXPAND_RANGE_LIMIT) continue;
                    for (let n = r.from; n <= r.to; n++) {
                        if (n >= p.number) continue;
                        if (!postsByNumber.has(n)) continue;
                        if (seen.has(n)) continue;
                        seen.add(n);
                        if (!currentReverseIndex.has(n)) currentReverseIndex.set(n, []);
                        currentReverseIndex.get(n).push(p.number);
                    }
                }
                for (const n of seen) updateReplyCountBadge(n);
            } else {
                replayPostIntoDom(p);
            }
        }

        // dedupTree-delta は batch 末に section B を再構築 (= section A の DOM はいじらない)
        if (useDedupBulkRebuild) rebuildSectionB();

        // 既存 .anchor.missing が新着レスにより解決可能になったケースを救う (= 末尾 batch ごと走査)
        document.querySelectorAll('a.anchor.missing').forEach(function (a) {
            const from = parseInt(a.dataset.from, 10);
            if (postsByNumber.has(from)) {
                a.classList.remove('missing');
                a.addEventListener('mouseenter', function () { cancelCloseAt(0); openPopup(a, 0); });
                a.addEventListener('mouseleave', function () { scheduleCloseAt(0); });
            }
        });

        observeImageSlots(root);
        tryScrollToTarget();
        updateRichScrollbar();
        updateNewPostsMarkBand();
        updateThreadEndMarkBand();
        updateMarkScrollbarMarker();
        markNewPosts();
        applyFilterToAllPosts();
        // 増分追加で同 ID/ワッチョイの件数が変わるので、既存装飾を破棄して全体を再装飾する。
        // (新規装飾だけだと、既に decoration 済の post も "5 件超え → 赤化" 等のしきい値変化に追従できない)
        clearMetaDecorations(root);
        recomputeMetaMaps();
        decorateMeta(root);
    };

    window.setViewMode = function (mode) {
        const next = mode || 'flat';
        // 未知のモード文字列は無視 (= C# 側 enum と JS 側 strategy table の値が同期している前提)。
        if (!Object.prototype.hasOwnProperty.call(VIEW_MODE_STRATEGIES, next)) return;
        if (next === viewMode) return;
        viewMode = next;
        if (allPosts.length > 0) renderCurrentViewMode();
    };

    /**
     * 書き込みダイアログのプレビュー専用エントリ。スレ表示シェル全体を流用しつつ、
     * 唯一の post として渡された 1 件を「全リセット → 単発 append」で再描画する。
     * post 形式は appendPosts と同じ ({number, name, mail, dateText, id, body, threadTitle?})。
     * preview 用にスクロール / mark / 差分管理は無効化、body に preview-mode class を付与して
     * CSS でリッチスクロールバー等を非表示にする。
     */
    window.setPreviewPost = function (post) {
        // body に preview class を付ける (= CSS で richScrollbar / 右 padding を消す目印)
        document.body.classList.add('preview-mode');

        // 内部状態を全クリア (allPosts / scrollTarget / mark / 逆引き indexes)
        allPosts                = [];
        postsByNumber           = new Map();
        currentReverseIndex     = new Map();
        pendingScrollTarget     = null;
        markPostNumber          = null;
        sessionNewPostNumbers.clear();
        lastDeltaMark = null;

        // DOM クリア + post-template の再使用は appendPosts に任せる
        const root = document.getElementById('posts');
        if (root) root.innerHTML = '';

        if (!post) return;
        window.appendPosts([post], null, null, false);
    };

    // ---------- C# からの postMessage 受信 (PostWebMessageAsJson 経由) ----------
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (e) {
            const msg = e.data;
            if (!msg || typeof msg !== 'object' || !msg.type) return;
            switch (msg.type) {
                case 'appendPosts':
                    // 「自分の書き込み」集合を上書き (= 毎バッチ送られてくる)。先に Set を更新してから
                    // appendPosts を呼ぶことで、レンダ時に postDataFor が正しい isOwn を読める。
                    if (Array.isArray(msg.ownPostNumbers)) {
                        ownPostNumbers = new Set(msg.ownPostNumbers);
                    }
                    window.appendPosts(msg.posts, msg.scrollTarget, msg.markPostNumber, msg.incremental);
                    break;
                case 'updateOwnPosts':
                    // 自分マークの増分トグル (post-no メニュー → ToggleOwnPost 経由)。
                    // Set を更新したうえで該当レスの DOM バッジを add/remove する (= 全再レンダはしない)。
                    if (msg.value && Array.isArray(msg.value.changes)) {
                        applyOwnPostsChanges(msg.value.changes);
                    }
                    break;
                case 'setViewMode': window.setViewMode(msg.mode); break;
                case 'setPreview':  window.setPreviewPost(msg.post); break;
                case 'setFilter':
                    // C# 側 ThreadFilter record の JSON 反映。新条件は currentFilter のフィールドを
                    // 増やすだけで対応できる (= 同時に postMatchesFilter にも条件評価を追加する)。
                    currentFilter = {
                        textQuery:   typeof msg.textQuery === 'string' ? msg.textQuery : '',
                        popularOnly: msg.popularOnly === true,
                        mediaOnly:   msg.mediaOnly   === true,
                    };
                    applyFilterToAllPosts();
                    break;
                case 'setMarkPostNumber':
                    // 「以降新レス」ラベル位置の単独 push (= 新着 0 件 refresh で mark を null クリアする等)。
                    // 数値なら設定、それ以外 (= null) ならクリア。後者では updateNewPostsMarkBand が
                    // 既存ラベルを取り除く + scrollbar マーカーも消す。
                    // is-new (= sessionNewPostNumbers) は別管理なのでここでは触らない (= 太字状態は維持)。
                    markPostNumber = (typeof msg.value === 'number') ? msg.value : null;
                    debugLog('setMarkPostNumber recv: value=' + msg.value
                        + ', resolved markPostNumber=' + markPostNumber
                        + ', allPosts=' + allPosts.length
                        + ', viewMode=' + viewMode);
                    if (allPosts.length > 0) {
                        // 全ビューモードで「ラベル位置の差分更新だけ」を行う (= フル再描画はしない)。
                        // tree / dedupTree の dedup 状態 (= section A/B 分割) は次回ビューモード切替や
                        // フル再オープンでだけ更新する方針 (= ユーザのスクロール位置と視線を温存)。
                        updateNewPostsMarkBand();
                        updateMarkScrollbarMarker();
                    }
                    break;
                case 'setConfig':
                    // Phase 11: アプリ設定の即時反映
                    if (typeof msg.popularThreshold     === 'number') POPULAR_THRESHOLD     = msg.popularThreshold;
                    if (typeof msg.imageSizeThresholdMb === 'number') IMAGE_SIZE_THRESHOLD  = msg.imageSizeThresholdMb * 1024 * 1024;
                    if (typeof msg.idHighlightThreshold === 'number' && msg.idHighlightThreshold !== ID_HIGHLIGHT_THRESHOLD) {
                        ID_HIGHLIGHT_THRESHOLD = msg.idHighlightThreshold;
                        // しきい値変化で id-many クラスの付与判定が変わるので decoration を再計算
                        clearMetaDecorations();
                        decorateMeta();
                    }
                    // 既存スレ表示のスクロールバーは閾値が変わると赤マーカーの集合も変わるので再計算
                    if (typeof updateRichScrollbar === 'function') updateRichScrollbar();
                    break;
                // setShortcutBindings は shortcut-bridge.js 内で直接受信するためここでは扱わない。
                case 'imageMeta':
                    // C# が HEAD で取った Content-Length / キャッシュ参照 / 非同期 URL 展開の結果を返してきた。
                    //   noMedia=true → ソース (= ツイート等) に画像/動画が付いていないと確定。スロットを DOM ごと削除。
                    //   resolvedUrl  → 非同期展開 (x.com/pixiv) で求まった実体画像 URL (slot の data-src を上書き)
                    //   ok=false     → HEAD 失敗 (size 不明、applyMetaToSlot で「不明 → 読み込む」)
                    //   cached       → ローカルキャッシュにあるので帯域コスト 0、しきい値無視で即ロード
                    if (typeof msg.url === 'string') {
                        if (msg.noMedia === true) {
                            // 確定: 画像なし → スロット削除 + 「以後この URL のスロットは作らない」フラグをキャッシュ
                            // (= モード切替などで再描画されたときに毎回作って毎回消す無駄を避ける)。
                            imageMetaCache.set(msg.url, { noMedia: true });
                            removeMediaSlotsForUrl(msg.url);
                        } else {
                            applyImageMeta(msg.url, {
                                ok:          !!msg.ok,
                                size:        (typeof msg.size === 'number' ? msg.size : null),
                                cached:      !!msg.cached,
                                resolvedUrl: (typeof msg.resolvedUrl === 'string' ? msg.resolvedUrl : null),
                            });
                        }
                    }
                    break;
                case 'aiMetadata':
                    // 画像ホバー時に C# が PNG/JPEG/WebP のメタを抽出して返してきた。
                    // hasData=false なら次回以降ポップアップを出さない (no-data でキャッシュ)。
                    onAiMetadataResponse(msg);
                    break;
                case 'threadPreview':
                    // 5ch.io スレ URL ホバーで投げた threadPreviewRequest の応答 (タイトル + 対象レス本文)。
                    onThreadPreviewResponse(msg);
                    break;
                case 'scrollToPost': {
                    // ContextMenu 「このレスに飛ぶ」から呼ばれる。primary レス (id="rN") に scrollIntoView。
                    // ポップアップは即時閉じる (= 飛んだ先が popup に隠れないようにする)。
                    // 該当 primary レスが DOM に居ない (= フィルタで非表示 / NG / dedupTree で折り畳み等) 場合は no-op。
                    const target = document.getElementById('r' + msg.number);
                    closeFrom(0);
                    if (target) target.scrollIntoView({ block: 'start' });
                    break;
                }
                case 'setHiddenPosts': {
                    // C# 側で NG ルールが追加された直後に呼ばれる。指定番号のレスを即時 DOM から取り除いて
                    // 内部状態 (allPosts / postsByNumber / reverseIndex / バッジ / scrollbar) を最新化する。
                    if (Array.isArray(msg.numbers)) hidePostsByNumber(msg.numbers);
                    break;
                }
            }
        });
    }

    // ---------- ready signal ----------
    function notifyReady() {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'ready' });
        }
    }
    if (document.readyState === 'complete' || document.readyState === 'interactive') {
        notifyReady();
    } else {
        document.addEventListener('DOMContentLoaded', notifyReady);
    }
})();
