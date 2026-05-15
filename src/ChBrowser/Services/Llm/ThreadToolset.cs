using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary><c>&lt;a href=...&gt;text&lt;/a&gt;</c> を可視テキストだけにする (= dat 中のリンクは
    /// LLM にとっては中身のテキストだけが意味を持つ)。</summary>
    private static readonly Regex AnchorTagRe = new(@"<a\s[^>]*>([^<]*)</a>", RegexOptions.Compiled);
    /// <summary>残ったあらゆる HTML タグ (= レアケース)。タグ自体を除去するが中身は残す。</summary>
    private static readonly Regex AnyTagRe = new(@"<[^>]+>", RegexOptions.Compiled);

    private readonly string                  _threadTitle;
    private readonly string                  _boardName;
    private readonly IReadOnlyList<Post>     _posts;

    /// <summary>会話開始時点のスナップショットのレス総数。</summary>
    public int PostCount => _posts.Count;

    /// <summary>会話開始時点のスレタイトル (= プロンプト構築側で参照する)。</summary>
    public string ThreadTitle => _threadTitle;

    /// <summary>会話開始時点の板名 (= プロンプト構築側で参照する)。</summary>
    public string BoardName => _boardName;

    public ThreadToolset(string threadTitle, string boardName, IReadOnlyList<Post> postsSnapshot)
    {
        _threadTitle = threadTitle ?? "";
        _boardName   = boardName ?? "";
        // スナップショット (= 後から外側で動いても影響を受けない) を取る。
        _posts       = postsSnapshot.ToArray();
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
                "get_thread_meta"  => GetThreadMeta(),
                "get_posts"        => GetPosts(argumentsJson),
                "search_posts"     => SearchPosts(argumentsJson),
                "get_post"         => GetPost(argumentsJson),
                "get_posts_by_id"  => GetPostsById(argumentsJson),
                _                  => ErrorJson($"未知のツール: {name}"),
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
