using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ChBrowser.Services.Bbs;

/// <summary>Markdown の本文の改行を、書いたとおりに表示されるよう整える (書き込み前の変換。掲示板ごとの ON/OFF は
/// <see cref="BbsHostSettings.MarkdownHardLineBreaks"/>)。</summary>
public static class MarkdownLineBreaks
{
    // 表の区切り行 (| --- | :---: |)
    private static readonly Regex TableDelimiterRe = new(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled);
    // コードブロックの囲み (``` / ~~~。行頭の字下げは 3 つまで)
    private static readonly Regex FenceRe = new(@"^ {0,3}(?<f>`{3,}|~{3,})", RegexOptions.Compiled);

    /// <summary>単発の改行 (次の行が空行でない改行) の行末に半角スペース 2 個を付け、Markdown の強制改行にする。
    /// reddit 公式の書き込み欄で Shift+Enter (段落内の改行) を押したときと同じ形。空行 (段落の区切り) はそのまま。
    /// 次の行は変えない: コードブロック (囲み・字下げ 4 つ) の中、表の行、既に強制改行の行 (行末がスペース 2 個 / <c>\</c>)。
    /// 改行は <c>\n</c> にそろえて返す。</summary>
    public static string ToHardBreaks(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length < 2) return lines[0];

        var table = TableLines(lines);
        string? fence = null;          // 開いているコードブロックの囲み (``` / ~~~)
        var indentedCode = false;      // 字下げ 4 つのコードブロックの中
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i];

            // 囲みのコードブロック: 開きと閉じの行・中身は変えない
            var fm = FenceRe.Match(line);
            if (fence is not null)
            {
                if (fm.Success && fm.Groups["f"].Value[0] == fence[0] && fm.Groups["f"].Value.Length >= fence.Length
                    && line.Trim().Length == fm.Groups["f"].Value.Length)
                    fence = null;
                continue;
            }
            if (fm.Success) { fence = fm.Groups["f"].Value; continue; }

            // 字下げ 4 つ (またはタブ) のコードブロック: 空行の後 (先頭含む) から始まり、字下げか空行が続く間
            var prevBlank = i == 0 || IsBlank(lines[i - 1]);
            if (IsIndentedCode(line) && (prevBlank || indentedCode)) { indentedCode = true; continue; }
            if (!IsBlank(line)) indentedCode = false;

            if (IsBlank(line) || IsBlank(lines[i + 1])) continue;   // 段落の終わり / 空行
            if (table[i]) continue;
            if (line.EndsWith("  ", StringComparison.Ordinal) || line.EndsWith('\\')) continue;
            lines[i] = line.TrimEnd(' ', '\t') + "  ";
        }
        return string.Join("\n", lines);
    }

    /// <summary>空行 (空白だけ、または引用の記号 <c>&gt;</c> だけの行 = 引用の中の空行)。</summary>
    private static bool IsBlank(string line)
    {
        foreach (var c in line)
            if (c is not (' ' or '\t' or '>')) return false;
        return true;
    }

    private static bool IsIndentedCode(string line)
        => line.StartsWith('\t') || line.StartsWith("    ", StringComparison.Ordinal);

    /// <summary>表の行 (見出し行・区切り行・その後に続く <c>|</c> を含む行) の印。</summary>
    private static bool[] TableLines(string[] lines)
    {
        var mark = new bool[lines.Length];
        for (var i = 1; i < lines.Length; i++)
        {
            // 区切り行の前の行が | を含めば表 (含まなければ見出しの下線 --- 等)
            if (!TableDelimiterRe.IsMatch(lines[i]) || !lines[i - 1].Contains('|')) continue;
            mark[i - 1] = mark[i] = true;
            for (var j = i + 1; j < lines.Length && !IsBlank(lines[j]) && lines[j].Contains('|'); j++) mark[j] = true;
        }
        return mark;
    }
}
