using GratingSensor.Lab.Models;
using GratingSensor.Lab.Protocol;

namespace GratingSensor.Lab.Transport;

/// <summary>
/// 进程内“假光栅设备”：按 GC-CT Modbus 协议回包，使无真机也能跑通全链路。
/// 行为：模拟探头在量程内正弦漂移；支持 保持/清零/极值模式/单位/方向；
/// 预设上下限、地址、波特、校验均可写可读。广播读内部参数回真地址。
/// </summary>
public class SimulatedDeviceTransport : IDeviceTransport
{
    /// <summary>设备从机地址（出厂默认 01H）。</summary>
    public byte Address { get; set; } = 0x01;

    /// <summary>换算除数：万分档 10000 / 千分档 1000。</summary>
    public int Divisor { get; set; } = 10000;

    private bool _connected;
    private readonly Random _rnd = new(98765);
    private int _tick;

    // 实时状态
    private double _posMm = 5.0;
    private bool _hold;
    private bool _imperial;
    private bool _reversed;
    private bool _extremaMode;
    private double _maxMm = double.MinValue;
    private double _minMm = double.MaxValue;

    // 配置
    private uint _preset, _upper, _lower;
    private byte _baudCode = 2;     // 38400
    private byte _parityStop = 0;   // 无校验/2停
    private byte _param4 = 4;

    public SimulatedDeviceTransport() { }

    public SimulatedDeviceTransport(int divisor) => Divisor = divisor;

    public bool IsConnected => _connected;
    public void Connect() => _connected = true;
    public void Disconnect() => _connected = false;
    public void Dispose() => Disconnect();

    public byte[] SendCommand(byte[] frame)
    {
        if (!_connected) throw new InvalidOperationException("模拟设备未连接");
        if (frame == null || frame.Length < 4) return Exception(frame?[0] ?? 0, frame?[1] ?? 0, 0x08);
        if (!ModbusRtu.CheckCrc(frame)) return Exception(frame[0], frame[1], 0x08); // CRC 错误

        byte addr = frame[0];
        byte func = frame[1];

        return func switch
        {
            0x03 => HandleRead(addr, reg: (ushort)((frame[2] << 8) | frame[3]), count: (ushort)((frame[4] << 8) | frame[5])),
            0x06 => HandleWriteSingle(addr, (ushort)((frame[2] << 8) | frame[3]), (ushort)((frame[4] << 8) | frame[5])),
            0x10 => HandleWriteMultiple(addr, frame),
            _ => Exception(addr, func, 0x01) // 非法功能
        };
    }

    // ---------- 读 ----------

    private byte[] HandleRead(byte addr, ushort reg, ushort count)
    {
        byte respAddr = addr == GratingProtocol.BroadcastAddress ? Address : addr;
        switch (reg)
        {
            case 0x0000:
                UpdatePosition();
                return ReadValue(respAddr, _posMm);
            case 0x1000:
                UpdatePosition();
                return ReadValue(respAddr, _extremaMode ? (_maxMm <= _minMm ? _posMm : _maxMm) : _posMm);
            case 0x2000:
                UpdatePosition();
                return ReadValue(respAddr, _extremaMode ? (_minMm >= _maxMm ? _posMm : _minMm) : _posMm);
            case 0x3000:
                UpdatePosition();
                double range = (!_extremaMode || _maxMm <= _minMm) ? 0 : (_maxMm - _minMm);
                return ReadValue(respAddr, range);
            case 0x0300:
                return ReadPresetUpperLower(respAddr);
            case 0x0200:
                return ReadInternalParams(respAddr);
            default:
                return Exception(respAddr, 0x03, 0x02); // 非法数据地址
        }
    }

    private byte[] ReadValue(byte respAddr, double mm)
    {
        double v = Math.Abs(mm);
        if (_imperial) v *= 25.4; // 英制：英寸
        int mag = (int)Math.Round(v * Divisor);
        mag = Math.Clamp(mag, 0, 0xFFFFFF);
        bool neg = mm < 0;
        if (_reversed) neg = !neg;
        return ModbusRtu.BuildFrame(respAddr, 0x03, 0x04,
            (byte)(neg ? 1 : 0), (byte)(mag >> 16), (byte)(mag >> 8), (byte)mag);
    }

    private byte[] ReadPresetUpperLower(byte respAddr)
    {
        var b = new byte[12];
        WriteU32(b, 0, _preset);
        WriteU32(b, 4, _upper);
        WriteU32(b, 8, _lower);
        return ModbusRtu.BuildFrame(respAddr, 0x03, new[] { (byte)0x0C }.Concat(b).ToArray());
    }

    private byte[] ReadInternalParams(byte respAddr)
    {
        var b = new byte[] { 0, Address, 0, _baudCode, 0, _parityStop, 0, _param4 };
        return ModbusRtu.BuildFrame(respAddr, 0x03, new[] { (byte)0x08 }.Concat(b).ToArray());
    }

    // ---------- 写单寄存器 ----------

    private byte[] HandleWriteSingle(byte addr, ushort reg, ushort val)
    {
        switch (reg)
        {
            case 0x0800:
                ApplyFunction(val);
                break;
            case 0x0200:
                if (val is < 1 or > 247) return Exception(addr, 0x06, 0x03); // 非法数据值
                Address = (byte)val;
                break;
            case 0x0201:
                _baudCode = (byte)(val & 0xFF);
                break;
            case 0x0202:
                _parityStop = (byte)(val & 0xFF);
                break;
            default:
                return Exception(addr, 0x06, 0x02); // 非法数据地址
        }
        return ModbusRtu.BuildFrame(addr, 0x06,
            (byte)(reg >> 8), (byte)reg, (byte)(val >> 8), (byte)val);
    }

    // ---------- 写多寄存器（预设/上下限）----------

    private byte[] HandleWriteMultiple(byte addr, byte[] frame)
    {
        ushort reg = (ushort)((frame[2] << 8) | frame[3]);
        int n = (frame[4] << 8) | frame[5];
        if (reg == 0x0300 && n == 6)
        {
            var vals = new ushort[6];
            int p = 7;
            for (int i = 0; i < 6; i++) { vals[i] = (ushort)((frame[p] << 8) | frame[p + 1]); p += 2; }
            _preset = (uint)((vals[0] << 16) | vals[1]);
            _upper = (uint)((vals[2] << 16) | vals[3]);
            _lower = (uint)((vals[4] << 16) | vals[5]);
            return ModbusRtu.BuildFrame(addr, 0x10,
                (byte)(reg >> 8), (byte)reg, (byte)(n >> 8), (byte)n);
        }
        return Exception(addr, 0x10, 0x02);
    }

    // ---------- 功能控制字 ----------

    private void ApplyFunction(ushort cmd)
    {
        switch ((FunctionCommand)cmd)
        {
            case FunctionCommand.Clear:
                _posMm = 0; _maxMm = double.MinValue; _minMm = double.MaxValue;
                break;
            case FunctionCommand.Metric: _imperial = false; break;
            case FunctionCommand.Imperial: _imperial = true; break;
            case FunctionCommand.Forward: _reversed = false; break;
            case FunctionCommand.Reverse: _reversed = true; break;
            case FunctionCommand.Hold: _hold = true; break;
            case FunctionCommand.Release: _hold = false; break;
            case FunctionCommand.ReopenExtrema:
                _maxMm = double.MinValue; _minMm = double.MaxValue;
                break;
            case FunctionCommand.NormalMode: _extremaMode = false; break;
            case FunctionCommand.ExtremaMode: _extremaMode = true; break;
        }
    }

    // ---------- 工具 ----------

    private void UpdatePosition()
    {
        if (_hold) return;
        _tick++;
        double mm = 5.0 + 3.0 * Math.Sin(_tick * 0.25) + (_rnd.NextDouble() - 0.5) * 0.06;
        mm = Math.Clamp(mm, -12.0, 12.0);
        _posMm = mm;
        if (_extremaMode)
        {
            if (mm > _maxMm) _maxMm = mm;
            if (mm < _minMm) _minMm = mm;
        }
    }

    private static void WriteU32(byte[] buf, int i, uint v)
    {
        buf[i] = (byte)(v >> 24); buf[i + 1] = (byte)(v >> 16);
        buf[i + 2] = (byte)(v >> 8); buf[i + 3] = (byte)v;
    }

    private static byte[] Exception(byte addr, byte func, byte code)
        => ModbusRtu.BuildFrame(addr, (byte)(func | 0x80), code);
}
