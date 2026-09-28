using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using ChBrowser.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>設定ウィンドウ「LLM」カテゴリ (LLM プロファイルの登録・削除・デフォルト指定・接続確認) と、
/// AI 系カテゴリ (AI / AI NG / AI翻訳) の「使うプロファイル」の選択肢。</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>登録済みの LLM プロファイル (編集用)。</summary>
    public ObservableCollection<LlmProfileItem> LlmProfiles { get; } = new();

    /// <summary>AI 系カテゴリの「使うプロファイル」の選択肢。先頭は「デフォルトのプロファイルを使う」(Id 空)。
    /// プロファイルの追加・削除・改名に合わせて中身を差分で更新する (作り直すと ComboBox の選択が外れるため)。</summary>
    public ObservableCollection<LlmProfileChoice> LlmProfileChoices { get; } = new();

    [ObservableProperty] private LlmProfileItem? _selectedLlmProfile;
    [ObservableProperty] private string _defaultLlmProfileId   = "";
    [ObservableProperty] private string _agentLlmProfileId     = "";
    [ObservableProperty] private string _workerLlmProfileId    = "";
    [ObservableProperty] private string _ngAiLlmProfileId      = "";
    [ObservableProperty] private string _translateLlmProfileId = "";
    [ObservableProperty] private bool   _ngAiEnabled;

    /// <summary>プロファイル一覧の編集 (追加・削除・項目の変更) の版。これが変わると保存する (一覧は入れ子なので)。</summary>
    [ObservableProperty] private int _llmProfilesVersion;

    public IRelayCommand       AddLlmProfileCommand        { get; private set; } = null!;
    public IRelayCommand       RemoveLlmProfileCommand     { get; private set; } = null!;
    public IRelayCommand       SetDefaultLlmProfileCommand { get; private set; } = null!;
    public IAsyncRelayCommand  TestLlmProfileCommand       { get; private set; } = null!;

    /// <summary>コンストラクタから呼ぶ: 設定値を流し込み、コマンドを作る。</summary>
    private void InitLlmProfiles(AppConfig initial)
    {
        foreach (var p in initial.LlmProfiles ?? Array.Empty<LlmProfile>()) AddProfileItem(new LlmProfileItem(p));
        DefaultLlmProfileId   = initial.DefaultLlmProfileId ?? "";
        if (LlmProfiles.Count > 0 && LlmProfiles.All(i => i.Id != DefaultLlmProfileId)) DefaultLlmProfileId = LlmProfiles[0].Id;
        AgentLlmProfileId     = initial.AgentLlmProfileId ?? "";
        WorkerLlmProfileId    = initial.WorkerLlmProfileId ?? "";
        NgAiLlmProfileId      = initial.NgAiLlmProfileId ?? "";
        TranslateLlmProfileId = initial.TranslateLlmProfileId ?? "";
        NgAiEnabled           = initial.NgAiEnabled;
        SelectedLlmProfile    = LlmProfiles.FirstOrDefault();
        RefreshLlmProfileChoices();

        AddLlmProfileCommand        = new RelayCommand(AddLlmProfile);
        RemoveLlmProfileCommand     = new RelayCommand(RemoveSelectedLlmProfile, () => SelectedLlmProfile is not null);
        SetDefaultLlmProfileCommand = new RelayCommand(() => { if (SelectedLlmProfile is { } s) DefaultLlmProfileId = s.Id; },
                                                       () => SelectedLlmProfile is not null);
        TestLlmProfileCommand       = new AsyncRelayCommand(TestSelectedLlmProfileAsync,
                                                            () => SelectedLlmProfile is not null && _testLlmConnectionAction is not null);
    }

    private void AddProfileItem(LlmProfileItem item)
    {
        item.PropertyChanged += OnProfileItemChanged;
        LlmProfiles.Add(item);
    }

    private void OnProfileItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LlmProfileItem.ConnectionStatus) or nameof(LlmProfileItem.IsDefault) or nameof(LlmProfileItem.DisplayName)) return;
        if (e.PropertyName == nameof(LlmProfileItem.Name)) RefreshLlmProfileChoices();
        LlmProfilesVersion++;
    }

    private void AddLlmProfile()
    {
        var name = "プロファイル" + (LlmProfiles.Count + 1);
        while (LlmProfiles.Any(p => p.Name == name)) name += "+";
        var item = new LlmProfileItem(new LlmProfile(LlmProfile.NewId(), name, "", "", "", 8192));
        AddProfileItem(item);
        if (LlmProfiles.Count == 1) DefaultLlmProfileId = item.Id;
        SelectedLlmProfile = item;
        RefreshLlmProfileChoices();
        LlmProfilesVersion++;
    }

    private void RemoveSelectedLlmProfile()
    {
        if (SelectedLlmProfile is not { } item) return;
        var index = LlmProfiles.IndexOf(item);
        item.PropertyChanged -= OnProfileItemChanged;
        LlmProfiles.Remove(item);
        // 消したプロファイルを使っていた所は「デフォルトを使う」に戻す。デフォルトを消したら先頭をデフォルトにする
        if (AgentLlmProfileId     == item.Id) AgentLlmProfileId     = "";
        if (WorkerLlmProfileId    == item.Id) WorkerLlmProfileId    = "";
        if (NgAiLlmProfileId      == item.Id) NgAiLlmProfileId      = "";
        if (TranslateLlmProfileId == item.Id) TranslateLlmProfileId = "";
        if (DefaultLlmProfileId   == item.Id) DefaultLlmProfileId   = LlmProfiles.FirstOrDefault()?.Id ?? "";
        SelectedLlmProfile = LlmProfiles.Count == 0 ? null : LlmProfiles[Math.Min(index, LlmProfiles.Count - 1)];
        RefreshLlmProfileChoices();
        LlmProfilesVersion++;
    }

    partial void OnSelectedLlmProfileChanged(LlmProfileItem? value)
    {
        RemoveLlmProfileCommand?.NotifyCanExecuteChanged();
        SetDefaultLlmProfileCommand?.NotifyCanExecuteChanged();
        TestLlmProfileCommand?.NotifyCanExecuteChanged();
    }

    partial void OnDefaultLlmProfileIdChanged(string value) => RefreshLlmProfileChoices();

    /// <summary>「使うプロファイル」の選択肢と、一覧の ★ (デフォルト) 表示を今の一覧に合わせる。</summary>
    private void RefreshLlmProfileChoices()
    {
        foreach (var p in LlmProfiles) p.IsDefault = p.Id == DefaultLlmProfileId;
        var def = LlmProfiles.FirstOrDefault(p => p.IsDefault);
        var defaultLabel = def is null ? "デフォルトのプロファイルを使う (未登録)" : $"デフォルトのプロファイルを使う ({def.Name})";
        if (LlmProfileChoices.Count == 0 || LlmProfileChoices[0].Id != "") LlmProfileChoices.Insert(0, new LlmProfileChoice("", defaultLabel));
        else LlmProfileChoices[0].Label = defaultLabel;
        // 消えたものを除き、残りは名前を更新、新しいものを足す (並びは一覧の順)
        for (var i = LlmProfileChoices.Count - 1; i >= 1; i--)
            if (LlmProfiles.All(p => p.Id != LlmProfileChoices[i].Id)) LlmProfileChoices.RemoveAt(i);
        for (var i = 0; i < LlmProfiles.Count; i++)
        {
            var p = LlmProfiles[i];
            var existing = LlmProfileChoices.FirstOrDefault(c => c.Id == p.Id);
            if (existing is null) LlmProfileChoices.Insert(Math.Min(i + 1, LlmProfileChoices.Count), new LlmProfileChoice(p.Id, p.Name));
            else existing.Label = p.Name;
        }
    }

    private async Task TestSelectedLlmProfileAsync()
    {
        if (_testLlmConnectionAction is null || SelectedLlmProfile is not { } item) return;
        item.ConnectionStatus = "確認中…";
        try
        {
            var p = item.ToProfile();
            if (!p.IsConfigured) { item.ConnectionStatus = "NG — API URL とモデル名を入力してください"; return; }
            var (ok, message) = await _testLlmConnectionAction(LlmSettings.FromProfile(p)).ConfigureAwait(true);
            item.ConnectionStatus = ok ? $"OK — {message}" : $"NG — {message}";
        }
        catch (Exception ex)
        {
            item.ConnectionStatus = $"NG — {ex.Message}";
        }
    }

    /// <summary>保存用: 編集中の一覧をプロファイルの配列にする。</summary>
    private LlmProfile[] LlmProfilesSnapshot() => LlmProfiles.Select(i => i.ToProfile()).ToArray();
}

/// <summary>設定ウィンドウで編集する LLM プロファイル 1 件。</summary>
public sealed partial class LlmProfileItem : ObservableObject
{
    public string Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _apiUrl;
    [ObservableProperty] private string _apiKey;
    [ObservableProperty] private string _model;
    [ObservableProperty] private int    _contextSize;
    [ObservableProperty] private bool   _supportsImages;
    /// <summary>デフォルトのプロファイルか (一覧の ★ 表示用。保存はしない)。</summary>
    [ObservableProperty] private bool   _isDefault;
    /// <summary>接続確認の結果 (表示専用)。"OK — …" / "NG — …" / "確認中…" / "未確認"。</summary>
    [ObservableProperty] private string _connectionStatus = "未確認";

    /// <summary>一覧に出す名前 (デフォルトには ★)。</summary>
    public string DisplayName => (IsDefault ? "★ " : "") + (string.IsNullOrWhiteSpace(Name) ? "(名前なし)" : Name);

    public LlmProfileItem(LlmProfile p)
    {
        Id              = p.Id;
        _name           = p.Name ?? "";
        _apiUrl         = p.ApiUrl ?? "";
        _apiKey         = p.ApiKey ?? "";
        _model          = p.Model ?? "";
        _contextSize    = p.ContextSize;
        _supportsImages = p.SupportsImages;
    }

    partial void OnNameChanged(string value)       => OnPropertyChanged(nameof(DisplayName));
    partial void OnIsDefaultChanged(bool value)    => OnPropertyChanged(nameof(DisplayName));

    /// <summary>読み上げ・UI Automation 用の名前 (一覧の表示と同じ)。</summary>
    public override string ToString() => DisplayName;

    public LlmProfile ToProfile()
        => new(Id, (Name ?? "").Trim(), (ApiUrl ?? "").Trim(), ApiKey ?? "", (Model ?? "").Trim(), Math.Max(0, ContextSize), SupportsImages);
}

/// <summary>「使うプロファイル」の選択肢 1 件 (Id 空 = デフォルトのプロファイルを使う)。</summary>
public sealed partial class LlmProfileChoice : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] private string _label;

    public LlmProfileChoice(string id, string label)
    {
        Id     = id;
        _label = label;
    }

    /// <summary>読み上げ・UI Automation 用の名前 (選択肢の表示と同じ)。</summary>
    public override string ToString() => Label;
}
