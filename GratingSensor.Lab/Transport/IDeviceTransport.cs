namespace GratingSensor.Lab.Transport;

/// <summary>设备传输抽象：连接/断开/收发一帧（由上层用 GratingProtocol 解析）。</summary>
public interface IDeviceTransport : IDisposable
{
    bool IsConnected { get; }
    void Connect();
    void Disconnect();
    /// <summary>发送一帧 Modbus 命令字节，返回完整响应帧字节。</summary>
    byte[] SendCommand(byte[] frame);
}
