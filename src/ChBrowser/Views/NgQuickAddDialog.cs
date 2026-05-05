using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ChBrowser.Models;
using ChBrowser.ViewModels;   // LabelValue を流用

namespace ChBrowser.Views;

/// <summary>スレ表示のレス番号メニュー → 「NG登録 (名前 / ID / ワッチョイ)」から呼ばれる
/// 簡易 NG 登録ダイアログ。本格的な編集は <see cref="NgWindow"/> 側で行うため、
/// こちらは「ワンクリックで素早く 1 件追加」だけに特化:
/// <list type="bullet">
/// <item><description>種別 (target): 名前 / ID / ワッチョイ / 本文 — 起動時に呼出元の選択をプリセレクト</description></item>
/// <item><description>パターン: 抽出済の値をプリフィル (空でも可、ユーザが手で書ける)</description></item>
/// <item><description>方式 (literal / regex): 既定 literal</description></item>
/// <item><description>スコープ: 「この板のみ」(既定) / 「全板 (グローバル)」</description></item>
/// <item><description>期限: 1 日 / 1 週間 / 1 か月 / 無期限。既定 1 日</description></item>
/// </list>
/// OK を押すと <see cref="CreatedRule"/> に新規 <see cref="NgRule"/> がセットされて DialogResult=true で閉じる。
/// 保存処理 (NgService への書き込み) は呼出元で行う。</summary>
public sealed class NgQuickAddDialog : Window
{
    private readonly Board _board;

    private readonly ComboBox _targetCombo   = new();
    private readonly TextBox  _patternBox    = new();
    private readonly ComboBox _kindCombo     = new();
    private readonly ComboBox _scopeCombo    = new();
    private readonly ComboBox _expiryCombo   = new();

    /// <summary>OK で確定された NG ルール。Cancel なら null のまま。</summary>
    public NgRule? CreatedRule { get; private set; }

    public NgQuickAddDialog(Board currentBoard, string presetTarget, string presetValue)
    {
        _board                = currentBoard ?? throw new ArgumentNullException(nameof(currentBoard));
        Title                 = "NG 登録";
        Width                 = 460;
        Height                = 280;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Content = BuildLayout();

        // プリセット
        SelectByValue(_targetCombo, NormalizeTarget(presetTarget));
        _patternBox.Text = presetValue ?? "";
        SelectByValue(_kindCombo,   "literal");
        SelectByValue(_scopeCombo,  "board");   // 既定: この板のみ
        SelectByValue(_expiryCombo, "1d");      // 既定: 1 日

        Loaded += (_, _) => _patternBox.Focus();
    }

    private FrameworkElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 種別
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // パターン
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 方式
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // スコープ
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 期限
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // spacer
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ComboBox の中身 (= 既存の ChBrowser.ViewModels.LabelValue を再利用、public 型なので
        // WPF の DisplayMemberPath / SelectedValuePath が安全に解決できる)。
        _targetCombo.ItemsSource = TargetItems();
        _kindCombo.ItemsSource   = KindItems();
        _scopeCombo.ItemsSource  = ScopeItems(_board);
        _expiryCombo.ItemsSource = ExpiryItems();
        foreach (var c in new[] { _targetCombo, _kindCombo, _scopeCombo, _expiryCombo })
        {
            c.DisplayMemberPath = nameof(LabelValue.Label);
            c.SelectedValuePath = nameof(LabelValue.Value);
        }

        AddRow(root, 0, "種別:",    _targetCombo);
        AddRow(root, 1, "パターン:", _patternBox);
        AddRow(root, 2, "方式:",    _kindCombo);
        AddRow(root, 3, "スコープ:", _scopeCombo);
        AddRow(root, 4, "期限:",    _expiryCombo);

        // ボタン列
        var btnPanel = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "登録", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => OnOk();
        var cancel = new Button { Content = "取消", Width = 90, IsCancel = true };
        btnPanel.Children.Add(ok);
        btnPanel.Children.Add(cancel);
        Grid.SetRow(btnPanel, 6);
        Grid.SetColumnSpan(btnPanel, 2);
        root.Children.Add(btnPanel);

        return root;
    }

    private static void AddRow(Grid g, int row, string label, FrameworkElement control)
    {
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 6, 4) };
        Grid.SetRow(t, row); Grid.SetColumn(t, 0);
        g.Children.Add(t);
        control.Margin = new Thickness(0, 4, 0, 4);
        if (control is Control c) c.VerticalContentAlignment = VerticalAlignment.Center;
        Grid.SetRow(control, row); Grid.SetColumn(control, 1);
        g.Children.Add(control);
    }

    private void OnOk()
    {
        try
        {
            var target  = (_targetCombo.SelectedValue as string) ?? "name";
            var kind    = (_kindCombo.SelectedValue   as string) ?? "literal";
            var scope   = (_scopeCombo.SelectedValue  as string) ?? "board";
            var expiry  = (_expiryCombo.SelectedValue as string) ?? "1d";
            var pattern = (_patternBox.Text ?? "").Trim();

            if (string.IsNullOrEmpty(pattern))
            {
                MessageBox.Show(this, "パターンを入力してください。", "NG 登録",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (kind == "regex" && !ChBrowser.Services.Ng.NgService.IsValidRegex(pattern, out var err))
            {
                MessageBox.Show(this, $"正規表現が不正です: {err}", "NG 登録",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // スコープ: "global" なら BoardHost / Directory ともに空、"board" なら現在板
            string boardHost = "";
            string boardDir  = "";
            if (scope == "board")
            {
                boardHost = _board.Host;
                boardDir  = _board.DirectoryName;
            }

            DateTimeOffset? expiresAt = expiry switch
            {
                "1d"  => DateTimeOffset.UtcNow.AddDays(1),
                "1w"  => DateTimeOffset.UtcNow.AddDays(7),
                "1m"  => DateTimeOffset.UtcNow.AddMonths(1),
                "inf" => null,
                _     => DateTimeOffset.UtcNow.AddDays(1),
            };

            CreatedRule = new NgRule
            {
                BoardHost      = boardHost,
                BoardDirectory = boardDir,
                Target         = target,
                MatchKind      = kind,
                Pattern        = pattern,
                Enabled        = true,
                ExpiresAt      = expiresAt,
            };
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NgQuickAdd] OnOk failed: {ex}");
            MessageBox.Show(this, "NG ルールの作成に失敗しました:\n" + ex.Message,
                            "NG 登録", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void SelectByValue(ComboBox cb, string value)
    {
        if (cb.ItemsSource is null) return;
        foreach (var item in cb.ItemsSource)
        {
            if (item is LabelValue lv && lv.Value == value)
            {
                cb.SelectedItem = item;
                return;
            }
        }
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
    }

    /// <summary>JS から来る target ("name"/"id"/"watchoi") を NgRule.Target の値域にマッピング。
    /// 想定外の値は "name" にフォールバック。</summary>
    private static string NormalizeTarget(string t) => t switch
    {
        "name" or "id" or "watchoi" or "word" => t,
        _ => "name",
    };

    private static IReadOnlyList<LabelValue> TargetItems() => new[]
    {
        new LabelValue("名前",       "name"),
        new LabelValue("ID",         "id"),
        new LabelValue("ワッチョイ", "watchoi"),
        new LabelValue("本文",       "word"),
    };
    private static IReadOnlyList<LabelValue> KindItems() => new[]
    {
        new LabelValue("通常 (部分一致)", "literal"),
        new LabelValue("正規表現",        "regex"),
    };
    private static IReadOnlyList<LabelValue> ScopeItems(Board b) => new[]
    {
        new LabelValue($"この板のみ ({b.BoardName})", "board"),
        new LabelValue("全板 (グローバル)",            "global"),
    };
    private static IReadOnlyList<LabelValue> ExpiryItems() => new[]
    {
        new LabelValue("1 日",     "1d"),
        new LabelValue("1 週間",   "1w"),
        new LabelValue("1 か月",   "1m"),
        new LabelValue("無期限",   "inf"),
    };
}
