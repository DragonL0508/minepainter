using System.Text.Json;
using System.Text.Json.Serialization;
using MinePainter.Core.Effects;

namespace MinePainter.App.Services;

/// <summary>效果預設集的一筆：一整個效果堆疊（不含遮罩）。</summary>
public sealed record EffectPreset(string Name, IReadOnlyList<(IEffect Effect, bool Enabled)> Effects, string Path);

/// <summary>
/// 效果預設集庫：%APPDATA%\MinePainter\EffectPresets 底下一個 .json 一筆（與文字樣式庫同一套作法：
/// 一檔一筆，使用者可直接用檔案總管整理／分享）。
/// </summary>
public static class EffectPresetStore
{
    public static string FolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MinePainter", "EffectPresets");

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

    public static List<EffectPreset> LoadAll()
    {
        var list = new List<EffectPreset>();
        try
        {
            EnsureFolder();
            foreach (var file in Directory.EnumerateFiles(FolderPath, "*.json"))
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<PresetDto>(File.ReadAllText(file), JsonOptions);
                    if (dto == null) continue;
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
                    list.Add(new EffectPreset(name, effects, file));
                }
                catch (Exception)
                {
                    // 壞檔略過
                }
            }
        }
        catch (Exception)
        {
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>存檔；同名覆蓋。回傳檔案路徑。</summary>
    public static string Save(string name, IEnumerable<LayerEffect> effects)
    {
        EnsureFolder();
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
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safe.Length == 0) safe = "preset";
        var path = Path.Combine(FolderPath, safe + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
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
    }

    private static void EnsureFolder()
    {
        if (!Directory.Exists(FolderPath)) Directory.CreateDirectory(FolderPath);
    }
}
