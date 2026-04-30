using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>ビューアウィンドウの ViewModel (Phase 10)。
/// アプリ全体でシングルトン。複数の画像を <see cref="Tabs"/> としてタブ管理する。
/// 同じ URL を再投入したらタブを増やさず、その既存タブを <see cref="SelectedTab"/> にする。</summary>
public sealed partial class ImageViewerViewModel : ObservableObject
{
    public ObservableCollection<ImageViewerTabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    private ImageViewerTabViewModel? _selectedTab;

    /// <summary>タブのサムネイルサイズ (px、正方形)。Phase 11 設定で変更可。
    /// XAML 側でタブ Grid の Width/Height にバインドされる。</summary>
    [ObservableProperty]
    private int _thumbnailSize = 80;

    public IRelayCommand PrevTabCommand          { get; }
    public IRelayCommand NextTabCommand          { get; }
    public IRelayCommand CloseCurrentTabCommand  { get; }
    public IRelayCommand SaveCurrentTabCommand   { get; }
    public IRelayCommand CopyCurrentUrlCommand   { get; }
    public IRelayCommand OpenCurrentInBrowserCommand { get; }

    /// <summary>「保存」要求時に Window 側 (= 実際の SaveFileDialog やファイル I/O) に処理を委譲するためのコールバック。
    /// VM がファイル系の WPF API に直接触らないようにするための分離。</summary>
    public Action<string>? SaveImageRequested        { get; set; }
    public Action<string>? CopyUrlRequested          { get; set; }
    public Action<string>? OpenInBrowserRequested    { get; set; }

    public ImageViewerViewModel()
    {
        PrevTabCommand          = new RelayCommand(PrevTab);
        NextTabCommand          = new RelayCommand(NextTab);
        CloseCurrentTabCommand  = new RelayCommand(() =>
        {
            if (SelectedTab is { } t) CloseTab(t);
        });
        SaveCurrentTabCommand   = new RelayCommand(() => InvokeIfHasUrl(SaveImageRequested));
        CopyCurrentUrlCommand   = new RelayCommand(() => InvokeIfHasUrl(CopyUrlRequested));
        OpenCurrentInBrowserCommand = new RelayCommand(() => InvokeIfHasUrl(OpenInBrowserRequested));
    }

    private void InvokeIfHasUrl(Action<string>? cb)
    {
        if (cb is null) return;
        if (SelectedTab is { Url: var url } && !string.IsNullOrEmpty(url)) cb(url);
    }

    partial void OnSelectedTabChanged(ImageViewerTabViewModel? value)
    {
        // 各タブの WebView2 は ItemsControl + Visibility=BoolToVis で表示制御する
        // (スレ表示と同じパターン)。選択タブだけ可視にするため IsSelected を更新。
        foreach (var t in Tabs) t.IsSelected = ReferenceEquals(t, value);
    }

    /// <summary>指定 URL を新規タブで開く。既に開いているなら既存タブをアクティブ化。</summary>
    public ImageViewerTabViewModel OpenOrAddTab(string url)
    {
        foreach (var t in Tabs)
        {
            if (t.Url == url)
            {
                SelectedTab = t;
                return t;
            }
        }
        var tab = new ImageViewerTabViewModel(url, t => CloseTab(t));
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    /// <summary>1 タブ閉じる。最後の 1 つを閉じてもウィンドウ自体は維持 (再 open で再利用)。</summary>
    public void CloseTab(ImageViewerTabViewModel tab)
    {
        var idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        Tabs.Remove(tab);
        // 閉じた直後に隣のタブを選択する
        if (Tabs.Count == 0)        SelectedTab = null;
        else if (idx >= Tabs.Count) SelectedTab = Tabs[^1];
        else                        SelectedTab = Tabs[idx];
    }

    /// <summary>JS の wheel 操作 (= ホイール) で次のタブへ。Tabs.Count &lt; 2 なら何もしない。</summary>
    public void NextTab()
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        var i = Tabs.IndexOf(SelectedTab);
        if (i < 0) return;
        SelectedTab = Tabs[(i + 1) % Tabs.Count];
    }

    /// <summary>JS の wheel 操作 (= ホイール) で前のタブへ。</summary>
    public void PrevTab()
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        var i = Tabs.IndexOf(SelectedTab);
        if (i < 0) return;
        SelectedTab = Tabs[(i - 1 + Tabs.Count) % Tabs.Count];
    }
}
