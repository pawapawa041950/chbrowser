namespace ChBrowser.Models;

/// <summary>
/// 1 レス。dat の 1 行に対応する。
/// dat 形式: <c>名前&lt;&gt;メール&lt;&gt;日付 ID&lt;&gt;本文&lt;&gt;タイトル(1レス目のみ)</c>
/// </summary>
public sealed record Post(
    int     Number,         // 1 始まりのレス番号
    string  Name,           // 名前 (HTML デコード済み)
    string  Mail,           // メール (sage 等)
    string  DateText,       // 例: "2026/04/25(土) 12:34:56.78"
    string  Id,             // 例: "abc1234" (ID: の後ろ)、無ければ空文字
    string  Body,           // 本文 (HTML デコード済み、改行は \n)
    string? ThreadTitle);   // 1 レス目のみ。それ以外は null
