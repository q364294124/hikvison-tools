using System.IO;

namespace HikDeployTool.Services;

/// <summary>
/// 通道管理偏好：每台设备"哪些通道在预览树里显示"（勾选结果）+ OSD 名称缓存。
///
/// 为什么单独存一份而不塞进 settings：这是按设备 keyed 的字典，台账里
/// 通道号会随接入情况变化，混在一起会让 settings.json 越长越乱。
/// 文件 humans 可读（WriteIndented），现场手工改坏也不至于崩——Load 失败当空表。
/// </summary>
public static class ChannelPrefs
{
    public sealed class DevicePref
    {
        /// <summary>被手动隐藏的通道号（SDK 真实通道号，如 33）。</summary>
        public List<int> Disabled { get; set; } = [];

        /// <summary>通道显示名（OSD 同步结果），key = 通道号字符串。</summary>
        public Dictionary<string, string> Names { get; set; } = [];
    }

    private static readonly object Gate = new();
    private static Dictionary<string, DevicePref> _prefs = Load();

    private static Dictionary<string, DevicePref> Load()
        => JsonStore.Load<Dictionary<string, DevicePref>>(AppPaths.ChannelPrefsFile) ?? [];

    private static void Save()
    {
        lock (Gate) JsonStore.Save(AppPaths.ChannelPrefsFile, _prefs);
    }

    private static DevicePref Get(string deviceKey)
    {
        lock (Gate)
        {
            if (!_prefs.TryGetValue(deviceKey, out var pref))
                _prefs[deviceKey] = pref = new DevicePref();
            return pref;
        }
    }

    /// <summary>该设备被手动隐藏的通道集合（快照，改了不回写）。</summary>
    public static HashSet<int> DisabledChannels(string deviceKey)
    {
        lock (Gate)
        {
            return _prefs.TryGetValue(deviceKey, out var pref)
                ? [.. pref.Disabled]
                : [];
        }
    }

    /// <summary>覆盖某台设备的隐藏通道表并落盘。</summary>
    public static void SetDisabled(string deviceKey, IEnumerable<int> disabled)
    {
        var pref = Get(deviceKey);
        lock (Gate) pref.Disabled = [.. disabled];
        Save();
    }

    /// <summary>取通道显示名：先查内存缓存，再查偏好文件（可能被通道管理同步过）。</summary>
    public static string? LookupName(string deviceKey, int channel)
    {
        lock (Gate)
        {
            return _prefs.TryGetValue(deviceKey, out var pref) &&
                   pref.Names.TryGetValue(channel.ToString(), out var name)
                ? name
                : null;
        }
    }

    /// <summary>整份通道名表（副本；缺省空表，不落盘）。</summary>
    public static Dictionary<string, string> NamesFor(string deviceKey)
    {
        lock (Gate)
        {
            return _prefs.TryGetValue(deviceKey, out var pref)
                ? new Dictionary<string, string>(pref.Names)
                : [];
        }
    }

    /// <summary>合并写入通道名并落盘（OSD 同步 / 通道管理保存时调用）。</summary>
    public static void SetNames(string deviceKey, IReadOnlyDictionary<int, string> names)
    {
        if (names.Count == 0) return;
        var pref = Get(deviceKey);
        lock (Gate)
        {
            foreach (var (ch, name) in names)
                if (!string.IsNullOrWhiteSpace(name)) pref.Names[ch.ToString()] = name.Trim();
        }
        Save();
    }

    /// <summary>设备从台账清掉时顺带清理（防 channel_prefs.json 无限膨胀）。</summary>
    public static void Forget(string deviceKey)
    {
        lock (Gate)
        {
            if (_prefs.Remove(deviceKey)) Save();
        }
    }
}
