using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ChBrowser.Models;
using ChBrowser.Services.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChBrowser.ViewModels;

/// <summary>お気に入りペイン (TreeView) のルート ViewModel。
/// ロード時に <see cref="FavoritesStorage"/> から <see cref="FavoritesData"/> を読み、
/// ViewModel ツリーに変換して <see cref="Items"/> に並べる。
/// 変更系の操作 (追加 / 名前変更 / 削除 / 上下移動) は本クラスのメソッド経由で行い、
/// 完了時に <see cref="Save"/> で <c>favorites.json</c> へ書き戻す。</summary>
public sealed class FavoritesViewModel : ObservableObject
{
    private readonly FavoritesStorage _storage;

    /// <summary>TreeView の <c>ItemsSource</c> にバインドする最上位エントリ列。</summary>
    public ObservableCollection<FavoriteEntryViewModel> Items { get; } = new();

    /// <summary>ツリー構造に変更があったときに発火 (Phase 14b)。
    /// MainViewModel が購読して FavoritesHtml の再生成に使う。
    /// 折りたたみ状態だけ変えた場合は発火しない (= HTML を再生成しなくても DOM の details 状態は維持される)。</summary>
    public event Action? Changed;

    private void NotifyChanged() => Changed?.Invoke();

    public FavoritesViewModel(FavoritesStorage storage)
    {
        _storage = storage;
    }

    // ---- ロード / セーブ ----

    /// <summary>ディスクから読み直して ViewModel ツリーを再構築。</summary>
    public void Reload()
    {
        var data = _storage.Load();
        Items.Clear();
        foreach (var entry in data.Root)
        {
            var vm = FavoriteEntryViewModel.Create(entry);
            if (vm is null) continue;
            vm.Parent = null;
            Items.Add(vm);
        }
        NotifyChanged();
    }

    /// <summary>現在の ViewModel ツリーを <see cref="FavoritesData"/> に変換してディスクに保存。</summary>
    public void Save()
    {
        var data = new FavoritesData
        {
            Version = 1,
            Root    = Items.Select(vm => vm.ToModel()).ToList(),
        };
        _storage.Save(data);
    }

    // ---- 追加 ----

    /// <summary>ツリー全体から指定 Id のエントリを返す (Phase 14b: WebView 側からの id ベース指定)。</summary>
    public FavoriteEntryViewModel? FindById(Guid id)
    {
        foreach (var vm in WalkAll())
            if (vm.Model.Id == id) return vm;
        return null;
    }

    /// <summary>名前変更 (フォルダのみ意味あり)。Save + Changed 通知。</summary>
    public void RenameFolder(FavoriteFolderViewModel folder, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        folder.Name = newName.Trim();
        Save();
        NotifyChanged();
    }

    /// <summary>ルート直下に新規エントリを追加して即座に保存する。</summary>
    public void AddRoot(FavoriteEntry entry)
    {
        var vm = FavoriteEntryViewModel.Create(entry);
        if (vm is null) return;
        vm.Parent = null;
        Items.Add(vm);
        Save();
        NotifyChanged();
    }

    /// <summary>指定フォルダ直下に新規エントリを追加して即座に保存する。</summary>
    public void AddInto(FavoriteFolderViewModel parent, FavoriteEntry entry)
    {
        var vm = FavoriteEntryViewModel.Create(entry);
        if (vm is null) return;
        vm.Parent = parent;
        parent.Children.Add(vm);
        Save();
        NotifyChanged();
    }

    // ---- 編集 ----

    /// <summary>エントリを親 (ルート or フォルダ) から取り除く。</summary>
    public void Remove(FavoriteEntryViewModel vm)
    {
        var siblings = SiblingsOf(vm);
        siblings.Remove(vm);
        vm.Parent = null;
        Save();
        NotifyChanged();
    }

    /// <summary>兄弟集合内で 1 つ上に動かす。先頭なら何もしない。</summary>
    public void MoveUp(FavoriteEntryViewModel vm)
    {
        var siblings = SiblingsOf(vm);
        var i = siblings.IndexOf(vm);
        if (i > 0)
        {
            siblings.Move(i, i - 1);
            Save();
            NotifyChanged();
        }
    }

    /// <summary>兄弟集合内で 1 つ下に動かす。末尾なら何もしない。</summary>
    public void MoveDown(FavoriteEntryViewModel vm)
    {
        var siblings = SiblingsOf(vm);
        var i = siblings.IndexOf(vm);
        if (i >= 0 && i < siblings.Count - 1)
        {
            siblings.Move(i, i + 1);
            Save();
            NotifyChanged();
        }
    }

    /// <summary>vm の兄弟コレクション (ルート or 親フォルダの Children) を返す。</summary>
    private ObservableCollection<FavoriteEntryViewModel> SiblingsOf(FavoriteEntryViewModel vm)
        => vm.Parent?.Children ?? Items;

    // ---- 重複チェック (追加 UX 用) ----

    /// <summary>(host, directoryName) の板がルート直下に既に登録されているか。</summary>
    public bool ContainsBoardAtRoot(string host, string directoryName)
    {
        foreach (var vm in Items)
            if (vm is FavoriteBoardViewModel b
                && b.Model.Host == host
                && b.Model.DirectoryName == directoryName)
                return true;
        return false;
    }

    /// <summary>(host, directoryName, threadKey) のスレがルート直下に既に登録されているか。</summary>
    public bool ContainsThreadAtRoot(string host, string directoryName, string threadKey)
    {
        foreach (var vm in Items)
            if (vm is FavoriteThreadViewModel t
                && t.Model.Host == host
                && t.Model.DirectoryName == directoryName
                && t.Model.ThreadKey == threadKey)
                return true;
        return false;
    }

    // ---- 再帰検索 ----

    /// <summary>ツリー全体を深さ優先で歩く列挙。</summary>
    public IEnumerable<FavoriteEntryViewModel> WalkAll()
    {
        foreach (var vm in Items)
            foreach (var sub in WalkSubtree(vm))
                yield return sub;
    }

    private static IEnumerable<FavoriteEntryViewModel> WalkSubtree(FavoriteEntryViewModel vm)
    {
        yield return vm;
        if (vm is FavoriteFolderViewModel folder)
            foreach (var c in folder.Children)
                foreach (var sub in WalkSubtree(c))
                    yield return sub;
    }

    /// <summary>スレッドが (フォルダ含む) ツリーのどこかに登録されているか。</summary>
    public bool IsThreadFavorited(string host, string directoryName, string threadKey)
        => FindThread(host, directoryName, threadKey) is not null;

    /// <summary>ツリーから指定スレッドの ViewModel を 1 件返す (重複登録は想定しないが、最初に見つかったもの)。</summary>
    public FavoriteThreadViewModel? FindThread(string host, string directoryName, string threadKey)
    {
        foreach (var vm in WalkAll())
            if (vm is FavoriteThreadViewModel t
                && t.Model.Host == host
                && t.Model.DirectoryName == directoryName
                && t.Model.ThreadKey == threadKey)
                return t;
        return null;
    }

    /// <summary>お気に入り済みスレ全集合 (key tuple)。スレ一覧描画時の bulk lookup 用。</summary>
    public HashSet<(string Host, string Dir, string Key)> CollectFavoriteThreadKeys()
    {
        var set = new HashSet<(string, string, string)>();
        foreach (var vm in WalkAll())
            if (vm is FavoriteThreadViewModel t)
                set.Add((t.Model.Host, t.Model.DirectoryName, t.Model.ThreadKey));
        return set;
    }

    // ---- D&D による移動 ----

    /// <summary>D&amp;D で <paramref name="source"/> を <paramref name="target"/> に移動できるか判定 (旧 API、後方互換)。
    /// 自分自身には移動できない。フォルダを自分の子孫の中に入れることもできない (循環防止)。
    /// ⚠ target が非フォルダのとき (= 兄弟挿入になるケース)、source がフォルダで target の祖先である
    /// ような循環は検出しない既知の制限がある。新規コードは <see cref="CanReparent"/> を使うこと。</summary>
    public bool CanMove(FavoriteEntryViewModel source, FavoriteEntryViewModel? target)
    {
        if (target is null) return source.Parent is not null || Items.IndexOf(source) != Items.Count - 1; // root 末尾以外なら可
        if (source == target) return false;
        if (target is FavoriteFolderViewModel folder)
        {
            // フォルダにドロップする場合、source がそのフォルダの祖先 (or 同一) なら不可
            if (source == folder) return false;
            if (source is FavoriteFolderViewModel)
            {
                var p = folder.Parent;
                while (p is not null)
                {
                    if (p == source) return false;
                    p = p.Parent;
                }
            }
        }
        return true;
    }

    /// <summary>source を newParent (= ルートなら null) の直下に置こうとした時、
    /// 循環参照になるかどうかを判定する (Phase 14b で追加)。
    /// source 自身が newParent と一致、または source の子孫 (含 newParent) なら不可。</summary>
    public bool CanReparent(FavoriteEntryViewModel source, FavoriteFolderViewModel? newParent)
    {
        if (newParent is null) return true;             // ルートへの移動は常に OK
        if (source == newParent) return false;
        if (source is not FavoriteFolderViewModel) return true; // 葉なら循環不可
        // newParent から親方向に source が現れたら循環
        FavoriteEntryViewModel? p = newParent;
        while (p is not null)
        {
            if (p == source) return false;
            p = p.Parent;
        }
        return true;
    }

    /// <summary>source をフォルダ <paramref name="newParent"/> 配下の末尾に移動する。
    /// 循環は <see cref="CanReparent"/> で弾く。</summary>
    public void MoveIntoFolder(FavoriteEntryViewModel source, FavoriteFolderViewModel newParent)
    {
        if (!CanReparent(source, newParent)) return;
        if (source == newParent)             return;
        var oldSiblings = SiblingsOf(source);
        oldSiblings.Remove(source);
        source.Parent = newParent;
        newParent.Children.Add(source);
        Save();
        NotifyChanged();
    }

    /// <summary>source を <paramref name="target"/> の直前 (兄弟) に挿入する。
    /// 移動後の新しい親は target.Parent。循環は <see cref="CanReparent"/> で弾く。</summary>
    public void MoveAsSiblingBefore(FavoriteEntryViewModel source, FavoriteEntryViewModel target)
    {
        if (source == target) return;
        if (!CanReparent(source, target.Parent)) return;
        var oldSiblings = SiblingsOf(source);
        var oldIdx      = oldSiblings.IndexOf(source);
        if (oldIdx < 0) return;
        oldSiblings.RemoveAt(oldIdx);
        var siblings = target.Parent?.Children ?? Items;
        var idx      = siblings.IndexOf(target);
        if (idx < 0) idx = 0;
        source.Parent = target.Parent;
        siblings.Insert(idx, source);
        Save();
        NotifyChanged();
    }

    /// <summary>source を <paramref name="target"/> の直後 (兄弟) に挿入する。
    /// target がフォルダでも「中に入れる」ではなく必ず兄弟扱い。</summary>
    public void MoveAsSiblingAfter(FavoriteEntryViewModel source, FavoriteEntryViewModel target)
    {
        if (source == target) return;
        if (!CanReparent(source, target.Parent)) return;
        var oldSiblings = SiblingsOf(source);
        var oldIdx      = oldSiblings.IndexOf(source);
        if (oldIdx < 0) return;
        oldSiblings.RemoveAt(oldIdx);
        var siblings = target.Parent?.Children ?? Items;
        var idx      = siblings.IndexOf(target);
        if (idx < 0) idx = siblings.Count - 1;
        source.Parent = target.Parent;
        siblings.Insert(idx + 1, source);
        Save();
        NotifyChanged();
    }

    /// <summary>source をルートの末尾に移動 (空エリアにドロップしたとき用)。</summary>
    public void MoveToRootEnd(FavoriteEntryViewModel source)
    {
        // ルートには無条件で動かせる (循環の起きようがない)
        var oldSiblings = SiblingsOf(source);
        var oldIdx      = oldSiblings.IndexOf(source);
        if (oldIdx < 0) return;
        // 既にルート末尾にいる場合は何もしない
        if (source.Parent is null && oldIdx == Items.Count - 1) return;
        oldSiblings.RemoveAt(oldIdx);
        source.Parent = null;
        Items.Add(source);
        Save();
        NotifyChanged();
    }

    /// <summary>D&amp;D による移動を確定する。
    /// <paramref name="target"/> がフォルダなら配下に追加 (末尾)、
    /// 非フォルダなら兄弟として直後に挿入、
    /// null なら root 末尾。CanMove で事前チェック前提。</summary>
    public void Move(FavoriteEntryViewModel source, FavoriteEntryViewModel? target)
    {
        if (!CanMove(source, target)) return;

        // まず現在位置から取り除く (Parent / Items を更新)
        var oldSiblings = SiblingsOf(source);
        var oldIndex    = oldSiblings.IndexOf(source);
        if (oldIndex < 0) return;
        oldSiblings.RemoveAt(oldIndex);

        if (target is FavoriteFolderViewModel folder)
        {
            source.Parent = folder;
            folder.Children.Add(source);
        }
        else if (target is FavoriteEntryViewModel)
        {
            var newSiblings = SiblingsOf(target);
            var idx         = newSiblings.IndexOf(target);
            source.Parent   = target.Parent;
            // 同じコレクション内で前方から後方に移動した場合、target の index は変わっていないので
            // そのまま idx + 1 に入れる。前後判定は不要 (oldSiblings と newSiblings が異なる場合も
            // 既に oldSiblings 側で取り除き済みなので idx は新コレクションの正しい位置を指す)。
            newSiblings.Insert(idx + 1, source);
        }
        else
        {
            source.Parent = null;
            Items.Add(source);
        }
        Save();
        NotifyChanged();
    }
}

/// <summary>お気に入りエントリの ViewModel 基底。
/// HierarchicalDataTemplate で派生型を判別して描画するため、各派生型は独自プロパティを持つ。</summary>
public abstract class FavoriteEntryViewModel : ObservableObject
{
    /// <summary>UI 上の表示名 (フォルダ名 / 板名 / スレタイトル)。</summary>
    public abstract string DisplayName { get; }

    /// <summary>対応するモデル (ロード時のスナップショット)。
    /// 永続化時は <see cref="ToModel"/> で現在の状態を反映した最新 record を返す。</summary>
    public FavoriteEntry Model { get; }

    /// <summary>親フォルダ。ルート直下なら null。
    /// 削除/移動操作で親をたどるために使う (FavoritesViewModel.SiblingsOf)。</summary>
    public FavoriteFolderViewModel? Parent { get; internal set; }

    protected FavoriteEntryViewModel(FavoriteEntry model)
    {
        Model = model;
    }

    /// <summary>現在の ViewModel 状態を反映した <see cref="FavoriteEntry"/> を返す。</summary>
    public abstract FavoriteEntry ToModel();

    public static FavoriteEntryViewModel? Create(FavoriteEntry entry) => entry switch
    {
        FavoriteFolder f => new FavoriteFolderViewModel(f),
        FavoriteBoard  b => new FavoriteBoardViewModel(b),
        FavoriteThread t => new FavoriteThreadViewModel(t),
        _                => null,
    };
}

/// <summary>フォルダの ViewModel。子を再帰的に保持。<see cref="Name"/> は編集可能。</summary>
public sealed class FavoriteFolderViewModel : FavoriteEntryViewModel
{
    private string _name;
    private bool   _isExpanded;

    public new FavoriteFolder Model => (FavoriteFolder)base.Model;

    /// <summary>フォルダ名 (UI で名前変更可)。変更で <see cref="DisplayName"/> も同時に通知。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>HTML 生成時に &lt;details open&gt; を出すか。Phase 14b。
    /// 永続化はしない (旧 WPF TreeView もセッションを跨いだ展開状態保持はなかった)。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    public override string DisplayName => _name;

    /// <summary>子エントリ。HierarchicalDataTemplate の <c>ItemsSource</c> にバインド。</summary>
    public ObservableCollection<FavoriteEntryViewModel> Children { get; } = new();

    public FavoriteFolderViewModel(FavoriteFolder folder) : base(folder)
    {
        _name = folder.Name;
        foreach (var child in folder.Children)
        {
            var vm = Create(child);
            if (vm is null) continue;
            vm.Parent = this;
            Children.Add(vm);
        }
    }

    public override FavoriteEntry ToModel()
    {
        return new FavoriteFolder
        {
            Id       = Model.Id,
            AddedAt  = Model.AddedAt,
            Name     = _name,
            Children = Children.Select(c => c.ToModel()).ToList(),
        };
    }
}

/// <summary>板お気に入り。クリックで通常の板表示を新規タブで開く。</summary>
public sealed class FavoriteBoardViewModel : FavoriteEntryViewModel
{
    public new FavoriteBoard Model => (FavoriteBoard)base.Model;
    public override string DisplayName => Model.BoardName;

    public FavoriteBoardViewModel(FavoriteBoard board) : base(board) { }

    public override FavoriteEntry ToModel() => Model; // 不変
}

/// <summary>スレお気に入り。クリックで対象スレを新規タブで開く。</summary>
public sealed class FavoriteThreadViewModel : FavoriteEntryViewModel
{
    public new FavoriteThread Model => (FavoriteThread)base.Model;
    public override string DisplayName => Model.Title;

    public FavoriteThreadViewModel(FavoriteThread thread) : base(thread) { }

    public override FavoriteEntry ToModel() => Model; // 不変
}
