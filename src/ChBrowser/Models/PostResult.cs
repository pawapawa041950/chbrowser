namespace ChBrowser.Models;

/// <summary>bbs.cgi のレスポンス HTML 解析結果。</summary>
public enum PostOutcome
{
    /// <summary>書き込みが完了 ("書きこみました" 等の成功 HTML を検出)。</summary>
    Success,

    /// <summary>1 段階目でサーバが確認画面 (Cookie チェック画面) を返した状態。
    /// <see cref="Services.Api.PostClient"/> はこれを観測したら同じパラメータで再 POST する。
    /// 呼び出し側に伝わるのは「2 段目を試したが再び確認画面で止まった」異常時のみ。</summary>
    NeedsConfirm,

    /// <summary>規制 (国外 IP / 連投制限 / 板規制) で書き込み拒否。</summary>
    BlockedByRule,

    /// <summary>どんぐりレベル不足。再試行は時間経過後に。</summary>
    LevelInsufficient,

    /// <summary>broken_acorn — acorn を破棄して再構築する必要あり。<see cref="Services.Donguri.DonguriService.HandleBrokenAcorn"/>。</summary>
    BrokenAcorn,

    /// <summary>上記いずれにも当てはまらないサーバエラー。詳細は <see cref="PostResult.Message"/>。</summary>
    UnknownError,
}

/// <summary>投稿 1 回分の結果 (= 2 段階フロー全体の最終状態)。</summary>
/// <param name="Outcome">何が起きたか。</param>
/// <param name="Message">エラー時のユーザ向けメッセージ (HTML から抜き出した本文要約)。成功時は空。</param>
/// <param name="RawHtmlSnippet">デバッグ表示用の HTML 抜粋 (先頭 N 文字)。空でも可。</param>
public sealed record PostResult(
    PostOutcome Outcome,
    string      Message,
    string      RawHtmlSnippet);
