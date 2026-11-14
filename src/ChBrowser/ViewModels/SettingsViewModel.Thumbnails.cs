using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChBrowser.Services.Image;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>設定ウィンドウ「サムネイルルール」がアプリ側に頼むこと。</summary>
/// <param name="UserPath">利用者のルールのファイル (data/app/thumbnail_rules.csv)。</param>
/// <param name="BuiltinCsv">既定のルール (埋め込み)。</param>
/// <param name="Apply">編集後のルールをアプリに反映する。</param>
/// <param name="Test">編集中のルールで URL を 1 つ試し、経過と結果の文章を返す。</param>
public sealed record ThumbnailSettingsHooks(
    string UserPath,
    string BuiltinCsv,
    Action<ThumbnailRuleSet> Apply,
    Func<ThumbnailRuleSet, string, Task<string>> Test);

/// <summary>設定ウィンドウ「サムネイルルール」カテゴリ: サムネイルルール (<see cref="ThumbnailRuleSet"/>) の一覧・編集・テスト。
/// 一覧は「利用者のルール (編集できる。上から順に試される)」→「既定のルール (見るだけ。同じ名前の利用者のルールがあれば出さない)」。
/// 利用者のルールは変更のたびにファイルへ保存し、すぐアプリに反映する (他の設定と同じ)。</summary>
public sealed partial class SettingsViewModel
{
    private ThumbnailSettingsHooks? _thumb;
    private List<ThumbnailRuleData> _builtinThumbnailRules = new();
    private bool _suppressThumbnailSave;
    /// <summary>利用者のファイルの、ルールに付かないコメント行 (見出し行の前 / 最後のルールの後)。保存のときにそのまま書き戻す。</summary>
    private string? _thumbnailFilePreamble;
    private string  _thumbnailFileTrailer = "";

    public ObservableCollection<ThumbnailRuleItem> ThumbnailRules { get; } = new();

    [ObservableProperty] private ThumbnailRuleItem? _selectedThumbnailRule;
    /// <summary>利用者のルールのファイルを読めなかったときの案内 (空なら問題なし)。</summary>
    [ObservableProperty] private string _thumbnailRulesFileError = "";
    [ObservableProperty] private string _thumbnailTestUrl = "";
    [ObservableProperty] private string _thumbnailTestResult = "";

    public IRelayCommand      AddThumbnailRuleCommand      { get; private set; } = null!;
    public IRelayCommand      EditBuiltinThumbnailRuleCommand { get; private set; } = null!;
    public IRelayCommand      RemoveThumbnailRuleCommand   { get; private set; } = null!;
    public IRelayCommand      MoveThumbnailRuleUpCommand   { get; private set; } = null!;
    public IRelayCommand      MoveThumbnailRuleDownCommand { get; private set; } = null!;
    public IAsyncRelayCommand TestThumbnailRuleCommand     { get; private set; } = null!;
    public IRelayCommand      OpenThumbnailRulesFolderCommand { get; private set; } = null!;

    /// <summary>コンストラクタから呼ぶ: ルールを読み込み、コマンドを作る。</summary>
    private void InitThumbnailRules(ThumbnailSettingsHooks? hooks)
    {
        _thumb = hooks;
        AddThumbnailRuleCommand         = new RelayCommand(AddThumbnailRule, () => _thumb is not null);
        EditBuiltinThumbnailRuleCommand = new RelayCommand(EditBuiltinThumbnailRule, () => SelectedThumbnailRule is { IsBuiltin: true });
        RemoveThumbnailRuleCommand      = new RelayCommand(RemoveThumbnailRule, () => SelectedThumbnailRule is { IsBuiltin: false });
        MoveThumbnailRuleUpCommand      = new RelayCommand(() => MoveThumbnailRule(-1), () => CanMoveThumbnailRule(-1));
        MoveThumbnailRuleDownCommand    = new RelayCommand(() => MoveThumbnailRule(+1), () => CanMoveThumbnailRule(+1));
        TestThumbnailRuleCommand        = new AsyncRelayCommand(TestThumbnailRuleAsync, () => _thumb is not null);
        OpenThumbnailRulesFolderCommand = new RelayCommand(OpenThumbnailRulesFolder, () => _thumb is not null);
        if (hooks is null) return;

        try { _builtinThumbnailRules = ThumbnailRuleSet.ReadData(hooks.BuiltinCsv); }
        catch (Exception) { _builtinThumbnailRules = new(); }

        var user = new List<ThumbnailRuleData>();
        if (File.Exists(hooks.UserPath))
        {
            try
            {
                var file = ThumbnailRuleSet.ReadFile(File.ReadAllText(hooks.UserPath));
                user = file.Rules;
                _thumbnailFilePreamble = file.Preamble;
                _thumbnailFileTrailer  = file.Trailer;
            }
            catch (Exception ex)
            {
                ThumbnailRulesFileError = $"{ThumbnailRuleSet.UserFileName} を読めませんでした ({ex.Message})。"
                                        + "いまは既定のルールだけが使われています。ここでルールを追加・編集すると、このファイルは上書きされます。";
            }
        }
        _suppressThumbnailSave = true;
        foreach (var d in user) AddUserThumbnailItem(new ThumbnailRuleItem(d, isBuiltin: false), ThumbnailRules.Count);
        RefreshBuiltinThumbnailItems();
        _suppressThumbnailSave = false;
        SelectedThumbnailRule = ThumbnailRules.FirstOrDefault();
    }

    private int UserThumbnailRuleCount => ThumbnailRules.Count(i => !i.IsBuiltin);

    private void AddUserThumbnailItem(ThumbnailRuleItem item, int index)
    {
        item.PropertyChanged += OnThumbnailItemChanged;
        ThumbnailRules.Insert(Math.Min(index, ThumbnailRules.Count), item);
    }

    /// <summary>一覧の後ろの既定のルールを、今の利用者のルールに合わせて並べ直す (同じ名前の利用者のルールがある既定は出さない)。</summary>
    private void RefreshBuiltinThumbnailItems()
    {
        var selected = SelectedThumbnailRule;
        var userNames = new HashSet<string>(ThumbnailRules.Where(i => !i.IsBuiltin).Select(i => (i.Name ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
        var wanted = _builtinThumbnailRules.Where(b => !userNames.Contains(b.Name)).ToList();
        var shown  = ThumbnailRules.Where(i => i.IsBuiltin).ToList();
        if (shown.Select(i => i.Name).SequenceEqual(wanted.Select(b => b.Name)))
        {
            MarkOverrides(userNames);
            return;
        }
        foreach (var i in shown) ThumbnailRules.Remove(i);
        foreach (var b in wanted) ThumbnailRules.Add(new ThumbnailRuleItem(b, isBuiltin: true));
        MarkOverrides(userNames);
        if (selected is not null && ThumbnailRules.Contains(selected)) SelectedThumbnailRule = selected;
    }

    private void MarkOverrides(HashSet<string> userNames)
    {
        var builtinNames = new HashSet<string>(_builtinThumbnailRules.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var i in ThumbnailRules.Where(i => !i.IsBuiltin))
            i.OverridesBuiltin = builtinNames.Contains((i.Name ?? "").Trim());
    }

    private void OnThumbnailItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ThumbnailRuleItem.Error) or nameof(ThumbnailRuleItem.DisplayName)
            or nameof(ThumbnailRuleItem.OverridesBuiltin) or nameof(ThumbnailRuleItem.IsFetch)) return;
        if (e.PropertyName == nameof(ThumbnailRuleItem.Name)) RefreshBuiltinThumbnailItems();
        SaveThumbnailRules();
    }

    private void AddThumbnailRule()
    {
        var name = "ルール" + (UserThumbnailRuleCount + 1);
        while (ThumbnailRules.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))) name += "+";
        var item = new ThumbnailRuleItem(new ThumbnailRuleData
        {
            Name     = name,
            Match    = @"^https?://example\.com/view/(?<id>[A-Za-z0-9]+)",
            FetchUrl = "https://example.com/api/{id}",
            Format   = "json",
            Extract  = new[] { "images[].url" },
        }, isBuiltin: false);
        AddUserThumbnailItem(item, UserThumbnailRuleCount);
        SelectedThumbnailRule = item;
        SaveThumbnailRules();
    }

    /// <summary>既定のルールを利用者のルールとして写し取る (同じ名前なので既定を置き換える。削除すれば既定に戻る)。</summary>
    private void EditBuiltinThumbnailRule()
    {
        if (SelectedThumbnailRule is not { IsBuiltin: true } b) return;
        var item = new ThumbnailRuleItem(b.ToData(), isBuiltin: false);
        AddUserThumbnailItem(item, UserThumbnailRuleCount);
        RefreshBuiltinThumbnailItems();
        SelectedThumbnailRule = item;
        SaveThumbnailRules();
    }

    private void RemoveThumbnailRule()
    {
        if (SelectedThumbnailRule is not { IsBuiltin: false } item) return;
        var index = ThumbnailRules.IndexOf(item);
        item.PropertyChanged -= OnThumbnailItemChanged;
        ThumbnailRules.Remove(item);
        RefreshBuiltinThumbnailItems();
        SelectedThumbnailRule = ThumbnailRules.Count == 0 ? null : ThumbnailRules[Math.Min(index, ThumbnailRules.Count - 1)];
        SaveThumbnailRules();
    }

    private bool CanMoveThumbnailRule(int delta)
    {
        if (SelectedThumbnailRule is not { IsBuiltin: false } item) return false;
        var to = ThumbnailRules.IndexOf(item) + delta;
        return to >= 0 && to < UserThumbnailRuleCount;
    }

    private void MoveThumbnailRule(int delta)
    {
        if (!CanMoveThumbnailRule(delta) || SelectedThumbnailRule is not { } item) return;
        var from = ThumbnailRules.IndexOf(item);
        ThumbnailRules.Move(from, from + delta);
        SelectedThumbnailRule = item;
        NotifyThumbnailCommands();
        SaveThumbnailRules();
    }

    partial void OnSelectedThumbnailRuleChanged(ThumbnailRuleItem? value) => NotifyThumbnailCommands();

    private void NotifyThumbnailCommands()
    {
        EditBuiltinThumbnailRuleCommand?.NotifyCanExecuteChanged();
        RemoveThumbnailRuleCommand?.NotifyCanExecuteChanged();
        MoveThumbnailRuleUpCommand?.NotifyCanExecuteChanged();
        MoveThumbnailRuleDownCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>編集中の内容 (利用者のルール + 既定) から、使える一覧を作る。</summary>
    private ThumbnailRuleSet BuildThumbnailRuleSet()
        => ThumbnailRuleSet.Build(ThumbnailRules.Where(i => !i.IsBuiltin).Select(i => i.ToData()).ToList(), _builtinThumbnailRules);

    /// <summary>利用者のルールをファイルに保存し、アプリに反映する。</summary>
    private void SaveThumbnailRules()
    {
        if (_suppressThumbnailSave || _thumb is null) return;
        try
        {
            var user = ThumbnailRules.Where(i => !i.IsBuiltin).Select(i => i.ToData()).ToList();
            // 利用者のルールが 1 つも無く、ファイルもまだ無いなら作らない
            if (user.Count > 0 || File.Exists(_thumb.UserPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_thumb.UserPath)!);
                File.WriteAllText(_thumb.UserPath, ThumbnailRuleSet.WriteData(user, _thumbnailFilePreamble, _thumbnailFileTrailer));
            }
            ThumbnailRulesFileError = "";
            _thumb.Apply(BuildThumbnailRuleSet());
        }
        catch (Exception ex)
        {
            ThumbnailRulesFileError = $"{ThumbnailRuleSet.UserFileName} を保存できませんでした: {ex.Message}";
        }
    }

    private async Task TestThumbnailRuleAsync()
    {
        if (_thumb is null) return;
        var url = (ThumbnailTestUrl ?? "").Trim();
        if (url.Length == 0) { ThumbnailTestResult = "試す URL を入力してください"; return; }
        ThumbnailTestResult = "取得中…";
        try { ThumbnailTestResult = await _thumb.Test(BuildThumbnailRuleSet(), url).ConfigureAwait(true); }
        catch (Exception ex) { ThumbnailTestResult = "失敗: " + ex.Message; }
    }

    private void OpenThumbnailRulesFolder()
    {
        if (_thumb is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_thumb.UserPath)!;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ThumbnailRulesFileError = "フォルダを開けませんでした: " + ex.Message; }
    }
}

/// <summary>設定ウィンドウで見る / 編集するサムネイルルール 1 件。</summary>
public sealed partial class ThumbnailRuleItem : ObservableObject
{
    public const string KindRewrite = "rewrite";
    public const string KindJson    = "json";
    public const string KindRegex   = "regex";

    /// <summary>「種類」の選択肢 (値 → 表示)。</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Kinds { get; } = new[]
    {
        new KeyValuePair<string, string>(KindJson,    "取得して JSON から取り出す"),
        new KeyValuePair<string, string>(KindRegex,   "取得して正規表現で取り出す (HTML 等)"),
        new KeyValuePair<string, string>(KindRewrite, "URL を組み立てるだけ (取得しない)"),
    };

    /// <summary>既定のルールか (見るだけで、編集できない)。</summary>
    public bool IsBuiltin  { get; }
    public bool IsEditable => !IsBuiltin;

    [ObservableProperty] private bool   _enabled;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _match;
    [ObservableProperty] private string _kind;
    /// <summary>種類が「URL を組み立てるだけ」のときの URL。</summary>
    [ObservableProperty] private string _result;
    [ObservableProperty] private string _fetchUrl;
    /// <summary>取得時に付けるヘッダー (1 行に 1 つ、「名前: 値」)。</summary>
    [ObservableProperty] private string _headersText;
    /// <summary>取り出し方の候補 (1 行に 1 つ。上から試す)。</summary>
    [ObservableProperty] private string _extractText;
    /// <summary>何も取れなかったとき「画像なし」と確定するか (false = 取得失敗の扱い)。</summary>
    [ObservableProperty] private bool   _emptyMeansNoMedia;
    /// <summary>検証エラー (空なら OK)。表示専用。</summary>
    [ObservableProperty] private string _error = "";
    /// <summary>同じ名前の既定のルールを置き換えているか (一覧の表示用)。</summary>
    [ObservableProperty] private bool   _overridesBuiltin;

    /// <summary>取得する種類か (取得先・ヘッダー・取り出し方の欄を出す)。</summary>
    public bool IsFetch => Kind != KindRewrite;

    /// <summary>一覧に出す名前。</summary>
    public string DisplayName
        => (Enabled ? "" : "(停止) ") + (string.IsNullOrWhiteSpace(Name) ? "(名前なし)" : Name)
         + (IsBuiltin ? "  [既定]" : OverridesBuiltin ? "  [既定を置き換え]" : "");

    /// <summary>ファイルでこのルールの直前に書いてあったコメント行 (編集はしない。保存のときにそのまま書き戻す)。</summary>
    private readonly string _comment;

    public ThumbnailRuleItem(ThumbnailRuleData d, bool isBuiltin)
    {
        _comment           = isBuiltin ? "" : d.Comment;
        IsBuiltin          = isBuiltin;
        _enabled           = d.Enabled;
        _name              = d.Name ?? "";
        _match             = d.Match ?? "";
        _result            = d.Result ?? "";
        _fetchUrl          = d.FetchUrl ?? "";
        _kind              = !string.IsNullOrWhiteSpace(d.Result) ? KindRewrite
                           : string.Equals(d.Format, "regex", StringComparison.OrdinalIgnoreCase) ? KindRegex : KindJson;
        _headersText       = string.Join("\n", d.Headers.Select(h => h.Key + ": " + h.Value));
        _extractText       = string.Join("\n", d.Extract);
        _emptyMeansNoMedia = string.Equals(d.Empty, "noMedia", StringComparison.OrdinalIgnoreCase);
        Validate();
    }

    public ThumbnailRuleData ToData()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(HeadersText))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }
        var rewrite = Kind == KindRewrite;
        return new ThumbnailRuleData
        {
            Name     = (Name ?? "").Trim(),
            Enabled  = Enabled,
            Match    = (Match ?? "").Trim(),
            Result   = rewrite ? (Result ?? "").Trim() : null,
            FetchUrl = rewrite ? null : (FetchUrl ?? "").Trim(),
            Headers  = rewrite ? new Dictionary<string, string>() : headers,
            Format   = Kind == KindRegex ? "regex" : "json",
            Extract  = rewrite ? Array.Empty<string>() : Lines(ExtractText).ToArray(),
            Empty    = EmptyMeansNoMedia ? "noMedia" : "unavailable",
            Comment  = _comment,
        };
    }

    private static IEnumerable<string> Lines(string? text)
        => (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(Error) or nameof(DisplayName) or nameof(IsFetch):
                return;
            case nameof(Kind):
                OnPropertyChanged(nameof(IsFetch));
                break;
            case nameof(Name) or nameof(Enabled) or nameof(OverridesBuiltin):
                OnPropertyChanged(nameof(DisplayName));
                break;
        }
        if (e.PropertyName != nameof(OverridesBuiltin)) Validate();
    }

    /// <summary>書き間違いを調べて <see cref="Error"/> に出す (アプリと表示側 (JavaScript) の両方で通るかも見る)。</summary>
    public void Validate()
    {
        try
        {
            var d = ToData();
            if (rewriteMissing(d)) throw new FormatException("組み立てる URL がありません");
            _ = d.Compile();
            // 対象 URL の正規表現は表示側 (JavaScript) でも使う: 後読みや (?i) 等の書き方は挙動が違ったり通らなかったりする
            if (Match.Contains("(?<=") || Match.Contains("(?<!")) throw new FormatException("対象 URL の正規表現に後読み (?<= (?<! は使えません");
            if (System.Text.RegularExpressions.Regex.IsMatch(Match, @"\(\?[imsnx-]+[:)]")) throw new FormatException("対象 URL の正規表現に (?i) のような指定は使えません (大文字小文字は常に区別しません)");
            Error = "";
        }
        catch (FormatException ex) { Error = ex.Message; }

        bool rewriteMissing(ThumbnailRuleData d) => Kind == KindRewrite && string.IsNullOrWhiteSpace(d.Result);
    }

    /// <summary>読み上げ・UI Automation 用の名前 (一覧の表示と同じ)。</summary>
    public override string ToString() => DisplayName;
}
