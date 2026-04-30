namespace ChBrowser.ViewModels;

/// <summary>設定ウィンドウ (Phase 11) の左ペインに並ぶ 1 カテゴリ。
/// 右ペインに表示する実際の設定 UI は Phase 11a/11b/Phase 13 で各カテゴリごとに追加していく。
/// 現状は骨格 (タイトル表示のみ)。</summary>
public sealed class SettingsCategoryViewModel
{
    public string Name { get; }

    /// <summary>カテゴリの内容を簡単に紹介する補助テキスト (右ペイン上部に表示)。
    /// 実際の設定 UI が入るまでの placeholder としても使う。</summary>
    public string Description { get; }

    public SettingsCategoryViewModel(string name, string description)
    {
        Name        = name;
        Description = description;
    }
}
