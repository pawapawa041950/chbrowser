using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChBrowser.Models;

namespace ChBrowser.Services.Llm;

/// <summary>
/// AI チャットで 1 スレッドに対して LLM が呼び出せるツール群を束ねる。
///
/// LLM に渡すツール定義 (= JSON Schema 配列) は <see cref="GetToolDefinitions"/>、
/// 実行は名前ベースでディスパッチする <see cref="Execute"/> で行う。各ツールは引数 JSON 文字列を受け取り、
/// JSON 文字列の結果を返す (= LLM が再度入力として食えるよう、文字列で完結する)。
///
/// <para><b>スナップショット方針</b>: コンストラクタで <see cref="Post"/> 列をコピーして保持し、以降の
/// スレ追従更新は反映しない。会話中にレス数や本文が動くと、LLM が一度ツール経由で取得した文脈と
/// 食い違うため。古さは <see cref="GetThreadMeta"/> の <c>total_posts</c> で読み取れる。</para>
///
/// <para><b>状態</b>: 既読位置 / 新着先頭 / 自分のレス / 自分への返信有無もスナップショットで保持し、
/// <c>get_thread_state</c> から読める。これがあると LLM は「新着の話題は？」「自分への返信は？」を
/// 推測なしに 1 発で起点を確定できる。</para>
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

    /// <summary><c>&lt;a href=...&gt;text&lt;/a&gt;</c> を可視テキストだけにする (= dat 中のリンクは
    /// LLM にとっては中身のテキストだけが意味を持つ)。</summary>
    private static readonly Regex AnchorTagRe = new(@"<a\s[^>]*>([^<]*)</a>", RegexOptions.Compiled);
    /// <summary>残ったあらゆる HTML タグ (= レアケース)。タグ自体を除去するが中身は残す。</summary>
    private static readonly Regex AnyTagRe = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>本文中のレス参照アンカー (<c>&gt;&gt;N</c> / <c>&gt;&gt;N-M</c> / <c>&gt;&gt;N,M</c>)。
    /// <c>&amp;gt;&amp;gt;</c> も CleanBody 通過後は <c>&gt;&gt;</c> に正規化されている前提。
    /// 全体マッチは <c>&gt;&gt;</c> + 数字列 (数字 / ハイフン / カンマ) 列 で受ける。</summary>
    private static readonly Regex AnchorRefRe = new(@">>(\d+(?:[\-,]\d+)*)", RegexOptions.Compiled);

    private readonly string                  _threadTitle;
    private readonly string                  _boardName;
    private readonly IReadOnlyList<Post>     _posts;

    // スレ状態スナップショット (= 会話開始時点の値を凍結)
    private readonly int?              _lastReadPostNumber;
    private readonly int?              _markPostNumber;
    private readonly IReadOnlySet<int> _ownPostNumbers;
    private readonly bool              _hasReplyToOwn;

    /// <summary>レス番号 → そのレスを <c>&gt;&gt;N</c> で参照しているレス番号集合
    /// (= 被アンカー逆引きインデックス)。ctor で 1 度だけ構築。find_replies_to / find_popular_posts で使う。</summary>
    private readonly Dictionary<int, List<int>> _inboundAnchors;

    /// <summary>会話開始時点のスナップショットのレス総数。</summary>
    public int PostCount => _posts.Count;

    /// <summary>会話開始時点のスレタイトル (= プロンプト構築側で参照する)。</summary>
    public string ThreadTitle => _threadTitle;

    /// <summary>会話開始時点の板名 (= プロンプト構築側で参照する)。</summary>
    public string BoardName => _boardName;

    public ThreadToolset(
        string                  threadTitle,
        string                  boardName,
        IReadOnlyList<Post>     postsSnapshot,
        int?                    lastReadPostNumber = null,
        int?                    markPostNumber     = null,
        IEnumerable<int>?       ownPostNumbers     = null,
        bool                    hasReplyToOwn      = false)
    {
        _threadTitle = threadTitle ?? "";
        _boardName   = boardName ?? "";
        // スナップショット (= 後から外側で動いても影響を受けない) を取る。
        _posts       = postsSnapshot.ToArray();

        _lastReadPostNumber = lastReadPostNumber;
        _markPostNumber     = markPostNumber;
        _ownPostNumbers     = ownPostNumbers is null
            ? new HashSet<int>()
            : new HashSet<int>(ownPostNumbers);
        _hasReplyToOwn      = hasReplyToOwn;

        _inboundAnchors = BuildInboundAnchorIndex(_posts);
    }

    /// <summary>本文を 1 度ずつ走査して被アンカーマップ (target -> from[]) を作る。
    /// アンカー範囲 (>>N-M) は両端含めて展開、リスト (>>N,M) は個別にカウント。
    /// 構築コストは O(レス数 × 平均参照数)、find_* 呼び出しは O(1)。</summary>
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
    /// 範囲が逆向き (例: <c>&gt;&gt;10-5</c>) なら正方向に解釈。範囲が異常に広い場合は安全のため 100 件で打ち切る。</summary>
    private static IEnumerable<int> ExtractAnchorTargets(string body)
    {
        if (string.IsNullOrEmpty(body)) yield break;
        foreach (Match m in AnchorRefRe.Matches(body))
        {
            var token = m.Groups[1].Value;
            // token は "5" or "5-7" or "5,7,9" or "5-7,9" など。
            // ハイフンを含むサブトークンを範囲、含まないものを単一値として処理する。
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
                        // 暴走防止: 1 サブトークンで 50 件まで。
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

    /// <summary>OpenAI 互換 <c>tools</c> パラメータに渡す配列を返す。要素は
    /// <c>{ type:"function", function:{ name, description, parameters:JSON Schema } }</c>。
    /// 中身は固定なので毎回同じものを返す。</summary>
    public IReadOnlyList<object> GetToolDefinitions()
    {
        return new object[]
        {
            new
            {
                type     = "function",
                function = new
                {
                    name        = "get_thread_meta",
                    description = "スレッドのメタ情報 (タイトル / 板 / 総レス数) と >>1 (オリジナルポスト) の全文を返す。会話開始時にまず呼ぶのが推奨。",
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
                    name        = "get_thread_state",
                    description = "現在のスレ状態スナップショットを返す: 既読位置 / 新着の範囲 / 「自分の書き込み」のレス番号一覧 / 直前の取得で自分宛て返信があったかフラグ。" +
                                  "「新着の話題は？」「自分への返信ある？」「いまどこまで読んだ？」系の質問の起点はまずこれを呼ぶこと。",
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
                    description = $"指定範囲のレスを取得する (1 始まり、両端含む)。1 度に最大 {MaxPostsPerCall} 件まで。広い範囲を読みたい場合は何度かに分けて呼ぶこと。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            start = new { type = "integer", description = "開始レス番号 (1 始まり、含む)" },
                            end   = new { type = "integer", description = "終了レス番号 (含む)" },
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
                    description = "本文中にキーワードを含むレスを検索し、ヒットしたレス番号と短い抜粋を返す。大小文字無視。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            keyword = new { type = "string",  description = "検索したい部分文字列" },
                            limit   = new { type = "integer", description = $"返す最大ヒット数 (既定 {DefaultSearchLimit}, 上限 {MaxSearchLimit})" },
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
                    description = "単一レスを番号指定で取得する。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            number = new { type = "integer", description = "取得するレス番号 (1 始まり)" },
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
                    description = "指定 ID (5ch の ID:XXXXXXXX) で書き込まれた全レスを返す (= 「同じ人物の発言一覧」)。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            id = new { type = "string", description = "ID 文字列 (例: \"abc1234\"。大文字小文字区別)" },
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
                    name        = "find_replies_to",
                    description = "指定レス番号 N にぶら下がっているレス (= 本文中で >>N / >>N-M / >>N,M で N を参照しているレス) を集めて返す。" +
                                  "「自分への返信を見たい」「あの話題への反応を集めたい」用途。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            number = new { type = "integer", description = "対象レス番号 (1 始まり)" },
                            limit  = new { type = "integer", description = $"返す最大件数 (既定 {DefaultRepliesLimit}, 上限 {MaxRepliesLimit})" },
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
                    name        = "get_my_posts",
                    description = "「自分の書き込み」とマークされているレスを取得する。" +
                                  "include_replies=true (既定) では各自分レスへの直接返信 (= >>N でぶら下がっているレス) もまとめて返すツリーモード。" +
                                  "include_replies=false なら自分レスのみフラットに返す。" +
                                  "なお会話開始時点のツリーはシステムプロンプト先頭に既に展開済み。" +
                                  "状況更新を確認したい / 返信本文を完全に再取得したい時にだけ呼べばよい。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            include_replies        = new { type = "boolean", description = "true: 各自分レスにその被アンカー (返信) をぶら下げてツリー化 (既定 true)。false: 自分レスのみ。" },
                            replies_per_post_limit = new { type = "integer", description = "1 自分レスあたり含める返信の最大件数 (既定 20, 上限 50)。超過分は truncated フラグで通知。" },
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
                    name        = "find_popular_posts",
                    description = "被アンカー数 (= 他レスから >>N で参照された回数) の多いレスを上位から返す。" +
                                  "「読んでおいたほうがいいレス」「盛り上がってるレス」を機械的に拾うのに使う。" +
                                  "range_start / range_end で範囲を絞ると「新着の中で人気」「特定区間で人気」を取れる。",
                    parameters  = new
                    {
                        type       = "object",
                        properties = new
                        {
                            top_k       = new { type = "integer", description = $"上位何件返すか (既定 {DefaultPopularTopK}, 上限 {MaxPopularTopK})" },
                            range_start = new { type = "integer", description = "対象範囲の開始レス番号 (省略時 1)" },
                            range_end   = new { type = "integer", description = "対象範囲の終了レス番号 (省略時 末尾)" },
                            min_count   = new { type = "integer", description = "被アンカー数の最小しきい値 (既定 1)。0 を指定すれば全レスがフラットに並ぶが通常は不要" },
                        },
                        required = Array.Empty<string>(),
                    },
                },
            },
        };
    }

    /// <summary>LLM が呼んできたツールを実行し、結果 JSON を文字列で返す。
    /// 例外 / 引数不正 / 未知ツールは <c>{"error":"..."}</c> 形式の文字列で返す
    /// (= LLM 側にとっては「ツールが失敗した」がそのまま読み取れる)。</summary>
    public string Execute(string name, string argumentsJson)
    {
        try
        {
            return name switch
            {
                "get_thread_meta"     => GetThreadMeta(),
                "get_thread_state"    => GetThreadState(),
                "get_posts"           => GetPosts(argumentsJson),
                "search_posts"        => SearchPosts(argumentsJson),
                "get_post"            => GetPost(argumentsJson),
                "get_posts_by_id"     => GetPostsById(argumentsJson),
                "find_replies_to"     => FindRepliesTo(argumentsJson),
                "find_popular_posts"  => FindPopularPosts(argumentsJson),
                "get_my_posts"        => GetMyPosts(argumentsJson),
                _                     => ErrorJson($"未知のツール: {name}"),
            };
        }
        catch (Exception ex)
        {
            return ErrorJson($"ツール実行で例外: {ex.Message}");
        }
    }

    // ---- 個別ツール ----

    private string GetThreadMeta()
    {
        var op = _posts.Count > 0 ? _posts[0] : null;
        var payload = new
        {
            title       = _threadTitle,
            board       = _boardName,
            total_posts = _posts.Count,
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
        int? newStart = null;
        int? newEnd   = null;
        int  newCount = 0;
        if (_markPostNumber is int mark && _posts.Count > 0)
        {
            // MarkPostNumber は「以降新レス」ラベルの対象 = ここから後ろが新着扱い。
            newStart = mark;
            newEnd   = _posts[^1].Number;
            if (newEnd.Value >= newStart.Value)
                newCount = newEnd.Value - newStart.Value + 1;
            else
                newCount = 0;
        }

        // hint: LLM が次にどう動くかの指針 (= 状況に応じた一言)
        string hint;
        if (_hasReplyToOwn && _ownPostNumbers.Count > 0)
            hint = "has_reply_to_own が true。own_post_numbers の各レスに find_replies_to を呼んで自分宛て返信を集めること。";
        else if (newCount > 0)
            hint = $"新着が {newCount} 件ある (>>{newStart}-{newEnd})。話題を聞かれていれば get_posts でこの範囲を読み、find_popular_posts(range_start={newStart}, range_end={newEnd}) で人気レスを補強せよ。";
        else if (_ownPostNumbers.Count > 0)
            hint = "新着なし。自分のレス (own_post_numbers) はあるが直前の取得で自分宛て返信は検出されていない。";
        else
            hint = "新着なし / 自分のレスマークなし。通常の get_posts / search_posts で読み進めること。";

        var payload = new
        {
            total_posts            = _posts.Count,
            last_read_post_number  = _lastReadPostNumber,
            new_posts_start        = newStart,
            new_posts_end          = newEnd,
            new_posts_count        = newCount,
            own_post_numbers       = _ownPostNumbers.OrderBy(n => n).ToArray(),
            has_reply_to_own       = _hasReplyToOwn,
            hint                   = hint,
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    private string GetPosts(string argsJson)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("start", out var startEl) || !TryGetIntLoose(startEl, out var start))
            return ErrorJson("start が指定されていないか、整数として読み取れません");
        if (!args.TryGetProperty("end",   out var endEl)   || !TryGetIntLoose(endEl,   out var end))
            return ErrorJson("end が指定されていないか、整数として読み取れません");

        if (_posts.Count == 0) return JsonSerializer.Serialize(new { posts = Array.Empty<object>() }, JsonOpts);

        // 範囲を [1, total] にクランプ
        var lo = Math.Max(1, start);
        var hi = Math.Min(_posts.Count, end);
        if (hi < lo)
            return ErrorJson($"範囲が空です (start={start}, end={end}, total={_posts.Count})");
        if (hi - lo + 1 > MaxPostsPerCall)
            return ErrorJson($"範囲が広すぎます ({hi - lo + 1} 件)。1 度に取れるのは {MaxPostsPerCall} 件まで。分割して呼んでください");

        var slice = new List<object>(hi - lo + 1);
        // Post.Number は 1 始まり、_posts の index は 0 始まり。番号 n のレスは _posts[n-1] にある想定だが、
        // 念のため Number で検索する (= NG あぼーんで穴が空いていても安全)。
        foreach (var p in _posts)
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

    private string SearchPosts(string argsJson)
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

        var matches  = new List<object>();
        var truncated = false;
        foreach (var p in _posts)
        {
            var body = CleanBody(p.Body);
            var idx  = body.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            // snippet: ヒット位置を中心に SearchSnippetMax 文字程度のウィンドウを切り出す。
            // 中身が短ければそのまま全文。
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

    private string GetPost(string argsJson)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("number", out var nEl) || !TryGetIntLoose(nEl, out var number))
            return ErrorJson("number が指定されていないか、整数として読み取れません");

        var p = _posts.FirstOrDefault(x => x.Number == number);
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

    private string GetPostsById(string argsJson)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return ErrorJson("id が指定されていません");
        var id = idEl.GetString() ?? "";
        if (id.Length == 0) return ErrorJson("id が空です");

        var matches = _posts
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

    private string FindRepliesTo(string argsJson)
    {
        if (!TryParseObject(argsJson, out var args))
            return ErrorJson("引数 JSON のパースに失敗");
        if (!args.TryGetProperty("number", out var nEl) || !TryGetIntLoose(nEl, out var target))
            return ErrorJson("number が指定されていないか、整数として読み取れません");

        var limit = DefaultRepliesLimit;
        if (args.TryGetProperty("limit", out var limEl) && TryGetIntLoose(limEl, out var lim))
            limit = Math.Clamp(lim, 1, MaxRepliesLimit);

        if (!_inboundAnchors.TryGetValue(target, out var fromList) || fromList.Count == 0)
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
            var p = _posts.FirstOrDefault(x => x.Number == fromN);
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

    private string FindPopularPosts(string argsJson)
    {
        var topK       = DefaultPopularTopK;
        var rangeStart = 1;
        var rangeEnd   = _posts.Count;
        var minCount   = 1;

        if (TryParseObject(argsJson, out var args))
        {
            if (args.TryGetProperty("top_k", out var topKEl) && TryGetIntLoose(topKEl, out var k))
                topK = Math.Clamp(k, 1, MaxPopularTopK);
            if (args.TryGetProperty("range_start", out var rsEl) && TryGetIntLoose(rsEl, out var rs))
                rangeStart = Math.Max(1, rs);
            if (args.TryGetProperty("range_end", out var reEl) && TryGetIntLoose(reEl, out var re))
                rangeEnd = Math.Min(_posts.Count, re);
            if (args.TryGetProperty("min_count", out var mcEl) && TryGetIntLoose(mcEl, out var mc))
                minCount = Math.Max(0, mc);
        }

        if (rangeEnd < rangeStart || _posts.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                range_start = rangeStart,
                range_end   = rangeEnd,
                top_k       = topK,
                ranked      = Array.Empty<object>(),
            }, JsonOpts);
        }

        // 範囲内のレスに絞り、(target, count) を集計して降順ソート。
        var ranked = _inboundAnchors
            .Where(kv => kv.Key >= rangeStart && kv.Key <= rangeEnd && kv.Value.Count >= minCount)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key) // タイブレークは番号昇順 (= 古いレスを優先)
            .Take(topK)
            .ToArray();

        var result = new List<object>(ranked.Length);
        foreach (var kv in ranked)
        {
            var p = _posts.FirstOrDefault(x => x.Number == kv.Key);
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

        if (_ownPostNumbers.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                total_my_posts = 0,
                include_replies = includeReplies,
                my_posts = Array.Empty<object>(),
                note     = "自分の書き込みとしてマークされたレスはありません",
            }, JsonOpts);
        }

        var sortedOwn = _ownPostNumbers.OrderBy(n => n).ToArray();
        var myPosts   = new List<object>(sortedOwn.Length);
        var totalRepliesAcrossAll = 0;
        foreach (var n in sortedOwn)
        {
            var p = _posts.FirstOrDefault(x => x.Number == n);
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

            // ツリーモード: この自分レスへの被アンカーを集める。
            var replies   = new List<object>();
            var totalForThis = 0;
            var truncatedForThis = false;
            if (_inboundAnchors.TryGetValue(n, out var fromList) && fromList.Count > 0)
            {
                totalForThis = fromList.Count;
                truncatedForThis = fromList.Count > repliesPerPostLimit;
                var picked = truncatedForThis ? fromList.Take(repliesPerPostLimit) : (IEnumerable<int>)fromList;
                foreach (var fromN in picked)
                {
                    var rp = _posts.FirstOrDefault(x => x.Number == fromN);
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

    // ---- システムプロンプト先頭注入用テキスト (= LLM が最初から見えていてほしい現況) ----

    /// <summary>会話開始時点のスレ現況を読みやすいテキスト 1 ブロックで返す。
    /// <c>get_thread_state</c> ツールの中身と同じ情報を、ツール呼び出しなしで初期プロンプトに焼き込むためのもの。</summary>
    public string BuildInitialStateSnapshot()
    {
        var sb = new StringBuilder();
        sb.Append("- 総レス数: ").Append(_posts.Count).AppendLine(" 件");
        if (_lastReadPostNumber is int lr)
            sb.Append("- 既読位置: >>").AppendLine(lr.ToString());
        else
            sb.AppendLine("- 既読位置: 未設定 (= まだスクロール痕跡が永続化されていない)");

        if (_markPostNumber is int mark && _posts.Count > 0)
        {
            var newEnd = _posts[^1].Number;
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

        if (_ownPostNumbers.Count > 0)
        {
            sb.Append("- 自分の書き込み: ");
            var sorted = _ownPostNumbers.OrderBy(n => n).ToArray();
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
        sb.AppendLine(_hasReplyToOwn
            ? "あり (直前の差分取得で自分のレスを参照したレスが来ている)"
            : "なし");

        return sb.ToString();
    }

    /// <summary>会話開始時点の「自分の書き込み + それへの直接返信」をプロンプトに埋め込み可能なテキストで返す。
    /// 1 自分レスあたり <paramref name="repliesPerOwnLimit"/> 件まで、全体で <paramref name="maxChars"/> 文字を
    /// 超えそうなら打ち切って「他は get_my_posts / find_replies_to で取得可」と書く。</summary>
    public string BuildOwnPostsWithRepliesSummary(int repliesPerOwnLimit = 10, int maxChars = 4000)
    {
        if (_ownPostNumbers.Count == 0)
            return "(自分の書き込みとしてマークされたレスはありません)";

        var sb = new StringBuilder();
        var sorted = _ownPostNumbers.OrderBy(n => n).ToArray();
        foreach (var n in sorted)
        {
            var p = _posts.FirstOrDefault(x => x.Number == n);
            if (p is null) continue;

            sb.Append(">>").Append(p.Number);
            sb.Append(" (").Append(p.DateText).Append(", ").Append(p.Name);
            if (!string.IsNullOrEmpty(p.Id)) sb.Append(", ID:").Append(p.Id);
            sb.AppendLine(")");
            sb.AppendLine(TrimSingleLine(CleanBody(p.Body), 400));
            sb.AppendLine();

            if (_inboundAnchors.TryGetValue(n, out var fromList) && fromList.Count > 0)
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
                    var rp = _posts.FirstOrDefault(x => x.Number == fromN);
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
        // 改行は半角スペースで畳む (= プロンプトの行構造を保つため)。
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
            // 後で参照するため Clone しておく (using out で doc を破棄しても安全)。
            obj = doc.RootElement.Clone();
            return obj.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            obj = default;
            return false;
        }
    }

    /// <summary>JSON の整数 or 数値文字列のどちらでも int に解釈する (LLM が "3" のように文字列で渡してくることがある)。</summary>
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
        // 日本語をそのまま出す (= \uXXXX エスケープを抑制してトークンを節約)。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 整形は不要 (= LLM 入力には無駄なホワイトスペース)。
        WriteIndented = false,
    };

    private static string ErrorJson(string message)
        => JsonSerializer.Serialize(new { error = message }, JsonOpts);
}
