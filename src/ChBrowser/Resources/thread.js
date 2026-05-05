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

    // 重複なしツリーの「incremental セクション」境界 (Phase 20)。
    //   incrementalPivotIndex: allPosts のうち、最初の差分 append (= リフレッシュ後 / お気に入りチェック差分等) が
    //                          始まる index。null = まだ差分 append が来ていない (= 通常の dedup-tree)。
    //   一度 pivot が設定されたら、同じタブセッション中は固定 (= 後続の incremental 追加でも更新しない)。
    //   ピボット以降のレスは Section B (末尾の chain block) として描画され、祖先付きで「ここまで読んだ」帯の
    //   下に新着として表示される。
    let incrementalPivotIndex = null;

    // 「ここまで読んだ」帯 (Phase 19)。
    //   readMarkPostNumber: 帯を出すレス番号。スレを開いた最初の appendPosts 時に C# から渡される値で固定し、
    //                       以降はスクロールでもリフレッシュでも動かさない (= 次回オープン時に最新位置から表示される)。
    //                       null = 未設定で帯なし。
    //   readMarkInitialized: appendPosts で初回値を取り込み済みかどうか (リフレッシュで上書きされないよう一度きり)。
    //   readMarkSentMax   : C# に送った最深値。同じ値を再送しないための dedupe 用 (帯位置とは独立)。
    //   showReadMark      : 設定 ON/OFF。OFF でも値は記録され続け、ON にすれば再び帯が現れる。
    let readMarkPostNumber  = null;
    let readMarkInitialized = false;
    let readMarkSentMax     = null;
    let showReadMark        = true;

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
            return '<span class="image-slot deferred async" data-src="' + escapeHtml(href) +
                   '" data-url="' + escapeHtml(href) + '" data-async-expand="1"></span>';
        }
        return '';
    }

    /** 本文処理中にメディアスロットを集める buffer。null のときは収集しない (post-name など)。 */
    let _currentBodyMediaSlots = null;

    function renderExternalLink(href, visible) {
        const lower = (href || '').toLowerCase();
        if (lower.indexOf('http://') !== 0 && lower.indexOf('https://') !== 0) {
            return '<span class="link-disabled">' + escapeHtml(visible) + '</span>';
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
    // body 直下にインライン展開する数 / 子の最大数。
    const INLINE_EXPAND_RANGE_LIMIT = 5;
    const REVERSE_EXPAND_LIMIT = 10;

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
            hasFewReplies:  count >= 1 && count < 3, // 1-2 件 → ピンク
            hasManyReplies: count >= 3,              // 3+ 件 → 赤
            isOwn:          ownPostNumbers.has(num), // 「自分の書き込み」バッジ表示用
            isEmbedded:     !!isEmbedded,
            domId:          !omitId,
            children:       children || '',
        };
    }

    /** テンプレートに data を当てて 1 レス分の HTML を返す。テンプレ未読なら fallback。 */
    function renderPost(data) {
        _ensurePostTemplate();
        if (_postTemplateChunks && _postTemplateChunks.length > 0) {
            return _renderTemplate(_postTemplateChunks, data);
        }
        // 組み込み fallback: テーマ読込が失敗した時用に最低限の HTML を返す。
        // バッジ span は :empty を使った incremental 更新前提で、常に DOM に出す形にする。
        let html = '<div class="post' + (data.isEmbedded ? ' embedded' : '') + '"';
        if (data.domId) html += ' id="r' + data.number + '"';
        html += '><div class="post-header">';
        let postNoCls = 'post-no';
        if (data.hasManyReplies) postNoCls += ' has-replies-many';
        else if (data.hasFewReplies) postNoCls += ' has-replies-few';
        html += '<span class="' + postNoCls + '" data-number="' + data.number + '" role="button" tabindex="0">' + data.number + ': </span>';
        html += '<span class="post-reply-count" data-replies="' + escapeHtml(data.replyNumbers) + '">';
        if (data.replyCount > 0) html += '返信 ' + data.replyCount + ' 件';
        html += '</span>';
        if (data.isOwn) html += ' <span class="post-own">自分</span>';
        html += ' <span class="post-name">' + data.name + '</span>';
        if (data.mail) html += ' <span class="post-mail">[' + escapeHtml(data.mail) + ']</span>';
        html += '<span class="post-meta">  ' + escapeHtml(data.date);
        if (data.id) html += ' ID:' + escapeHtml(data.id);
        html += '</span>';
        html += '</div><div class="post-body">' + data.body + '</div>';
        html += '<div class="post-media">' + (data.media || '') + '</div>';
        html += data.children;
        html += '</div>';
        return html;
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
     *  共通祖先を持つ chain は同じノード配下に集約される (= 同じ親が複数回描画されないように)。 */
    function buildIncrementalForest(incrementalNumbers) {
        const rootMap = new Map(); // number -> node{number, childMap}
        for (const num of incrementalNumbers) {
            const chain = buildAncestorChain(num);
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

    /** ツリー (重複あり): トップレベルレス + 1 段の親 (>>X 先) + 1 段の子 (>>X 元) を直下に展開。 */
    function buildTreePostHtml(p, reverseIndex, isEmbedded) {
        let children = '';
        if (!isEmbedded) {
            // 親 (forward)
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
                    children += buildTreePostHtml(parent, reverseIndex, true);
                    children += '</div>';
                }
            }
            // 子 (reverse)
            const reverseChildren = reverseIndex.get(p.number) || [];
            const limit = Math.min(reverseChildren.length, REVERSE_EXPAND_LIMIT);
            for (let i = 0; i < limit; i++) {
                const child = postsByNumber.get(reverseChildren[i]);
                if (!child) continue;
                children += '<div class="inline-expansion reverse">';
                children += buildTreePostHtml(child, reverseIndex, true);
                children += '</div>';
            }
            if (reverseChildren.length > REVERSE_EXPAND_LIMIT) {
                children += '<div class="inline-expansion reverse-more">…他 ' +
                            (reverseChildren.length - REVERSE_EXPAND_LIMIT) + ' 件</div>';
            }
        }
        // 重複あり tree では埋め込みは id を付けない (DOM の id 重複を避けるため)。
        return renderPost(postDataFor(p, isEmbedded, /*omitId*/ isEmbedded, children));
    }

    /** ツリー (重複なし): 既描画レスは再描画しない。再帰的に親/子を展開。 */
    function buildDedupPostHtml(p, reverseIndex, rendered, isEmbedded) {
        if (rendered.has(p.number)) return '';
        rendered.add(p.number);

        // 親 (forward) — 再帰的、dedup-aware
        let children = '';
        const emitted = new Set();
        for (const r of extractAnchorRefs(p.body || '')) {
            if (r.to - r.from + 1 > INLINE_EXPAND_RANGE_LIMIT) continue;
            for (let n = r.from; n <= r.to; n++) {
                if (n >= p.number) continue;
                if (emitted.has(n)) continue;
                emitted.add(n);
                if (rendered.has(n)) continue;
                const parent = postsByNumber.get(n);
                if (!parent) continue;
                children += '<div class="inline-expansion forward">';
                children += buildDedupPostHtml(parent, reverseIndex, rendered, true);
                children += '</div>';
            }
        }

        // 子 (reverse) — 再帰的、dedup-aware
        const reverseChildren = reverseIndex.get(p.number) || [];
        let count = 0;
        for (const cn of reverseChildren) {
            if (count >= REVERSE_EXPAND_LIMIT) break;
            if (rendered.has(cn)) continue;
            const child = postsByNumber.get(cn);
            if (!child) continue;
            children += '<div class="inline-expansion reverse">';
            children += buildDedupPostHtml(child, reverseIndex, rendered, true);
            children += '</div>';
            count++;
        }

        // 重複なし tree では各レスは正確に 1 度しか出ないので id を常に付ける (アンカークリック解決のため)。
        return renderPost(postDataFor(p, isEmbedded, /*omitId*/ false, children));
    }

    // ---------- top-level renderer (viewMode に基づき分岐) ----------
    function renderCurrentViewMode() {
        const root = document.getElementById('posts');
        if (!root) return;

        // 返信数バッジ用の逆引きを描画前に再構築 (tree 系はそのまま流用)
        currentReverseIndex = buildReverseIndex();

        let html = '';
        if (viewMode === 'tree') {
            for (const p of allPosts) html += buildTreePostHtml(p, currentReverseIndex, false);
        } else if (viewMode === 'dedupTree') {
            // Phase 20: pivot 設定済 (= リフレッシュ等で差分 append が来た) なら 2 セクション構成。
            //   Section A: pre-pivot レスを通常の dedup tree で描画。reverseIndex も pre-pivot 限定にして
            //              incremental 分が祖先の子として埋め込まれてしまうのを防ぐ。
            //   Section B: post-pivot レス (= 新規取得分) を、各レスの祖先 chain ごと末尾に並べる。
            //              共通祖先は forest で merge。ancestor は embedded、incremental 自身は primary。
            if (incrementalPivotIndex == null || incrementalPivotIndex >= allPosts.length) {
                const rendered = new Set();
                for (const p of allPosts) {
                    if (rendered.has(p.number)) continue;
                    html += buildDedupPostHtml(p, currentReverseIndex, rendered, false);
                }
            } else {
                const sectionA = allPosts.slice(0, incrementalPivotIndex);
                const sectionB = allPosts.slice(incrementalPivotIndex);
                // Section A
                const reverseA = buildReverseIndexFrom(sectionA);
                const renderedA = new Set();
                for (const p of sectionA) {
                    if (renderedA.has(p.number)) continue;
                    html += buildDedupPostHtml(p, reverseA, renderedA, false);
                }
                // Section B
                const incNumbers = sectionB.map(p => p.number);
                const incSet     = new Set(incNumbers);
                const forest     = buildIncrementalForest(incNumbers);
                if (forest.size > 0) {
                    html += '<div class="incremental-section">';
                    for (const node of forest.values()) {
                        html += '<div class="incremental-block">';
                        html += renderIncrementalForestNode(node, incSet, /*isEmbedded*/ false);
                        html += '</div>';
                    }
                    html += '</div>';
                }
            }
        } else {
            for (const p of allPosts) html += buildPostHtml(p);
        }
        root.innerHTML = html;

        // 存在しないアンカーをグレーアウト
        document.querySelectorAll('a.anchor').forEach(function (a) {
            const from = parseInt(a.dataset.from, 10);
            if (!postsByNumber.has(from)) a.classList.add('missing');
        });

        attachAnchorHandlers(document, 0);
        observeImageSlots(document);
        tryScrollToTarget();
        updateRichScrollbar();
        updateReadUpToBand();
        updateReadMarkScrollbarMarker();
        markNewPosts();
        recomputeMetaMaps();
        decorateMeta();
    }

    /** スレを最初に開いた後の差分取得 (= incrementalPivotIndex 以降) で来たレスを「新規」として
     *  is-new クラスでマークする。post.css 側で .post.is-new .post-no を太字強調。
     *  primary instance (= id="rN" 付き) のみマークするので、tree モード重複表示の embedded 側は対象外
     *  (= ユーザの主視点である primary のレスでだけ強調が出ればよい)。
     *  classList.add は冪等なので renderCurrentViewMode + appendPosts flat の両方から呼んで OK。 */
    function markNewPosts() {
        if (incrementalPivotIndex == null) return;
        for (let i = incrementalPivotIndex; i < allPosts.length; i++) {
            const el = document.getElementById('r' + allPosts[i].number);
            if (el) el.classList.add('is-new');
        }
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
                    if (metaEl) wrapTextMatches(metaEl, /\bID(?=:)/, function (matched) {
                        const span = document.createElement('span');
                        span.className = 'id-link';
                        if (list.length >= ID_HIGHLIGHT_THRESHOLD) span.classList.add('id-many');
                        span.dataset.id = p.id;
                        span.dataset.idList = list.join(',');
                        span.textContent = matched;
                        return span;
                    }, /*onlyFirst*/ true);
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

    /** 全 .image-slot.deferred を IntersectionObserver で監視し、近接時に HEAD 要求を送る。 */
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
    }, { rootMargin: '300px 0px' });

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
        // 「画像取得失敗」プレースホルダにして以降ロードしない。
        const isAsyncSlot = slot.classList.contains('async');
        if (isAsyncSlot && !meta.resolvedUrl) {
            slot.classList.remove('deferred');
            slot.classList.add('expand-failed');
            const text = document.createElement('span');
            text.className = 'image-placeholder-text';
            text.textContent = '画像取得失敗';
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

    /** スロット内に <img> を生成して実際に画像を読み込む。 */
    function loadSlotImage(slot) {
        const url = slot.dataset.src;
        if (!url) return;
        slot.classList.remove('deferred', 'over-threshold');
        slot.classList.add('loaded');
        slot.innerHTML = '';
        const img = document.createElement('img');
        img.className = 'inline-image';
        img.loading   = 'lazy';
        img.alt       = '';
        img.src       = url;
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
    // tryScrollToTarget は対象レスが DOM に出現したらスクロールしてクリア。
    function tryScrollToTarget() {
        if (pendingScrollTarget == null) return;
        const el = document.getElementById('r' + pendingScrollTarget);
        if (!el) return; // まだ DOM に居ない、後続の append で再試行

        // 対象レスが viewport 内に少しでも見えていれば scroll しない (jolt 防止)。
        const rect = el.getBoundingClientRect();
        const vh = document.documentElement.clientHeight;
        const visible = rect.bottom > 0 && rect.top < vh;
        if (!visible) {
            el.scrollIntoView({ block: 'start' });
        }
        pendingScrollTarget = null;
    }

    // ---------- scroll position save (debounced) ----------
    // 各タブが専属 WebView2 なので、ユーザーがスクロールした時にだけイベントが発火する。
    // 過渡的な scrollY 値の汚染対策 (suppress) は不要。シンプルな debounce のみ。
    let scrollSaveTimer = null;
    let scrollSaveLastSent = null;
    const SCROLL_SAVE_DEBOUNCE_MS = 200;

    function findTopmostVisiblePostNumber() {
        const root = document.getElementById('posts');
        if (!root) return null;
        for (const post of root.children) {
            const id = post.id;
            if (!id || !id.startsWith('r')) continue;
            const rect = post.getBoundingClientRect();
            if (rect.bottom > 0) {
                return parseInt(id.slice(1), 10);
            }
        }
        return null;
    }

    function scheduleSendScrollPosition() {
        if (scrollSaveTimer != null) return; // 既に飛んでいる timer に統合
        scrollSaveTimer = setTimeout(function () {
            scrollSaveTimer = null;
            const num = findTopmostVisiblePostNumber();
            if (num == null) return;
            if (num === scrollSaveLastSent) return; // 変化なしは送らない
            scrollSaveLastSent = num;
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({ type: 'scrollPosition', postNumber: num });
            }
        }, SCROLL_SAVE_DEBOUNCE_MS);
    }

    // ---------- 「ここまで読んだ」帯 (Phase 19) ----------
    // 判定基準: primary レス (= #posts の直下子、id="rN") のうち
    //   rect.bottom <= window.innerHeight (= レス下端が viewport 下端以下)
    // を満たす最大番号。
    //   - 短文レス: 全体表示で即条件成立
    //   - 長文レス: 読み飛ばしてスクロールが下端を抜けたとき成立
    //   - 末尾レス: スレ最下端までスクロールしたとき成立
    // 値は減少しない (前回より大きいときだけ採用)。
    let readMarkSendTimer = null;
    function findDeepestReadMarkPostNumber() {
        const root = document.getElementById('posts');
        if (!root) return null;
        const vh   = window.innerHeight;
        let best   = null;
        for (const post of root.children) {
            const id = post.id;
            if (!id || !id.startsWith('r')) continue;
            const rect = post.getBoundingClientRect();
            if (rect.bottom <= vh) {
                const n = parseInt(id.slice(1), 10);
                if (best == null || n > best) best = n;
            }
        }
        return best;
    }
    function scheduleSendReadMark() {
        if (readMarkSendTimer != null) return;
        readMarkSendTimer = setTimeout(function () {
            readMarkSendTimer = null;
            const n = findDeepestReadMarkPostNumber();
            if (n == null) return;
            // dedupe: 同じ値や過去最大以下は C# に送らない。永続化された値は次回オープン時に取り込まれる。
            if (readMarkSentMax != null && n <= readMarkSentMax) return;
            readMarkSentMax = n;
            // ※ 現セッション中は帯を動かさない (= readMarkPostNumber / 帯 DOM はそのまま)。
            //   次回スレを開いた瞬間に C# から最新の readMarkPostNumber が来て、新しい位置に表示される。
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({ type: 'readMark', postNumber: n });
            }
        }, SCROLL_SAVE_DEBOUNCE_MS);
    }

    /** 現在の readMarkPostNumber と DOM 状態を見て、ここまで読んだ帯を再配置する。
     *  対象レスが DOM に居ない (= NG / view mode で primary でない) 場合は、
     *  「番号がそれ以上の primary レスのうち最初のもの」の後ろに帯を挿入する。 */
    function updateReadUpToBand() {
        // 既存帯を一旦取り除く (= 配置場所の更新も再挿入で行う)
        const existing = document.getElementById('read-up-to-band');
        if (existing && existing.parentNode) existing.parentNode.removeChild(existing);

        if (readMarkPostNumber == null) return;

        const root = document.getElementById('posts');
        if (!root) return;

        // 対象レス、無ければ番号 >= readMark の最初の primary レスを探す
        let target = document.getElementById('r' + readMarkPostNumber);
        if (!target || target.parentNode !== root) {
            target = null;
            for (const post of root.children) {
                const id = post.id;
                if (!id || !id.startsWith('r')) continue;
                const n = parseInt(id.slice(1), 10);
                if (n >= readMarkPostNumber) { target = post; break; }
            }
        }
        if (!target) return; // 帯を出す場所がない (該当以降に primary レスが無い)

        const band = document.createElement('div');
        band.id        = 'read-up-to-band';
        band.className = 'read-up-to-band';
        target.parentNode.insertBefore(band, target.nextSibling);
    }

    /** リッチスクロールバーの readmark トラックに帯マーカーを描画。
     *  対象レスの DOM 上の縦位置 (= scrollHeight に対する比率) に細い横線を出すだけ。 */
    function updateReadMarkScrollbarMarker() {
        const sb = document.getElementById('richScrollbar');
        if (!sb) return;
        const track = sb.querySelector('.track-readmark');
        if (!track) return;
        track.innerHTML = '';
        if (readMarkPostNumber == null) return;
        const scrollHeight = document.documentElement.scrollHeight;
        if (!scrollHeight) return;
        // 帯か対象レス (帯が無ければレス本体) の Y 座標で位置を決める
        const band = document.getElementById('read-up-to-band');
        const ref  = band || document.getElementById('r' + readMarkPostNumber);
        if (!ref) return;
        const m = document.createElement('div');
        m.className = 'marker';
        m.style.top = ((ref.offsetTop + ref.offsetHeight) / scrollHeight * 100) + '%';
        track.appendChild(m);
    }

    window.addEventListener('scroll', scheduleSendScrollPosition, { passive: true });
    window.addEventListener('scroll', scheduleSendReadMark,       { passive: true });

    // ---------- click handling (anchor scroll / external URL / inline image) ----------
    // 注意: スレ表示内の <a> は href 属性を持たず非 focusable にしている (Chromium が
    // 「先頭の focusable」へ auto scroll する経路を断つため)。よってクリック判定は
    // dataset (data-url, data-from) ベース。
    function postOpenUrl(url) {
        if (!url) return;
        if (url.indexOf('http://') !== 0 && url.indexOf('https://') !== 0) return;
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

    // ---------- post-no クリックメニュー (返信 / NG登録) ----------
    // .post-no をクリックするとメニューを出し、選択でメッセージを C# に送る。
    //   返信         → { type:'replyToPost', number }    : C# が「>>N」入りで投稿ダイアログを開く
    //   NG: 名前/ID/ワッチョイ → { type:'ngAdd', target, value, number } : C# が NG 登録ダイアログを開く
    // メニューは body 直下の単一 <div class="post-no-menu"> として管理。再表示時は前のを閉じる。
    let postNoMenuEl = null;

    function closePostNoMenu() {
        if (postNoMenuEl && postNoMenuEl.parentNode) postNoMenuEl.parentNode.removeChild(postNoMenuEl);
        postNoMenuEl = null;
    }

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
    }

    function showPostNoMenu(postNo, anchorEl) {
        closePostNoMenu();
        const post = postsByNumber.get(postNo);
        if (!post) return;

        const nameVal     = plainName(post.name);
        const idVal       = post.id || '';
        const watchoiVal  = extractWatchoiFromName(post.name);

        const menu = document.createElement('div');
        menu.className = 'post-no-menu';

        // クリックで C# にメッセージを送るアクション項目
        function addItem(label, action, payloadExtras, disabled, extraClass) {
            const it = document.createElement('div');
            it.className = 'menu-item' + (disabled ? ' disabled' : '') + (extraClass ? ' ' + extraClass : '');
            it.textContent = label;
            if (!disabled) {
                it.addEventListener('mousedown', function(ev) {
                    ev.preventDefault();
                    ev.stopPropagation();
                    closePostNoMenu();
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage(Object.assign(
                            { type: action, number: postNo }, payloadExtras || {}));
                    }
                });
            }
            return menu.appendChild(it);
        }

        // 返信
        addItem('返信', 'replyToPost', null, false);

        // 自分の書き込みトグル — 現在の状態によって label が反転、isOwn=新状態 を C# に送る。
        const isCurrentlyOwn = ownPostNumbers.has(postNo);
        addItem(isCurrentlyOwn ? '自分の書き込み解除' : '自分の書き込みに登録',
                'toggleOwnPost', { isOwn: !isCurrentlyOwn }, false);

        // NG 親項目 — クリックで子項目 (名前/ID/ワッチョイ) をインライン展開する。
        // 親クリック自体は C# にメッセージを送らない (= toggle のみ)。
        const ngParent = document.createElement('div');
        ngParent.className = 'menu-item menu-parent';
        const arrow = document.createElement('span');
        arrow.className = 'menu-arrow';
        arrow.textContent = '▶';   // ▶ (折り畳まれている時)
        const ngLabel = document.createElement('span');
        ngLabel.textContent = 'NG';
        ngParent.appendChild(ngLabel);
        ngParent.appendChild(arrow);
        menu.appendChild(ngParent);

        // 子項目コンテナ — display: none で初期は隠す。
        const ngChildren = document.createElement('div');
        ngChildren.className = 'menu-children';
        ngChildren.style.display = 'none';
        menu.appendChild(ngChildren);

        function addNgChild(label, target, value) {
            const disabled = !value;
            const it = document.createElement('div');
            it.className = 'menu-item menu-child' + (disabled ? ' disabled' : '');
            it.textContent = label;
            if (!disabled) {
                it.addEventListener('mousedown', function(ev) {
                    ev.preventDefault();
                    ev.stopPropagation();
                    closePostNoMenu();
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({
                            type: 'ngAdd', number: postNo, target: target, value: value,
                        });
                    }
                });
            }
            ngChildren.appendChild(it);
        }
        addNgChild('名前 — '       + (nameVal    || '(空)'),  'name',    nameVal);
        addNgChild('ID — '         + (idVal      || '(空)'),  'id',      idVal);
        addNgChild('ワッチョイ — ' + (watchoiVal || '(なし)'), 'watchoi', watchoiVal);

        ngParent.addEventListener('mousedown', function(ev) {
            ev.preventDefault();
            ev.stopPropagation();
            const open = ngChildren.style.display !== 'none';
            ngChildren.style.display = open ? 'none' : 'block';
            arrow.textContent       = open ? '▶' : '▼';   // ▶ / ▼
            // 子展開でメニューが伸びると画面下を超える可能性があるので再配置
            repositionPostNoMenu(menu, anchorEl);
        });

        // 仮で body に追加してサイズを確定させてから配置
        document.body.appendChild(menu);
        repositionPostNoMenu(menu, anchorEl);
        postNoMenuEl = menu;
    }

    /** post-no メニューを anchor の周りに配置する。
     *  既定は anchor の真下、画面下を超えるなら anchor の上にフリップ。
     *  画面右端を超えるなら左にスライド。 */
    function repositionPostNoMenu(menu, anchorEl) {
        const r       = anchorEl.getBoundingClientRect();
        const menuW   = menu.offsetWidth;
        const menuH   = menu.offsetHeight;
        const docW    = document.documentElement.clientWidth;
        const vpTop   = window.scrollY;
        const vpBot   = window.scrollY + window.innerHeight;

        // 横: anchor の左揃え、右端を超えるなら左へスライド
        let x = r.left + window.scrollX;
        if (x + menuW > docW + window.scrollX) x = docW + window.scrollX - menuW - 4;
        if (x < window.scrollX) x = window.scrollX + 4;

        // 縦: 下に置けるなら下、ダメなら上にフリップ
        let y = r.bottom + window.scrollY + 2;
        if (y + menuH > vpBot) {
            const above = r.top + window.scrollY - menuH - 2;
            // 上にも収まらないなら viewport 上端に貼り付け
            y = (above >= vpTop) ? above : vpTop + 4;
        }

        menu.style.left = x + 'px';
        menu.style.top  = y + 'px';
    }

    // 外側クリック / ESC でメニュー閉じる
    document.addEventListener('mousedown', function(e) {
        if (!postNoMenuEl) return;
        if (e.target.closest && e.target.closest('.post-no-menu')) return;
        closePostNoMenu();
    }, true);
    document.addEventListener('keydown', function(e) {
        if (e.key === 'Escape') closePostNoMenu();
    });

    document.addEventListener('click', function (e) {
        // .post-no クリック → メニュー表示。anchor/URL ロジックより先に拾って早期 return。
        const postNo = e.target.closest && e.target.closest('.post-no');
        if (postNo && postNo.dataset && postNo.dataset.number) {
            e.preventDefault();
            e.stopPropagation();
            const n = parseInt(postNo.dataset.number, 10);
            if (!isNaN(n)) showPostNoMenu(n, postNo);
            return;
        }

        // メディアスロットのクリックを mediaType ごとに分岐:
        //   image    → .loaded なら URL を開く / それ以外は強制 <img> 化
        //   video    → 常に <video controls autoplay> に置換 (インライン再生)
        //   youtube  → デフォルトブラウザで開く (インライン iframe は YouTube の embed 制限で
        //              Error 153 が出るため、サムネ表示のみに留める方針)
        const slot = e.target.closest && e.target.closest('.image-slot');
        if (slot) {
            if (slot.classList.contains('expand-failed') || slot.classList.contains('playing')) {
                return; // 失敗済み / 再生中は何もしない (再生中は内部要素が click を消費)
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
                // Phase 10: ロード済みなら外部ブラウザではなくビューアウィンドウに送る
                if (slot.dataset.url) postOpenInViewer(slot.dataset.url);
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
     *  バッジや子レスは元レス側にはそのまま残るので、通常スレ表示でのホバー / ツリー表示は引き続き機能する。 */
    function clonePostForPopup(src) {
        const clone = src.cloneNode(true);
        clone.querySelectorAll('.post-reply-count').forEach(function (el) { el.remove(); });
        clone.querySelectorAll('.post.embedded').forEach(function (el) { el.remove(); });
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

    /** 返信件数バッジ (.post-reply-count[data-replies]) をホバーした時に開くポップアップ。
     *  data-replies のカンマ区切りレス番号を順にレンダリングする。 */
    function openReplyPopup(badge, level) {
        closeFrom(level);
        const list = (badge.dataset.replies || '')
            .split(',')
            .map(function (s) { return parseInt(s, 10); })
            .filter(function (n) { return !isNaN(n); });
        if (list.length === 0) return;
        const el = document.createElement('div');
        el.className = 'anchor-popup';
        el.appendChild(buildPopupContentFromList(list));
        document.body.appendChild(el);
        positionPopup(el, badge);
        el.addEventListener('mouseenter', function () { cancelCloseAtOrBelow(level); });
        el.addEventListener('mouseleave', function () { scheduleCloseAt(level); });
        attachAnchorHandlers(el, level + 1);
        popups.push({ el: el, anchor: badge, level: level });
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
        // 返信件数バッジ → 返信元レスをポップアップ
        root.querySelectorAll('.post-reply-count[data-replies]').forEach(function (el) {
            el.addEventListener('mouseenter', function () {
                cancelCloseAt(level);
                openReplyPopup(el, level);
            });
            el.addEventListener('mouseleave', function () { scheduleCloseAt(level); });
        });
    }

    /** 既存レス DOM の返信数バッジ + post-no の色クラスを差分更新する (flat モード appendPosts 用)。
     *  テンプレで <span class="post-reply-count" data-replies=""> は常に出ている前提で、
     *  textContent と data-replies を書き換えるだけ。位置が動かないのでテンプレで指定した位置を保てる。
     *  ホバーリスナは初期描画時に attachAnchorHandlers が data-replies 属性ベースで取り付けているので
     *  この関数では触らない。 */
    function updateReplyCountBadge(num) {
        const post = document.getElementById('r' + num);
        if (!post) return;
        const header = post.querySelector(':scope > .post-header');
        if (!header) return;

        const replies = currentReverseIndex.get(num) || [];
        const count = replies.length;

        // 1) post-no の色クラス (1-2 件 → ピンク、3+ 件 → 赤)
        const postNo = header.querySelector(':scope > .post-no');
        if (postNo) {
            postNo.classList.remove('has-replies-few', 'has-replies-many');
            if (count >= 3)      postNo.classList.add('has-replies-many');
            else if (count >= 1) postNo.classList.add('has-replies-few');
        }

        // 2) バッジの中身と data-replies を更新 (CSS の :empty で 0 件時は非表示になる)。
        //    テンプレが post-reply-count を出していないテーマでは何もしない (互換)。
        const badge = header.querySelector(':scope > .post-reply-count');
        if (!badge) return;
        badge.textContent = count > 0 ? '返信 ' + count + ' 件' : '';
        badge.dataset.replies = replies.join(',');
    }

    // ---------- public API ----------
    /** streaming で逐次追加。flat モードは既存 DOM 末尾に増分追加、tree 系はフル再描画。
     *  スレ表示の唯一の描画 API。「全置換」は使わず、各 WebView2 はライフタイム中ずっと
     *  この関数だけで posts が積み上がる前提で動作する (旧 setPosts チャネルは撤去)。 */
    window.appendPosts = function (batch, scrollTarget, readMark, incremental) {
        if (!Array.isArray(batch) || batch.length === 0) return;
        const root = document.getElementById('posts');
        if (!root) return;

        // 同梱された scrollTarget があれば保留中ターゲットを更新
        if (typeof scrollTarget === 'number') pendingScrollTarget = scrollTarget;
        // 「ここまで読んだ」帯の対象 (Phase 19) — 初回 appendPosts のみ採用 (= リフレッシュでは動かさない)。
        if (!readMarkInitialized) {
            if (typeof readMark === 'number') readMarkPostNumber = readMark;
            readMarkInitialized = true;
        }

        // 重複なしツリー用 incremental pivot (Phase 20) — 初めて incremental=true が来たとき
        // この時点での allPosts.length を pivot として固定する。以降は pivot は動かさない。
        if (incremental === true && incrementalPivotIndex == null) {
            incrementalPivotIndex = allPosts.length;
        }

        // 内部状態は常に同じ更新
        for (const p of batch) {
            allPosts.push(p);
            postsByNumber.set(p.number, p);
        }

        if (viewMode !== 'flat') {
            // 親/子のネスト関係が変わるので、一括再描画する。
            renderCurrentViewMode();
            return;
        }

        // ---- flat モード: 末尾に追記するだけの高速経路 ----
        let html = '';
        for (const p of batch) html += buildPostHtml(p);
        const tmp = document.createElement('div');
        tmp.innerHTML = html;

        tmp.querySelectorAll('a.anchor').forEach(function (a) {
            const from = parseInt(a.dataset.from, 10);
            if (!postsByNumber.has(from)) a.classList.add('missing');
        });
        attachAnchorHandlers(tmp, 0);

        while (tmp.firstChild) root.appendChild(tmp.firstChild);

        document.querySelectorAll('a.anchor.missing').forEach(function (a) {
            const from = parseInt(a.dataset.from, 10);
            if (postsByNumber.has(from)) {
                a.classList.remove('missing');
                a.addEventListener('mouseenter', function () { cancelCloseAt(0); openPopup(a, 0); });
                a.addEventListener('mouseleave', function () { scheduleCloseAt(0); });
            }
        });

        // 増分追加された各レスのアンカー先 (既存レス) に対し、currentReverseIndex を +1 して
        // バッジ表示を差分更新する (新規レス自身は誰からも参照されていないので 0 件のまま)。
        for (const p of batch) {
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
                    updateReplyCountBadge(n);
                }
            }
        }

        observeImageSlots(root);
        tryScrollToTarget();
        updateRichScrollbar();
        updateReadUpToBand();
        updateReadMarkScrollbarMarker();
        markNewPosts();
        // 増分追加で同 ID/ワッチョイの件数が変わるので、既存装飾を破棄して全体を再装飾する。
        // (新規装飾だけだと、既に decoration 済の post も "5 件超え → 赤化" 等のしきい値変化に追従できない)
        clearMetaDecorations(root);
        recomputeMetaMaps();
        decorateMeta(root);
    };

    window.setViewMode = function (mode) {
        const next = mode || 'flat';
        if (next === viewMode) return;
        viewMode = next;
        if (allPosts.length > 0) renderCurrentViewMode();
    };

    /**
     * 書き込みダイアログのプレビュー専用エントリ。スレ表示シェル全体を流用しつつ、
     * 唯一の post として渡された 1 件を「全リセット → 単発 append」で再描画する。
     * post 形式は appendPosts と同じ ({number, name, mail, dateText, id, body, threadTitle?})。
     * preview 用にスクロール / readMark / 差分管理は無効化、body に preview-mode class を付与して
     * CSS でリッチスクロールバー等を非表示にする。
     */
    window.setPreviewPost = function (post) {
        // body に preview class を付ける (= CSS で richScrollbar / 右 padding を消す目印)
        document.body.classList.add('preview-mode');

        // 内部状態を全クリア (allPosts / 重複ツリー pivot / scrollTarget / readMark / 逆引き indexes)
        allPosts                = [];
        postsByNumber           = new Map();
        currentReverseIndex     = new Map();
        incrementalPivotIndex   = null;
        pendingScrollTarget     = null;
        readMarkInitialized     = true;   // 初回 readMark を採用しないようにフラグだけ立てる
        readMarkPostNumber      = null;

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
                    window.appendPosts(msg.posts, msg.scrollTarget, msg.readMarkPostNumber, msg.incremental);
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
                case 'setConfig':
                    // Phase 11: アプリ設定の即時反映
                    if (typeof msg.popularThreshold     === 'number') POPULAR_THRESHOLD     = msg.popularThreshold;
                    if (typeof msg.imageSizeThresholdMb === 'number') IMAGE_SIZE_THRESHOLD  = msg.imageSizeThresholdMb * 1024 * 1024;
                    if (typeof msg.showReadMark         === 'boolean') {
                        showReadMark = msg.showReadMark;
                        document.body.classList.toggle('no-read-mark', !showReadMark);
                    }
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
                    //   resolvedUrl → 非同期展開 (x.com/pixiv) で求まった実体画像 URL (slot の data-src を上書き)
                    //   ok=false    → HEAD 失敗 (size 不明、applyMetaToSlot で「不明 → 読み込む」)
                    //   cached      → ローカルキャッシュにあるので帯域コスト 0、しきい値無視で即ロード
                    if (typeof msg.url === 'string') {
                        applyImageMeta(msg.url, {
                            ok:          !!msg.ok,
                            size:        (typeof msg.size === 'number' ? msg.size : null),
                            cached:      !!msg.cached,
                            resolvedUrl: (typeof msg.resolvedUrl === 'string' ? msg.resolvedUrl : null),
                        });
                    }
                    break;
                case 'aiMetadata':
                    // 画像ホバー時に C# が PNG/JPEG/WebP のメタを抽出して返してきた。
                    // hasData=false なら次回以降ポップアップを出さない (no-data でキャッシュ)。
                    onAiMetadataResponse(msg);
                    break;
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
