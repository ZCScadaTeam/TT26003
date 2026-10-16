namespace GratingSensor.Lab.Models;

/// <summary>预设值 / 上限 / 下限（读/写 0x0300，各 4 字节 = 32 位原始幅值）。</summary>
public class PresetConfig
{
    public uint Preset { get; set; }
    public uint UpperLimit { get; set; }
    public uint LowerLimit { get; set; }

    public double PresetMm(int divisor) => (double)Preset / divisor;
    public double UpperMm(int divisor) => (double)UpperLimit / divisor;
    public double LowerMm(int divisor) => (double)LowerLimit / divisor;
}

/// <summary>设备内部参数（广播 FF 读 0x0200，4 寄存器）。</summary>
public class InternalParams
{
    public byte Address { get; set; }
    public byte BaudCode { get; set; }
    public byte ParityStopCode { get; set; }
    public byte Param4 { get; set; }
}
