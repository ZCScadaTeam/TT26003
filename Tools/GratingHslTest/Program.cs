using HslCommunication;
using HslCommunication.Core.Net;
using System.Text;

// ZX-EN-T6 位移传感器控制器（LAN 口）HSL(TCP + ASCII) 连通性测试工具
// 手册：ZX-EN-T6使用手册V5.0 —— EtherNet(TCP/IP) ASCII 指令协议（非 Modbus）
// 用法: dotnet run --project Tools/GratingHslTest -- <IP> [端口] [指令]
// 示例: dotnet run --project Tools/GratingHslTest -- 192.168.1.1 64000 M0
//   默认 IP=192.168.1.1（拨码00）/ 端口=64000（现场确认）/ 指令=M0
// 常用指令（手册 4.x）：
//   M0          读 01~06 所有通道测量值
//   MS          读测量值 + 输出状态（03=异常/未接，04=正常）
//   SR,01,037   读 01 通道测量值
//   SW,00,003   全通道清零（返回 SW,00,03 成功 / 04 失败）

// 双写输出：控制台 + exe 同目录日志文件（闪退时也能查记录）
string logPath = Path.Combine(AppContext.BaseDirectory, "GratingHslTest.log");
void Log(string msg)
{
    Console.WriteLine(msg);
    try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}"); } catch { }
}

bool wait = !args.Contains("--nowait");
// 过滤开关参数：--nowait、--scan 及扫描模式下 --scan 的数值（避免被当作 IP/端口解析）
var pargs = args.Where(a => a != "--nowait" && a != "--scan" && !(args.Contains("--scan") && int.TryParse(a, out _))).ToArray();
string ip = pargs.Length > 0 ? pargs[0] : "192.168.1.1";
int port = pargs.Length > 1 ? int.Parse(pargs[1]) : 64000;
string cmd = pargs.Length > 2 ? pargs[2] : "M0";
// 批量扫描：--scan [N]，或【无任何位置参数（如双击）】默认扫描全部控制器（1~N），N 默认 10
bool scanMode = args.Contains("--scan") || pargs.Length == 0;
int scanN = 10;
if (scanMode)
{
    var si = Array.IndexOf(args, "--scan");
    if (si >= 0 && args.Length > si + 1 && int.TryParse(args[si + 1], out var sn)) scanN = sn;
}

try
{
    if (scanMode)
    {
        Log($"== ZX-EN-T6 批量扫描: 192.168.1.1 ~ .{scanN} 端口 {port} 指令=[{cmd}] ==");
        int online = 0;
        for (int i = 1; i <= scanN; i++)
        {
            string hip = $"192.168.1.{i}";
            var z = new ZxEnT6Net(hip, port);
            var c = z.ConnectServer();
            if (!c.IsSuccess)
            {
                Log($"[{hip}] 离线/拒绝");
                continue;
            }
            online++;
            var rr = z.Send(cmd);
            if (!rr.IsSuccess)
            {
                Log($"[{hip}] 连接成功但指令失败: {rr.Message}");
                z.ConnectClose();
                continue;
            }
            string rsp = rr.Content.TrimEnd('\r', '\n');
            Log($"[{hip}] ✅ 在线  响应: {rsp}");
            z.ConnectClose();
        }
        Log($"== 扫描完成: {online}/{scanN} 个控制器在线 ==");
        return;
    }

    Log($"== ZX-EN-T6 HSL 测试: {ip}:{port}  指令=[{cmd}] ==");

    var zx = new ZxEnT6Net(ip, port);

    // 1) TCP 连接（LAN 通 + IP/端口 正确即成功）
    var conn = zx.ConnectServer();
    Log($"连接: {(conn.IsSuccess ? "成功 ✅" : "失败 ❌ " + conn.Message)}");
    if (!conn.IsSuccess) return;

    // 2) 发送指令
    var r = zx.Send(cmd);
    Log($"发送 [{cmd}] → 响应: {(r.IsSuccess ? "成功 ✅" : "失败 ❌ " + r.Message)}");
    if (!r.IsSuccess) { zx.ConnectClose(); return; }

    string resp = r.Content.TrimEnd('\r', '\n');
    Log($"原始响应: {resp}");

    // 3) 解析 M0/MS 六通道测量值：+100000000=未接传感器；其他=已接（位移值）
    if ((cmd == "M0" || cmd == "MS") && resp.Contains(','))
    {
        var parts = resp.Split(',');
        // M0: [0]=M0 [1..6]=6通道值；MS: [0]=MS [1]=状态 [2]=值 [3]=状态 [4]=值 ...
        int idx = 1;
        for (int ch = 1; ch <= 6; ch++)
        {
            string val;
            if (cmd == "M0") val = parts.Length > ch ? parts[ch] : "?";
            else
            {
                if (parts.Length <= idx + 1) { val = "?"; }
                else { string st = parts[idx]; string v = parts[idx + 1]; idx += 2; val = st == "04" ? v : $"未接/异常({st})"; }
            }
            string state = val == "+100000000" ? "未接传感器 ❌" : "通信正常 ✅";
            Log($"  通道{ch:D2}: {val,-14} {state}");
        }
    }

    zx.ConnectClose();
    Log("== 测试结束 ==");
}
catch (Exception ex)
{
    Log($"异常: {ex.Message}");
}
finally
{
    // 防止双击运行/终端关闭过快看不到结果；脚本调用加 --nowait 跳过
    if (wait)
    {
        Log("按任意键退出...");
        try { Console.ReadKey(); } catch { }
    }
}

/// <summary>ZX-EN-T6 最小 HSL 封装：TCP + ASCII 指令（继承 HSL 的 NetworkDoubleBase）。</summary>
public sealed class ZxEnT6Net : NetworkDoubleBase
{
    public ZxEnT6Net(string ip, int port)
    {
        IpAddress = ip;
        Port = port;
        ConnectTimeOut = 3000;
    }

    /// <summary>发送 ASCII 指令（自动补 CRLF），返回设备原文。</summary>
    public OperateResult<string> Send(string cmd)
    {
        var r = ReadFromCoreServer(Encoding.ASCII.GetBytes(cmd + "\r\n"));
        return r.IsSuccess
            ? OperateResult.CreateSuccessResult(Encoding.ASCII.GetString(r.Content))
            : new OperateResult<string>(r.Message);
    }
}
