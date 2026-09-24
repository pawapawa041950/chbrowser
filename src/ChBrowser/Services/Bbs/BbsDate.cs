using System;
using System.Globalization;

namespace ChBrowser.Services.Bbs;

/// <summary>投稿時刻 (epoch 秒) を 5ch と同じ書式の日時文字列にする (JSON で時刻を返す掲示板用: reddit / 4chan / ふたば)。</summary>
public static class BbsDate
{
    private static readonly CultureInfo Ja = CultureInfo.GetCultureInfo("ja-JP");

    /// <summary>ローカル時刻で <c>2026/09/23(火) 12:34:56</c>。0 以下なら空。</summary>
    public static string FromEpoch(long epoch)
    {
        if (epoch <= 0) return "";
        var t = DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime();
        return t.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) + "(" + t.ToString("ddd", Ja) + ") " + t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
