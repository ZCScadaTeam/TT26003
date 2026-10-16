namespace GratingSensor.Lab.Protocol;

/// <summary>
/// Modbus RTU 底层：CRC16/Modbus 计算、组帧、CRC 校验。
/// 多项式 0x8005，初始 0xFFFF，输入/输出均反射，传输时低字节在前。
/// </summary>
public static class ModbusRtu
{
    public static ushort Crc16(byte[] data) => Crc16(data, 0, data.Length);

    public static ushort Crc16(byte[] data, int offset, int count)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < count; i++)
        {
            crc ^= data[offset + i];
            for (int b = 0; b < 8; b++)
            {
                if ((crc & 1) != 0)
                    crc = (ushort)((crc >> 1) ^ 0xA001);
                else
                    crc >>= 1;
            }
        }
        return crc; // 线上传输顺序：低字节在前
    }

    /// <summary>组一帧：addr + func + pdu + CRC(低,高)。</summary>
    public static byte[] BuildFrame(byte addr, byte func, params byte[] pdu)
    {
        var frame = new byte[1 + 1 + pdu.Length + 2];
        int p = 0;
        frame[p++] = addr;
        frame[p++] = func;
        foreach (var b in pdu) frame[p++] = b;
        ushort crc = Crc16(frame, 0, p);
        frame[p++] = (byte)(crc & 0xFF);
        frame[p++] = (byte)(crc >> 8);
        return frame;
    }

    /// <summary>校验整帧 CRC（最后一字节为高，倒数第二为低）。</summary>
    public static bool CheckCrc(byte[] frame)
    {
        if (frame == null || frame.Length < 4) return false;
        ushort calc = Crc16(frame, 0, frame.Length - 2);
        ushort recv = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
        return calc == recv;
    }
}
