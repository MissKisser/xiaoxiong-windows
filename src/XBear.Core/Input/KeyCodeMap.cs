using XBear.Core.Abstractions;

namespace XBear.Core.Input;

/// <summary>
/// 按键编码表。每个按键同时给出实例侧按键码与宿主侧输入事件码，
/// 使原生通路与投屏通路共用同一份按键语义，避免两处各写一份映射而互相漂移。
/// </summary>
public static class KeyCodeMap
{
    private static readonly Dictionary<AndroidKey, VirtualKey> Map = new()
    {
        [AndroidKey.Back] = new(AndroidKey.Back, 4, 158),
        [AndroidKey.Home] = new(AndroidKey.Home, 3, 102),
        [AndroidKey.AppSwitch] = new(AndroidKey.AppSwitch, 187, 187),
        [AndroidKey.Enter] = new(AndroidKey.Enter, 66, 28),
        [AndroidKey.Delete] = new(AndroidKey.Delete, 67, 14),
        [AndroidKey.Menu] = new(AndroidKey.Menu, 82, 127),
        [AndroidKey.Tab] = new(AndroidKey.Tab, 61, 15),
        [AndroidKey.Space] = new(AndroidKey.Space, 62, 57),
        [AndroidKey.DpadCenter] = new(AndroidKey.DpadCenter, 23, 28),
        [AndroidKey.ArrowUp] = new(AndroidKey.ArrowUp, 19, 103),
        [AndroidKey.ArrowDown] = new(AndroidKey.ArrowDown, 20, 108),
        [AndroidKey.ArrowLeft] = new(AndroidKey.ArrowLeft, 21, 105),
        [AndroidKey.ArrowRight] = new(AndroidKey.ArrowRight, 22, 106),
        [AndroidKey.VolumeUp] = new(AndroidKey.VolumeUp, 24, 115),
        [AndroidKey.VolumeDown] = new(AndroidKey.VolumeDown, 25, 114),
        [AndroidKey.Power] = new(AndroidKey.Power, 26, 116),
    };

    /// <summary>取按键编码。</summary>
    /// <param name="key">按键标识。</param>
    /// <returns>该按键在两条通路下的编码。</returns>
    /// <exception cref="KeyNotFoundException">按键标识不在表内时抛出。</exception>
    public static VirtualKey Get(AndroidKey key) => Map[key];

    /// <summary>尝试取按键编码。</summary>
    /// <param name="key">按键标识。</param>
    /// <param name="value">取到的编码，未命中时为默认值。</param>
    /// <returns>表内存在该按键时返回 true。</returns>
    public static bool TryGet(AndroidKey key, out VirtualKey value) => Map.TryGetValue(key, out value);
}
