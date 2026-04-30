using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ChBrowser.Models;
using ChBrowser.Services.Ng;
using ChBrowser.Services.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>NG 設定ウィンドウ (Phase 13、Phase 13e で統一リスト化)。
/// グローバル + 板単位を 1 つの <see cref="Rules"/> リストで保持し、各ルールの板スコープ (= 板名列) は
/// <see cref="NgRuleViewModel.SelectedScope"/> で切り替える。「(グローバル)」を選ぶと BoardHost/Directory が空に。
/// 「保存」ボタンで NgService に書き戻し、ウィンドウクローズ時に MainViewModel に再ロード通知する。</summary>
public sealed partial class NgWindowViewModel : ObservableObject
{
    private readonly NgService _ng;
    private readonly Action<string, string?>? _showWarning;
    private readonly Action _onClosed;

    /// <summary>1 リストで保持するすべてのルール (グローバル + 板単位)。</summary>
    public ObservableCollection<NgRuleViewModel> Rules { get; } = new();

    /// <summary>板選択ピッカー (BoardPickerWindow) に渡す全候補。先頭に「(グローバル)」、後ろに bbsmenu の全板 +
    /// 既存ルールの読み込みで作られた transient エントリ。</summary>
    public ObservableCollection<BoardScopeViewModel> AvailableScopes { get; } = new();

    public IRelayCommand AddRuleCommand { get; }
    public IRelayCommand<NgRuleViewModel?> RemoveRuleCommand { get; }
    public IRelayCommand SaveCommand { get; }

    /// <summary>「なし (全板)」(BoardHost/Directory が空) を表す特殊スコープ。</summary>
    public static readonly BoardScopeViewModel GlobalScope = new("", "", "なし (全板)");

    /// <summary>対象列のドロップダウン (= NgRule.Target の選択肢)。表示は日本語、保存値は英語キー。</summary>
    public static IReadOnlyList<LabelValue> TargetOptions { get; } = new[]
    {
        new LabelValue("名前",       "name"),
        new LabelValue("ID",         "id"),
        new LabelValue("ワッチョイ", "watchoi"),
        new LabelValue("本文",       "word"),
    };

    /// <summary>方式列のドロップダウン (= NgRule.MatchKind の選択肢)。</summary>
    public static IReadOnlyList<LabelValue> MatchKindOptions { get; } = new[]
    {
        new LabelValue("通常",     "literal"),
        new LabelValue("正規表現", "regex"),
    };

    public NgWindowViewModel(
        NgService                       ng,
        IEnumerable<BoardScopeViewModel> boards,
        Action<string, string?>?        showWarning,
        Action                          onClosed)
    {
        _ng          = ng;
        _showWarning = showWarning;
        _onClosed    = onClosed;

        // 板選択候補: 先頭にグローバル、その後に bbsmenu の全板
        AvailableScopes.Add(GlobalScope);
        foreach (var b in boards) AvailableScopes.Add(b);

        // ルール読み込み + 各ルールの SelectedScope を AvailableScopes 内のインスタンスに紐付け
        foreach (var r in ng.All.Rules)
        {
            var vm = new NgRuleViewModel(r);
            vm.SelectedScope = ResolveScope(r.BoardHost, r.BoardDirectory);
            Rules.Add(vm);
        }

        AddRuleCommand    = new RelayCommand(() =>
        {
            var newRule = new NgRuleViewModel
            {
                SelectedScope = GlobalScope,  // 既定はグローバル
            };
            Rules.Add(newRule);
        });
        RemoveRuleCommand = new RelayCommand<NgRuleViewModel?>(rule =>
        {
            if (rule is null) return;
            Rules.Remove(rule);
        });
        SaveCommand       = new RelayCommand(Save);
    }

    /// <summary>(host, dir) → AvailableScopes 内の対応エントリを返す。
    /// dir が空ならグローバル。dir が設定されていて host も指定されていれば root domain + dir で検索。
    /// host が空なら dir のみで検索 (= 自由入力ルールの読み込み)。
    /// 旧バグで dir に DisplayName ("news (ニュース速報)") が入っているケースもここで救済する。
    /// どこにも見つからない場合は AvailableScopes に新規 transient エントリを追加して返す。</summary>
    private BoardScopeViewModel ResolveScope(string host, string dir)
    {
        if (string.IsNullOrEmpty(dir)) return GlobalScope;

        if (!string.IsNullOrEmpty(host))
        {
            var ruleRoot = DataPaths.ExtractRootDomain(host);
            foreach (var s in AvailableScopes)
            {
                if (s == GlobalScope) continue;
                if (string.IsNullOrEmpty(s.Host)) continue; // transient (host 空) は除外
                if (DataPaths.ExtractRootDomain(s.Host) == ruleRoot &&
                    string.Equals(s.DirectoryName, dir, StringComparison.Ordinal))
                    return s;
            }
        }
        // host 空 (= 自由入力 or 旧バグ由来) のとき、dir が DisplayName / DirectoryName のどちらかに一致する
        // 既存板があればそれに紐付け直す (= 旧 LostFocus バグで DisplayName を dir に保存してしまった rule の救済)。
        if (string.IsNullOrEmpty(host))
        {
            foreach (var s in AvailableScopes)
            {
                if (ReferenceEquals(s, GlobalScope)) continue;
                if (string.IsNullOrEmpty(s.Host)) continue;
                if (string.Equals(s.DisplayName,   dir, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.DirectoryName, dir, StringComparison.OrdinalIgnoreCase))
                    return s;
            }
        }
        // それでも見つからない → 自由入力扱いの transient で表示する。
        return GetOrCreateTransientScope(dir, host);
    }

    /// <summary>既存ルールのロード時 (= ResolveScope の救済パス) に、その host+dir に対応する
    /// transient な <see cref="BoardScopeViewModel"/> を生成 or 再利用する。
    /// 新規ルールは BoardPickerWindow からしか作れない (= 必ず AvailableScopes 内のエントリを指す) ため、
    /// このメソッドは UI 経由では呼ばれない。bbsmenu に存在しない板を NG したい上級用途は rules.json 直接編集。</summary>
    public BoardScopeViewModel GetOrCreateTransientScope(string directoryName, string host = "")
    {
        if (string.IsNullOrEmpty(directoryName)) return GlobalScope;
        foreach (var s in AvailableScopes)
        {
            if (ReferenceEquals(s, GlobalScope)) continue;
            if (string.Equals(s.Host,          host,          StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.DirectoryName, directoryName, StringComparison.Ordinal))
                return s;
        }
        var entry = new BoardScopeViewModel(host, directoryName, directoryName);
        AvailableScopes.Add(entry);
        return entry;
    }

    /// <summary>「保存」ボタンで呼ばれる。各ルールの正規表現バリデーションを行い、
    /// 異常なものは Enabled=false に倒した上で保存。1 件以上の異常があれば警告ダイアログを出す。</summary>
    private void Save()
    {
        var issues = ValidateAndForceDisable(Rules);

        _ng.Save(new NgRuleSet
        {
            Version = 1,
            Rules   = Rules.Select(vm => vm.ToModel()).ToList(),
        });

        if (issues.Count > 0 && _showWarning is not null)
        {
            var lines = string.Join("\n", issues);
            _showWarning(
                $"以下 {issues.Count} 件の正規表現が不正なため、自動的に無効化しました。",
                lines);
        }
    }

    private static List<string> ValidateAndForceDisable(IEnumerable<NgRuleViewModel> rules)
    {
        var issues = new List<string>();
        foreach (var r in rules)
        {
            if (r.MatchKind != "regex") continue;
            if (!NgService.IsValidRegex(r.Pattern, out var err))
            {
                r.Enabled = false;
                issues.Add($"  - 「{r.Pattern}」 ({r.Target}): {err}");
            }
        }
        return issues;
    }

    /// <summary>ルールを有効化しようとしたとき (= UI のトグルクリック直後) に呼ばれる。
    /// regex で pattern が不正なら警告を出して Enabled を false に戻し、true (拒否) を返す。</summary>
    public bool ValidateBeforeEnable(NgRuleViewModel rule)
    {
        if (rule.MatchKind != "regex") return false;
        if (NgService.IsValidRegex(rule.Pattern, out var err)) return false;
        _showWarning?.Invoke(
            "正規表現が不正なため、このルールは有効化できません。",
            $"パターン: {rule.Pattern}\n\n{err}");
        rule.Enabled = false;
        return true;
    }

    public void NotifyClosed() => _onClosed();
}

/// <summary>板単位 NG の対象選択用 (host + directory + 表示名)。</summary>
public sealed record BoardScopeViewModel(string Host, string DirectoryName, string DisplayName);

/// <summary>対象列 / 方式列のドロップダウン用 (= 表示は日本語、保存値は英語キー)。
/// SelectedValueBinding で <see cref="Value"/> がモデルに直接書き込まれるので、永続化フォーマットは
/// 旧来通り "name"/"id"/"watchoi"/"word" / "literal"/"regex" のまま。</summary>
public sealed record LabelValue(string Label, string Value);
