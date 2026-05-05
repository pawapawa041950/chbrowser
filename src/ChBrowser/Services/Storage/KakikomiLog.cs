using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ChBrowser.Services.Storage;

/// <summary>1 件の書き込み記録 (kakikomi.txt の 1 エントリ)。
/// 板 / スレ情報を URL に集約し、本文は <see cref="Body"/> にそのまま保持する (改行込み)。</summary>
public sealed record KakikomiEntry(
    DateTime When,
    string   Subject,
    string   Url,
    string   Name,
    string   Mail,
    string   Body);

/// <summary>
/// 書き込み記録 (kakikomi.txt) の append-only ロガー。
///
/// <para>フォーマット: Jane Xeno 互換。1 エントリは:
/// <code>
/// --------------------------------------------
/// Date   : YY/MM/DD HH:MM:SS
/// Subject: スレタイトル
/// URL    : https://...
/// FROM   : 名前
/// MAIL   : メール (sage 等)
/// (空行)
/// 本文行 1
/// 本文行 2
/// (空行)
/// </code>
/// 区切り行は 44 個のハイフン。</para>
///
/// <para>運用方針:
/// <list type="bullet">
/// <item><description>UTF-8 (BOM なし) + CRLF</description></item>
/// <item><description>常時オープンしない (= 追記時だけ open → 即 close)。<see cref="FileShare.ReadWrite"/> 指定で
///                    ユーザがメモ帳等で同時編集していてもブロックしない</description></item>
/// <item><description>append-only — 既存内容には触らない (ユーザの手編集が消えない)</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class KakikomiLog
{
    private const string Separator = "--------------------------------------------"; // 44 hyphens
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _filePath;
    private readonly object _lock = new();

    /// <summary>true で <see cref="AppendEntry"/> 時に追記する。false なら no-op (= 設定の即時反映用)。
    /// AppConfig.EnableKakikomiLog から App.ApplyConfigImmediate 経由で書き込まれる。</summary>
    public bool IsEnabled { get; set; } = true;

    public KakikomiLog(DataPaths paths) => _filePath = paths.KakikomiTxtPath;

    /// <summary>1 エントリを kakikomi.txt の末尾に追記する。失敗時は Debug.WriteLine のみで例外は投げない
    /// (= 書き込み成功フローを止めないため)。<see cref="IsEnabled"/> が false なら何もしない。</summary>
    public void AppendEntry(KakikomiEntry entry)
    {
        if (!IsEnabled) return;
        try
        {
            var text  = FormatEntry(entry);
            var bytes = Utf8NoBom.GetBytes(text);

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            // FileMode.Append は新規時自動作成 + 既存時は末尾位置で open。
            // FileShare.ReadWrite を指定してメモ帳等の同時オープンを許容する。
            // _lock で同 process 内の同時書き込みを直列化 (= 行混在防止)。
            lock (_lock)
            {
                using var fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KakikomiLog] append failed: {ex.Message}");
        }
    }

    /// <summary>Jane Xeno 形式に整形する。改行は CRLF、本文末尾の空行は 1 個に揃える。</summary>
    private static string FormatEntry(KakikomiEntry e)
    {
        var sb = new StringBuilder();
        sb.Append(Separator).Append("\r\n");
        sb.Append("Date   : ").Append(e.When.ToString("yy/MM/dd HH:mm:ss")).Append("\r\n");
        sb.Append("Subject: ").Append(e.Subject ?? "").Append("\r\n");
        sb.Append("URL    : ").Append(e.Url     ?? "").Append("\r\n");
        sb.Append("FROM   : ").Append(e.Name    ?? "").Append("\r\n");
        sb.Append("MAIL   : ").Append(e.Mail    ?? "").Append("\r\n");
        sb.Append("\r\n");
        sb.Append(NormalizeBody(e.Body)).Append("\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    /// <summary>本文の改行を CRLF に統一し、末尾の空行を取り除く (= 出力時に固定で 1 つだけ末尾空行を付ける)。
    /// WPF の TextBox は \r\n で出力するが念のため \r 単独 / \n 単独も吸収する。</summary>
    private static string NormalizeBody(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var lines = body.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var end   = lines.Length;
        while (end > 0 && string.IsNullOrEmpty(lines[end - 1])) end--;
        return string.Join("\r\n", lines, 0, end);
    }
}
