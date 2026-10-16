using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GratingSensor.Lab.Models;
using GratingSensor.Lab.Protocol;
using GratingSensor.Lab.Transport;

namespace GratingSensor.Lab.UI;

public partial class MainWindow : Window
{
    private IDeviceTransport? _transport;
    private byte _addr = 1;
    private int _divisor = 10000;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => PollTick();
        chkPoll.IsCheckedChanged += (_, _) =>
        {
            if (chkPoll.IsChecked == true)
            {
                if (_transport != null) _timer.Start();
                else Log("[提示] 请先连接设备再开启连续采样。");
            }
            else _timer.Stop();
        };
    }

    private void Log(string s) => txtLog.Text += s + "\n";
    private void LogHex(string label, byte[] b) => Log($"{label}: {Convert.ToHexString(b)}");

    private void BtnConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (_transport == null)
        {
            _addr = (byte)(int)(numAddr.Value ?? 1);
            _divisor = cmbDivisor.SelectedIndex == 0 ? 10000 : 1000;
            if (cmbMode.SelectedIndex == 0)
            {
                _transport = new SimulatedDeviceTransport(_divisor) { Address = _addr };
                Log($"[连接] 模拟设备 (地址 {_addr}, {(_divisor == 10000 ? "万分" : "千分")}档)");
            }
            else
            {
#if REAL_SERIAL
                _transport = new SerialTransport(txtPort.Text ?? "COM3", (int)numBaud.Value, _addr);
#else
                Log("[连接] 真实串口未启用：请在 GratingSensor.Lab/Transport/SerialTransport.cs 顶部取消注释 #define REAL_SERIAL，并在 csproj 引用 System.IO.Ports。");
                return;
#endif
            }
            try { _transport.Connect(); btnConnect.Content = "断开"; }
            catch (Exception ex) { Log($"[连接失败] {ex.Message}"); _transport = null; }
        }
        else
        {
            _timer.Stop(); chkPoll.IsChecked = false;
            try { _transport.Disconnect(); _transport.Dispose(); } catch { }
            _transport = null; btnConnect.Content = "连接"; Log("[断开]");
        }
    }

    private byte[]? Send(byte[] frame, string label)
    {
        if (_transport == null) { Log("[提示] 请先连接设备。"); return null; }
        try
        {
            LogHex(label + " 发送", frame);
            var resp = _transport.SendCommand(frame);
            LogHex(label + " 响应", resp);
            return resp;
        }
        catch (Exception ex) { Log($"[异常] {ex.Message}"); return null; }
    }

    private void BtnSelfTest_Click(object? sender, RoutedEventArgs e)
    {
        Log("=== 协议自测 (CRC 校验文档示例帧) ===");
        bool ok = GratingProtocol.RunSelfTest();
        Log(ok ? "自测全部通过 ✅" : "自测存在失败 ❌");
    }

    private void BtnReadDisp_Click(object? s, RoutedEventArgs e) => ReadDisplacement();

    private void ReadDisplacement()
    {
        var resp = Send(GratingProtocol.ReadMeasurement(_addr), "读取位移");
        if (resp == null) return;
        if (!GratingProtocol.TryParseFrame(resp, out _, out _, out var data, out var ex) || ex != null) { Log("[解析失败]"); return; }
        var m = GratingProtocol.ParseMeasurement(data, _divisor);
        tbDisp.Text = $"位移: {m.Millimeters:F4} mm";
        AddSample(m.Millimeters);
    }

    private void AddSample(double mm)
    {
        var list = (lbSamples.ItemsSource as List<string>) ?? new List<string>();
        list.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {mm:F4} mm");
        if (list.Count > 200) list.RemoveAt(list.Count - 1);
        lbSamples.ItemsSource = list;
    }

    private void PollTick()
    {
        if (_transport == null) { _timer.Stop(); chkPoll.IsChecked = false; return; }
        ReadDisplacement();
    }

    private void BtnReadExtrema_Click(object? s, RoutedEventArgs e)
    {
        var tag = (s as Control)?.Tag?.ToString() ?? "range";
        byte[] frame = tag switch
        {
            "max" => GratingProtocol.ReadMax(_addr),
            "min" => GratingProtocol.ReadMin(_addr),
            _ => GratingProtocol.ReadRange(_addr)
        };
        var resp = Send(frame, "读取" + tag);
        if (resp == null) return;
        if (!GratingProtocol.TryParseFrame(resp, out _, out _, out var data, out var ex) || ex != null) { Log("[解析失败]"); return; }
        var m = GratingProtocol.ParseMeasurement(data, _divisor);
        Log($"[{tag}] {m.Millimeters:F4} mm");
    }

    private void BtnFunc_Click(object? s, RoutedEventArgs e)
    {
        var tag = (s as Control)?.Tag?.ToString();
        if (tag == null || !ushort.TryParse(tag, NumberStyles.HexNumber, null, out var code)) return;
        Send(GratingProtocol.SetFunction(_addr, (FunctionCommand)code), $"功能 {(FunctionCommand)code}");
    }

    private void BtnWritePreset_Click(object? s, RoutedEventArgs e)
    {
        uint preset = (uint)Math.Round((double)(numPreset.Value ?? 0) * _divisor);
        uint upper = (uint)Math.Round((double)(numUpper.Value ?? 0) * _divisor);
        uint lower = (uint)Math.Round((double)(numLower.Value ?? 0) * _divisor);
        Send(GratingProtocol.SetPresetUpperLower(_addr, preset, upper, lower), "写入预设/上下限");
        Log($"  原始幅值: 预设{preset} 上限{upper} 下限{lower}");
    }

    private void BtnReadPreset_Click(object? s, RoutedEventArgs e)
    {
        var resp = Send(GratingProtocol.ReadPresetUpperLower(_addr), "读取预设/上下限");
        if (resp == null) return;
        if (!GratingProtocol.TryParseFrame(resp, out _, out _, out var data, out var ex) || ex != null) { Log("[解析失败]"); return; }
        var pc = GratingProtocol.ParsePresetUpperLower(data);
        Log($"预设 {pc.PresetMm(_divisor):F4} / 上限 {pc.UpperMm(_divisor):F4} / 下限 {pc.LowerMm(_divisor):F4} mm");
    }

    private void BtnSetAddr_Click(object? s, RoutedEventArgs e)
    {
        byte na = (byte)(int)(numNewAddr.Value ?? 1);
        var resp = Send(GratingProtocol.SetAddress(_addr, na), "改地址");
        if (resp == null) return;
        if (GratingProtocol.TryParseFrame(resp, out _, out _, out _, out var ex) && ex == null)
        {
            _addr = na;
            Log($"  地址已改为 {na}，后续命令使用新地址。");
        }
    }

    private void BtnSetBaud_Click(object? s, RoutedEventArgs e)
    {
        byte code = (byte)cmbBaud.SelectedIndex;
        Send(GratingProtocol.SetBaud(_addr, code), "改波特");
        Log($"  波特码 {code} ({BaudCode.Name(code)})，真实设备需重连用新波特。");
    }

    private void BtnSetParity_Click(object? s, RoutedEventArgs e)
    {
        byte code = (byte)cmbParity.SelectedIndex;
        Send(GratingProtocol.SetParityStop(_addr, code), "改校验/停止");
        Log($"  校验停止码 {code} ({ParityStopCode.Name(code)})");
    }

    private void BtnReadInternal_Click(object? s, RoutedEventArgs e)
    {
        var resp = Send(GratingProtocol.ReadInternalParams(), "广播读内部参数");
        if (resp == null) return;
        if (!GratingProtocol.TryParseFrame(resp, out var a, out _, out var data, out var ex) || ex != null) { Log("[解析失败]"); return; }
        var ip = GratingProtocol.ParseInternalParams(data);
        Log($"  地址={ip.Address} 波特={BaudCode.Name(ip.BaudCode)} 校验停止={ParityStopCode.Name(ip.ParityStopCode)} 参数4={ip.Param4} (响应地址 {a})");
    }
}
