using System;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Api;
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
    /// ユーザーは書き込みダイアログで RadioButton で切替可能。</summary>
    [ObservableProperty]
    private PostAuthMode _authMode = PostAuthMode.MailAuth;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>エラーバナーに出すメッセージ。空のときバナー非表示。</summary>
    [ObservableProperty]
    private string _errorMessage = "";

    /// <summary>ステータス領域に出す簡易情報 (送信中・成功)。</summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>送信完了後 (Outcome=Success) に true。ダイアログ側はこれを観測して Close する。</summary>
    [ObservableProperty]
    private bool _shouldClose;

    /// <summary>直近の送信結果 (UI からのデバッグ抜粋表示用)。null=まだ送っていない。</summary>
    [ObservableProperty]
    private PostResult? _lastResult;

    public bool IsNewThread => _threadKey is null;

    public IAsyncRelayCommand SubmitCommand { get; }

    /// <summary>レス書き込み用コンストラクタ。<paramref name="defaultAuthMode"/> で初期選択する認証モードを指定する。</summary>
    public PostFormViewModel(PostClient postClient, Board board, string threadKey, string threadTitle,
                             PostAuthMode defaultAuthMode = PostAuthMode.MailAuth)
    {
        _postClient  = postClient;
        _board       = board;
        _threadKey   = threadKey;
        _threadTitle = threadTitle ?? "";
        DialogTitle  = $"レスを書き込む: {threadTitle}";
        AuthMode     = defaultAuthMode;
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Message));
    }

    /// <summary>スレ立て用コンストラクタ。<paramref name="defaultAuthMode"/> で初期選択する認証モードを指定する。</summary>
    public PostFormViewModel(PostClient postClient, Board board,
                             PostAuthMode defaultAuthMode = PostAuthMode.MailAuth)
    {
        _postClient  = postClient;
        _board       = board;
        _threadKey   = null;
        _threadTitle = "";
        DialogTitle  = $"新規スレッド作成: {board.BoardName}";
        AuthMode     = defaultAuthMode;
        SubmitCommand = new AsyncRelayCommand(SubmitAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(Message) && !string.IsNullOrWhiteSpace(Subject));
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

    partial void OnMessageChanged(string value)  => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnSubjectChanged(string value)  => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value)     => SubmitCommand.NotifyCanExecuteChanged();

    private async Task SubmitAsync(CancellationToken ct)
    {
        ErrorMessage  = "";
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
                ThreadTitle: IsNewThread ? null : _threadTitle);

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
