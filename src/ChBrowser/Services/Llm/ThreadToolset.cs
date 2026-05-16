using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;

namespace ChBrowser.Services.Llm;

/// <summary>
/// AI チャットで LLM が呼び出せるツール群を束ねる。
///
/// LLM に渡すツール定義 (= JSON Schema 配列) は <see cref="GetToolDefinitions"/>、
/// 実行は名前ベースでディスパッチする <see cref="ExecuteAsync"/> で行う。各ツールは引数 JSON 文字列を受け取り、
/// JSON 文字列の結果を返す (= LLM が再度入力として食えるよう、文字列で完結する)。
///
/// <para><b>2 つのスコープ</b>:
/// <list type="bullet">
///   <item><description><b>attached 文脈</b>: チャット開始時に「いま見てるスレ」のスナップショットを抱える (任意)。
///         状態系 (既読位置 / 自分のレス / 新着) はここからのみ取れる。</description></item>
///   <item><description><b>cross-thread / cross-board</b>: <see cref="ThreadDataLoader"/> 経由で他スレ / 他板の
///         情報を取りに行ける。スレ読み取り系ツールは <c>thread_url</c> 引数を取り、省略時は attached を使う。</description></item>
/// </list>
/// attached 無し (= スタンドアロンチャット) でも cross-thread ツール群はそのまま動く。
/// 状態系ツールは attached 必須 (= 無ければエラー JSON)。</para>
///
/// <para><b>スナップショット方針</b>: attached の Post 列はコンストラクタでコピーして保持し、以降の
/// スレ追従更新は反映しない。会話中にレス数や本文が動くと、LLM が一度ツール経由で取得した文脈と
/// 食い違うため。古さは <c>get_thread_meta</c> の <c>total_posts</c> で読み取れる。</para>
/// </summary>
public sealed class ThreadToolset
{
    /// <summary>1 度の <c>get_posts</c> で返す最大件数。これを超える要求はエラーにする
    /// (= LLM に「分割して呼んで」と伝えるため)。</summary>
    private const int MaxPostsPerCall = 50;
    /// <summary>1 度の <c>search_posts</c> で返す最大ヒット数。LLM のコンテキスト消費を抑える。</summary>
    private const int DefaultSearchLimit = 20;
    private const int MaxSearchLimit     = 100;
    /// <summary>search_posts の snippet 長 (= 文字数)。長すぎると 1 検索でコンテキストを食い切る。</summary>
    private const int SearchSnippetMax = 240;

    /// <summary>find_replies_to の既定 / 上限件数。</summary>
    private const int DefaultRepliesLimit = 30;
    private const int MaxRepliesLimit     = 100;

    /// <summary>find_popular_posts の既定 / 上限件数。</summary>
    private const int DefaultPopularTopK = 10;
    private const int MaxPopularTopK     = 50;
    /// <summary>popular の snippet 文字数。</summary>
    private const int PopularSnippetMax = 160;

    /// <summary>list_threads / list_boards の既定 / 上限件数。</summary>
    private const int DefaultListLimit = 30;
    /// <summary>上限件数。5ch の板総数 (= 1100 程度) を 1 度で返せるよう余裕を持たせる。</summary>
    private const int MaxListLimit     = 2000;
    /// <summary>list_threads で keyword 省略時 (= scan モード) の既定件数。
    /// 板の上位 100 スレぐらいを取って、AI がタイトルから関連を判定する用途。</summary>
    private const int ThreadsScanDefaultLimit = 100;
    /// <summary>list_boards で keyword 省略時 (= scan モード) の既定件数。
    /// 5ch の全板 (~1100) を 1 ショットで返して、AI がカテゴリ単位で関連板を pick できるようにする。</summary>
    private const int BoardsScanDefaultLimit = 1500;

    /// <summary><c>&lt;a href=...&gt;text&lt;/a&gt;</c> を可視テキストだけにする (= dat 中のリンクは
    /// LLM にとっては中身のテキストだけが意味を持つ)。</summary>
    private static readonly Regex AnchorTagRe = new(@"<a\s[^>]*>([^<]*)</a>", RegexOptions.Compiled);
    /// <summary>残ったあらゆる HTML タグ (= レアケース)。タグ自体を除去するが中身は残す。</summary>
    private static readonly Regex AnyTagRe = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>本文中のレス参照アンカー (<c>&gt;&gt;N</c> / <c>&gt;&gt;N-M</c> / <c>&gt;&gt;N,M</c>)。
    /// <c>&amp;gt;&amp;gt;</c> も CleanBody 通過後は <c>&gt;&gt;</c> に正規化されている前提。
    /// 全体マッチは <c>&gt;&gt;</c> + 数字列 (数字 / ハイフン / カンマ) 列 で受ける。</summary>
    private static readonly Regex AnchorRefRe = new(@">>(\d+(?:[\-,]\d+)*)", RegexOptions.Compiled);

    private readonly ThreadDataLoader              _dataLoader;
    private readonly Func<string, Task<string>>    _openThreadInAppAsync;
    private readonly Func<string, Task<string>>    _openBoardInAppAsync;
    private readonly Func<string, IReadOnlyList<AiSearchResultEntry>, Task<string>> _openThreadListInAppAsync;

    /// <summary>会話開始時の attached スレッド (任意)。<c>thread_url</c> 省略時のデフォルト先で、状態系ツールの唯一のソース。</summary>
    private readonly ThreadContext? _attached;

    /// <summary>attached スレの「現在のレス数」(= プロンプト構築側で参照する)。attached 無しなら 0。</summary>
    public int PostCount => _attached?.Posts.Count ?? 0;

    /// <summary>attached スレのタイトル (= プロンプト構築側で参照する)。attached 無しなら空文字。</summary>
    public string ThreadTitle => _attached?.Title ?? "";

    /// <summary>attached スレの板名 (= プロンプト構築側で参照する)。attached 無しなら空文字。</summary>
    public string BoardName => _attached?.BoardName ?? "";

    /// <summary>attached スレが有るかどうか。BuildAiSystemPrompt がモード分岐に使う。</summary>
    public bool HasAttached => _attached is not null;

    public ThreadToolset(
        ThreadDataLoader              dataLoader,
        Func<string, Task<string>>    openThreadInAppAsync,
        Func<string, Task<string>>    openBoardInAppAsync,
        Func<string, IReadOnlyList<AiSearchResultEntry>, Task<string>> openThreadListInAppAsync,
        Board?                        attachedBoard           = null,
        string?                       attachedThreadKey       = null,
        string?                       attachedTitle           = null,
        IReadOnlyList<Post>?          attachedPosts           = null,
        int?                          attachedLastRead        = null,
        int?                          attachedMarkPostNumber  = null,
        IEnumerable<int>?             attachedOwnPostNumbers  = null,
        bool                          attachedHasReplyToOwn   = false)
    {
        _dataLoader               = dataLoader;
        _openThreadInAppAsync     = openThreadInAppAsync;
        _openBoardInAppAsync      = openBoardInAppAsync;
        _openThreadListInAppAsync = openThreadListInAppAsync;

        if (attachedBoard is not null && attachedThreadKey is not null && attachedPosts is not null)
        {
            var posts = attachedPosts.ToArray();
            _attached = new ThreadContext(
                Board:              attachedBoard,
                ThreadKey:          attachedThreadKey,
                Title:              attachedTitle ?? "",
                BoardName:          attachedBoard.BoardName ?? attachedBoard.DirectoryName,
                Posts:              posts,
                InboundAnchors:     BuildInboundAnchorIndex(posts),
                LastReadPostNumber: attachedLastRead,
                MarkPostNumber:     attachedMarkPostNumber,
                OwnPostNumbers:     attachedOwnPostNumbers is null ? new HashSet<int>() : new HashSet<int>(attachedOwnPostNumbers),
                HasReplyToOwn:      attachedHasReplyToOwn);
        }
    }

    /// <summary>会話中のスレ 1 件分の文脈。attached 用と、cross-thread で動的ロードしたスレ用の両方で使う。
    /// 状態系 (LastRead / Mark / Own / HasReplyToOwn) は attached でしか有効値を持たない。</summary>
    private sealed record ThreadContext(
        Board                       Board,
        string                      ThreadKey,
        string                      Title,
        string                      BoardName,
        IReadOnlyList<Post>         Posts,
        Dictionary<int, List<int>>  InboundAnchors,
        int?                        LastReadPostNumber,
        int?                        MarkPostNumber,
        IReadOnlySet<int>           OwnPostNumbers,
        bool                        HasReplyToOwn);

    /// <summary>本文を 1 度ずつ走査して被アンカーマップ (target -> from[]) を作る。
    /// アンカー範囲 (>>N-M) は両端含めて展開、リスト (>>N,M) は個別にカウント。</summary>
    private static Dictionary<int, List<int>> BuildInboundAnchorIndex(IReadOnlyList<Post> posts)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (var p in posts)
        {
            var body = CleanBody(p.Body);
            foreach (var target in ExtractAnchorTargets(body))
            {
                if (target <= 0 || target == p.Number) continue;
                if (!map.TryGetValue(target, out var list))
                {
                    list = new List<int>();
                    map[target] = list;
                }
                // 同一レス内に >>N と >>N が複数あっても 1 票扱い。
                if (list.Count == 0 || list[^1] != p.Number) list.Add(p.Number);
            }
        }
        return map;
    }

    /// <summary>本文中のアンカー記法から参照先レス番号を全列挙する。
    /// <c>&gt;&gt;5</c>, <c>&gt;&gt;5-7</c> (=5,6,7), <c>&gt;&gt;5,7,9</c> (=5,7,9) の混在に対応。
    /// 範囲が異常に広い場合は安全のため 50/100 件で打ち切る。</summary>
    private static IEnumerable<int> ExtractAnchorTargets(string body)
    {
        if (string.IsNullOrEmpty(body)) yield break;
        foreach (Match m in AnchorRefRe.Matches(body))
        {
            var token = m.Groups[1].Value;
            int emitted = 0;
            foreach (var sub in token.Split(','))
            {
                var dash = sub.IndexOf('-');
                if (dash < 0)
                {
                    if (int.TryParse(sub, out var n))
                    {
                        yield return n;
                        if (++emitted >= 100) yield break;
                    }
                }
                else
                {
                    if (int.TryParse(sub[..dash], out var a) &&
                        int.TryParse(sub[(dash + 1)..], out var b))
                    {
                        var lo = Math.Min(a, b);
                        var hi = Math.Max(a, b);
                        var capped = Math.Min(hi, lo + 50);
                        for (int n = lo; n <= capped; n++)
                        {
                            yield return n;
                            if (++emitted >= 100) yield break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>OpenAI 互換 <c>tools</c> パラメータに渡す配列を返す。</summary>
    public IReadOnlyList<object> GetToolDefinitions()
    {
        return new object[]
        {
            // ---- スレ読み取り系 (thread_url 省略時は attached を使う) ----
            new
            {
                type     = "function",
                function = new
                {
                    name        = "get_thread_meta",
                    description = "スレッドのメタ情報 (タイトル / 板 / 総レス数) と >>1 (オリジナルポスト) の全文を返す。" +
                                  "thread_url 省略時は attached スレ。会話開始時にまず呼ぶのが推奨。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            thread_url = ThreadUrlParam(),
                        },
                        required = Array.Empty<string>(),
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "get_thread_state",
                    description = "attached スレッドの状態スナップショット (= 既読位置 / 新着の範囲 / 自分のレス番号 / 自分宛て返信フラグ) を返す。" +
                                  "attached が無い (= スタンドアロンチャット) 場合はエラー。状態は会話開始時点のものなので通常はシステムプロンプトで既に見られている。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new { },
                        required   = Array.Empty<string>(),
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "get_posts",
                    description = $"指定範囲のレスを取得する (1 始まり、両端含む)。1 度に最大 {MaxPostsPerCall} 件まで。" +
                                  "thread_url 省略時は attached スレ。広い範囲を読みたい場合は何度かに分けて呼ぶこと。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            start      = new { type = "integer", description = "開始レス番号 (1 始まり、含む)" },
                            end        = new { type = "integer", description = "終了レス番号 (含む)" },
                            thread_url = ThreadUrlParam(),
                        },
                        required = new[] { "start", "end" },
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "search_posts",
                    description = "本文中にキーワードを含むレスを検索し、ヒットしたレス番号と短い抜粋を返す。大小文字無視。" +
                                  "thread_url 省略時は attached スレ。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            keyword    = new { type = "string",  description = "検索したい部分文字列" },
                            limit      = new { type = "integer", description = $"返す最大ヒット数 (既定 {DefaultSearchLimit}, 上限 {MaxSearchLimit})" },
                            thread_url = ThreadUrlParam(),
                        },
                        required = new[] { "keyword" },
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "get_post",
                    description = "単一レスを番号指定で取得する。thread_url 省略時は attached スレ。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            number     = new { type = "integer", description = "取得するレス番号 (1 始まり)" },
                            thread_url = ThreadUrlParam(),
                        },
                        required = new[] { "number" },
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "get_posts_by_id",
                    description = "指定 ID (5ch の ID:XXXXXXXX) で書き込まれた全レスを返す (= 「同じ人物の発言一覧」)。thread_url 省略時は attached スレ。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            id         = new { type = "string", description = "ID 文字列 (例: \"abc1234\"。大文字小文字区別)" },
                            thread_url = ThreadUrlParam(),
                        },
                        required = new[] { "id" },
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "get_my_posts",
                    description = "attached スレッドで「自分の書き込み」とマークされているレスを取得する。" +
                                  "include_replies=true (既定) では各自分レスへの直接返信もぶら下げてツリーモード。" +
                                  "他スレの自分マークは持たないので thread_url 引数は無い。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            include_replies        = new { type = "boolean", description = "true: 各自分レスにその被アンカー (返信) をぶら下げてツリー化 (既定 true)。false: 自分レスのみ。" },
                            replies_per_post_limit = new { type = "integer", description = "1 自分レスあたり含める返信の最大件数 (既定 20, 上限 50)。" },
                        },
                        required = Array.Empty<string>(),
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "find_replies_to",
                    description = "指定レス番号 N にぶら下がっているレス (= 本文中で >>N / >>N-M / >>N,M で N を参照しているレス) を集めて返す。" +
                                  "thread_url 省略時は attached スレ。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            number     = new { type = "integer", description = "対象レス番号 (1 始まり)" },
                            limit      = new { type = "integer", description = $"返す最大件数 (既定 {DefaultRepliesLimit}, 上限 {MaxRepliesLimit})" },
                            thread_url = ThreadUrlParam(),
                        },
                        required = new[] { "number" },
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "find_popular_posts",
                    description = "被アンカー数 (= 他レスから >>N で参照された回数) の多いレスを上位から返す。" +
                                  "range_start / range_end で範囲を絞ると「新着の中で人気」「特定区間で人気」を取れる。thread_url 省略時は attached スレ。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            top_k       = new { type = "integer", description = $"上位何件返すか (既定 {DefaultPopularTopK}, 上限 {MaxPopularTopK})" },
                            range_start = new { type = "integer", description = "対象範囲の開始レス番号 (省略時 1)" },
                            range_end   = new { type = "integer", description = "対象範囲の終了レス番号 (省略時 末尾)" },
                            min_count   = new { type = "integer", description = "被アンカー数の最小しきい値 (既定 1)。" },
                            thread_url  = ThreadUrlParam(),
                        },
                        required = Array.Empty<string>(),
                    },
                },
            },

            // ---- ワークスペース横断系 ----
            new
            {
                type     = "function",
                function = new
                {
                    name        = "list_boards",
                    description = "アプリにロード済みの板一覧 (bbsmenu) を返す。各エントリに board_url / category が含まれる。\n" +
                                  "**呼び分けを意識すること**:\n" +
                                  "(a) **単純な板名マッチで十分** → keyword 指定 (例: 「ニュース板を開いて」「将棋板を見せて」)。\n" +
                                  "(b) **テーマ / ジャンル絞り込みで複数板に跨りそう** → **keyword 省略**で全板取得 (scan モード)。" +
                                  "5ch の板はカテゴリ別に整理されており、漫画関連だけで「マンガ」「コミック」「漫画作品」「漫画サロン」など複数、" +
                                  "アニメ関連も「アニメ」「アニメ実況」「声優」「アニソン」など複数の板が存在する。" +
                                  "「ダン飯関連スレを探す」のようなテーマ検索では、漫画系板群 + アニメ系板群を **複数まとめて pick して** から、" +
                                  "各板に対して list_threads (scan モード) を回すべき。" +
                                  "scan モードでは category 別にグルーピングされた構造で返されるので、" +
                                  "カテゴリ単位で関連を pick しやすい (= カテゴリ「漫画」「アニメ」「アニメ実況」全体をまとめて pick できる)。" +
                                  $"keyword 省略時の既定取得数は {BoardsScanDefaultLimit} 件 (= 5ch 全板)、上限 {MaxListLimit} 件。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            keyword = new { type = "string",  description = "板名・カテゴリ名の部分一致フィルタ (大小文字無視)。テーマ検索で複数板候補を見たい場合は省略すること。" },
                            limit   = new { type = "integer", description = $"返す最大件数 (keyword 指定時の既定 {DefaultListLimit}, keyword 省略時の既定 {BoardsScanDefaultLimit}, 上限 {MaxListLimit})" },
                        },
                        required = Array.Empty<string>(),
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "list_threads",
                    description = "指定した板のスレッド一覧を返す (subject.txt ベース、勢い順)。" +
                                  "**2 つの使い方を意識して呼び分けること**:\n" +
                                  "(a) **単純な単語マッチで十分な場合** → keyword を指定。例: スレタイにそのまま「初音ミク」と入ってる確率が高いケース。\n" +
                                  "(b) **曖昧 / 略称 / ジャンル判定が必要な場合** → **keyword を省略**して多めに取得し、" +
                                  "AI 自身がタイトル一覧を読んでテーマとの関連を判断する。" +
                                  $"例: 「ソニー製品関連スレ」(= 単に \"ソニー\" でマッチしない、PS5 / α7 / WH-1000XM5 等の製品名が並ぶ)、" +
                                  "「ダンジョン飯関連スレ」(= スレタイには \"ダン飯\" と略されていることが多い)、" +
                                  "「異世界転生もの」(= ジャンル判定が必要、特定キーワードでは取りこぼす)。" +
                                  $"keyword 省略時の既定取得数は {ThreadsScanDefaultLimit} 件、上限 {MaxListLimit} 件。" +
                                  "返値の各エントリには thread_url が含まれ、関連と判定したものをそのまま open_thread_list_in_app の threads 配列に詰めれば良い。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            board_url = new { type = "string",  description = "対象板の URL (list_boards の board_url をそのまま渡せる)" },
                            keyword   = new { type = "string",  description = "スレタイの部分一致フィルタ (大小文字無視)。曖昧 / 略称 / ジャンル判定が必要なケースでは省略すること (= AI が手動で取捨選択するモード)" },
                            limit     = new { type = "integer", description = $"返す最大件数 (keyword 指定時の既定 {DefaultListLimit}, keyword 省略時の既定 {ThreadsScanDefaultLimit}, 上限 {MaxListLimit})" },
                        },
                        required = new[] { "board_url" },
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "open_thread_in_app",
                    description = "指定スレをユーザのアプリのスレ表示ペインで開く (= 実際に画面にスレタブを増やすアクション)。" +
                                  "ユーザが見ることを意図した依頼 (例: 「開いて」「見せて」「探して開いて」「次スレ開いて」など)" +
                                  "に対して、対象スレが特定できたら必ず呼ぶこと。" +
                                  "「見つけました」とテキストで報告するだけでは依頼を満たさない — 開く動作までやって完了。" +
                                  "確信が無い場合は get_posts で内容確認してから、確信が取れた時点で呼ぶ。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            thread_url = new { type = "string", description = "対象スレッドの URL。**必ず list_threads / search_posts 等のツール結果から取得した実在の URL を使う**こと。形式は https://<host>/test/read.cgi/<板dir>/<数字key>/ で、key は数字のみ。\"thread_id_1\" のような placeholder や記憶からの推測 URL は絶対禁止 (= 不正 URL は parse エラーになる)。" },
                        },
                        required = new[] { "thread_url" },
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "open_board_in_app",
                    description = "指定した板をユーザのアプリのスレ一覧ペインで開く (= 実際に画面にスレ一覧タブを増やすアクション)。" +
                                  "ユーザが「この板開いて」「○○板見せて」のように板を見ることを意図した依頼を出した場合に、" +
                                  "対象板が特定できたら必ず呼ぶ。報告だけで済ませない。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            board_url = new { type = "string", description = "対象板の URL (https://<host>/<dir>/)" },
                        },
                        required = new[] { "board_url" },
                    },
                },
            },
            new
            {
                type     = "function",
                function = new
                {
                    name        = "open_thread_list_in_app",
                    description = "**複数のスレ候補をまとめて 1 つのタブに並べてユーザに見せる**ためのアクション (= スレ一覧ペインに新規タブを作る)。" +
                                  "板をまたぐ検索 (例: 「○○の関連スレを見つけて」「△△に関するスレ集めて」) で複数ヒットしたときに使う。" +
                                  "1 つのスレを開くだけなら open_thread_in_app の方が良い。" +
                                  "**threads 配列の各要素は、必ず先に list_threads を呼んで得た実在のスレのみを入れる**。" +
                                  "「thread_id_1」「key_N」のような placeholder 風 URL や、記憶 / 推測で生成した URL は絶対に入れない (parse エラーになる)。" +
                                  "thread_url / title / post_count は list_threads 結果のフィールドをそのまま流用する。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            title   = new { type = "string", description = "タブのヘッダラベル (例: 「鬼滅の刃 関連スレ」「AI 検索: ○○」)。検索の意図がわかる短いタイトル。" },
                            threads = new
                            {
                                type        = "array",
                                description = "並べるスレッドの配列。各要素は {thread_url, title?, post_count?}。",
                                items       = new
                                {
                                    type       = "object",
                                    properties = new
                                    {
                                        thread_url = new { type = "string",  description = "スレ URL (必須)" },
                                        title      = new { type = "string",  description = "スレタイ (list_threads の title をそのまま渡せる)" },
                                        post_count = new { type = "integer", description = "レス数 (list_threads の post_count をそのまま渡せる)" },
                                    },
                                    required = new[] { "thread_url" },
                                },
                            },
                        },
                        required = new[] { "title", "threads" },
                    },
                },
            },
        };
    }

    /// <summary>各スレ読み取り系ツールの thread_url 引数の共通 schema を吐く (= 説明文を一本化)。</summary>
    private static object ThreadUrlParam() => new
    {
        type = "string",
        description = "対象スレッドの URL (省略時は attached スレッド)。他スレを読みたいときに渡す。例: https://news.5ch.io/test/read.cgi/news/1234567890/",
    };

    /// <summary>LLM が呼んできたツールを実行し、結果 JSON を文字列で返す (async 版)。
    /// cross-thread ロードに伴うディスク I/O / 必要時はネット取得が走るため Task ベース。</summary>
    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            return name switch
            {
                "get_thread_meta"     => await GetThreadMetaAsync(argumentsJson, ct).ConfigureAwait(false),
                "get_thread_state"    => GetThreadState(),
                "get_posts"           => await GetPostsAsync(argumentsJson, ct).ConfigureAwait(false),
                "search_posts"        => await SearchPostsAsync(argumentsJson, ct).ConfigureAwait(false),
                "get_post"            => await GetPostAsync(argumentsJson, ct).ConfigureAwait(false),
                "get_posts_by_id"     => await GetPostsByIdAsync(argumentsJson, ct).ConfigureAwait(false),
                "find_replies_to"     => await FindRepliesToAsync(argumentsJson, ct).ConfigureAwait(false),
                "find_popular_posts"  => await FindPopularPostsAsync(argumentsJson, ct).ConfigureAwait(false),
                "get_my_posts"        => GetMyPosts(argumentsJson),
                "list_boards"         => ListBoards(argumentsJson),
                "list_threads"        => await ListThreadsAsync(argumentsJson, ct).ConfigureAwait(false),
                "open_thread_in_app"      => await OpenThreadInAppAsync(argumentsJson).ConfigureAwait(false),
                "open_board_in_app"       => await OpenBoardInAppAsync(argumentsJson).ConfigureAwait(false),
                "open_thread_list_in_app" => await OpenThreadListInAppAsync(argumentsJson).ConfigureAwait(false),
                _                     => ErrorJson($"未知のツール: {name}"),
            };
        }
        catch (Exception ex)
        {
            return ErrorJson($"ツール実行で例外: {ex.Message}");
        }
    }

    /// <summary>thread_url 引数 (= 文字列 / 無指定) から ThreadContext を解決する。
    /// 指定無し → attached を返す。指定あり → URL パース、attached と一致するなら attached、違うなら動的ロード。
    /// 解決不能なら (null, エラーメッセージ) を返す。</summary>
    private async Task<(ThreadContext? ctx, string? error)> ResolveContextAsync(JsonElement args, CancellationToken ct)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("thread_url", out var tuEl)
            && tuEl.ValueKind == JsonValueKind.String)
        {
            var url = tuEl.GetString() ?? "";
            if (!string.IsNullOrEmpty(url))
            {
                if (!_dataLoader.TryParseThreadUrl(url, out var h, out var d, out var k))
                    return (null, $"thread_url の解釈に失敗: \"{url}\" (5ch.io / bbspink.com のスレ URL である必要があります)");

                // attached と一致するならそれを使う (= 状態系も読める)。
                if (_attached is not null
                    && string.Equals(_attached.Board.Host,          h, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_attached.Board.DirectoryName, d, StringComparison.Ordinal)
                    && string.Equals(_attached.ThreadKey,           k, StringComparison.Ordinal))
                {
                    return (_attached, null);
                }

                var board = _dataLoader.ResolveBoard(h, d);
                var posts = await _dataLoader.LoadPostsAsync(board, k, ct).ConfigureAwait(false);
                var title = posts.Count > 0 ? posts[0].ThreadTitle ?? "" : "";
                var ctx = new ThreadContext(
                    Board:              board,
                    ThreadKey:          k,
                    Title:              title,
                    BoardName:          board.BoardName ?? board.DirectoryName,
                    Posts:              posts,
                    InboundAnchors:     BuildInboundAnchorIndex(posts),
                    LastReadPostNumber: null,
                    MarkPostNumber:     null,
                    OwnPostNumbers:     new HashSet<int>(),
                    HasReplyToOwn:      false);
                return (ctx, null);
            }
        }

        if (_attached is null)
            return (null, "thread_url が指定されていません。このチャットには attached スレッドが無いので、明示的に thread_url を指定するか、list_boards / list_threads で対象スレを特定してください。");

        return (_attached, null);
    }

    // ---- 個別ツール (= thread context 系) ----

    private async Task<string> GetThreadMetaAsync(string argsJson, CancellationToken ct)
    {
        TryParseObject(argsJson, out var args);
        var (ctx, err) = await ResolveContextAsync(args, ct).ConfigureAwait(false);
        if (ctx is null) return ErrorJson(err!);

        var op = ctx.Posts.Count > 0 ? ctx.Posts[0] : null;
        var payload = new
        {
            title       = ctx.Title,
            board       = ctx.BoardName,
            board_url   = ctx.Board.Url,
            thread_url  = $"https://{ctx.Board.Host}/test/read.cgi/{ctx.Board.DirectoryName}/{ctx.ThreadKey}/",
            total_posts = ctx.Posts.Count,
            op = op is null ? null : new
            {
                n    = op.Number,
                name = op.Name,
                id   = op.Id,
                date = op.DateText,
                body = CleanBody(op.Body),
            },
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    private string GetThreadState()
    {
        if (_attached is null)
            return ErrorJson("attached スレッドが無いので状態は取得できません (このツールは attached スレ専用)。");

        var ctx = _attached;
        int? newStart = null;
        int? newEnd   = null;
        int  newCount = 0;
        if (ctx.MarkPostNumber is int mark && ctx.Posts.Count > 0)
        {
            newStart = mark;
            newEnd   = ctx.Posts[^1].Number;
            if (newEnd.Value >= newStart.Value)
                newCount = newEnd.Value - newStart.Value + 1;
        }

        string hint;
        if (ctx.HasReplyToOwn && ctx.OwnPostNumbers.Count > 0)
            hint = "has_reply_to_own が true。own_post_numbers の各レスに find_replies_to を呼んで自分宛て返信を集めること。";
        else if (newCount > 0)
            hint = $"新着が {newCount} 件ある (>>{newStart}-{newEnd})。話題を聞かれていれば get_posts でこの範囲を読み、find_popular_posts(range_start={newStart}, range_end={newEnd}) で人気レスを補強せよ。";
        else if (ctx.OwnPostNumbers.Count > 0)
            hint = "新着なし。自分のレス (own_post_numbers) はあるが直前の取得で自分宛て返信は検出されていない。";
        else
            hint = "新着なし / 自分のレスマークなし。通常の get_posts / search_posts で読み進めること。";

        var payload = new
        {
            total_posts            = ctx.Posts.Count,
            last_read_post_number  = ctx.LastReadPostNumber,
            new_posts_start        = newStart,
            new_posts_end          = newEnd,
            new_posts_count        = newCount,
            own_post_numbers       = ctx.OwnPostNumbers.OrderBy(n => n).ToArray(),
            has_reply_to_own       = ctx.HasReplyToOwn,
            hint                   = hint,
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    private async Task<string> GetPostsAsync(string argsJson, CancellationToken ct)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("start", out var startEl) || !TryGetIntLoose(startEl, out var start))
            return ErrorJson("start が指定されていないか、整数として読み取れません");
        if (!args.TryGetProperty("end",   out var endEl)   || !TryGetIntLoose(endEl,   out var end))
            return ErrorJson("end が指定されていないか、整数として読み取れません");

        var (ctx, err) = await ResolveContextAsync(args, ct).ConfigureAwait(false);
        if (ctx is null) return ErrorJson(err!);

        if (ctx.Posts.Count == 0) return JsonSerializer.Serialize(new { posts = Array.Empty<object>() }, JsonOpts);

        var lo = Math.Max(1, start);
        var hi = Math.Min(ctx.Posts.Count, end);
        if (hi < lo)
            return ErrorJson($"範囲が空です (start={start}, end={end}, total={ctx.Posts.Count})");
        if (hi - lo + 1 > MaxPostsPerCall)
            return ErrorJson($"範囲が広すぎます ({hi - lo + 1} 件)。1 度に取れるのは {MaxPostsPerCall} 件まで。分割して呼んでください");

        var slice = new List<object>(hi - lo + 1);
        foreach (var p in ctx.Posts)
        {
            if (p.Number < lo || p.Number > hi) continue;
            slice.Add(new
            {
                n    = p.Number,
                name = p.Name,
                id   = p.Id,
                date = p.DateText,
                body = CleanBody(p.Body),
            });
        }
        return JsonSerializer.Serialize(new { posts = slice }, JsonOpts);
    }

    private async Task<string> SearchPostsAsync(string argsJson, CancellationToken ct)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("keyword", out var kwEl) || kwEl.ValueKind != JsonValueKind.String)
            return ErrorJson("keyword が指定されていません");
        var keyword = kwEl.GetString() ?? "";
        if (keyword.Length == 0) return ErrorJson("keyword が空です");

        var limit = DefaultSearchLimit;
        if (args.TryGetProperty("limit", out var limEl) && TryGetIntLoose(limEl, out var lim))
            limit = Math.Clamp(lim, 1, MaxSearchLimit);

        var (ctx, err) = await ResolveContextAsync(args, ct).ConfigureAwait(false);
        if (ctx is null) return ErrorJson(err!);

        var matches   = new List<object>();
        var truncated = false;
        foreach (var p in ctx.Posts)
        {
            var body = CleanBody(p.Body);
            var idx  = body.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            string snippet;
            if (body.Length <= SearchSnippetMax)
            {
                snippet = body;
            }
            else
            {
                var half  = SearchSnippetMax / 2;
                var sstart = Math.Max(0, idx - half);
                var slen  = Math.Min(SearchSnippetMax, body.Length - sstart);
                snippet = body.Substring(sstart, slen);
                if (sstart > 0) snippet = "…" + snippet;
                if (sstart + slen < body.Length) snippet += "…";
            }

            matches.Add(new
            {
                n       = p.Number,
                name    = p.Name,
                id      = p.Id,
                date    = p.DateText,
                snippet = snippet,
            });
            if (matches.Count >= limit) { truncated = true; break; }
        }

        return JsonSerializer.Serialize(new
        {
            keyword,
            total_matches_returned = matches.Count,
            truncated,
            matches,
        }, JsonOpts);
    }

    private async Task<string> GetPostAsync(string argsJson, CancellationToken ct)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("number", out var nEl) || !TryGetIntLoose(nEl, out var number))
            return ErrorJson("number が指定されていないか、整数として読み取れません");

        var (ctx, err) = await ResolveContextAsync(args, ct).ConfigureAwait(false);
        if (ctx is null) return ErrorJson(err!);

        var p = ctx.Posts.FirstOrDefault(x => x.Number == number);
        if (p is null)
            return ErrorJson($"レス番号 {number} は存在しません (もしくは NG であぼーん済み)");

        return JsonSerializer.Serialize(new
        {
            n    = p.Number,
            name = p.Name,
            id   = p.Id,
            date = p.DateText,
            body = CleanBody(p.Body),
        }, JsonOpts);
    }

    private async Task<string> GetPostsByIdAsync(string argsJson, CancellationToken ct)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return ErrorJson("id が指定されていません");
        var id = idEl.GetString() ?? "";
        if (id.Length == 0) return ErrorJson("id が空です");

        var (ctx, err) = await ResolveContextAsync(args, ct).ConfigureAwait(false);
        if (ctx is null) return ErrorJson(err!);

        var matches = ctx.Posts
            .Where(p => string.Equals(p.Id, id, StringComparison.Ordinal))
            .Select(p => new
            {
                n    = p.Number,
                name = p.Name,
                date = p.DateText,
                body = CleanBody(p.Body),
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            id,
            total_posts = matches.Length,
            posts = matches,
        }, JsonOpts);
    }

    private async Task<string> FindRepliesToAsync(string argsJson, CancellationToken ct)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("number", out var nEl) || !TryGetIntLoose(nEl, out var target))
            return ErrorJson("number が指定されていないか、整数として読み取れません");

        var limit = DefaultRepliesLimit;
        if (args.TryGetProperty("limit", out var limEl) && TryGetIntLoose(limEl, out var lim))
            limit = Math.Clamp(lim, 1, MaxRepliesLimit);

        var (ctx, err) = await ResolveContextAsync(args, ct).ConfigureAwait(false);
        if (ctx is null) return ErrorJson(err!);

        if (!ctx.InboundAnchors.TryGetValue(target, out var fromList) || fromList.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                target,
                total_matches = 0,
                truncated = false,
                replies   = Array.Empty<object>(),
                note      = $">>{target} への被アンカーは検出されませんでした",
            }, JsonOpts);
        }

        var truncated = fromList.Count > limit;
        var picked    = truncated ? fromList.Take(limit) : (IEnumerable<int>)fromList;
        var replies   = new List<object>(Math.Min(fromList.Count, limit));
        foreach (var fromN in picked)
        {
            var p = ctx.Posts.FirstOrDefault(x => x.Number == fromN);
            if (p is null) continue;
            replies.Add(new
            {
                n    = p.Number,
                name = p.Name,
                id   = p.Id,
                date = p.DateText,
                body = CleanBody(p.Body),
            });
        }

        return JsonSerializer.Serialize(new
        {
            target,
            total_matches = fromList.Count,
            truncated,
            replies,
        }, JsonOpts);
    }

    private async Task<string> FindPopularPostsAsync(string argsJson, CancellationToken ct)
    {
        TryParseObject(argsJson, out var args);
        var (ctx, err) = await ResolveContextAsync(args, ct).ConfigureAwait(false);
        if (ctx is null) return ErrorJson(err!);

        var topK       = DefaultPopularTopK;
        var rangeStart = 1;
        var rangeEnd   = ctx.Posts.Count;
        var minCount   = 1;

        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("top_k", out var topKEl) && TryGetIntLoose(topKEl, out var k))
                topK = Math.Clamp(k, 1, MaxPopularTopK);
            if (args.TryGetProperty("range_start", out var rsEl) && TryGetIntLoose(rsEl, out var rs))
                rangeStart = Math.Max(1, rs);
            if (args.TryGetProperty("range_end", out var reEl) && TryGetIntLoose(reEl, out var re))
                rangeEnd = Math.Min(ctx.Posts.Count, re);
            if (args.TryGetProperty("min_count", out var mcEl) && TryGetIntLoose(mcEl, out var mc))
                minCount = Math.Max(0, mc);
        }

        if (rangeEnd < rangeStart || ctx.Posts.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                range_start = rangeStart,
                range_end   = rangeEnd,
                top_k       = topK,
                ranked      = Array.Empty<object>(),
            }, JsonOpts);
        }

        var ranked = ctx.InboundAnchors
            .Where(kv => kv.Key >= rangeStart && kv.Key <= rangeEnd && kv.Value.Count >= minCount)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key)
            .Take(topK)
            .ToArray();

        var result = new List<object>(ranked.Length);
        foreach (var kv in ranked)
        {
            var p = ctx.Posts.FirstOrDefault(x => x.Number == kv.Key);
            if (p is null) continue;
            var body = CleanBody(p.Body);
            var snippet = body.Length <= PopularSnippetMax
                ? body
                : body[..PopularSnippetMax] + "…";
            result.Add(new
            {
                n            = p.Number,
                name         = p.Name,
                id           = p.Id,
                date         = p.DateText,
                anchor_count = kv.Value.Count,
                snippet,
            });
        }

        return JsonSerializer.Serialize(new
        {
            range_start = rangeStart,
            range_end   = rangeEnd,
            top_k       = topK,
            min_count   = minCount,
            ranked      = result,
        }, JsonOpts);
    }

    private string GetMyPosts(string argsJson)
    {
        if (_attached is null)
            return ErrorJson("attached スレッドが無いので「自分の書き込み」は取得できません (このツールは attached スレ専用)。");

        var ctx = _attached;
        var includeReplies       = true;
        var repliesPerPostLimit  = 20;
        if (TryParseObject(argsJson, out var args))
        {
            if (args.TryGetProperty("include_replies", out var irEl))
            {
                switch (irEl.ValueKind)
                {
                    case JsonValueKind.True:  includeReplies = true;  break;
                    case JsonValueKind.False: includeReplies = false; break;
                    case JsonValueKind.String:
                        if (bool.TryParse(irEl.GetString(), out var parsed)) includeReplies = parsed;
                        break;
                }
            }
            if (args.TryGetProperty("replies_per_post_limit", out var lEl) && TryGetIntLoose(lEl, out var l))
                repliesPerPostLimit = Math.Clamp(l, 1, 50);
        }

        if (ctx.OwnPostNumbers.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                total_my_posts  = 0,
                include_replies = includeReplies,
                my_posts        = Array.Empty<object>(),
                note            = "自分の書き込みとしてマークされたレスはありません",
            }, JsonOpts);
        }

        var sortedOwn             = ctx.OwnPostNumbers.OrderBy(n => n).ToArray();
        var myPosts               = new List<object>(sortedOwn.Length);
        var totalRepliesAcrossAll = 0;
        foreach (var n in sortedOwn)
        {
            var p = ctx.Posts.FirstOrDefault(x => x.Number == n);
            if (p is null) continue;

            if (!includeReplies)
            {
                myPosts.Add(new
                {
                    n    = p.Number,
                    name = p.Name,
                    id   = p.Id,
                    date = p.DateText,
                    body = CleanBody(p.Body),
                });
                continue;
            }

            var replies          = new List<object>();
            var totalForThis     = 0;
            var truncatedForThis = false;
            if (ctx.InboundAnchors.TryGetValue(n, out var fromList) && fromList.Count > 0)
            {
                totalForThis     = fromList.Count;
                truncatedForThis = fromList.Count > repliesPerPostLimit;
                var picked = truncatedForThis ? fromList.Take(repliesPerPostLimit) : (IEnumerable<int>)fromList;
                foreach (var fromN in picked)
                {
                    var rp = ctx.Posts.FirstOrDefault(x => x.Number == fromN);
                    if (rp is null) continue;
                    replies.Add(new
                    {
                        n    = rp.Number,
                        name = rp.Name,
                        id   = rp.Id,
                        date = rp.DateText,
                        body = CleanBody(rp.Body),
                    });
                    totalRepliesAcrossAll++;
                }
            }

            myPosts.Add(new
            {
                n                 = p.Number,
                name              = p.Name,
                id                = p.Id,
                date              = p.DateText,
                body              = CleanBody(p.Body),
                total_replies     = totalForThis,
                replies_truncated = truncatedForThis,
                replies,
            });
        }

        return JsonSerializer.Serialize(new
        {
            total_my_posts          = sortedOwn.Length,
            include_replies         = includeReplies,
            total_replies_returned  = totalRepliesAcrossAll,
            my_posts                = myPosts,
        }, JsonOpts);
    }

    // ---- ワークスペース横断系 ----

    private string ListBoards(string argsJson)
    {
        TryParseObject(argsJson, out var args);
        string? keyword = null;
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("keyword", out var kwEl) && kwEl.ValueKind == JsonValueKind.String)
        {
            keyword = kwEl.GetString();
        }

        // keyword 指定の有無で既定件数を切り替える (= scan モードは全板返却が原則)。
        var defaultLimit = string.IsNullOrEmpty(keyword) ? BoardsScanDefaultLimit : DefaultListLimit;
        var limit        = defaultLimit;
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("limit", out var limEl) && TryGetIntLoose(limEl, out var lim))
        {
            limit = Math.Clamp(lim, 1, MaxListLimit);
        }

        var all = _dataLoader.ListBoardsSnapshot();
        IEnumerable<Board> filtered = all;
        if (!string.IsNullOrEmpty(keyword))
        {
            filtered = all.Where(b =>
                (b.BoardName    ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (b.CategoryName ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (b.DirectoryName?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        var picked    = filtered.Take(limit).ToArray();
        var totalAll  = all.Count;
        var matched   = string.IsNullOrEmpty(keyword) ? totalAll : filtered.Count();
        var truncated = picked.Length < matched;
        var isScanMode = string.IsNullOrEmpty(keyword);

        // scan モード時のヒント: AI に「カテゴリ単位で関連を pick、漏れなく複数カテゴリを跨いで」と促す。
        var hint = isScanMode
            ? $"スキャンモード (= keyword 無し)。全 {totalAll} 板中 {picked.Length} 件をカテゴリ別にグルーピングして返した。" +
              "**categories の各 category 名を見て、テーマに関連するカテゴリを複数まとめて pick すること**。" +
              "例: 「ダン飯関連」→ category=「漫画」「漫画作品」「漫画系」「アニメ」「アニメ実況」「声優」など漫画/アニメ系を全部 pick。" +
              "「ソニー製品」→ category=「家電」「AV機器」「PCハードウェア」「ゲーム機」「デジカメ」など複数の家電/ゲーム系を全部 pick。" +
              "1 カテゴリ 1 板に絞らない (= 同テーマでも複数の category や複数の板に分散しているのが普通)。" +
              "返値の categories は CategoryOrder 順で並んでいる。" +
              (truncated ? $" 全 {matched} 件中 {picked.Length} 件のみ。続きが必要なら limit={MaxListLimit} で再取得可。" : "")
            : $"キーワード検索モード (\"{keyword}\")。テーマ検索で複数板候補を見たい場合は keyword 省略で再取得すること。";

        if (isScanMode)
        {
            // scan モード: カテゴリ別にグルーピングして返す。各カテゴリ内では出現順 (= bbsmenu の元順序) を維持。
            var grouped = picked
                .GroupBy(b => string.IsNullOrEmpty(b.CategoryName) ? "(未分類)" : b.CategoryName)
                .OrderBy(g => g.Min(b => b.CategoryOrder))
                .Select(g => new
                {
                    category = g.Key,
                    count    = g.Count(),
                    boards   = g.Select(b => new
                    {
                        name      = b.BoardName,
                        dir       = b.DirectoryName,
                        board_url = b.Url,
                    }).ToArray(),
                })
                .ToArray();

            return JsonSerializer.Serialize(new
            {
                total_boards_in_app = totalAll,
                matched,
                returned         = picked.Length,
                truncated,
                keyword          = "",
                mode             = "scan",
                hint,
                categories_count = grouped.Length,
                categories       = grouped,
            }, JsonOpts);
        }

        // keyword モード: 平坦リスト (= 検索結果は数件〜数十件と想定、グルーピング不要)。
        var boards = picked.Select(b => new
        {
            name      = b.BoardName,
            dir       = b.DirectoryName,
            category  = b.CategoryName,
            board_url = b.Url,
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            total_boards_in_app = totalAll,
            matched,
            returned = boards.Length,
            truncated,
            keyword  = keyword ?? "",
            mode     = "keyword",
            hint,
            boards,
        }, JsonOpts);
    }

    private async Task<string> ListThreadsAsync(string argsJson, CancellationToken ct)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("board_url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            return ErrorJson("board_url が指定されていません");

        var url = urlEl.GetString() ?? "";
        if (!_dataLoader.TryParseBoardUrl(url, out var host, out var dir))
            return ErrorJson($"board_url の解釈に失敗: \"{url}\" (5ch.io / bbspink.com の板 URL である必要があります)");

        string? keyword = null;
        if (args.TryGetProperty("keyword", out var kwEl) && kwEl.ValueKind == JsonValueKind.String)
            keyword = kwEl.GetString();

        // keyword 指定の有無で既定件数を切り替える (= 曖昧検索モードでは多めに返す)。
        var defaultLimit = string.IsNullOrEmpty(keyword) ? ThreadsScanDefaultLimit : DefaultListLimit;
        var limit        = defaultLimit;
        if (args.TryGetProperty("limit", out var limEl) && TryGetIntLoose(limEl, out var lim))
            limit = Math.Clamp(lim, 1, MaxListLimit);

        var board   = _dataLoader.ResolveBoard(host, dir);
        var threads = await _dataLoader.ListThreadsAsync(board, ct).ConfigureAwait(false);

        IEnumerable<ThreadInfo> filtered = threads;
        if (!string.IsNullOrEmpty(keyword))
        {
            filtered = threads.Where(t =>
                (t.Title ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        var picked    = filtered.Take(limit).ToArray();
        var totalAll  = threads.Count;
        var matched   = string.IsNullOrEmpty(keyword) ? totalAll : filtered.Count();
        var truncated = picked.Length < matched;

        var list = picked.Select(t => new
        {
            key         = t.Key,
            title       = t.Title,
            post_count  = t.PostCount,
            order       = t.Order,
            thread_url  = $"https://{board.Host}/test/read.cgi/{board.DirectoryName}/{t.Key}/",
        }).ToArray();

        // モード判定 hint: keyword なしで呼ばれた = AI が自分で取捨選択するべきスキャンモード。
        var hint = string.IsNullOrEmpty(keyword)
            ? $"スキャンモード (= keyword 無し)。タイトル {list.Length} 件を返した。" +
              "これらを自分で読んで、ユーザの依頼するテーマに関連すると判断したスレだけを open_thread_list_in_app の threads 配列に詰めること。" +
              "略称・連想・ジャンル判定は AI の知識で行う (例: スレタイ \"PS5\" → ソニー製品関連、スレタイ \"ダン飯\" → ダンジョン飯関連)。" +
              (truncated ? $" 全 {matched} 件中 {list.Length} 件のみ。続きが必要なら limit={MaxListLimit} で再取得可。" : "")
            : $"キーワード検索モード (\"{keyword}\")。単語の部分一致のみなので、略称 / 連想含むテーマ検索の場合は keyword 省略で再取得すること。";

        return JsonSerializer.Serialize(new
        {
            board_url = board.Url,
            board     = board.BoardName ?? board.DirectoryName,
            total_threads = totalAll,
            matched,
            returned = list.Length,
            truncated,
            keyword  = keyword ?? "",
            mode     = string.IsNullOrEmpty(keyword) ? "scan" : "keyword",
            hint,
            threads  = list,
        }, JsonOpts);
    }

    private async Task<string> OpenThreadInAppAsync(string argsJson)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("thread_url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            return ErrorJson("thread_url が指定されていません");
        var url = urlEl.GetString() ?? "";
        if (!_dataLoader.TryParseThreadUrl(url, out _, out _, out _))
            return ErrorJson($"thread_url の解釈に失敗: \"{url}\"");

        var msg = await _openThreadInAppAsync(url).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { ok = true, thread_url = url, message = msg }, JsonOpts);
    }

    private async Task<string> OpenBoardInAppAsync(string argsJson)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("board_url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            return ErrorJson("board_url が指定されていません");
        var url = urlEl.GetString() ?? "";
        if (!_dataLoader.TryParseBoardUrl(url, out _, out _))
            return ErrorJson($"board_url の解釈に失敗: \"{url}\"");

        var msg = await _openBoardInAppAsync(url).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { ok = true, board_url = url, message = msg }, JsonOpts);
    }

    /// <summary>open_thread_list_in_app のパース + dispatch。AI のスキーマ違反を寛容に救済する:
    /// - title が空 / 欠落でも default "AI 検索結果" を採用
    /// - threads キーが無くて root が array でもそれを採用、別名 (urls / items / list) も拾う
    /// - threads の要素が string (URL のみ) でも受け入れる
    /// 失敗時のエラー JSON には「何が悪かったか」を入れ、AI が次ラウンドで自己修正できるようにする。
    /// ログは <see cref="ChBrowser.Services.Logging.LogService"/> に出すので、ログペインで動作確認できる。</summary>
    private async Task<string> OpenThreadListInAppAsync(string argsJson)
    {
        var log = ChBrowser.Services.Logging.LogService.Instance;
        log.Write($"[open_thread_list_in_app] 引数受信: {Trunc(argsJson, 400)}");

        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗 (引数は JSON オブジェクトであること)");

        // ---- title (寛容) ----
        var title = "AI 検索結果";
        if (args.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
        {
            var t = titleEl.GetString();
            if (!string.IsNullOrWhiteSpace(t)) title = t!;
        }

        // ---- threads 配列を見つける (寛容) ----
        // 標準: args.threads / 救済: args.urls / args.items / args.list / args 自身が配列
        JsonElement listEl = default;
        var found = false;
        foreach (var key in new[] { "threads", "urls", "items", "list" })
        {
            if (args.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Array)
            {
                listEl = el; found = true; break;
            }
        }
        // root 自体が array で渡ってきた場合 — TryParseObject は object 限定なので、その経路では拾えない。
        // 個別に再パースする (= AI が `[{...}, {...}]` を投げてきたケース)。
        if (!found)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    listEl = doc.RootElement.Clone();
                    found  = true;
                }
            }
            catch { /* 既に上で TryParseObject 通過してるはずなので来ないが念のため */ }
        }
        if (!found)
            return ErrorJson("threads 配列が指定されていません。スキーマ: {title:string, threads:[{thread_url, title?, post_count?}]} で送ること");

        // ---- 各要素を AiSearchResultEntry に変換 (寛容) ----
        var entries  = new List<AiSearchResultEntry>(listEl.GetArrayLength());
        var invalids = new List<string>();
        foreach (var item in listEl.EnumerateArray())
        {
            string? rawUrl    = null;
            string  entryTitle = "";
            int     postCount  = 0;

            if (item.ValueKind == JsonValueKind.String)
            {
                // 救済: 要素が文字列 = URL だけ渡された
                rawUrl = item.GetString();
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                // 標準: {thread_url, title?, post_count?}。url / link 等の別名も拾う。
                foreach (var k in new[] { "thread_url", "url", "link", "href" })
                {
                    if (item.TryGetProperty(k, out var ue) && ue.ValueKind == JsonValueKind.String)
                    {
                        rawUrl = ue.GetString(); break;
                    }
                }
                if (item.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                    entryTitle = tEl.GetString() ?? "";
                if (item.TryGetProperty("post_count", out var pcEl) && TryGetIntLoose(pcEl, out var pc))
                    postCount = Math.Max(0, pc);
            }
            else
            {
                invalids.Add("(オブジェクトでも文字列でもない要素)");
                continue;
            }

            if (string.IsNullOrEmpty(rawUrl))
            {
                invalids.Add("(thread_url 欠落)");
                continue;
            }
            if (!_dataLoader.TryParseThreadUrl(rawUrl, out _, out _, out _))
            {
                invalids.Add(rawUrl);
                continue;
            }
            entries.Add(new AiSearchResultEntry(rawUrl, entryTitle, postCount));
        }

        log.Write($"[open_thread_list_in_app] パース結果: title=\"{title}\", entries={entries.Count}, invalids={invalids.Count}");
        if (invalids.Count > 0)
            log.Write($"[open_thread_list_in_app]   不正要素例: {string.Join(" | ", invalids.Take(5))}");

        if (entries.Count == 0)
        {
            // 不正 URL の中に placeholder 風文字列 (= AI が list_threads を呼ばずに想像で URL を作った疑い) が
            // 含まれていないか検査。「thread_id」「placeholder」「example」や、数字ではない key 部分を持つ URL を疑う。
            var suspiciousPlaceholder = invalids.Any(u =>
                u.Contains("thread_id", StringComparison.OrdinalIgnoreCase) ||
                u.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
                u.Contains("example",    StringComparison.OrdinalIgnoreCase) ||
                u.Contains("xxxx",       StringComparison.OrdinalIgnoreCase) ||
                u.Contains("yyyy",       StringComparison.OrdinalIgnoreCase));

            var errMsg = "有効なスレッドエントリが 1 件もありません。" +
                         "正しい thread_url の形式は https://<host>/test/read.cgi/<板dir>/<数字key>/ で、key は数字のみ。" +
                         (invalids.Count > 0 ? $" 受け取って解釈できなかった値の例: [{string.Join(", ", invalids.Take(3))}]" : "");

            if (suspiciousPlaceholder)
            {
                errMsg += " ⚠ URL に \"thread_id\" のような placeholder 文字列が含まれています。" +
                          "**list_threads を呼ばずに想像で URL を作っていませんか?** " +
                          "必ず先に list_threads(board_url=...) を呼んで実在のスレ一覧を取得し、" +
                          "その結果の thread_url をそのまま使ってください。" +
                          "scheme は /test/read.cgi/ を必ず含み、key は 10 桁前後の epoch 数字です。";
            }
            else
            {
                errMsg += " URL に /test/read.cgi/ が含まれているか、key が数字か再確認してください。";
            }
            return ErrorJson(errMsg);
        }

        var msg = await _openThreadListInAppAsync(title, entries).ConfigureAwait(false);
        log.Write($"[open_thread_list_in_app] callback 完了: {msg}");
        return JsonSerializer.Serialize(new
        {
            ok                = true,
            title,
            opened_count      = entries.Count,
            skipped_count     = invalids.Count,
            message           = msg,
        }, JsonOpts);
    }

    /// <summary>ログ用に文字列を上限文字数で切る (= 長すぎる argsJson でログを埋め尽くさない)。</summary>
    private static string Trunc(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    // ---- システムプロンプト先頭注入用テキスト (= LLM が最初から見えていてほしい attached スレの現況) ----

    /// <summary>会話開始時点の attached スレ現況を読みやすいテキスト 1 ブロックで返す。
    /// attached 無しなら "" を返す (= 呼出元が条件分岐すべき)。</summary>
    public string BuildInitialStateSnapshot()
    {
        if (_attached is null) return "";
        var ctx = _attached;
        var sb = new StringBuilder();
        sb.Append("- 総レス数: ").Append(ctx.Posts.Count).AppendLine(" 件");
        if (ctx.LastReadPostNumber is int lr)
            sb.Append("- 既読位置: >>").AppendLine(lr.ToString());
        else
            sb.AppendLine("- 既読位置: 未設定 (= まだスクロール痕跡が永続化されていない)");

        if (ctx.MarkPostNumber is int mark && ctx.Posts.Count > 0)
        {
            var newEnd = ctx.Posts[^1].Number;
            if (newEnd >= mark)
                sb.Append("- 新着レス: >>").Append(mark).Append('-').Append(newEnd)
                  .Append(" (").Append(newEnd - mark + 1).AppendLine(" 件) [このアプリ起動以降の差分取得で増えた範囲]");
            else
                sb.AppendLine("- 新着レス: 範囲不整合 (mark > tail)");
        }
        else
        {
            sb.AppendLine("- 新着レス: なし (= このセッション中の差分取得で新規に増えたレスは無い)");
        }

        if (ctx.OwnPostNumbers.Count > 0)
        {
            sb.Append("- 自分の書き込み: ");
            var sorted = ctx.OwnPostNumbers.OrderBy(n => n).ToArray();
            for (int i = 0; i < sorted.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(">>").Append(sorted[i]);
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("- 自分の書き込み: マーク無し");
        }

        sb.Append("- 自分宛て返信フラグ: ");
        sb.AppendLine(ctx.HasReplyToOwn
            ? "あり (直前の差分取得で自分のレスを参照したレスが来ている)"
            : "なし");

        return sb.ToString();
    }

    /// <summary>会話開始時点の「自分の書き込み + それへの直接返信」をプロンプトに埋め込み可能なテキストで返す。
    /// attached 無し or 自分マーク無しなら "" を返す。</summary>
    public string BuildOwnPostsWithRepliesSummary(int repliesPerOwnLimit = 10, int maxChars = 4000)
    {
        if (_attached is null || _attached.OwnPostNumbers.Count == 0) return "";

        var ctx    = _attached;
        var sb     = new StringBuilder();
        var sorted = ctx.OwnPostNumbers.OrderBy(n => n).ToArray();
        foreach (var n in sorted)
        {
            var p = ctx.Posts.FirstOrDefault(x => x.Number == n);
            if (p is null) continue;

            sb.Append(">>").Append(p.Number);
            sb.Append(" (").Append(p.DateText).Append(", ").Append(p.Name);
            if (!string.IsNullOrEmpty(p.Id)) sb.Append(", ID:").Append(p.Id);
            sb.AppendLine(")");
            sb.AppendLine(TrimSingleLine(CleanBody(p.Body), 400));
            sb.AppendLine();

            if (ctx.InboundAnchors.TryGetValue(n, out var fromList) && fromList.Count > 0)
            {
                sb.Append("  └─ への返信 (").Append(fromList.Count).AppendLine(" 件):");
                var shown = 0;
                foreach (var fromN in fromList)
                {
                    if (shown >= repliesPerOwnLimit)
                    {
                        sb.Append("     ... 他 ").Append(fromList.Count - shown)
                          .AppendLine(" 件 (find_replies_to / get_my_posts で完全取得可)");
                        break;
                    }
                    var rp = ctx.Posts.FirstOrDefault(x => x.Number == fromN);
                    if (rp is null) continue;
                    sb.Append("     >>").Append(rp.Number);
                    sb.Append(" (").Append(rp.Name);
                    if (!string.IsNullOrEmpty(rp.Id)) sb.Append(", ID:").Append(rp.Id);
                    sb.Append("): ");
                    sb.AppendLine(TrimSingleLine(CleanBody(rp.Body), 200));
                    shown++;
                }
                sb.AppendLine();
            }

            if (sb.Length > maxChars)
            {
                sb.AppendLine("... 以降は省略 (get_my_posts(include_replies=true) で完全取得可)");
                break;
            }
        }
        return sb.ToString();
    }

    /// <summary>本文を 1 行に潰して上限文字数で切る (= プロンプトに埋め込む際の見栄え対策)。</summary>
    private static string TrimSingleLine(string s, int max)
    {
        var collapsed = s.Replace("\r", " ").Replace("\n", " ");
        if (collapsed.Length <= max) return collapsed;
        return collapsed[..max] + "…";
    }

    // ---- helpers ----

    /// <summary>dat の本文に残っている <c>&lt;a&gt;</c> やその他のタグを取り除き、LLM に渡しやすい
    /// 純テキストにする。dat の改行は既に \n に正規化されている前提。</summary>
    private static string CleanBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var s = AnchorTagRe.Replace(body, "$1");
        s = AnyTagRe.Replace(s, "");
        return s;
    }

    private static bool TryParseObject(string json, out JsonElement obj)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            obj = doc.RootElement.Clone();
            return obj.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            obj = default;
            return false;
        }
    }

    private static bool TryGetIntLoose(JsonElement el, out int value)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetInt32(out value);
            case JsonValueKind.String:
                return int.TryParse(el.GetString(), out value);
            default:
                value = 0;
                return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static string ErrorJson(string message)
        => JsonSerializer.Serialize(new { error = message }, JsonOpts);
}
