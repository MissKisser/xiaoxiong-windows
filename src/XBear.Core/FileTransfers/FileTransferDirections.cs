namespace XBear.Core.FileTransfers;

/// <summary>传输方向字面量常量，与契约定义严格一致。</summary>
public static class FileTransferDirections
{
    /// <summary>宿主到实例：来源落在宿主文件系统域，目标落在实例文件系统域。</summary>
    public const string HostToInstance = "host-to-instance";

    /// <summary>实例到宿主：来源落在实例文件系统域，目标落在宿主文件系统域。</summary>
    public const string InstanceToHost = "instance-to-host";
}
