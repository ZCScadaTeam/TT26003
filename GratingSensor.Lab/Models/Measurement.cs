namespace GratingSensor.Lab.Models;

/// <summary>
/// 光栅位移测量值。原始协议返回：符号(1B) + 24 位幅值(3B)，
/// 位移 = ±幅值 / Divisor（万分档 ÷10000，千分档 ÷1000）。
/// </summary>
public class Measurement
{
    /// <summary>true 表示负值（协议符号字节 = 0x01）。</summary>
    public bool Negative { get; set; }

    /// <summary>24 位幅值（0 ~ 0xFFFFFF）。</summary>
    public uint Magnitude { get; set; }

    /// <summary>换算除数：万分档 10000，千分档 1000。</summary>
    public int Divisor { get; set; } = 10000;

    /// <summary>换算后的位移（毫米）。</summary>
    public double Millimeters => (Negative ? -1.0 : 1.0) * Magnitude / Divisor;

    public override string ToString()
    {
        var unit = Divisor == 10000 ? "万分" : (Divisor == 1000 ? "千分" : "÷" + Divisor);
        return $"{(Negative ? "-" : "")}{Magnitude} 原始幅值 | {Millimeters:F4} mm（{unit}档）";
    }
}
