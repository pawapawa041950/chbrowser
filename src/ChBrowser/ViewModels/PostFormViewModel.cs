using System;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Api;
using ChBrowser.Services.Bbs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>
/// 投稿ダイアログ (PostDialog) のバインディング元。
/// レス書き込みとスレ立ての両方を扱える (= <see cref="ThreadKey"/> が空ならスレ立て、そうでなければ返信)。
///
/// 送信中は <see cref="IsBusy"/>=true、完了/失敗で <see cref="LastResult"/> が更新される。
/// 失敗時は本文を保持したままダイアログを閉じない (設計書 §2.2)。
/// </summary>
public sealed partial class PostFormViewModel : ObservableObject
{
    private readonly PostClient _postClient;
    private readonly Board      _board;
    private readonly string?    _threadKey;
    /// <summary>レス書き込み時の元スレタイトル (kakikomi.txt 用)。新スレ立て時は空文字。</summary>
    private readonly string     _threadTitle;
    /// <summary>掲示板側の認証トークン (設定 <see cref="AppConfig.PostAuthTokens"/>、エッヂ等)。無ければ null。</summary>
    private readonly string?    _authToken;

    public string DialogTitle { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _mail = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private string _subject = ""; // スレ立て時のみ使用

    /// <summary>true で <see cref="Mail"/> に "sage" を自動投入。トグルで戻すと空に。</summary>
    [ObservableProperty]
    private bool _isSage;

    /// <summary>送信時の認証モード (= どの Cookie 集合をリクエストに添付するか)。
    /// 既定はコンストラクタ呼び出し側 (MainViewModel) が現在の どんぐり 状態を見て決める:
    /// メール認証ログイン中なら MailAuth、acorn だけなら Cookie、なにもないなら None。
    /// ユーザーは書き込みダイアログで RadioButton で切替可能。
    /// どんぐりを使わない提供者 (<see cref="PostForm"/>.UsesDonguriAuth == false) では常に <see cref="PostAuthMode.None"/> に固定される。</summary>
    [ObservableProperty]
    private PostAuthMode _authMode = PostAuthMode.MailAuth;

    /// <summary>書き込み先の提供者が宣言する投稿フォーム仕様 (名前 / メール / スレ立て / どんぐり認証の有無)。
    /// PostDialog はこれを見て UI を出し分ける。</summary>
    public PostFormSpec PostForm { get; }

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>エラーバナーに出すメッセージ。空のときバナー非表示。</summary>
    [ObservableProperty]
    private string _errorMessage = "";

    /// <summary>ステータス領域に出す簡易情報 (送信中・成功)。</summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>掲示板側の認証が必要なとき (<see cref="PostOutcome.AuthRequired"/>) にブラウザで開く認証ページ URL。
    /// 空ならエラーバナーの「認証ページを開く」ボタンは非表示。</summary>
    [ObservableProperty]
    private string _authUrl = "";

    /// <summary>認証ページに入力する認証コード (エッヂは 6 桁)。「認証ページを開く」でクリップボードへコピーする。</summary>
    [ObservableProperty]
    private string _authCode = "";

    /// <summary>認証コードをクリップボードへコピーし、<see cref="AuthUrl"/> を既定ブラウザで開く。</summary>
    public IRelayCommand OpenAuthPageCommand { get; }

    /// <summary>板の SETTING.TXT で指定された 1 投稿あたりの行数上限 (= <c>BBS_LINE_NUMBER</c>)。
    /// 取得失敗 / SETTING.TXT に該当キーが無い場合は null。表示時は「上限不明」扱いになる。</summary>
    [ObservableProperty]
    private int? _lineLimit;

    /// <summary>本文の現在行数 (LF 換算)。<see cref="Message"/> 変化に同期して
    /// <see cref="OnMessageChanged"/> から手動で通知発火する。</summary>
    public int CurrentLineCount => CountLines(Message);

    /// <summary>「7 / 32 行」のような表示用文字列。上限不明なら「7 行」のみ。
    /// PostDialog のラベルが直接 Binding する派生プロパティ。</summary>
    public string LineCountText =>
        LineLimit is int limit
            ? $"{CurrentLineCount} / {limit} 行"
            : $"{CurrentLineCount} 行";

    /// <summary>現在行数が上限を超えているか (= ラベルを赤字に変える判定に使う)。
    /// 上限不明 (= null) のときは常に false。</summary>
    public bool IsOverLineLimit => LineLimit is int limit && CurrentLineCount > limit;

    /// <summary>送信完了後 (Outcome=Success) に true。ダイアログ側はこれを観測して Close する。</summary>
    [ObservableProperty]
    private bool _shouldClose;

    /// <summary>直近の送信結果 (UI からのデバッグ抜粋表示用)。null=まだ送っていない。</summary>
    [ObservableProperty]
    private PostResult? _lastResult;

    public bool IsNewThread => _threadKey is null;

    /// <summary>書き込み対象スレの板。書き込み先確認ダイアログで「今表示中のスレと同じか」判定に使う。</summary>
    public Board Board => _board;

    /// <summary>書き込み対象スレのキー (=dat ファイル名から拡張子を除いたもの)。
    /// スレ立てモードでは null (= 比較対象スレが無い)。</summary>
    public string? ThreadKey => _threadKey;

    /// <summary>送信開始直前 (= IsBusy=true セット前) に呼ばれる確認 hook。
    /// false を返すと送信を中止し、ダイアログは閉じない (= ユーザ入力を保持)。
    /// View 側で「現在表示中のスレと異なるスレに書き込もうとしている」警告ダイアログを出すために使う。</summary>
    public Func<Task<bool>>? PreSubmitConfirm { get; set; }

    public IAsyncRelayCommand SubmitCommand { get; }

    /// <summary>レス書き込み用コンストラクタ。<paramref name="defaultAuthMode"/> で初期選択する認証モードを指定する。</summary>
    public PostFormViewModel(PostClient postClient, Board board, string threadKey, string threadTitle,
                             PostAuthMode defaultAuthMode = PostAuthMode.MailAuth, string? authToken = null)
    {
        _postClient  = postClient;
        _board       = board;
        _threadKey   = threadKey;
        _threadTitle = threadTitle ?? "";
        _authToken   = authToken;
        DialogTitle  = $"レスを書き込む: {threadTitle}";
        PostForm     = BbsRegistry.ResolveOrDefault(board.Host).PostForm;
        AuthMode     = PostForm.UsesDonguriAuth ? defaultAuthMode : PostAuthMode.None;
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Message));
        OpenAuthPageCommand = new RelayCommand(OpenAuthPage, () => !string.IsNullOrEmpty(AuthUrl));
    }

    /// <summary>スレ立て用コンストラクタ。<paramref name="defaultAuthMode"/> で初期選択する認証モードを指定する。</summary>
    public PostFormViewModel(PostClient postClient, Board board,
                             PostAuthMode defaultAuthMode = PostAuthMode.MailAuth, string? authToken = null)
    {
        _postClient  = postClient;
        _board       = board;
        _threadKey   = null;
        _threadTitle = "";
        _authToken   = authToken;
        DialogTitle  = $"新規スレッド作成: {board.BoardName}";
        PostForm     = BbsRegistry.ResolveOrDefault(board.Host).PostForm;
        AuthMode     = PostForm.UsesDonguriAuth ? defaultAuthMode : PostAuthMode.None;
        SubmitCommand = new AsyncRelayCommand(SubmitAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(Message) && !string.IsNullOrWhiteSpace(Subject));
        OpenAuthPageCommand = new RelayCommand(OpenAuthPage, () => !string.IsNullOrEmpty(AuthUrl));
    }

    partial void OnAuthUrlChanged(string value) => OpenAuthPageCommand.NotifyCanExecuteChanged();

    /// <summary>「認証ページを開く (コードをコピー)」。コードをクリップボードへ入れてから既定ブラウザで認証ページを開く。
    /// エッヂの認証ページは Cloudflare の確認を通るためアプリ内では完結できず、通常のブラウザに任せる。</summary>
    private void OpenAuthPage()
    {
        if (string.IsNullOrEmpty(AuthUrl)) return;
        try
        {
            if (!string.IsNullOrEmpty(AuthCode)) System.Windows.Clipboard.SetText(AuthCode);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PostFormViewModel] clipboard failed: {ex.Message}");
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = AuthUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"ブラウザで認証ページを開けませんでした: {ex.Message}\n{AuthUrl}";
        }
    }

    partial void OnIsSageChanged(bool value)
    {
        if (value)
        {
            if (string.IsNullOrEmpty(Mail)) Mail = "sage";
        }
        else
        {
            // ユーザが手動で他のメール値を入れていたら触らない
            if (string.Equals(Mail, "sage", StringComparison.Ordinal)) Mail = "";
        }
    }

    partial void OnMessageChanged(string value)
    {
        SubmitCommand.NotifyCanExecuteChanged();
        // 行数派生プロパティを手動で通知発火 (= ObservableProperty にしていないため自動更新されない)。
        OnPropertyChanged(nameof(CurrentLineCount));
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(IsOverLineLimit));
    }
    partial void OnSubjectChanged(string value)  => SubmitCommand.NotifyCanExecuteChanged();
    /// <summary>どんぐりを使わない提供者では None 以外に切り替えられても None に戻す (UI 側は RadioButton を出さないが念のため)。</summary>
    partial void OnAuthModeChanged(PostAuthMode value)
    {
        if (!PostForm.UsesDonguriAuth && value != PostAuthMode.None) AuthMode = PostAuthMode.None;
    }
    partial void OnIsBusyChanged(bool value)     => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnLineLimitChanged(int? value)
    {
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(IsOverLineLimit));
    }

    /// <summary>本文の改行数を数える。LF / CRLF どちらでも「LF の個数 + 1」を行数とする方針
    /// (= CR は数えない、CRLF の \r 部分が二重カウントされるのを防ぐ)。
    /// 空文字は 0 行。「最終行に文字が無い (= 末尾 LF) 」場合も新しい行扱いになるが、
    /// これは TextBox の見た目 (カーソルが次の行に居る) と一致する直感的挙動。</summary>
    private static int CountLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var n = 1;
        foreach (var c in s) if (c == '\n') n++;
        return n;
    }

    private async Task SubmitAsync(CancellationToken ct)
    {
        // 書き込み先確認 hook (= View 側で「表示中スレと違うスレに書こうとしてないか」を確認する)。
        // false が返ると送信を中止し、IsBusy も触らずに抜ける = ダイアログは開いたまま、入力内容も保持される。
        if (PreSubmitConfirm is not null)
        {
            var ok = await PreSubmitConfirm().ConfigureAwait(true);
            if (!ok) return;
        }
        ErrorMessage  = "";
        AuthUrl       = "";
        AuthCode      = "";
        StatusMessage = "送信中…";
        IsBusy        = true;
        try
        {
            var req = new PostRequest(
                Board:       _board,
                ThreadKey:   _threadKey,
                Subject:     IsNewThread ? Subject : null,
                Name:        Name,
                Mail:        Mail,
                Message:     Message,
                AuthMode:    AuthMode,
                ThreadTitle: IsNewThread ? null : _threadTitle,
                AuthToken:   _authToken);

            var result = await _postClient.PostAsync(req, ct).ConfigureAwait(true);
            LastResult = result;

            switch (result.Outcome)
            {
                case PostOutcome.Success:
                    StatusMessage = "書き込みました。";
                    ShouldClose   = true;
                    break;
                case PostOutcome.NeedsConfirm:
                    ErrorMessage  = "確認画面で止まりました。Cookie を取得できなかった可能性があります。もう一度お試しください。";
                    StatusMessage = "";
                    break;
                case PostOutcome.LevelInsufficient:
                    ErrorMessage  = "どんぐりレベルが不足しています。しばらく待ってから再投稿してください。";
                    StatusMessage = "";
                    break;
                case PostOutcome.BlockedByRule:
                    ErrorMessage  = string.IsNullOrEmpty(result.Message)
                        ? "規制により書き込めませんでした。"
                        : $"規制により書き込めませんでした: {result.Message}";
                    StatusMessage = "";
                    break;
                case PostOutcome.BrokenAcorn:
                    ErrorMessage  = "どんぐり Cookie が破損しています。再取得しました — もう一度送信してみてください。";
                    StatusMessage = "";
                    break;
                case PostOutcome.AuthRequired:
                    AuthUrl  = result.AuthUrl  ?? "";
                    AuthCode = result.AuthCode ?? "";
                    ErrorMessage = string.IsNullOrEmpty(AuthCode)
                        ? "掲示板側の認証が必要です。「認証ページを開く」でブラウザから認証してから、もう一度送信してください。"
                          + (string.IsNullOrEmpty(result.Message) ? "" : $" ({result.Message})")
                        : $"掲示板側の認証が必要です。認証コード {AuthCode} を、「認証ページを開く」で開いたブラウザの認証ページに入力してください "
                          + "(コードはクリップボードにコピーされます)。認証が済んだら、このままもう一度送信してください。";
                    StatusMessage = "";
                    break;
                default:
                    ErrorMessage  = string.IsNullOrEmpty(result.Message)
                        ? "未知のエラーで書き込みに失敗しました。"
                        : $"書き込みに失敗しました: {result.Message}";
                    StatusMessage = "";
                    break;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage  = $"送信時にエラー: {ex.Message}";
            StatusMessage = "";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
