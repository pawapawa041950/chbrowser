using System;
using ChBrowser.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChBrowser.ViewModels;

/// <summary>NG 設定ウィンドウの DataGrid 1 行分。元の <see cref="NgRule"/> を変更可能なプロパティとして
/// 露出する。<see cref="SelectedScope"/> でスコープ (グローバル / 板単位) を切り替える。</summary>
public sealed partial class NgRuleViewModel : ObservableObject
{
    public Guid Id { get; }

    /// <summary>選択中のスコープ。"(グローバル)" 用 BoardScopeViewModel か実在の板。
    /// このプロパティは NgWindowViewModel.AvailableScopes 内のインスタンスのいずれかを参照する
    /// (ComboBox の SelectedItem 比較を成立させるため)。</summary>
    [ObservableProperty]
    private BoardScopeViewModel? _selectedScope;

    [ObservableProperty]
    private string _target = "word";        // "name" / "id" / "watchoi" / "word"

    [ObservableProperty]
    private string _matchKind = "literal";  // "literal" / "regex"

    [ObservableProperty]
    private string _pattern = "";

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private DateTime? _expiresAt;           // null = 無期限

    /// <summary>期限ありフラグ (UI: 期限列の CheckBox)。setter で ExpiresAt を up/down する。
    /// ON の瞬間に未設定なら今日 + 1 ヶ月をデフォルト値として埋める (= 期限ありかつ日付未指定の中間状態を作らない)。</summary>
    public bool HasExpiry
    {
        get => ExpiresAt is not null;
        set
        {
            if (value && ExpiresAt is null)
                ExpiresAt = DateTime.Today.AddMonths(1);
            else if (!value && ExpiresAt is not null)
                ExpiresAt = null;
        }
    }

    /// <summary>ExpiresAt が変わったら HasExpiry の PropertyChanged も飛ばす (= CheckBox の表示が追従するように)。</summary>
    partial void OnExpiresAtChanged(DateTime? value) => OnPropertyChanged(nameof(HasExpiry));

    public DateTimeOffset CreatedAt { get; }

    public NgRuleViewModel()
    {
        Id        = Guid.NewGuid();
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public NgRuleViewModel(NgRule rule)
    {
        Id          = rule.Id;
        _target     = rule.Target;
        _matchKind  = rule.MatchKind;
        _pattern    = rule.Pattern;
        _enabled    = rule.Enabled;
        _expiresAt  = rule.ExpiresAt?.LocalDateTime.Date;
        CreatedAt   = rule.CreatedAt;
        // SelectedScope は NgWindowViewModel が AvailableScopes と突き合わせて後から設定する
    }

    /// <summary>現在の VM 状態を NgRule に変換。SelectedScope が "(グローバル)" or null なら BoardHost/Directory は空文字。</summary>
    public NgRule ToModel() => new()
    {
        Id             = Id,
        BoardHost      = SelectedScope?.Host ?? "",
        BoardDirectory = SelectedScope?.DirectoryName ?? "",
        Target         = Target,
        MatchKind      = MatchKind,
        Pattern        = Pattern,
        Enabled        = Enabled,
        ExpiresAt      = ExpiresAt is { } d
            ? new DateTimeOffset(d.Date.AddDays(1).AddSeconds(-1), TimeZoneInfo.Local.GetUtcOffset(d))
            : (DateTimeOffset?)null,
        CreatedAt      = CreatedAt,
    };
}
