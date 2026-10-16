using GratingSensor.Lab.Models;

namespace GratingSensor.Lab.Protocol;

/// <summary>
/// GC-CT 光栅位移传感器协议命令构造与响应解析（标准 Modbus RTU）。
/// 寄存器一览：
///   0x0000 当前测量  0x1000 最大值  0x2000 最小值  0x3000 极差（均 func03, count2）
///   0x0200 地址  0x0201 波特码  0x0202 校验停止码（func06）
///   0x0300 预设/上限/下限（各 4B，func03 读 count6 / func10 写）
///   0x0800 功能控制字（func06）
///   广播地址 0xFF + func03 读 0x0200 count4 = 读内部参数
/// </summary>
public static class GratingProtocol
{
    public const byte BroadcastAddress = 0xFF;

    // ---------- 命令构造 ----------

    /// <summary>读保持寄存器：func03，reg 起始，count 寄存器数。</summary>
    public static byte[] ReadHolding(byte addr, ushort reg, ushort count)
        => ModbusRtu.BuildFrame(addr, 0x03,
            (byte)(reg >> 8), (byte)reg,
            (byte)(count >> 8), (byte)count);

    /// <summary>写单个寄存器：func06。</summary>
    public static byte[] WriteSingle(byte addr, ushort reg, ushort value)
        => ModbusRtu.BuildFrame(addr, 0x06,
            (byte)(reg >> 8), (byte)reg,
            (byte)(value >> 8), (byte)value);

    /// <summary>写多个寄存器：func10。values 为寄存器值数组（每个 16 位）。</summary>
    public static byte[] WriteMultiple(byte addr, ushort reg, ushort[] values)
    {
        int n = values.Length;
        var pdu = new byte[5 + n * 2];
        pdu[0] = (byte)(reg >> 8);
        pdu[1] = (byte)reg;
        pdu[2] = (byte)(n >> 8);
        pdu[3] = (byte)n;
        pdu[4] = (byte)(n * 2);
        int p = 5;
        foreach (var v in values)
        {
            pdu[p++] = (byte)(v >> 8);
            pdu[p++] = (byte)v;
        }
        return ModbusRtu.BuildFrame(addr, 0x10, pdu);
    }

    // ---------- 语义化命令 ----------

    public static byte[] ReadMeasurement(byte addr) => ReadHolding(addr, 0x0000, 2);
    public static byte[] ReadMax(byte addr) => ReadHolding(addr, 0x1000, 2);
    public static byte[] ReadMin(byte addr) => ReadHolding(addr, 0x2000, 2);
    public static byte[] ReadRange(byte addr) => ReadHolding(addr, 0x3000, 2);

    public static byte[] SetFunction(byte addr, FunctionCommand cmd)
        => WriteSingle(addr, 0x0800, (ushort)cmd);

    /// <summary>写预设/上限/下限。各值为 32 位原始幅值（÷divisor 得 mm）。</summary>
    public static byte[] SetPresetUpperLower(byte addr, uint preset, uint upper, uint lower)
        => WriteMultiple(addr, 0x0300, new ushort[]
        {
            (ushort)(preset >> 16), (ushort)preset,
            (ushort)(upper >> 16), (ushort)upper,
            (ushort)(lower >> 16), (ushort)lower,
        });

    public static byte[] ReadPresetUpperLower(byte addr) => ReadHolding(addr, 0x0300, 6);

    public static byte[] SetAddress(byte addr, byte newAddress) => WriteSingle(addr, 0x0200, newAddress);
    public static byte[] SetBaud(byte addr, byte baudCode) => WriteSingle(addr, 0x0201, baudCode);
    public static byte[] SetParityStop(byte addr, byte code) => WriteSingle(addr, 0x0202, code);

    /// <summary>读内部参数（广播地址，无需知道从机地址）。</summary>
    public static byte[] ReadInternalParams() => ReadHolding(BroadcastAddress, 0x0200, 4);

    // ---------- 响应解析 ----------

    /// <summary>
    /// 解析响应帧。返回 true 表示 CRC 通过且成功解析。
    /// exception 非 null 表示设备返回了 Modbus 异常（func|0x80）。
    /// </summary>
    public static bool TryParseFrame(byte[] frame, out byte addr, out byte func, out byte[] data, out byte? exception)
    {
        addr = 0; func = 0; data = Array.Empty<byte>(); exception = null;
        if (frame == null || frame.Length < 5 || !ModbusRtu.CheckCrc(frame))
            return false;
        addr = frame[0];
        func = frame[1];
        if ((func & 0x80) != 0)
        {
            exception = frame[2];
            return true;
        }
        if (func == 0x03)
        {
            int bc = frame[2];
            if (frame.Length < 3 + bc + 2) return false;
            data = new byte[bc];
            Array.Copy(frame, 3, data, 0, bc);
        }
        // func06 / func10：仅回显，无变长数据
        return true;
    }

    public static Measurement ParseMeasurement(byte[] data, int divisor)
    {
        bool negative = data[0] != 0;
        uint mag = (uint)((data[1] << 16) | (data[2] << 8) | data[3]);
        return new Measurement { Negative = negative, Magnitude = mag, Divisor = divisor };
    }

    public static PresetConfig ParsePresetUpperLower(byte[] data)
    {
        uint ReadU32(int i) => (uint)((data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3]);
        return new PresetConfig
        {
            Preset = ReadU32(0),
            UpperLimit = ReadU32(4),
            LowerLimit = ReadU32(8),
        };
    }

    public static InternalParams ParseInternalParams(byte[] data)
    {
        ushort ReadU16(int i) => (ushort)((data[i] << 8) | data[i + 1]);
        return new InternalParams
        {
            Address = (byte)ReadU16(0),
            BaudCode = (byte)ReadU16(2),
            ParityStopCode = (byte)ReadU16(4),
            Param4 = (byte)ReadU16(6),
        };
    }

    // ---------- 协议自测：校验文档示例帧 CRC ----------

    public static bool RunSelfTest()
    {
        bool ok = true;
        void Check(string name, byte[] frame, string expectHex)
        {
            var got = Convert.ToHexString(frame);
            var exp = expectHex.Replace(" ", "").ToUpperInvariant();
            bool pass = got.Equals(exp, StringComparison.OrdinalIgnoreCase);
            ok &= pass;
            Console.WriteLine($"  {(pass ? "OK " : "XX ")}{name}");
            if (!pass) Console.WriteLine($"       期望 {exp}\n       实际 {got}");
        }

        Console.WriteLine("  校验文档示例帧（含 CRC）：");
        Check("读测量 cmd",        ReadMeasurement(0x01),
              "01 03 00 00 00 02 C4 0B");
        Check("读测量 resp",       ModbusRtu.BuildFrame(0x01, 0x03, 0x04, 0x01, 0x00, 0x12, 0x39),
              "01 03 04 01 00 12 39 37 7D");
        Check("设功能(清零) cmd",  SetFunction(0x01, FunctionCommand.Clear),
              "01 06 08 00 AB 56 74 A4");
        Check("设预设/上下限 cmd", SetPresetUpperLower(0x01, 0x000007D0, 0x00001388, 0x010003E8),
              "01 10 03 00 00 06 0C 00 00 07 D0 00 00 13 88 01 00 03 E8 35 C6");
        Check("读预设/上下限 resp", ModbusRtu.BuildFrame(0x01, 0x03, 0x0C, 0x00, 0x00, 0x07, 0xD0, 0x00, 0x00, 0x13, 0x88, 0x01, 0x00, 0x03, 0xE8),
              "01 03 0C 00 00 07 D0 00 00 13 88 01 00 03 E8 5C 91");
        // 注：下方为协议文档原值，其中改波特率 CRC 经 Python 校验应为 58 73（文档写的 18 72 为笔误）
        Check("改地址 cmd",        SetAddress(0x01, 0x02),
              "01 06 02 00 00 02 09 B3");
        Check("改波特率 cmd",      SetBaud(0x01, 0x02),
              "01 06 02 01 00 02 58 73");
        Check("改校验停止 cmd",    SetParityStop(0x01, 0x02),
              "01 06 02 02 00 02 A8 73");
        Check("读内部参数 cmd",    ReadInternalParams(),
              "FF 03 02 00 00 04 50 6F");
        Check("读内部参数 resp",   ModbusRtu.BuildFrame(0x01, 0x03, 0x08, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x04),
              "01 03 08 00 01 00 02 00 00 00 04 FD 14");

        return ok;
    }
}
