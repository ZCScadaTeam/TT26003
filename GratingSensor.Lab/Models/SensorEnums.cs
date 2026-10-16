namespace GratingSensor.Lab.Models;

/// <summary>功能控制字（写 0x0800 寄存器）。</summary>
public enum FunctionCommand : ushort
{
    Clear = 0xAB56,        // 清零
    Metric = 0x0001,       // 公制
    Imperial = 0x0002,     // 英制
    Forward = 0x0003,      // 正向
    Reverse = 0x0004,      // 反向
    Hold = 0x0005,         // 保持
    Release = 0x0006,      // 释放
    ReopenExtrema = 0x0007,// 重开极值
    NormalMode = 0x0008,   // 正常模式
    ExtremaMode = 0x0009,  // 极值模式
}

/// <summary>波特率代码（写 0x0201 寄存器）。</summary>
public static class BaudCode
{
    public const byte B9600 = 0;
    public const byte B19200 = 1;
    public const byte B38400 = 2;
    public const byte B57600 = 3;
    public const byte B115200 = 4;
    public const byte B4800 = 5;

    public static int ToBaud(byte code) => code switch
    {
        0 => 9600, 1 => 19200, 2 => 38400, 3 => 57600, 4 => 115200, 5 => 4800,
        _ => 38400
    };

    public static string Name(byte code) => code switch
    {
        0 => "9600", 1 => "19200", 2 => "38400", 3 => "57600", 4 => "115200", 5 => "4800",
        _ => "?"
    };
}

/// <summary>校验/停止位代码（写 0x0202 寄存器）。</summary>
public static class ParityStopCode
{
    public const byte None2 = 0;   // 无校验 / 2 停止
    public const byte Odd1 = 1;    // 奇校验 / 1 停止
    public const byte Even1 = 2;   // 偶校验 / 1 停止
    public const byte None1 = 3;   // 无校验 / 1 停止
    public const byte Odd2 = 4;    // 奇校验 / 2 停止
    public const byte Even2 = 5;   // 偶校验 / 2 停止

    public static string Name(byte code) => code switch
    {
        0 => "无校验/2停", 1 => "奇校验/1停", 2 => "偶校验/1停",
        3 => "无校验/1停", 4 => "奇校验/2停", 5 => "偶校验/2停",
        _ => "?"
    };
}
