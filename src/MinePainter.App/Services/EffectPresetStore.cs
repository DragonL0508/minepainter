using System.Text.Json;
using System.Text.Json.Serialization;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;

namespace MinePainter.App.Services;

/// <summary>
/// 效果預設集的一筆：一整個效果堆疊（不含遮罩）。
/// <paramref name="Folder"/> 是相對於預設集根目錄的資料夾（"" = 根目錄；多層以 "/" 相接）。
/// </summary>
public sealed record EffectPreset(string Name, IReadOnlyList<(IEffect Effect, bool Enabled)> Effects, string Path, string Folder)
{
    /// <summary>「資料夾/名稱」（根目錄的就只有名稱）。</summary>
    public string DisplayPath => Folder.Length == 0 ? Name : Folder + "/" + Name;
}

/// <summary>
/// 效果預設集庫：%APPDATA%\MinePainter\EffectPresets 底下一個 .json 一筆（與文字樣式庫同一套作法：
/// 一檔一筆，使用者可直接用檔案總管整理／分享）。子資料夾 = 預設集面板裡的資料夾。
/// </summary>
public static class EffectPresetStore
{
    /// <summary>庫的根目錄。MINEPAINTER_PRESETS_DIR 可覆寫（開發驗證用，不動使用者的庫）。</summary>
    public static string FolderPath { get; } =
        Environment.GetEnvironmentVariable("MINEPAINTER_PRESETS_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MinePainter", "EffectPresets");

    /// <summary>庫的內容變了（存檔／刪除／搬移／資料夾異動）；預設集面板靠它重新整理。</summary>
    public static event Action? Changed;

    private sealed class PresetDto
    {
        public string Name { get; set; } = "";
        public List<EntryDto> Effects { get; set; } = new();
    }

    private sealed class EntryDto
    {
        public string Type { get; set; } = "";
        public Dictionary<string, string>? Params { get; set; }
        public bool Enabled { get; set; } = true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>所有預設集（含子資料夾），先依資料夾再依名稱排序。</summary>
    public static List<EffectPreset> LoadAll()
    {
        var list = new List<EffectPreset>();
        try
        {
            EnsureFolder();
            foreach (var file in Directory.EnumerateFiles(FolderPath, "*.json", SearchOption.AllDirectories))
            {
                var preset = LoadFile(file);
                if (preset != null) list.Add(preset);
            }
        }
        catch (Exception)
        {
        }
        return list
            .OrderBy(p => p.Folder, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>讀一個預設集檔（壞檔／未知效果回傳 null 或略過那道效果）。</summary>
    public static EffectPreset? LoadFile(string file)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<PresetDto>(File.ReadAllText(file), JsonOptions);
            if (dto == null) return null;
            var effects = new List<(IEffect, bool)>();
            foreach (var e in dto.Effects)
            {
                try
                {
                    effects.Add((EffectSerializer.Load(e.Type, e.Params), e.Enabled));
                }
                catch (Exception)
                {
                    // 未知效果略過
                }
            }
            var name = string.IsNullOrWhiteSpace(dto.Name) ? Path.GetFileNameWithoutExtension(file) : dto.Name;
            return new EffectPreset(name, effects, file, RelativeFolderOf(file));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>所有子資料夾（相對路徑，"/" 相接），含空資料夾，依名稱排序。</summary>
    public static List<string> Folders()
    {
        var list = new List<string>();
        try
        {
            EnsureFolder();
            foreach (var dir in Directory.EnumerateDirectories(FolderPath, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(FolderPath, dir).Replace('\\', '/');
                if (rel.Length > 0 && rel != ".") list.Add(rel);
            }
        }
        catch (Exception)
        {
        }
        list.Sort(StringComparer.CurrentCultureIgnoreCase);
        return list;
    }

    /// <summary>存檔到指定資料夾；同名覆蓋。回傳檔案路徑。</summary>
    public static string Save(string name, IEnumerable<LayerEffect> effects, string folder = "") =>
        SaveEntries(name, effects.Select(e => (e.Effect, e.Enabled)), folder);

    public static string SaveEntries(string name, IEnumerable<(IEffect Effect, bool Enabled)> effects, string folder = "")
    {
        var dir = AbsoluteFolder(folder);
        Directory.CreateDirectory(dir);
        var dto = new PresetDto
        {
            Name = name,
            Effects = effects.Select(e => new EntryDto
            {
                Type = EffectSerializer.TypeIdOf(e.Effect),
                Params = EffectSerializer.Save(e.Effect),
                Enabled = e.Enabled,
            }).ToList(),
        };
        var path = Path.Combine(dir, SafeFileName(name, "preset") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
        Changed?.Invoke();
        return path;
    }

    public static void Delete(EffectPreset preset)
    {
        try
        {
            File.Delete(preset.Path);
        }
        catch (Exception)
        {
        }
        Changed?.Invoke();
    }

    /// <summary>改名（檔名與內容的名稱一起改）。同名已存在則失敗。</summary>
    public static bool Rename(EffectPreset preset, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return false;
        try
        {
            var dir = Path.GetDirectoryName(preset.Path)!;
            var target = Path.Combine(dir, SafeFileName(newName, "preset") + ".json");
            if (!string.Equals(target, preset.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(target)) return false;
            var dto = JsonSerializer.Deserialize<PresetDto>(File.ReadAllText(preset.Path), JsonOptions) ?? new PresetDto();
            dto.Name = newName;
            File.WriteAllText(preset.Path, JsonSerializer.Serialize(dto, JsonOptions));
            if (!string.Equals(target, preset.Path, StringComparison.OrdinalIgnoreCase)) File.Move(preset.Path, target);
            Changed?.Invoke();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>搬到別的資料夾。目標已有同名檔則失敗。</summary>
    public static bool Move(EffectPreset preset, string folder)
    {
        try
        {
            var dir = AbsoluteFolder(folder);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Path.GetFileName(preset.Path));
            if (string.Equals(target, preset.Path, StringComparison.OrdinalIgnoreCase)) return true;
            if (File.Exists(target)) return false;
            File.Move(preset.Path, target);
            Changed?.Invoke();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>建資料夾（<paramref name="parent"/> 底下）。回傳新資料夾的相對路徑，失敗 null。</summary>
    public static string? CreateFolder(string parent, string name)
    {
        var safe = SafeFileName(name, "");
        if (safe.Length == 0) return null;
        var rel = parent.Length == 0 ? safe : parent + "/" + safe;
        try
        {
            Directory.CreateDirectory(AbsoluteFolder(rel));
            Changed?.Invoke();
            return rel;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>資料夾改名（只改最後一段）。回傳新的相對路徑，失敗 null。</summary>
    public static string? RenameFolder(string folder, string newName)
    {
        var safe = SafeFileName(newName, "");
        if (safe.Length == 0 || folder.Length == 0) return null;
        var slash = folder.LastIndexOf('/');
        var rel = slash < 0 ? safe : folder[..(slash + 1)] + safe;
        if (rel == folder) return folder;
        try
        {
            var target = AbsoluteFolder(rel);
            if (Directory.Exists(target)) return null;
            Directory.Move(AbsoluteFolder(folder), target);
            Changed?.Invoke();
            return rel;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>資料夾裡有沒有東西（預設集或子資料夾）。</summary>
    public static bool FolderIsEmpty(string folder)
    {
        try
        {
            var dir = AbsoluteFolder(folder);
            return !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>刪空資料夾（有內容就不刪，回傳 false）。</summary>
    public static bool DeleteFolder(string folder)
    {
        if (folder.Length == 0 || !FolderIsEmpty(folder)) return false;
        try
        {
            Directory.Delete(AbsoluteFolder(folder));
            Changed?.Invoke();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 把預設集套到圖層（一步 undo）。<paramref name="replace"/> = 取代整個堆疊，否則接在現有堆疊之後。
    /// 效果的主色取目前前景色（雲朵、物件外框那些「預設帶主色」的會用到）。
    /// </summary>
    public static void Apply(EditorSession session, RasterLayer layer, EffectPreset preset, bool replace)
    {
        var doc = session.Document;
        IReadOnlyList<LayerEffect> before;
        lock (doc.SyncRoot) before = layer.Effects;
        var added = preset.Effects.Select(e =>
            LayerEffect.Create(e.Effect, null, session.Foreground) with { Enabled = e.Enabled }).ToList();
        var after = replace ? added : before.Concat(added).ToList();
        LayerEffectCommands.SetEffects(doc, session.History, layer, before, after, $"套用預設集：{preset.Name}");
    }

    /// <summary>相對資料夾 → 絕對路徑（一律限制在根目錄底下）。</summary>
    public static string AbsoluteFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return FolderPath;
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => SafeFileName(p, "_"));
        return Path.Combine(FolderPath, Path.Combine(parts.ToArray()));
    }

    private static string RelativeFolderOf(string file)
    {
        var dir = Path.GetDirectoryName(file);
        if (dir == null) return "";
        var rel = Path.GetRelativePath(FolderPath, dir).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    private static string SafeFileName(string name, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return safe.Length == 0 ? fallback : safe;
    }

    private static void EnsureFolder()
    {
        if (!Directory.Exists(FolderPath)) Directory.CreateDirectory(FolderPath);
    }
}
