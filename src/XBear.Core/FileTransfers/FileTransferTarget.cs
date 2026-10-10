namespace XBear.Core.FileTransfers;

/// <summary>
/// 文件传输操作的目标：一个运行中的实例及其在宿主上映射的 adb 端口。
/// </summary>
/// <param name="InstanceId">实例标识，取值须符合实例标识命名规范。</param>
/// <param name="AdbPort">该实例在宿主上映射的 adb 端口。</param>
public sealed record FileTransferTarget(string InstanceId, int AdbPort);
