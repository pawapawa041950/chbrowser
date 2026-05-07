namespace ChBrowser.Models;

/// <summary>
/// ウィンドウ位置・サイズと「4 ペインのレイアウトツリー」を保存する永続化用レコード。
/// data/app/layout.json として直列化する。
///
/// <para>Phase 23 より、旧 BoardListWidth / ThreadListHeight / FavoritesPaneHeight 系の固定スロット保存は廃止。
/// レイアウトは <see cref="PaneLayout"/> として binary tree (LeafLayoutNode / SplitLayoutNode) を直接保存する。
/// 旧フォーマットファイルが残っていても <see cref="PaneLayout"/> は null になり、起動時にデフォルトレイアウトが構築される。</para>
///
/// <para><see cref="WindowMaximized"/> が true の時、Width/Height/Left/Top には RestoreBounds (= 通常表示時の
/// サイズ・位置) を入れて保存する。次回起動時にまず通常サイズで復元してから最大化する。</para>
///
/// <para><see cref="ViewerWindow"/> は画像ビューアウィンドウのジオメトリ。ビューアは lazy 生成 + 「✕」で
/// Hide の常駐モデルだが、サイズ/位置はアプリ起動を跨いで保持する (= ユーザが調整した寸法を毎回作り直さない)。
/// 一度も開かれずアプリを閉じた場合は、起動時に読み出した値が再保存され失われない。</para>
/// </summary>
public sealed record LayoutState(
    double                WindowLeft,
    double                WindowTop,
    double                WindowWidth,
    double                WindowHeight,
    bool                  WindowMaximized,
    LayoutNode?           PaneLayout   = null,
    ViewerWindowGeometry? ViewerWindow = null);

/// <summary>画像ビューアウィンドウのジオメトリ。最大化状態の場合 Left/Top/Width/Height は RestoreBounds 値。</summary>
public sealed record ViewerWindowGeometry(
    double Left,
    double Top,
    double Width,
    double Height,
    bool   Maximized);
