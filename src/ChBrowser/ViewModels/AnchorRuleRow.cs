using System;
using System.Text.RegularExpressions;
using ChBrowser.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChBrowser.ViewModels;

/// <summary>設定ウィンドウ「アンカー判定」の 1 行 (= <see cref="AnchorRule"/> の編集用ラッパ)。
/// パターン変更のたびに正規表現の妥当性と <c>(?&lt;spec&gt;)</c> グループの有無を検証し、<see cref="Error"/> に出す。</summary>
public sealed partial class AnchorRuleRow : ObservableObject
{
    [ObservableProperty] private bool   _enabled = true;
    [ObservableProperty] private string _name    = "";
    [ObservableProperty] private string _pattern = "";
    /// <summary>"number" | "attachment" (<see cref="AnchorRule.KindNumber"/> / <see cref="AnchorRule.KindAttachment"/>)。</summary>
    [ObservableProperty] private string _kind    = AnchorRule.KindNumber;
    [ObservableProperty] private bool   _ranges  = true;
    /// <summary>検証エラー (空なら OK)。表示専用。</summary>
    [ObservableProperty] private string _error   = "";

    public AnchorRuleRow() { }

    public AnchorRuleRow(AnchorRule rule)
    {
        _enabled = rule.Enabled;
        _name    = rule.Name;
        _pattern = rule.Pattern;
        _kind    = rule.IsAttachment ? AnchorRule.KindAttachment : AnchorRule.KindNumber;
        _ranges  = rule.Ranges;
        Validate();
    }

    public AnchorRule ToRule() => new(Name ?? "", Pattern ?? "", Kind ?? AnchorRule.KindNumber, Ranges, Enabled);

    partial void OnPatternChanged(string value) => Validate();

    /// <summary>C# / JS 両方で使える式かを最低限確認する: .NET でコンパイルできること、spec グループを含むこと、
    /// 後読み (JS で挙動差が出やすい) を使っていないこと。</summary>
    public void Validate()
    {
        var p = Pattern ?? "";
        if (p.Trim().Length == 0)                       { Error = "パターンが空です"; return; }
        if (!p.Contains("(?<spec>", StringComparison.Ordinal)) { Error = "(?<spec>...) グループがありません"; return; }
        if (p.Contains("(?<=", StringComparison.Ordinal) || p.Contains("(?<!", StringComparison.Ordinal))
                                                          { Error = "後読み (?<= / (?<! は使えません"; return; }
        try { _ = new Regex(p); Error = ""; }
        catch (ArgumentException ex) { Error = "正規表現エラー: " + ex.Message; }
    }
}
