using System.Text;
using AppZC.Configs;
using HslCommunication;
using HslCommunication.Core;
using HslCommunication.Profinet.Inovance;

namespace AppZC.Services;

/// <summary>
/// 现场取值探针（<c>AppZC.exe --plcprobe [IP] [端口]</c>，无界面、不写库、不启服务）。
///
/// 目的：一眼定出"针高/位置度读出来不对"到底是**字序选错**还是**数据区本身没写**。
/// 对同一段 PLC 字节同时给出 CDAB / ABCD 两种口径的换算结果，哪种等于现场应有的值就选哪种
/// （对应配置项 <c>PinHeightFormat</c> = Int32Cdab / Int32Abcd，<c>PositionFormat</c> = FloatCdab / FloatAbcd）。
/// </summary>
public static class ThaPlcProbe
{
	public static async Task<int> RunAsync(AppConfig cfg, string? ip, int? port)
	{
		var host = string.IsNullOrWhiteSpace(ip) ? cfg.PlcIpAddress : ip!;
		var tcpPort = port ?? cfg.PlcPort;
		Console.WriteLine("=== THA26003 PLC 取值探针（--plcprobe）===");
		Console.WriteLine($"目标 {host}:{tcpPort}  地址前缀='{cfg.PlcAddressPrefix}'  汇川系列={cfg.InovanceSeries}");
		Console.WriteLine("说明：同一段字节分别按 CDAB（低字在前，默认）与 ABCD（高字在前）换算出两种结果，");
		Console.WriteLine("      哪个与现场应得值一致，就把配置 PinHeightFormat / PositionFormat 切成对应口径。\n");

		using var plc = new InovanceTcpNet(ParseSeries(cfg.InovanceSeries), host, tcpPort, 1)
		{
			AddressStartWithZero = true,
			IsStringReverse = false,
			DataFormat = DataFormat.CDAB,
			ConnectTimeOut = 5000,
			ReceiveTimeOut = 10000,
		};

		var conn = await plc.ConnectServerAsync();
		Console.WriteLine($"[连接] {(conn.IsSuccess ? "OK" : "失败：" + conn.Message)}");
		if (!conn.IsSuccess) return 2;

		// 结果字 + 上针高 + 上位置度（各取前几个点就够判断字序）
		await DumpWordsAsync(plc, cfg, "结果字", Tha26003.SegResultBase, Tha26003.SegResultLen);
		await DumpInt32Async(plc, cfg, "上针高 Pin1~8", Tha26003.SegTopPinBase, 16);
		await DumpFloatAsync(plc, cfg, "上位置度 PIN1 X/Y ~ PIN4 X/Y", Tha26003.SegTopPosBase, 16);

		Console.WriteLine("\n提示：");
		Console.WriteLine("  · 针高整段全是 0 → PLC 还没往数据区写（不是字序问题）");
		Console.WriteLine("  · 针高按 CDAB 得到 2.115 这类 mm 值 → 保持 Int32Cdab；得到十几万 → 改用 Int32Abcd");
		Console.WriteLine("  · 位置度按 CDAB 得到 0.0xx 这类 mm 值 → 保持 FloatCdab；得到 4.6E-41 之类 → 改用 FloatAbcd");
		return 0;
	}

	private static async Task DumpWordsAsync(InovanceTcpNet plc, AppConfig cfg, string title, int baseAddr, ushort len)
	{
		var r = await plc.ReadAsync(R(cfg, baseAddr), len);
		Console.WriteLine($"\n[{title}] {Tha26003.MwText(baseAddr)} x{len} → {(r.IsSuccess ? "OK" : "失败：" + r.Message)}");
		if (!r.IsSuccess || r.Content == null) return;
		Console.WriteLine("  hex: " + Hex(r.Content));
		var vals = new List<string>();
		for (int i = 0; i < len; i++)
			vals.Add(Tha26003.Word(r.Content, i).ToString());
		Console.WriteLine("  WORD: " + string.Join(" ", vals));
	}

	private static async Task DumpInt32Async(InovanceTcpNet plc, AppConfig cfg, string title, int baseAddr, int count)
	{
		var r = await plc.ReadAsync(R(cfg, baseAddr), (ushort)count);
		Console.WriteLine($"\n[{title}] {Tha26003.MwText(baseAddr)} x{count} → {(r.IsSuccess ? "OK" : "失败：" + r.Message)}");
		if (!r.IsSuccess || r.Content == null) return;
		Console.WriteLine("  hex: " + Hex(r.Content));

		// HSL 直接读（与 plc.ReadInt32 同源），一个点一个点对照
		var direct = new List<string>();
		for (int i = 0; i < count / 2; i++)
		{
			var v = await plc.ReadInt32Async(R(cfg, baseAddr + i * 2));
			direct.Add(v.IsSuccess ? v.Content.ToString() : "err");
		}
		Console.WriteLine("  ReadInt32(逐点，客户端 DataFormat=CDAB): " + string.Join(" ", direct));

		var cdab = new List<string>();
		var abcd = new List<string>();
		for (int i = 0; i < count / 2; i++)
		{
			cdab.Add(Tha26003.Int32Div1000(r.Content, i * 2, PinHeightFormat.Int32Cdab).ToString("F3"));
			abcd.Add(Tha26003.Int32Div1000(r.Content, i * 2, PinHeightFormat.Int32Abcd).ToString("F3"));
		}
		Console.WriteLine("  整块读 + CDAB ÷1000: " + string.Join(" ", cdab));
		Console.WriteLine("  整块读 + ABCD ÷1000: " + string.Join(" ", abcd));
	}

	private static async Task DumpFloatAsync(InovanceTcpNet plc, AppConfig cfg, string title, int baseAddr, int count)
	{
		var r = await plc.ReadAsync(R(cfg, baseAddr), (ushort)count);
		Console.WriteLine($"\n[{title}] {Tha26003.MwText(baseAddr)} x{count} → {(r.IsSuccess ? "OK" : "失败：" + r.Message)}");
		if (!r.IsSuccess || r.Content == null) return;
		Console.WriteLine("  hex: " + Hex(r.Content));

		var direct = new List<string>();
		for (int i = 0; i < count / 2; i++)
		{
			var v = await plc.ReadFloatAsync(R(cfg, baseAddr + i * 2));
			direct.Add(v.IsSuccess ? v.Content.ToString("F4") : "err");
		}
		Console.WriteLine("  ReadFloat(逐点，客户端 DataFormat=CDAB): " + string.Join(" ", direct));

		var cdab = new List<string>();
		var abcd = new List<string>();
		for (int i = 0; i < count / 2; i++)
		{
			cdab.Add(Tha26003.Float(r.Content, i * 2, PositionFormat.FloatCdab).ToString("F4"));
			abcd.Add(Tha26003.Float(r.Content, i * 2, PositionFormat.FloatAbcd).ToString("F4"));
		}
		Console.WriteLine("  整块读 + CDAB: " + string.Join(" ", cdab));
		Console.WriteLine("  整块读 + ABCD: " + string.Join(" ", abcd));
	}

	private static string Hex(byte[] data, int max = 64)
	{
		var sb = new StringBuilder();
		for (int i = 0; i < data.Length && i < max; i++)
			sb.Append(data[i].ToString("X2")).Append(i % 2 == 1 ? " " : "");
		if (data.Length > max) sb.Append("…");
		return sb.ToString().Trim();
	}

	private static string R(AppConfig cfg, int mw) => ThaAddressMap.Create(cfg).R(mw);

	private static InovanceSeries ParseSeries(string s)
		=> Enum.TryParse<InovanceSeries>(s, true, out var v) ? v : InovanceSeries.AM;
}
