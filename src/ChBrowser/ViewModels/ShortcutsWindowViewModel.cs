using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using ChBrowser.Models;
using ChBrowser.Services.Shortcuts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChBrowser.ViewModels;

/// <summary>ショートカット & マウスジェスチャー設定ウィンドウの ViewModel (Phase 15)。
/// <see cref="ShortcutRegistry.Actions"/> から行を生成し、ユーザの永続化 <see cref="ShortcutSettings"/> をオーバーレイする。
/// 編集 → <see cref="MarkDirty"/> → 「保存」で <see cref="Save"/> → 永続化 + マネージャ再適用。</summary>
public sealed partial class ShortcutsWindowViewModel : ObservableObject
{
    public ObservableCollection<ShortcutItem> Items { get; } = new();

    /// <summary>UI で CollectionView を介してカテゴリでグルーピング + 検索フィルタするため。</summary>
    public ICollectionView ItemsView { get; }

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    private readonly Func<ShortcutSettings>     _loadSettings;
    private readonly Action<ShortcutSettings>   _saveSettings;

    public ShortcutsWindowViewModel(Func<ShortcutSettings> loadSettings, Action<ShortcutSettings> saveSettings)
    {
        _loadSettings = loadSettings;
        _saveSettings = saveSettings;

        var current = _loadSettings();
        foreach (var action in ShortcutRegistry.Actions)
        {
            var (sc, ms, gs) = ResolveEffective(action, current);
            Items.Add(new ShortcutItem(action.Id, action.Category, action.DisplayName, sc, ms, gs));
        }

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ShortcutItem.Category)));
        ItemsView.Filter = FilterPredicate;
    }

    public void MarkDirty() => HasUnsavedChanges = true;

    /// <summary>永続化: 全アクションのバインドを書き出す。
    /// (= シンプルに「現在ユーザに表示されている値」をそのまま全件保存。後続でユーザがレジストリ既定値を上書きしたい場合も意図通りに動く)</summary>
    public void Save()
    {
        var settings = new ShortcutSettings
        {
            Version = 1,
            Bindings = Items.Select(i => new ShortcutBinding
            {
                Id       = i.Id,
                Shortcut = i.Shortcut,
                Mouse    = i.Mouse,
                Gesture  = i.Gesture,
            }).ToList(),
        };
        _saveSettings(settings);
        HasUnsavedChanges = false;
    }

    partial void OnFilterTextChanged(string value) => ItemsView.Refresh();

    private bool FilterPredicate(object obj)
    {
        if (obj is not ShortcutItem item) return false;
        var f = (FilterText ?? "").Trim();
        if (f.Length == 0) return true;
        return item.Action.Contains(f,   StringComparison.OrdinalIgnoreCase)
            || item.Category.Contains(f, StringComparison.OrdinalIgnoreCase)
            || item.Shortcut.Contains(f, StringComparison.OrdinalIgnoreCase)
            || item.Mouse.Contains(f,    StringComparison.OrdinalIgnoreCase)
            || item.Gesture.Contains(f,  StringComparison.OrdinalIgnoreCase);
    }

    private static (string shortcut, string mouse, string gesture) ResolveEffective(ShortcutAction action, ShortcutSettings settings)
    {
        foreach (var b in settings.Bindings)
        {
            if (b.Id == action.Id) return (b.Shortcut, b.Mouse, b.Gesture);
        }
        return (action.DefaultShortcut, action.DefaultMouse, action.DefaultGesture);
    }
}

/// <summary>1 行 = 1 アクション。<see cref="Shortcut"/> / <see cref="Mouse"/> / <see cref="Gesture"/> は編集ダイアログから上書きされ、
/// <see cref="ShortcutsWindowViewModel.Save"/> で永続化される。</summary>
public sealed partial class ShortcutItem : ObservableObject
{
    public string Id       { get; }
    public string Category { get; }
    public string Action   { get; }

    [ObservableProperty]
    private string _shortcut;

    [ObservableProperty]
    private string _mouse;

    [ObservableProperty]
    private string _gesture;

    public ShortcutItem(string id, string category, string action, string shortcut, string mouse, string gesture)
    {
        Id        = id;
        Category  = category;
        Action    = action;
        _shortcut = shortcut;
        _mouse    = mouse;
        _gesture  = gesture;
    }
}
