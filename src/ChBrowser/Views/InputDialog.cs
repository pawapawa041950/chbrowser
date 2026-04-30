using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ChBrowser.Views;

/// <summary>
/// 1 行入力 + OK/キャンセル のシンプルな WPF 入力ダイアログ。
/// 専用 XAML を作るほどではない用途 (フォルダ名 / 名前変更) のために programmatic に生成する。
/// </summary>
public static class InputDialog
{
    /// <summary>テキスト入力プロンプトを表示し、OK 押下なら入力値、キャンセルなら null を返す。</summary>
    public static string? Prompt(Window? owner, string title, string prompt, string defaultValue = "")
    {
        var window = new Window
        {
            Title                  = title,
            Width                  = 360,
            Height                 = 160,
            Owner                  = owner,
            WindowStartupLocation  = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ResizeMode             = ResizeMode.NoResize,
            ShowInTaskbar          = false,
            SizeToContent          = SizeToContent.Manual,
        };

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptText = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(promptText, 0);
        grid.Children.Add(promptText);

        var textBox = new TextBox { Text = defaultValue };
        Grid.SetRow(textBox, 1);
        grid.Children.Add(textBox);

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var okBtn     = new Button { Content = "OK",         Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);

        window.Content = grid;

        var confirmed = false;
        okBtn.Click += (_, _) => { confirmed = true; window.Close(); };

        // ダイアログ表示前にフォーカスを TextBox に当てる (Loaded 後でないと SelectAll が効かない)
        window.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
            Keyboard.Focus(textBox);
        };

        window.ShowDialog();
        return confirmed ? textBox.Text : null;
    }
}
