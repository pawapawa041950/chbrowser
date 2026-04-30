// ChBrowser thread view renderer.
// Public API exposed on window (C# → JS):
//   window.appendPosts(batch, scrollTarget?) — レスを末尾に追加。スレ表示の唯一の描画チャネル。
//                                              optional scrollTarget で初回スクロール位置を渡す。
//   window.setViewMode(mode)                 — 'flat' | 'tree' | 'dedupTree'
// 受信メッセージ:
//   { type: 'setConfig', popularThreshold?, imageSizeThresholdMb? } — Phase 11 設定の即時反映
// Messages sent to host (C#) via window.chrome.webview.postMessage:
//   { type: 'ready' }                       — JS が初期化完了したことを通知
//   { type: 'openUrl', url }                — 外部 URL クリック
//   { type: 'scrollPosition', postNumber }  — viewport 上端のレス番号 (debounced)
//   { type: 'paneActivated' }               — Phase 14: pane 内任意の mousedown (アドレスバー切替用)
//
// 各タブが専属 WebView2 を持つので、タブ切替で DOM が再構築されることはない。
// scroll target の同梱と tryScrollToTarget は「初回ロード時 (idx.json からの位置復元)」用。
// scrollY 状態は WebView2 自身が保持するので、scrollTo(0,0) や save 抑制は不要。

(function () {
    'use strict';

    // Phase 14: pane の任意のクリックで C# にアクティブ化通知 (= アドレスバー切替)。
    // capture phase に登録して内部 click ハンドラより先に拾う。
    document.addEventListener('mousedown', function() {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'paneActivated' });
        }
    }, true);

    let allPosts = [];
    let postsByNumber = new Map();
    /** num → 当該レスを >>参照しているレス番号配列。renderCurrentViewMode で全再構築、
     *  appendPosts (flat) で増分更新。返信数バッジ生成のために常に最新を保つ。 */
    let currentReverseIndex = new Map();
    let viewMode = 'flat';

    // スクロール対象レス番号。setPosts / appendPosts のメッセージから受け取り、
    // 対象レスが DOM に現れたタイミングで scrollIntoView してクリアする。
    let pendingScrollTarget = null;

    // 設定 (Phase 11) で動的変更可能。setConfig メッセージで上書きされる。
    let POPULAR_THRESHOLD = 3;

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
            date:           p.dateText || '',
            id:             p.id || '',
            body:           built.body,
            media:          built.media,
            replyCount:     count,
            replyNumbers:   replies.join(','),       // バッジの data-replies 用 (ホバーポップアップで使う)
            hasFewReplies:  count >= 1 && count < 3, // 1-2 件 → ピンク
            hasManyReplies: count >= 3,              // 3+ 件 → 赤
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
        html += '<span class="' + postNoCls + '">' + data.number + ': </span>';
        html += '<span class="post-reply-count" data-replies="' + escapeHtml(data.replyNumbers) + '">';
        if (data.replyCount > 0) html += '返信 ' + data.replyCount + ' 件';
        html += '</span>';
        html += '<span class="post-name">' + data.name + '</span>';
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

    /** レス番号 N → N を >>参照しているレス番号配列、を返す逆引き。 */
    function buildReverseIndex() {
        const rev = new Map();
        for (const post of allPosts) {
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
            const rendered = new Set();
            for (const p of allPosts) {
                if (rendered.has(p.number)) continue;
                html += buildDedupPostHtml(p, currentReverseIndex, rendered, false);
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

    window.addEventListener('scroll', scheduleSendScrollPosition, { passive: true });

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

    document.addEventListener('click', function (e) {
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

    function buildPopupContent(from, to) {
        const frag = document.createDocumentFragment();
        const span = Math.min(to - from + 1, MAX_RANGE);
        let any = false;
        for (let i = 0; i < span; i++) {
            const n = from + i;
            const src = document.getElementById('r' + n);
            if (src) {
                frag.appendChild(src.cloneNode(true));
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
                frag.appendChild(src.cloneNode(true));
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
    window.appendPosts = function (batch, scrollTarget) {
        if (!Array.isArray(batch) || batch.length === 0) return;
        const root = document.getElementById('posts');
        if (!root) return;

        // 同梱された scrollTarget があれば保留中ターゲットを更新
        if (typeof scrollTarget === 'number') pendingScrollTarget = scrollTarget;

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
    };

    window.setViewMode = function (mode) {
        const next = mode || 'flat';
        if (next === viewMode) return;
        viewMode = next;
        if (allPosts.length > 0) renderCurrentViewMode();
    };

    // ---------- C# からの postMessage 受信 (PostWebMessageAsJson 経由) ----------
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (e) {
            const msg = e.data;
            if (!msg || typeof msg !== 'object' || !msg.type) return;
            switch (msg.type) {
                case 'appendPosts': window.appendPosts(msg.posts, msg.scrollTarget); break;
                case 'setViewMode': window.setViewMode(msg.mode); break;
                case 'setConfig':
                    // Phase 11: アプリ設定の即時反映
                    if (typeof msg.popularThreshold     === 'number') POPULAR_THRESHOLD     = msg.popularThreshold;
                    if (typeof msg.imageSizeThresholdMb === 'number') IMAGE_SIZE_THRESHOLD  = msg.imageSizeThresholdMb * 1024 * 1024;
                    // 既存スレ表示のスクロールバーは閾値が変わると赤マーカーの集合も変わるので再計算
                    if (typeof updateRichScrollbar === 'function') updateRichScrollbar();
                    break;
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
