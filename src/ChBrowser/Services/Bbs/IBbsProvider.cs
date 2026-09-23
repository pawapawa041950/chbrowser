using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>スレ一覧が並び順の選択とページング (続きを読み込む) を持つ (reddit)。<see cref="IBbsProvider.ListingSorts"/>。</summary>
    ListingPaging   = 1 << 10,
}

/// <summary>スレ本文の取得方式 (<c>doc/reddit-design.md</c> §3 B3)。</summary>
public enum ThreadFetchStrategy
{
    /// <summary>5ch の生 dat を Range 差分で追記保存 (5ch / bbspink / エッヂ)。</summary>
    RawDat,
    /// <summary>「番号以降」の差分を番号列付きログへ追記 (したらば / まちBBS)。</summary>
    NumberedDelta,
    /// <summary>毎回スレ全体 (JSON 等) を取り、外部 ID で新着を判別して番号列付きログへ追記 (reddit / 4chan / ふたば)。
    /// 提供者は <see cref="ISnapshotThreadProvider"/> を実装する。</summary>
    Snapshot,
}

/// <summary>スレ一覧の並び順 1 つ (reddit の hot / new / top 等)。<see cref="Value"/> は提供者が URL 組み立てに使う値。</summary>
public sealed record ListingSort(string Value, string DisplayName);

/// <summary>スレ一覧 1 回分の取得条件。<see cref="Sort"/> = null は提供者の既定、<see cref="After"/> = null は 1 ページ目。</summary>
public sealed record ThreadListQuery(string? Sort = null, string? After = null)
{
    public static ThreadListQuery Default { get; } = new();
    public bool IsFirstPage => string.IsNullOrEmpty(After);
}

/// <summary>スレ一覧 1 ページ分。<see cref="NextCursor"/> が null なら続きは無い。</summary>
public sealed record ThreadListPage(IReadOnlyList<ThreadInfo> Items, string? NextCursor);

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

    // -----------------------------------------------------------------
    // reddit 対応で広げた拡張点 (doc/reddit-design.md §3)。既定実装 = 従来の動作なので、既存の提供者は実装不要。
    // -----------------------------------------------------------------

    /// <summary>スレ本文の取得方式 (B3)。既定は <see cref="UsesNativeDat"/> から導く (true → RawDat、false → NumberedDelta)。
    /// Snapshot を返す提供者は <see cref="ISnapshotThreadProvider"/> を実装すること。</summary>
    ThreadFetchStrategy FetchStrategy => UsesNativeDat ? ThreadFetchStrategy.RawDat : ThreadFetchStrategy.NumberedDelta;

    /// <summary>スレ一覧の並び順の選択肢 (B5)。空なら並び順の選択は無い。先頭が既定。</summary>
    IReadOnlyList<ListingSort> ListingSorts => Array.Empty<ListingSort>();

    /// <summary>並び順・ページ指定付きのスレ一覧 URL (B5)。既定は <see cref="ThreadListUrl(Board)"/> (条件を無視)。</summary>
    string ThreadListUrl(Board board, ThreadListQuery query) => ThreadListUrl(board);

    /// <summary>スレ一覧 1 ページ分のパース (B5)。既定は <see cref="ParseThreadList"/> で、続きは無し。</summary>
    ThreadListPage ParseThreadListPage(byte[] bytes) => new(ParseThreadList(bytes), null);

    /// <summary>スレ一覧がその板の全スレを含むか (B5)。true (既定) なら「一覧に無いローカルログ = dat 落ち」とみなす。
    /// reddit のように一覧が常に部分集合の掲示板は false (dat 落ち判定をしない)。</summary>
    bool ThreadListIsComplete => true;

    /// <summary>板一覧が空のときに板一覧ペインへ出す案内 (B7)。null なら共通の「未取得 (板一覧 → …)」文言。</summary>
    string? BoardListEmptyHint => null;

    /// <summary>板情報 (SETTING.TXT 相当) の生バイト列を KEY → VALUE にする (B8)。既定は SETTING.TXT 形式 (<c>KEY=VALUE</c> 行) を
    /// <see cref="TextEncoding"/> で読む。板名は <c>BBS_TITLE</c> キーで返すこと (板名解決が使う)。</summary>
    IReadOnlyDictionary<string, string> ParseBoardInfo(byte[] bytes) => Api.SettingTxtClient.ParseKeyValue(bytes, TextEncoding);

    /// <summary>この提供者の要求を通常の HTTP ではなく専用の経路 (<see cref="IProviderTransport"/>) に回すホスト (B10)。
    /// 空 (既定) なら通常の HttpClient で送る。経路の登録は <see cref="ProviderTransports"/>。</summary>
    IReadOnlyList<string> RoutedHosts => Array.Empty<string>();

    /// <summary>スレ表示でレス番号を見せるか (B11)。false (reddit) なら番号の位置にメニュー記号を出し、返信先は
    /// <see cref="PostExtra.ParentNumber"/> から「↳ 投稿者」として示す。番号自体は内部の鍵として使い続ける。</summary>
    bool ShowsPostNumbers => true;

    /// <summary>名前欄の「ワッチョイ」(<c>xxxx-yyyy</c>) を検出して一覧・装飾するか。5ch 系の慣習。
    /// reddit はユーザー名に <c>xxxx-xxxx</c> を含みうる (<c>Particular-Lemon-556</c>) ので false。</summary>
    bool UsesWatchoi => true;

    /// <summary>キーワードで板を検索できるか (板一覧を持たない掲示板の「検索」項目。したらば / reddit)。</summary>
    bool SupportsBoardSearch => false;

    /// <summary>キーワードで板を検索する。<see cref="SupportsBoardSearch"/> が false なら <see cref="NotSupportedException"/>。</summary>
    Task<IReadOnlyList<BoardSearchHit>> SearchBoardsAsync(HttpClient http, string keyword, CancellationToken ct)
        => throw new NotSupportedException($"{DisplayName} は板の検索に対応していません。");

    /// <summary>レスへの評価 (いいね / 賛否) の仕様。null (既定) なら評価できない (評価値 <see cref="PostExtra.Score"/> があれば表示だけ)。
    /// reddit は賛成・反対・取り消し、ふたばの「そうだね」は加算のみ (取り消し不可)。</summary>
    VoteSpec? Voting => null;

    /// <summary>レス <paramref name="post"/> に評価を送る。<paramref name="direction"/> は 1 = 賛成 (いいね)、-1 = 反対、0 = 取り消し。
    /// <see cref="Voting"/> が null なら <see cref="NotSupportedException"/>。</summary>
    Task<VoteResult> VoteAsync(HttpClient http, Board board, string threadKey, Post post, int direction, CancellationToken ct)
        => throw new NotSupportedException($"{DisplayName} はレスの評価に対応していません。");

    /// <summary>投稿者ごとの情報 (アイコン・プロフィール) を持つか。true ならスレ表示で名前の前にアイコンを出し、
    /// クリックでプロフィールのカードを出す。投稿者は名前で識別し、取得にはアカウント ID (<see cref="PostExtra.AuthorId"/>) を使う。</summary>
    bool SupportsAuthorProfiles => false;

    /// <summary>アカウント ID の集合から投稿者情報を取る。要求回数の予算に配慮して一部だけ問い合わせてよい
    /// (<see cref="AuthorProfileFetch.Asked"/> に実際に問い合わせた ID を入れる。残りは次の機会に取る)。</summary>
    Task<AuthorProfileFetch> FetchAuthorProfilesAsync(HttpClient http, IReadOnlyCollection<string> authorIds, CancellationToken ct)
        => Task.FromResult(AuthorProfileFetch.Empty);
}

/// <summary>投稿者情報の取得結果。<see cref="Found"/> はアカウント ID → 情報、<see cref="Asked"/> は問い合わせた ID
/// (問い合わせたのに <see cref="Found"/> に無い = 退会・凍結等で見つからない)。</summary>
public sealed record AuthorProfileFetch(IReadOnlyDictionary<string, AuthorProfile> Found, IReadOnlyCollection<string> Asked)
{
    public static AuthorProfileFetch Empty { get; } = new(new Dictionary<string, AuthorProfile>(), Array.Empty<string>());
}

/// <summary>投稿者 1 人分の情報 (スレ表示のアイコンとプロフィールのカード)。<see cref="DisplayName"/> は表示用 (reddit: <c>u/name</c>)、
/// <see cref="Stats"/> はカードに並べる項目 (reddit: 投稿カルマ / コメントカルマ)、<see cref="CreatedEpoch"/> はアカウント作成日時。</summary>
public sealed record AuthorProfile(
    string                     Name,
    string                     DisplayName,
    string?                    IconUrl,
    string?                    ProfileUrl,
    long?                      CreatedEpoch,
    IReadOnlyList<AuthorStat>  Stats,
    bool                       Nsfw = false);

/// <summary>プロフィールのカードの 1 項目 (例: 「投稿カルマ」「1,234」)。</summary>
public sealed record AuthorStat(string Label, string Value);

/// <summary>評価の種類。</summary>
public enum VoteMode
{
    /// <summary>賛成と反対 (reddit)。評価値は賛成 − 反対。</summary>
    UpDown,
    /// <summary>賛成 (いいね) だけ (ふたばの「そうだね」等)。</summary>
    UpOnly,
}

/// <summary>レス評価の仕様。<see cref="UpLabel"/> / <see cref="DownLabel"/> はボタンの表示 (評価値の前に付く)。
/// <see cref="CanUndo"/> が false なら一度押したら取り消せない (押し済みのボタンは押せない)。</summary>
public sealed record VoteSpec(VoteMode Mode, string UpLabel = "\U0001F44D", string DownLabel = "\U0001F44E", bool CanUndo = true);

/// <summary>評価の送信結果。<see cref="AuthRequired"/> はログインが必要 (失効) だったことを示す。</summary>
public sealed record VoteResult(bool Ok, string? Message = null, bool AuthRequired = false)
{
    public static VoteResult Success { get; } = new(true);
}

/// <summary>板検索の 1 件。<see cref="Description"/> は説明文 (スレ一覧の「板」列に出す)、<see cref="Members"/> は購読者数など (「数」列)。</summary>
public sealed record BoardSearchHit(Board Board, string Description, long? Members = null);

/// <summary>Snapshot 方式 (<see cref="ThreadFetchStrategy.Snapshot"/>) の提供者が実装する、スレ全体の取得 (B3)。
/// 複数要求 (reddit の morechildren 等) を伴いうるので IO は提供者が持つ。採番・保存・差分判定は共通の
/// <see cref="SnapshotThreadFetcher"/> が行う。</summary>
public interface ISnapshotThreadProvider
{
    Task<ThreadSnapshot> FetchSnapshotAsync(
        HttpClient http, Board board, string threadKey, SnapshotFetchOptions options, CancellationToken ct);
}

/// <summary>スナップショット取得の条件。<see cref="KnownExternalIds"/> は既に保存済みの投稿 ID、<see cref="KnownParentIds"/> は
/// 保存済みの投稿のうち返信を 1 件以上持つものの ID (提供者が展開を省く判断に使ってよい。reddit の「continue this thread」は
/// 親が既に子を持っていれば取り直さない)。
/// <see cref="OnPartial"/> があれば、提供者は要求 1 回ごとに「今回新しく得た投稿」を渡してよい (1 回目の先頭はスレ本体)。
/// 受け手 (<see cref="SnapshotThreadFetcher"/>) はその場で採番・保存・表示するので、大きいスレでも最初の要求の分から表示が始まる。
/// 渡さなかった投稿は最後に <see cref="ThreadSnapshot"/> からまとめて処理される。</summary>
public sealed record SnapshotFetchOptions(
    IReadOnlySet<string>                        KnownExternalIds,
    int                                         MaxExpansions  = 10,
    IReadOnlySet<string>?                       KnownParentIds = null,
    Func<IReadOnlyList<SnapshotPost>, Task>?    OnPartial      = null);

/// <summary>スナップショット中の 1 投稿 (番号はまだ無い)。<see cref="ParentExternalId"/> が null / スレ本体の ID ならトップレベル。
/// <see cref="Ext"/> の <c>ParentNumber</c> / <c>ExternalId</c> は <see cref="SnapshotThreadFetcher"/> が埋める。</summary>
public sealed record SnapshotPost(
    string     ExternalId,
    string?    ParentExternalId,
    long       CreatedEpoch,
    string     Name,
    string     Mail,
    string     DateText,
    string     Id,
    string     Body,
    string?    ThreadTitle,
    PostExtra? Ext = null);

/// <summary>スレ全体の取得結果。<see cref="Posts"/> の先頭がスレ本体 (レス 1 になる)。
/// <see cref="Truncated"/> は取り切れなかった分 (reddit の more) が残っていることを示す (次回取得で埋まる)。</summary>
public sealed record ThreadSnapshot(IReadOnlyList<SnapshotPost> Posts, bool Truncated = false, string? ProviderCursor = null);
