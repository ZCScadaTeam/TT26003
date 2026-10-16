// 真实 RS232 传输实现（标准 Modbus RTU）。
// 默认不编译（避免无 System.IO.Ports 包时无法构建）。启用步骤：
//   1) 在 GratingSensor.Lab.csproj 取消注释 System.IO.Ports 的 PackageReference
//   2) 在本文件顶部加 #define REAL_SERIAL
#define REAL_SERIAL
#if REAL_SERIAL
using System.IO.Ports;

namespace GratingSensor.Lab.Transport;

/// <summary>真实串口传输：默认 8 数据位 / 无校验或配置 / 2 停止位，按 Modbus RTU 读响应。</summary>
public class SerialTransport : IDeviceTransport
{
    private readonly SerialPort _port;

    public SerialTransport(string portName, int baud, byte address,
        Parity parity = Parity.None, StopBits stopBits = StopBits.Two)
    {
        _port = new SerialPort(portName, baud, parity, 8, stopBits)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
        };
        Address = address;
    }

    public byte Address { get; }

    public bool IsConnected => _port.IsOpen;

    public void Connect()
    {
        if (!_port.IsOpen) _port.Open();
    }

    public void Disconnect()
    {
        if (_port.IsOpen) _port.Close();
    }

    public void Dispose()
    {
        Disconnect();
        _port.Dispose();
    }

    public byte[] SendCommand(byte[] frame)
    {
        _port.DiscardInBuffer();
        _port.Write(frame, 0, frame.Length);

        // Modbus RTU：帧间至少 3.5 字符静默；这里用简短轮询收集完整响应
        var buf = new List<byte>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 250)
        {
            if (_port.BytesToRead > 0)
            {
                int b = _port.ReadByte();
                if (b >= 0)
                {
                    buf.Add((byte)b);
                    sw.Restart();
                }
            }
            else
            {
                System.Threading.Thread.Sleep(5);
            }
        }
        return buf.ToArray();
    }
}
#endif
