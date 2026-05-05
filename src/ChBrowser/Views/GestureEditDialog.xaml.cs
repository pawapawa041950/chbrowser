using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ChBrowser.Views;

/// <summary>マウスジェスチャー編集ダイアログ (Phase 15)。
/// 描画キャンバス上で右ドラッグするとパスを描画し、方向認識 (↑↓←→) を表示する。
/// OK で <see cref="DialogResult"/>=true、<see cref="NewBinding"/> に方向列が入る。
/// 呼出元 (ShortcutsWindow.EditGesture_Click) が衝突検査を通したうえで反映し「保存」で永続化される。</summary>
public partial class GestureEditDialog : Window
{
    public string ActionName { get; }
    public string NewBinding { get; private set; }

    private bool _drawing;
    private Point _lastSamplePoint;
    private const double SampleDistance = 18.0; // この間隔ごとに方向を判定 (= ジェスチャー解像度)
    private readonly List<char> _directions = new();
    private string _currentGesture = "";
    private Polyline? _currentLine;

    public GestureEditDialog(string actionName, string currentBinding)
    {
        InitializeComponent();
        ActionName = actionName;
        NewBinding = currentBinding;
        _currentGesture = currentBinding;
        CurrentBindingRun.Text = string.IsNullOrEmpty(currentBinding) ? "(未設定)" : currentBinding;
    }

    private void DrawCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _drawing = true;
        _lastSamplePoint = e.GetPosition(DrawCanvas);
        _directions.Clear();
        DrawCanvas.Children.Clear();
        HintText.Visibility = Visibility.Collapsed;

        _currentLine = new Polyline
        {
            Stroke          = Brushes.SteelBlue,
            StrokeThickness = 2.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap   = PenLineCap.Round,
        };
        _currentLine.Points.Add(_lastSamplePoint);
        DrawCanvas.Children.Add(_currentLine);

        DrawCanvas.CaptureMouse();
        LiveGestureRun.Text = "(描画中…)";
        e.Handled = true;
    }

    private void DrawCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing || _currentLine is null) return;
        var p = e.GetPosition(DrawCanvas);
        _currentLine.Points.Add(p);

        var dx = p.X - _lastSamplePoint.X;
        var dy = p.Y - _lastSamplePoint.Y;
        var dist = System.Math.Sqrt(dx * dx + dy * dy);
        if (dist < SampleDistance) return;

        // 主成分方向で 4 方向に量子化
        char dir;
        if (System.Math.Abs(dx) > System.Math.Abs(dy)) dir = dx > 0 ? '→' : '←';
        else                                            dir = dy > 0 ? '↓' : '↑';

        // 直前と同じ方向なら追加しない (= 連続移動の重複削減)
        if (_directions.Count == 0 || _directions[^1] != dir)
            _directions.Add(dir);

        LiveGestureRun.Text = BuildGestureString();
        _lastSamplePoint = p;
    }

    private void DrawCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        DrawCanvas.ReleaseMouseCapture();
        _currentGesture = BuildGestureString();
        LiveGestureRun.Text = string.IsNullOrEmpty(_currentGesture) ? "(未入力)" : _currentGesture;
        e.Handled = true;
    }

    private string BuildGestureString()
    {
        if (_directions.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var c in _directions) sb.Append(c);
        return sb.ToString();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _directions.Clear();
        _currentGesture = "";
        DrawCanvas.Children.Clear();
        LiveGestureRun.Text = "(未入力)";
        HintText.Visibility = Visibility.Visible;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        NewBinding   = _currentGesture;
        DialogResult = true;
    }
}
