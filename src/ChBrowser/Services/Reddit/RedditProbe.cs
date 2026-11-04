using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChBrowser.Services.Reddit;

/// <summary>reddit の接続テスト (設定 → 認証 → 「接続テスト」)。ログインセッションの診断 (<see cref="RedditWebViewHost.DiagnoseAsync"/>:
/// Cookie とアプリが使う要求を 1 回ずつ) を行い、結果を <c>data/reddit.com/probe.txt</c> に書き出す。
///
/// <para>以前はアプリの通信経路で r/popular の JSON 一覧 (hot.json) を取り、そこから選んだスレで本文・機械翻訳 (?tl=ja)・動画も
/// 調べていたが、JSON の一覧が 2026-11-04 から弾かれるため最初の要求で止まり、後の段は動かなくなっていた。
/// reddit への要求を減らすため外した (スレ本文は診断の comments.json で確かめる)。</para></summary>
public static class RedditProbe
{
    public static async Task<string> RunAsync(string reportPath, CancellationToken ct, RedditWebViewHost? host = null)
    {
        var sb = new StringBuilder();
        void L(string s) { sb.AppendLine(s); ChBrowser.Services.Logging.LogService.Instance.Write("[redditProbe] " + s); }
        L($"reddit 接続テスト {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        // セッションの診断 (窓を出さず、通信経路を通さずに直接確かめる)
        if (host is not null)
        {
            try
            {
                foreach (var line in await host.DiagnoseAsync(ct, Path.GetDirectoryName(reportPath)).ConfigureAwait(false)) L(line);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                L($"診断に失敗: {ex.Message}");
            }
        }
        else L("診断できません (reddit の隠しページがありません)");

        var text = sb.ToString();
        try { await File.WriteAllTextAsync(reportPath, text, new UTF8Encoding(false), ct).ConfigureAwait(false); }
        catch (IOException ex) { text += $"(レポートの保存に失敗: {ex.Message})"; }
        return text;
    }
}
