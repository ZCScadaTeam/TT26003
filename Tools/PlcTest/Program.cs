using HslCommunication;
using HslCommunication.ModBus;
using HslCommunication.Core;

// 欧姆龙 NX502 Modbus-TCP 连通性验证工具
// 用法: dotnet run --project Tools/PlcTest -- <IP> <端口> <起始地址> <长度>
// 示例: dotnet run --project Tools/PlcTest -- 192.168.250.210 502 3000 21

var ip = args.Length > 0 ? args[0] : "192.168.250.210";
var port = args.Length > 1 ? int.Parse(args[1]) : 502;
var addr = args.Length > 2 ? args[2] : "3000";
var len = args.Length > 3 ? ushort.Parse(args[3]) : (ushort)21;

Console.WriteLine($"== Modbus-TCP 测试: {ip}:{port}  地址={addr}  长度={len} ==");

var client = new ModbusTcpNet(ip, port)
{
    AddressStartWithZero = true,
    DataFormat = DataFormat.CDAB,
    ConnectTimeOut = 3000,
};

var conn = client.ConnectServer();
Console.WriteLine($"连接: {(conn.IsSuccess ? "成功 ✅" : "失败 ❌ " + conn.Message)}");
if (!conn.IsSuccess) return;

// 试 3 种地址格式，看哪种能读到
string[] formats = { addr, $"D{addr}", $"x{addr}" };
foreach (var f in formats.Distinct())
{
    var r = client.ReadInt16(f, len);
    if (r.IsSuccess && r.Content != null)
    {
        var vals = string.Join(",", r.Content.Take(8));
        Console.WriteLine($"读 [{f}] 成功: {r.Content.Length} 字 → [{vals}{(r.Content.Length > 8 ? "..." : "")}]");
    }
    else
    {
        Console.WriteLine($"读 [{f}] 失败: {r.Message}");
    }
}

client.ConnectClose();
Console.WriteLine("== 测试结束 ==");
