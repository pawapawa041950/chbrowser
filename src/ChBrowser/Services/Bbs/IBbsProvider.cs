using System;
using System.Collections.Generic;
using System.Text;
using ChBrowser.Models;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Bbs;

/// <summary>掲示板提供者 (プロバイダ) が持つ能力。<see cref="IBbsProvider.Capabilities"/> で宣言し、
/// UI / ViewModel は「できないこと」をここで判断する (例: 4chan は <see cref="Posting"/> を持たない)。
/// 設計は <c>doc/multi-bbs-design.md</c> §3.2。</summary>
[Flags]
public enum BbsCapabilities
{
    None            = 0,
    /// <summary>板一覧を取得できる (bbsmenu.json / bbsmenu.html / boards.json 等)。したらばは持たない。</summary>
    BoardList       = 1 << 0,
    /// <summary>スレ一覧を取得できる。</summary>
    ThreadList      = 1 << 1,
    /// <summary>スレ本文を取得できる。</summary>
    ThreadFetch     = 1 << 2,
    /// <summary>差分取得を HTTP Range (バイト追記) で行う (5ch の生 dat)。</summary>
    DeltaByRange    = 1 << 3,
    /// <summary>差分取得を「番号以降」の範囲指定で行う (したらば rawmode / まちBBS offlaw)。</summary>
    DeltaByNumber   = 1 << 4,
    /// <summary>レス書き込みができる。</summary>
    Posting         = 1 << 5,
    /// <summary>スレ立てができる。</summary>
    ThreadCreation  = 1 << 6,
    /// <summary>書き込みに認証 (Cookie / OAuth 等) を使う。</summary>
    Auth            = 1 << 7,
    /// <summary>レスに添付ファイル (画像等) を持つ (4chan / ふたば)。</summary>
    Attachments     = 1 << 8,
    /// <summary>板の設定情報 (SETTING.TXT / setting.cgi 等) を取得できる。</summary>
    BoardInfo       = 1 << 9,
}

/// <summary>掲示板提供者。ホスト名から <see cref="BbsRegistry"/> が解決し、URL の解釈・生成、
/// エンコーディング、保存先ルート、各エンドポイントの組み立てを一手に引き受ける。
///
/// <para>設計方針 (<c>doc/multi-bbs-design.md</c> §3):</para>
/// <list type="bullet">
/// <item><description>板は <c>(host, dir)</c>、スレは <c>(host, dir, key)</c> の不透明な文字列で識別する。
///   <see cref="Board"/> には種別を持たせず、ホストから毎回この提供者を解決する (= 保存形式を変えない)。</description></item>
/// <item><description>プロトコルを知るコードはすべて提供者の実装に閉じ込め、クライアント (dat / subject / setting / post) は
///   提供者から URL とエンコーディングを受け取るだけにする。</description></item>
/// </list></summary>
public interface IBbsProvider
{
    /// <summary>設定・ログ・保存先の識別子 (例: "5ch", "shitaraba", "machi")。安定した英数字。</summary>
    string Id { get; }

    /// <summary>板一覧のトップノード等に出す表示名 (例: "5ch", "まちBBS")。</summary>
    string DisplayName { get; }

    BbsCapabilities Capabilities { get; }

    /// <summary>サーバとのやり取り (dat / subject / SETTING / 投稿フォーム) に使う文字コード。</summary>
    Encoding TextEncoding { get; }

    /// <summary>この提供者のログを置く <c>data/&lt;root&gt;/</c> のルート名一覧 (例: 5ch は "5ch.io" と "bbspink.com")。
    /// 全ログ走査はこの一覧を回る。</summary>
    IReadOnlyList<string> StorageRoots { get; }

    /// <summary>板の host が分からないとき (= 保存ディレクトリ名しか手がかりが無い「板一覧に無い取得済み板」等) に
    /// <see cref="BoardUrl"/> へ渡す既定ホスト (例: 5ch は "5ch.io"、したらばは "jbbs.shitaraba.net"、まちBBS は "machi.to")。</summary>
    string DefaultHost { get; }

    /// <summary>レス番号ジャンプ (数字キー連打) で受け付ける最大桁数。5ch は 4、板全体で一意な番号を使う掲示板は 10。</summary>
    int PostNumberDigits { get; }

    /// <summary>本文中のスレ URL を「アプリ内リンク」と判定するためのホスト末尾一覧 (例: "5ch.io", "5ch.net", "bbspink.com")。
    /// JS (thread.js) へ <c>setConfig.threadLinkRules</c> として送る。</summary>
    IReadOnlyList<string> HostSuffixes { get; }

    /// <summary>本文中のスレ URL 全体に当てる正規表現ソース (JS 構文)。名前付きグループ <c>host</c> / <c>dir</c> / <c>key</c> /
    /// <c>post</c> (任意) を持つこと。<see cref="HostSuffixes"/> とあわせて JS のスレリンク判定に使う。</summary>
    string ThreadLinkJsPattern { get; }

    /// <summary>この掲示板の既定アンカー規則 (設定 <see cref="AppConfig.AnchorRules"/> で上書き可)。
    /// <c>doc/multi-bbs-design.md</c> §6.2。</summary>
    IReadOnlyList<AnchorRule> DefaultAnchorRules { get; }

    /// <summary>このホストがこの提供者のものか (旧ホスト名も含めて判定する: 5ch.net 等)。</summary>
    bool OwnsHost(string host);

    /// <summary>旧ホスト名 / 別名を正規ホストへ (例: <c>news.5ch.net</c> → <c>news.5ch.io</c>、
    /// <c>kanto.machi.to</c> → <c>machi.to</c>)。正規化不要ならそのまま返す。</summary>
    string NormalizeHost(string host);

    /// <summary>URL を板 / スレ / 不明に分類する。<paramref name="host"/> は <see cref="NormalizeHost"/> 済み。
    /// この提供者の URL 形式でなければ null。</summary>
    AddressBarTarget? TryParseUrl(Uri uri, string host);

    /// <summary>板の正規 URL (= <see cref="Board.Url"/> と同じ形式)。</summary>
    string BoardUrl(string host, string directoryName);

    /// <summary>スレの正規 URL (アドレスバー表示・URL コピー・お気に入り・外部ブラウザ用)。
    /// <paramref name="postNumber"/> &gt; 0 ならそのレスを指す形にする。</summary>
    string ThreadUrl(string host, string directoryName, string threadKey, long postNumber = 0);

    /// <summary>スレ一覧 (subject.txt 相当) の取得 URL。</summary>
    string ThreadListUrl(Board board);

    /// <summary>スレ本文 (dat 相当) の取得 URL。差分の付け方 (Range / 番号以降) は能力で決まる。</summary>
    string ThreadFetchUrl(Board board, string threadKey);

    /// <summary>人間向けのスレページ URL (dat 落ち時のブラウザ fallback、「ブラウザで開く」)。
    /// 5ch では read.cgi。<see cref="ThreadUrl"/> と同じで構わない提供者はそう実装する。</summary>
    string ThreadPageUrl(Board board, string threadKey);

    /// <summary>板の設定情報 (SETTING.TXT 相当) の取得 URL。<see cref="BbsCapabilities.BoardInfo"/> が無ければ null。</summary>
    string? BoardInfoUrl(Board board);

    /// <summary>書き込み先エンドポイント。<see cref="BbsCapabilities.Posting"/> が無ければ null。</summary>
    string? PostEndpointUrl(Board board);

    // -----------------------------------------------------------------
    // 段階 2: スレ一覧 / スレ本文の行形式と差分方式 (提供者ごとに違う部分)
    // -----------------------------------------------------------------

    /// <summary>板一覧の取得 URL (5ch: bbsmenu.json、まちBBS: bbsmenu.html)。<see cref="BbsCapabilities.BoardList"/> が無ければ null。</summary>
    string? BoardListUrl { get; }

    /// <summary>板一覧のローカル保存に使う拡張子 ("json" / "html")。板一覧を持たない提供者は空文字。</summary>
    string BoardListCacheExtension { get; }

    /// <summary>板一覧の生バイト列を提供者の形式でパースする (5ch: bbsmenu.json、まちBBS: bbsmenu.html、エッヂ: /api/boards)。
    /// <see cref="BoardCategory.ProviderId"/> は <see cref="Id"/>。板一覧を持たない提供者は空配列。</summary>
    IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes);

    /// <summary>true なら 5ch の生 dat (Range 差分・行番号 = レス番号) をそのまま保存する。
    /// false なら取得結果を <see cref="Api.NumberedLogFormat"/> (番号列付きログ) に変換して保存し、
    /// 差分は <see cref="ThreadFetchRangeUrl"/> (番号以降) で取る。</summary>
    bool UsesNativeDat { get; }

    /// <summary>「<paramref name="fromNumber"/> 番以降」の差分取得 URL (<see cref="BbsCapabilities.DeltaByNumber"/> 用)。
    /// 番号以降取得を持たない提供者 (5ch) では <see cref="NotSupportedException"/>。</summary>
    string ThreadFetchRangeUrl(Board board, string threadKey, long fromNumber);

    /// <summary>スレ一覧 (subject.txt 相当) の生バイト列を提供者の形式・文字コードでパースする。
    /// <see cref="ThreadInfo.Order"/> は出現順 (1 始まり)。</summary>
    IReadOnlyList<ThreadInfo> ParseThreadList(byte[] bytes);

    /// <summary>スレ本文取得の 1 行 (文字コード復号済み、改行除去済み) をサーバの実番号付きの <see cref="Post"/> に変換する。
    /// 空行・壊れた行は null。生 dat の提供者 (5ch、行番号 = レス番号) では使わないので null を返してよい。</summary>
    Post? ParseThreadLine(string line);

    // -----------------------------------------------------------------
    // 段階 2: 書き込み (提供者ごとに違う部分)。設計は doc/multi-bbs-design.md §7。
    // -----------------------------------------------------------------

    /// <summary>投稿フォームで使える項目 (投稿ダイアログの UI 出し分け用)。</summary>
    PostFormSpec PostForm { get; }

    /// <summary>1 段目 POST の内容を組み立てる (エンドポイント / フィールド / エンコーディング / Referer)。
    /// <see cref="BbsCapabilities.Posting"/> が無ければ <see cref="NotSupportedException"/>。</summary>
    PostSubmission BuildPostSubmission(PostRequest request);

    /// <summary>レスポンス HTML (提供者の文字コードで復号済み) と HTTP 上の手がかり (リダイレクト等) から結果を判定する。</summary>
    PostResult ClassifyPostResponse(string html, PostResponseContext context);
}
