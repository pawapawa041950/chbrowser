using System;
using System.ComponentModel;

namespace ChBrowser.Controls;

/// <summary>ツールバー等のボタンのアイコンをカラーで描くか (設定 → 全般「ボタンをカラーで表示する」、<see cref="ChBrowser.Models.AppConfig.ColorButtonIcons"/>)。
///
/// <para>XAML からは <c>{Binding Colored, Source={x:Static ctrl:ButtonIconSettings.Instance}}</c> で参照する
/// (カラーの絵文字アイコンと、OFF のときの線画アイコンの出し分け)。
/// <see cref="ColorEmojiTextBlock.IsButtonIcon"/> が true の絵文字は、OFF のときモノクロで描かれる (タブ見出しの絵文字は対象外)。
/// 設定の適用 (App.ApplyConfigImmediate) で <see cref="Colored"/> を変えると即時に描き直される。</para></summary>
public sealed class ButtonIconSettings : INotifyPropertyChanged
{
    public static ButtonIconSettings Instance { get; } = new();

    private bool _colored = true;

    /// <summary>ボタンのアイコンをカラーで描くか (既定 true)。</summary>
    public bool Colored
    {
        get => _colored;
        set
        {
            if (_colored == value) return;
            _colored = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Colored)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Monochrome)));
            Changed?.Invoke();
        }
    }

    /// <summary><see cref="Colored"/> の裏 (XAML で線画アイコンを出す側のトリガー用)。</summary>
    public bool Monochrome => !_colored;

    /// <summary><see cref="Colored"/> が変わった (表示中の <see cref="ColorEmojiTextBlock"/> が描き直すのに使う)。</summary>
    public event Action? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;
}
