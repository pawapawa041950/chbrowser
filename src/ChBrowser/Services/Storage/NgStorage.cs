using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChBrowser.Models;

namespace ChBrowser.Services.Storage;

/// <summary>
/// NG ルール (グローバル + 板単位を一括) を <c>data/ng/rules.json</c> に保存する (Phase 13e で単一ファイル化)。
/// 旧 (`data/ng/global.json` + `data/ng/by_board/*.json`) からの移行は <see cref="LoadAndMigrate"/> 内で
/// 自動的に行い、移行後は古いファイルを削除する。
/// </summary>
public sealed class NgStorage
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
        Converters           = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly DataPaths _paths;

    public NgStorage(DataPaths paths)
    {
        _paths = paths;
    }

    /// <summary>新ファイル <c>data/ng/rules.json</c> を読む。
    /// 存在せず、旧 <c>global.json</c> / <c>by_board/*.json</c> がある場合は自動的に統合 + 移行する
    /// (旧ファイルは削除)。</summary>
    public NgRuleSet LoadAndMigrate()
    {
        var rulesPath = Path.Combine(_paths.NgDir, "rules.json");
        if (File.Exists(rulesPath))
        {
            try
            {
                var json = File.ReadAllText(rulesPath);
                return JsonSerializer.Deserialize<NgRuleSet>(json, JsonOpts) ?? new NgRuleSet();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NgStorage] load {rulesPath} failed: {ex.Message}");
                return new NgRuleSet();
            }
        }

        // 新ファイルが無い → 旧フォーマットを探して移行
        var migrated = MigrateFromLegacy();
        Save(migrated);
        return migrated;
    }

    public void Save(NgRuleSet set)
    {
        var rulesPath = Path.Combine(_paths.NgDir, "rules.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(rulesPath)!);
            var json = JsonSerializer.Serialize(set, JsonOpts);
            var tmp  = rulesPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(rulesPath)) File.Delete(rulesPath);
            File.Move(tmp, rulesPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NgStorage] save {rulesPath} failed: {ex.Message}");
        }
    }

    /// <summary>旧 <c>global.json</c> + <c>by_board/*.json</c> をすべて読み込み、各ルールに
    /// スコープ (BoardHost / BoardDirectory) を埋めて 1 つの NgRuleSet にまとめる。
    /// 移行後、旧ファイルは削除して再発を防ぐ。</summary>
    private NgRuleSet MigrateFromLegacy()
    {
        var merged = new List<NgRule>();

        // 旧 global.json
        var legacyGlobal = Path.Combine(_paths.NgDir, "global.json");
        if (File.Exists(legacyGlobal))
        {
            try
            {
                var json = File.ReadAllText(legacyGlobal);
                var set  = JsonSerializer.Deserialize<NgRuleSet>(json, JsonOpts);
                if (set is not null)
                {
                    foreach (var r in set.Rules)
                        merged.Add(r with { BoardHost = "", BoardDirectory = "" });
                }
                File.Delete(legacyGlobal);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NgStorage] legacy global migration failed: {ex.Message}");
            }
        }

        // 旧 by_board/*.json
        var byBoardDir = _paths.NgByBoardDir;
        if (Directory.Exists(byBoardDir))
        {
            foreach (var file in Directory.EnumerateFiles(byBoardDir, "*.json"))
            {
                try
                {
                    var (root, dir) = ParseLegacyFileName(Path.GetFileNameWithoutExtension(file));
                    if (root is null || dir is null) continue;
                    var json = File.ReadAllText(file);
                    var set  = JsonSerializer.Deserialize<NgRuleSet>(json, JsonOpts);
                    if (set is null) continue;
                    foreach (var r in set.Rules)
                        merged.Add(r with { BoardHost = root, BoardDirectory = dir });
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NgStorage] legacy board file {file} migration failed: {ex.Message}");
                }
            }
        }

        return new NgRuleSet { Version = 1, Rules = merged };
    }

    /// <summary>"5ch_io__news" → ("5ch.io", "news")。アンダーバー 1 個目で host/dir を分け、host 側のアンダーバーは
    /// ドットに復元する。失敗時は (null, null)。</summary>
    private static (string? Root, string? Dir) ParseLegacyFileName(string name)
    {
        var idx = name.IndexOf("__", StringComparison.Ordinal);
        if (idx <= 0 || idx + 2 >= name.Length) return (null, null);
        var root = name[..idx].Replace('_', '.');
        var dir  = name[(idx + 2)..];
        return (root, dir);
    }
}
