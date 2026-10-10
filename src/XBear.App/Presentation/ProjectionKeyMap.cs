using XBear.Core.Abstractions;
using XBear.Core.Input;
using XLivingRoom = System.Windows.Input.Key;

namespace XBear.App.Presentation;

/// <summary>
/// 宿主键盘按键到实例按键标识的映射。投屏窗口只负责识别宿主按键，
/// 具体投递哪一键由本表决定，按下与抬起成对投递由调用方保证。
/// </summary>
public static class ProjectionKeyMap
{
    /// <summary>
    /// 尝试把宿主按键映射为实例按键标识。
    /// </summary>
    /// <param name="key">宿主按键。</param>
    /// <param name="value">映射到的实例按键标识，未命中时为默认值。</param>
    /// <returns>存在映射时返回 true。</returns>
    public static bool TryResolve(XLivingRoom key, out AndroidKey value)
    {
        switch (key)
        {
            case XLivingRoom.Escape:
            case XLivingRoom.Back:
                value = AndroidKey.Back;
                return true;

            case XLivingRoom.Home:
                value = AndroidKey.Home;
                return true;

            case XLivingRoom.Apps:
            case XLivingRoom.System:
                value = AndroidKey.AppSwitch;
                return true;

            case XLivingRoom.Enter:
                value = AndroidKey.Enter;
                return true;

            case XLivingRoom.Delete:
                value = AndroidKey.Delete;
                return true;

            case XLivingRoom.Tab:
                value = AndroidKey.Tab;
                return true;

            case XLivingRoom.Space:
                value = AndroidKey.Space;
                return true;

            case XLivingRoom.Left:
                value = AndroidKey.ArrowLeft;
                return true;

            case XLivingRoom.Right:
                value = AndroidKey.ArrowRight;
                return true;

            case XLivingRoom.Up:
                value = AndroidKey.ArrowUp;
                return true;

            case XLivingRoom.Down:
                value = AndroidKey.ArrowDown;
                return true;

            case XLivingRoom.OemPlus:
            case XLivingRoom.Add:
                value = AndroidKey.VolumeUp;
                return true;

            case XLivingRoom.OemMinus:
            case XLivingRoom.Subtract:
                value = AndroidKey.VolumeDown;
                return true;

            case XLivingRoom.Sleep:
                value = AndroidKey.Power;
                return true;

            default:
                value = default;
                return false;
        }
    }

    /// <summary>
    /// 取按键在实例侧的编码。
    /// </summary>
    /// <param name="key">实例按键标识。</param>
    /// <returns>该按键的编码。</returns>
    public static VirtualKey Encode(AndroidKey key) => KeyCodeMap.Get(key);
}