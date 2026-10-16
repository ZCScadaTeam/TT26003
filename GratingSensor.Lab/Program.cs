using GratingSensor.Lab.Models;
using GratingSensor.Lab.Protocol;
using GratingSensor.Lab.Transport;

namespace GratingSensor.Lab;

internal static class Program
{
    private static IDeviceTransport? _transport;
    private static byte _address = 0x01;       // 设备出厂默认 01H
    private static int _divisor = 10000;       // 万分档（千分档改 1000）
    private static bool _imperial;             // 仅影响显示

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== GC-CT 光栅位移传感器 · 实验台（Modbus RTU）===");
        Console.WriteLine($".NET {Environment.Version}  | 默认地址 0x{_address:X2}  换算除数 {_divisor}（万分档）");
        Console.WriteLine($"协议自测：{(GratingProtocol.RunSelfTest() ? "全部通过" : "存在失败")}");

        if (args.Contains("--selftest"))
        {
            RunSelfTestFlow();
            return 0;
        }

        RunInteractive();
        return 0;
    }

    // ---------- 交互菜单 ----------

    private static void RunInteractive()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("──────── 菜单 ────────");
            Console.WriteLine("1) 连接-模拟设备   2) 连接-真实串口");
            Console.WriteLine("3) 读位移   4) 读最大   5) 读最小   6) 读极差");
            Console.WriteLine("7) 功能控制(清零/单位/方向/保持/极值)");
            Console.WriteLine("8) 写预设/上下限(演示)   9) 读预设/上下限");
            Console.WriteLine("10) 改地址   11) 改波特率   12) 改校验/停止位");
            Console.WriteLine("13) 读内部参数(广播)   14) 断开   0) 退出");
            Console.Write("选择> ");
            var line = Console.ReadLine();
            if (line == null || line.Trim() == "0") break;

            try
            {
                switch (line.Trim())
                {
                    case "1": ConnectSimulator(); break;
                    case "2": ConnectSerial(); break;
                    case "3": Send(GratingProtocol.ReadMeasurement(_address), "读位移", ShowValue); break;
                    case "4": Send(GratingProtocol.ReadMax(_address), "读最大", ShowValue); break;
                    case "5": Send(GratingProtocol.ReadMin(_address), "读最小", ShowValue); break;
                    case "6": Send(GratingProtocol.ReadRange(_address), "读极差", ShowValue); break;
                    case "7": FunctionMenu(); break;
                    case "8":
                        Send(GratingProtocol.SetPresetUpperLower(_address, 2000, 5000, 1000),
                            "写预设/上下限(2000/5000/1000)", ShowAck);
                        break;
                    case "9": Send(GratingProtocol.ReadPresetUpperLower(_address), "读预设/上下限", ShowPreset); break;
                    case "10":
                        Console.Write("  新地址(1-247)> ");
                        if (byte.TryParse(Console.ReadLine(), out var na) && na is >= 1 and <= 247)
                        {
                            Send(GratingProtocol.SetAddress(_address, na), $"改地址→0x{na:X2}", r =>
                            {
                                _address = na;
                                Console.WriteLine($"  后续通信地址已更新为 0x{na:X2}");
                            });
                        }
                        break;
                    case "11":
                        Console.Write("  波特码(0=9600 1=19200 2=38400 3=57600 4=115200 5=4800)> ");
                        if (byte.TryParse(Console.ReadLine(), out var bc) && bc <= 5)
                            Send(GratingProtocol.SetBaud(_address, bc), $"改波特率→{BaudCode.Name(bc)}", ShowAck);
                        break;
                    case "12":
                        Console.Write("  校验停止码(0=无/2停 1=奇/1停 2=偶/1停 3=无/1停 4=奇/2停 5=偶/2停)> ");
                        if (byte.TryParse(Console.ReadLine(), out var pc) && pc <= 5)
                            Send(GratingProtocol.SetParityStop(_address, pc), $"改校验停止→{ParityStopCode.Name(pc)}", ShowAck);
                        break;
                    case "13":
                        Send(GratingProtocol.ReadInternalParams(), "读内部参数(广播)", ShowInternal);
                        break;
                    case "14": _transport?.Disconnect(); Console.WriteLine("  已断开。"); break;
                    default: Console.WriteLine("  无效选择。"); break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  !! 错误：{ex.Message}");
            }
        }
        _transport?.Dispose();
    }

    private static void FunctionMenu()
    {
        Console.WriteLine("  功能控制：a)清零 b)公制 c)英制 d)正向 e)反向 f)保持 g)释放 h)重开极值 i)正常模式 j)极值模式");
        Console.Write("  选择> ");
        var k = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        var map = new Dictionary<string, FunctionCommand>
        {
            ["a"] = FunctionCommand.Clear, ["b"] = FunctionCommand.Metric, ["c"] = FunctionCommand.Imperial,
            ["d"] = FunctionCommand.Forward, ["e"] = FunctionCommand.Reverse, ["f"] = FunctionCommand.Hold,
            ["g"] = FunctionCommand.Release, ["h"] = FunctionCommand.ReopenExtrema, ["i"] = FunctionCommand.NormalMode,
            ["j"] = FunctionCommand.ExtremaMode,
        };
        if (map.TryGetValue(k, out var cmd))
            Send(GratingProtocol.SetFunction(_address, cmd), $"功能控制→{cmd}", ShowAck);
        else Console.WriteLine("  无效选择。");
    }

    // ---------- 收发 ----------

    private static void Send(byte[] frame, string label, Action<ResponseInfo>? onParse = null)
    {
        if (_transport is null || !_transport.IsConnected)
        {
            Console.WriteLine("  !! 尚未连接设备（先用 1 或 2 连接）。");
            return;
        }
        Console.WriteLine($">> {label}：{Convert.ToHexString(frame)}");
        var resp = _transport.SendCommand(frame);
        Console.WriteLine($"<< 响应：{Convert.ToHexString(resp)}");

        if (!GratingProtocol.TryParseFrame(resp, out var addr, out var func, out var data, out var ex))
        {
            Console.WriteLine("   !! 响应 CRC 校验失败 / 无法解析");
            return;
        }
        Console.WriteLine($"   地址=0x{addr:X2} 功能码=0x{func:X2}");
        if (ex.HasValue)
        {
            Console.WriteLine($"   !! 设备异常，异常码=0x{ex.Value:X2}");
            return;
        }
        onParse?.Invoke(new ResponseInfo(addr, func, data));
    }

    private readonly record struct ResponseInfo(byte Address, byte Func, byte[] Data);

    // ---------- 展示 ----------

    private static void ShowValue(ResponseInfo r)
    {
        if (r.Data.Length < 4) { Console.WriteLine("  数据长度不足。"); return; }
        var m = GratingProtocol.ParseMeasurement(r.Data, _divisor);
        double mm = _imperial ? m.Millimeters / 25.4 : m.Millimeters;
        string unit = _imperial ? "inch" : "mm";
        Console.WriteLine($"   位移 = {(mm >= 0 ? "+" : "")}{mm:F4} {unit}  （原始：{m}）");
    }

    private static void ShowPreset(ResponseInfo r)
    {
        if (r.Data.Length < 12) { Console.WriteLine("  数据长度不足。"); return; }
        var c = GratingProtocol.ParsePresetUpperLower(r.Data);
        Console.WriteLine($"   预设={c.PresetMm(_divisor):F4}mm  上限={c.UpperMm(_divisor):F4}mm  下限={c.LowerMm(_divisor):F4}mm");
        Console.WriteLine($"   （原始幅值 预设={c.Preset} 上限={c.UpperLimit} 下限={c.LowerLimit}）");
    }

    private static void ShowInternal(ResponseInfo r)
    {
        if (r.Data.Length < 8) { Console.WriteLine("  数据长度不足。"); return; }
        var p = GratingProtocol.ParseInternalParams(r.Data);
        Console.WriteLine($"   地址=0x{p.Address:X2}  波特={BaudCode.Name(p.BaudCode)}(码{p.BaudCode})  校验停止={ParityStopCode.Name(p.ParityStopCode)}(码{p.ParityStopCode})  参数4=0x{p.Param4:X2}");
    }

    private static void ShowAck(ResponseInfo r) => Console.WriteLine("   执行成功（设备回显）。");

    // ---------- 连接 ----------

    private static void ConnectSimulator()
    {
        _transport?.Dispose();
        _transport = new SimulatedDeviceTransport(_divisor) { Address = _address };
        _transport.Connect();
        Console.WriteLine("   已连接【模拟设备】，可进行控制/读取实验。");
    }

    private static void ConnectSerial()
    {
        Console.Write("  串口号(如 COM3)> ");
        var port = Console.ReadLine()?.Trim() ?? "COM3";
        Console.Write("  波特率(默认38400)> ");
        var baudStr = Console.ReadLine()?.Trim();
        int baud = int.TryParse(baudStr, out var b) && b > 0 ? b : 38400;
        Console.Write("  从机地址(默认1)> ");
        var addrStr = Console.ReadLine()?.Trim();
        _address = byte.TryParse(addrStr, out var a) ? a : (byte)1;

#if REAL_SERIAL
        _transport?.Dispose();
        _transport = new SerialTransport(port, baud, _address);
        _transport.Connect();
        Console.WriteLine($"   已连接真实串口 {port} @ {baud}。");
#else
        Console.WriteLine("   !! 真实串口未启用：请在 GratingSensor.Lab.csproj 与 SerialTransport.cs 中按注释开启（需 System.IO.Ports 包）。当前可用菜单 1 走模拟设备。");
#endif
    }

    // ---------- 非交互自测流程 ----------

    private static void RunSelfTestFlow()
    {
        Console.WriteLine("\n--- 模拟设备全链路自测 ---");
        var dev = new SimulatedDeviceTransport(_divisor) { Address = _address };
        dev.Connect();
        byte addr = _address;

        SendVia(dev, addr, GratingProtocol.ReadMeasurement(addr), "读位移(1)", ShowValue);
        SendVia(dev, addr, GratingProtocol.SetFunction(addr, FunctionCommand.Clear), "清零", ShowAck);
        SendVia(dev, addr, GratingProtocol.ReadMeasurement(addr), "读位移(清零后)", ShowValue);
        SendVia(dev, addr, GratingProtocol.SetFunction(addr, FunctionCommand.ExtremaMode), "进入极值模式", ShowAck);
        for (int i = 0; i < 5; i++)
            SendVia(dev, addr, GratingProtocol.ReadMeasurement(addr), $"读位移(极值采样{i + 1})", ShowValue);
        SendVia(dev, addr, GratingProtocol.ReadMax(addr), "读最大", ShowValue);
        SendVia(dev, addr, GratingProtocol.ReadMin(addr), "读最小", ShowValue);
        SendVia(dev, addr, GratingProtocol.ReadRange(addr), "读极差", ShowValue);
        SendVia(dev, addr, GratingProtocol.SetFunction(addr, FunctionCommand.NormalMode), "回到正常模式", ShowAck);

        SendVia(dev, addr, GratingProtocol.SetPresetUpperLower(addr, 2000, 5000, 1000), "写预设/上下限(2000/5000/1000)", ShowAck);
        SendVia(dev, addr, GratingProtocol.ReadPresetUpperLower(addr), "读预设/上下限", ShowPreset);

        SendVia(dev, addr, GratingProtocol.ReadInternalParams(), "读内部参数(广播)", r =>
        {
            ShowInternal(r);
            addr = r.Address; // 用设备真实地址继续
        });

        SendVia(dev, addr, GratingProtocol.SetAddress(addr, 0x05), "改地址→0x05", r =>
        {
            Console.WriteLine("   后续通信地址已更新为 0x05");
            addr = 0x05;
        });
        SendVia(dev, addr, GratingProtocol.ReadMeasurement(addr), "读位移(改地址后)", ShowValue);

        Console.WriteLine("\n自测完成。");
    }

    private static void SendVia(IDeviceTransport dev, byte addr, byte[] frame, string label, Action<ResponseInfo>? onParse = null)
    {
        Console.WriteLine($">> {label}：{Convert.ToHexString(frame)}");
        var resp = dev.SendCommand(frame);
        Console.WriteLine($"<< 响应：{Convert.ToHexString(resp)}");
        if (GratingProtocol.TryParseFrame(resp, out var a, out var f, out var d, out var ex))
        {
            if (ex.HasValue) Console.WriteLine($"   !! 设备异常 0x{ex.Value:X2}");
            else onParse?.Invoke(new ResponseInfo(a, f, d));
        }
        else Console.WriteLine("   !! 响应解析失败 / CRC 错误");
    }
}
