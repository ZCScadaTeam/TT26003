using HslCommunication;
using HslCommunication.ModBus;
using HslCommunication.Core;

// Modbus 地址读写扫描工具：默认扫 [0, 65536) 保持寄存器（Modbus 全范围），找出能实际写入的地址/区间。
// 对每个地址：读原值 → 写特征值 0x5A5A → 回读 → 若匹配标记"可写" → 恢复原值（避免破坏 PLC 数据）。
//
// 用法: ModbusScan.exe [IP] [端口] [起始地址] [结束地址(不含)] [步进]
// 示例:
//   ModbusScan.exe 192.168.250.210 502          ← 全范围 0-65535
//   ModbusScan.exe 192.168.250.210 502 0 10000  ← 扫 0-9999
//   ModbusScan.exe 192.168.250.210 502 0 65536 100  ← 快速模式：每隔100个地址测一个（先粗扫找区域）
// 注意：关闭其它占用 502 端口的工具再运行。

var ip = args.Length > 0 ? args[0] : "192.168.250.210";
var port = args.Length > 1 ? int.Parse(args[1]) : 502;
int start = args.Length > 2 ? int.Parse(args[2]) : 0;
int end = args.Length > 3 ? int.Parse(args[3]) : 65536;
int step = args.Length > 4 ? int.Parse(args[4]) : 1;

if (end > 65536) end = 65536;

Console.WriteLine($"== Modbus 地址扫描: {ip}:{port}  [{start}, {end}) 步进{step} ==");
Console.WriteLine("每个地址：读原值→写0x5A5A→回读→恢复原值。\n");

var client = new ModbusTcpNet(ip, port)
{
    AddressStartWithZero = true,
    DataFormat = DataFormat.CDAB,
    ConnectTimeOut = 3000,
};

var conn = client.ConnectServer();
Console.WriteLine($"连接: {(conn.IsSuccess ? "成功 ✅" : "失败 ❌ " + conn.Message)}");
if (!conn.IsSuccess) { Pause(); return; }

var writable = new List<int>();
var readOnly = new List<int>();
int timeoutCount = 0, errorCount = 0;

for (int addr = start; addr < end; addr += step)
{
    string a = addr.ToString();

    // 1) 读原值
    var r0 = client.ReadInt16(a, 1);
    if (!r0.IsSuccess)
    {
        if (r0.Message?.Contains("超时", StringComparison.OrdinalIgnoreCase) == true
            || r0.Message?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true)
            timeoutCount++;
        else
            errorCount++;
        if (addr % 500 == 0) Console.WriteLine($"  地址 {addr}: 读取失败 {r0.Message}");
        continue;
    }
    short original = r0.Content![0];

    // 2) 写特征值
    var w = client.Write(a, (short)0x5A5A);
    if (!w.IsSuccess)
    {
        readOnly.Add(addr);
        if (addr % 500 == 0) Console.WriteLine($"  地址 {addr}: 写入失败(只读?) {w.Message}");
        continue;
    }

    // 3) 回读验证
    var r1 = client.ReadInt16(a, 1);
    bool ok = r1.IsSuccess && r1.Content != null && r1.Content[0] == (short)0x5A5A;
    if (ok)
        writable.Add(addr);
    else
        readOnly.Add(addr);

    // 4) 恢复原值（尽力而为）
    client.Write(a, original);

    if (addr % 500 == 0 || ok)
        Console.WriteLine($"  地址 {addr}: {(ok ? $"可写 ✅ (原={original})" : $"不可写 (原={original}, 回读={r1.Content?[0]})")}");
}

client.ConnectClose();

Console.WriteLine($"\n== 扫描完成 ==");
Console.WriteLine($"  可写地址 {writable.Count} 个");
if (writable.Count > 0)
{
    Console.WriteLine($"  可写地址列表: {string.Join(",", writable)}");
    // 合并连续区间显示
    var ranges = MergeRanges(writable);
    Console.WriteLine($"  可写区间（合并）: {string.Join("  ", ranges.Select(r => $"[{r.Item1}-{r.Item2}]"))}");
    // 转换提示：D 区地址 = Modbus 地址 + 偏移？给出常见 0-基 提示
    Console.WriteLine("  ※ 若可写区集中在某段，说明 D 区映射到该 Modbus 起始段");
}
if (readOnly.Count > 0)
    Console.WriteLine($"  不可写地址 {readOnly.Count} 个（前50）: {string.Join(",", readOnly.Take(50))}");
Console.WriteLine($"  读取失败: 超时 {timeoutCount} 次, 其它 {errorCount} 次");
Console.WriteLine("== 结束 ==");

List<(int, int)> MergeRanges(List<int> list)
{
    var result = new List<(int, int)>();
    if (list.Count == 0) return result;
    var sorted = list.Distinct().OrderBy(x => x).ToList();
    int s = sorted[0], e = sorted[0];
    foreach (var v in sorted.Skip(1))
    {
        if (v == e + 1 || v == e + step) { e = v; continue; }
        result.Add((s, e));
        s = v; e = v;
    }
    result.Add((s, e));
    return result;
}

void Pause()
{
    Console.WriteLine();
    Console.WriteLine("按任意键退出...");
    try { Console.ReadKey(); } catch { }
}
