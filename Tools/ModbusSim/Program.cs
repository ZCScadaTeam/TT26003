using System.Net;
using System.Net.Sockets;
using System.Text;

// THA26003 ModbusTCP 从站模拟器（PLC 侧）：
//   监听 502，支持功能码 03（读保持寄存器）/ 06（写单寄存器）/ 16（写多寄存器）。
//   实现三站交互 + 数据区冻结契约，用于脱机验证上位机的整条链路：
//     打码触发 → 上位机写内容到 %MW10420 → 打码结果 %MW10502
//     读码触发 → 上位机写读到的码到 %MW10560 → 读码结果 %MW10522
//     上传触发（同时冻结数据区）→ 上位机分段读 10002~10379 → 上传结果 %MW10532 → 解冻
//   交互：回车/空格 = 自动循环一轮；m/s/u = 单独置某站触发；d = 换一件新数据；q = 退出
internal static class Program
{
	private const int RegSize = 65536;
	private static readonly ushort[] Regs = new ushort[RegSize];

	/// <summary>
	/// PLC 编程 %MW → Modbus 寄存器号。
	/// 协议 Rev.2 已把全部地址压到 %MW10560 以内（均 ≤65535）→ 直接直传，无需换算。
	/// 上位机侧同样默认直传（AppConfig.ModbusAddressRules 为空）。
	/// </summary>
	private static int R(int mw) => mw;

	// 站地址（%MW 编程地址 → Modbus 寄存器）
	private static readonly int A_MarkTrig = R(10500);
	private static readonly int A_MarkRes = R(10502);
	private static readonly int A_ScanTrig = R(10520);
	private static readonly int A_ScanRes = R(10522);
	private static readonly int A_UploadTrig = R(10700);
	private static readonly int A_UploadRes = R(10702);
	private static readonly int A_MarkEcho = R(10420);
	private static readonly int A_ScanEcho = R(10560);
	private static readonly int A_PlcContent = R(10540);
	private static readonly int A_Heartbeat = R(20502);

	// 数据区
	private static readonly int A_Result = R(10002);
	private static readonly int A_TopPin = R(10022);    // 15 点 × 2 WORD = 30
	private static readonly int A_BotPin = R(10122);    // 30
	private static readonly int A_TopPos = R(10220);    // 15 点 × 2 字段 × 2 WORD = 60
	private static readonly int A_BotPos = R(10320);    // 60

	/// <summary>数据区冻结标志：上位机置 10530=1 后冻结，回写 10532≠0 后解冻。</summary>
	private static volatile bool _frozen;
	private static readonly object Gate = new();
	private static int _partNo;
	private static int _lastPrintedRes = -1;

	private static void Main(string[] args)
	{
		var autoCycle = args.Contains("--auto");
		var port = 502;
		var pi = Array.IndexOf(args, "--port");
		if (pi >= 0 && pi + 1 < args.Length && int.TryParse(args[pi + 1], out var p)) port = p;
		BuildPartData(++_partNo);

		var listener = new TcpListener(IPAddress.Any, port);
		listener.Start();
		PrintBanner();
		Console.WriteLine($"[SIM] 监听端口 {port}");
		_ = Task.Run(() => AcceptLoop(listener));

		if (autoCycle)
		{
			Console.WriteLine("[SIM] --auto：每 3 秒自动跑一轮（打码→读码→上传）");
			while (true)
			{
				RunOneCycle();
				Thread.Sleep(3000);
			}
		}

		while (true)
		{
			var key = Console.ReadKey(true).KeyChar;
			switch (key)
			{
				case 'q': return;
				case '\r' or '\n' or ' ': RunOneCycle(); break;
				case 'm': SetTrigger(A_MarkTrig, "打码"); break;
				case 's': SetTrigger(A_ScanTrig, "读码"); break;
				case 'u': SetTrigger(A_UploadTrig, "上传"); break;
				case 'd': BuildPartData(++_partNo); break;
				case 'f': Console.WriteLine($"[SIM] 数据区冻结状态 = {_frozen}"); break;
			}
		}
	}

	private static void PrintBanner()
	{
		Console.WriteLine("=== THA26003 PLC 模拟器（ModbusTCP；上位机用汇川 InovanceTcpNet 连接）===");
		Console.WriteLine("  [回车/空格] 自动跑一轮（打码→读码→上传）");
		Console.WriteLine("  [m] 置打码触发 10500=1   [s] 置读码触发 10520=1   [u] 置上传触发 10530=1");
		Console.WriteLine("  [d] 换一件新数据（重新生成测量值）   [f] 查看冻结状态   [q] 退出");
		Console.WriteLine("  数据区(Rev.2)：%MW10002 结果字 / %MW10022 上针高15 / %MW10122 下针高15 / %MW10220 上位置度16X/Y / %MW10320 下位置度16X/Y");
		Console.WriteLine($"  地址映射：%MW10500→寄存器{A_MarkTrig}  %MW10002→寄存器{A_Result}  %MW20502→寄存器{A_Heartbeat}");
		Console.WriteLine();
	}

	private static void AcceptLoop(TcpListener listener)
	{
		while (true)
		{
			var client = listener.AcceptTcpClient();
			_ = Task.Run(() => HandleClient(client));
		}
	}

	// ============================================================
	// 交互：跑一轮完整流程
	// ============================================================

	private static void RunOneCycle()
	{
		BuildPartData(++_partNo);

		Console.WriteLine($"\n[SIM] ---------- 第 {_partNo} 件开始 ----------");
		if (!WaitStation(A_MarkTrig, A_MarkRes, "打码")) { Console.WriteLine("[SIM] 打码站超时（15s）"); return; }
		if (!WaitStation(A_ScanTrig, A_ScanRes, "读码")) { Console.WriteLine("[SIM] 读码站超时（15s）"); return; }
		if (!WaitStation(A_UploadTrig, A_UploadRes, "上传")) { Console.WriteLine("[SIM] 上传站超时（15s）"); return; }
		Console.WriteLine($"[SIM] ---------- 第 {_partNo} 件结束 ----------\n");
	}

	/// <summary>置触发位 → 等上位机回写结果 → 清结果位；上传站按契约冻结/解冻数据区。</summary>
	private static bool WaitStation(int trigAddr, int resAddr, string name)
	{
		SetTrigger(trigAddr, name);
		var deadline = DateTime.Now.AddSeconds(15);
		while (DateTime.Now < deadline)
		{
			var res = Regs[resAddr];
			if (res != 0)
			{
				lock (Gate)
				{
					Console.WriteLine($"[SIM] {name}站结果 = {res}  {Describe(name, res)}"
						+ (name == "打码" ? $"  内容={ReadAscii(A_MarkEcho, 40)}" : "")
						+ (name == "读码" ? $"  码={ReadAscii(A_ScanEcho, 20)}" : ""));
					Regs[resAddr] = 0;
					if (name == "上传") _frozen = false;
				}
				return true;
			}
			Thread.Sleep(50);
		}
		return false;
	}

	private static void SetTrigger(int addr, string name)
	{
		lock (Gate)
		{
			Regs[addr] = 1;
			if (name == "上传")
			{
				_frozen = true;   // 契约：置 10530=1 的同一时刻冻结数据区
				Console.WriteLine("[SIM] 上传触发 → 数据区已冻结（收到 10532 后解冻）");
			}
		}
	}

	private static string Describe(string name, int res) => name switch
	{
		"打码" => res switch
		{
			1 => "(打码OK)", 2 => "(打标机NG)", 3 => "(回执超时)",
			4 => "(码生成失败)", 5 => "(MES异常)", 6 => "(回执内容不符)", _ => "",
		},
		"读码" => res switch
		{
			1 => "(读码OK)", 2 => "(未扫到)", 3 => "(重码)",
			4 => "(错码)", 5 => "(枪异常)", 6 => "(MES异常)", _ => "",
		},
		_ => res switch { 1 => "(落库OK)", 2 => "(数据NG)", 3 => "(失败)", _ => "" },
	};

	// ============================================================
	// 数据区构造
	// ============================================================

	private static void BuildPartData(int partNo)
	{
		lock (Gate)
		{
			if (_frozen)
			{
				Console.WriteLine("[SIM] 数据区已冻结，忽略本次数据刷新（等上位机回写上传结果）");
				return;
			}

			WriteU16(A_Result, 1);          // 导通耐压 OK
			WriteU16(A_Result + 2, 1);      // 针高 OK
			WriteU16(A_Result + 4, partNo % 3 == 0 ? (ushort)2 : (ushort)1); // 每 3 件造一个位置度 NG

			for (int i = 0; i < 15; i++)
			{
				// 针高：PLC 侧 DINT（÷1000 显示）—— 2115 → 2.115
				WriteInt32HiLo(A_TopPin + i * 2, 2115 + i);
				WriteInt32HiLo(A_BotPin + i * 2, 3446 + i);
			}
			for (int i = 0; i < 15; i++)   // 位置度有效点数 15（PIN16 为协议预留，不写）
			{
				WriteFloatCdab(A_TopPos + i * 4, (float)(0.100 + i * 0.010));
				WriteFloatCdab(A_TopPos + i * 4 + 2, (float)(0.200 + i * 0.010));
				WriteFloatCdab(A_BotPos + i * 4, (float)(1.100 + i * 0.010));
				WriteFloatCdab(A_BotPos + i * 4 + 2, (float)(1.200 + i * 0.010));
			}
			Console.WriteLine($"[SIM] 已生成第 {partNo} 件测量数据（上针高 {Regs[A_TopPin]:X4}… / 位置度结果 {Regs[A_Result + 4]}）");
		}
	}

	// ============================================================
	// Modbus TCP 服务端
	// ============================================================

	private static void HandleClient(TcpClient client)
	{
		Console.WriteLine($"[SIM] 上位机接入 {client.Client.RemoteEndPoint}");
		try
		{
			var stream = client.GetStream();
			var buf = new byte[260];
			while (client.Connected)
			{
				if (!ReadExactly(stream, buf, 0, 7)) break;
				var txId = (ushort)((buf[0] << 8) | buf[1]);
				var len = (ushort)((buf[4] << 8) | buf[5]);
				if (len < 2 || len > 253) break;
				if (!ReadExactly(stream, buf, 7, len - 1)) break;

				var fc = buf[7];
				var unitId = buf[6];
				byte[] resp;
				switch (fc)
				{
					case 3:
					{
						var start = (buf[8] << 8) | buf[9];
						var qty = (buf[10] << 8) | buf[11];
						if (qty is < 1 or > 125)
						{
							resp = Exception(fc, 3);
							break;
						}
						lock (Gate)
						{
							var data = new byte[qty * 2];
							for (int i = 0; i < qty; i++)
							{
								var a = start + i;
								var v = a < RegSize ? Regs[a] : (ushort)0;
								data[i * 2] = (byte)(v >> 8);
								data[i * 2 + 1] = (byte)(v & 0xFF);
							}
							resp = new byte[9 + data.Length];
							resp[7] = 3;
							resp[8] = (byte)data.Length;
							Array.Copy(data, 0, resp, 9, data.Length);
						}
						Console.WriteLine($"[SIM] FC03 读 {start} 长度 {qty}" + (qty > 8 ? "（数据区分段读）" : ""));
						break;
					}
					case 6:
					{
						var addr = (buf[8] << 8) | buf[9];
						var value = (ushort)((buf[10] << 8) | buf[11]);
						lock (Gate)
						{
							if (addr < RegSize) Regs[addr] = value;
							OnWrite(addr, 1);
						}
						resp = new byte[12];
						Array.Copy(buf, resp, 12);
						Console.WriteLine($"[SIM] FC06 写 {addr} = {value}{HintFor(addr)}");
						break;
					}
					case 16:
					{
						var addr = (buf[8] << 8) | buf[9];
						var qty = (buf[10] << 8) | buf[11];
						lock (Gate)
						{
							for (int i = 0; i < qty; i++)
							{
								var a = addr + i;
								if (a < RegSize) Regs[a] = (ushort)((buf[13 + i * 2] << 8) | buf[14 + i * 2]);
							}
							OnWrite(addr, qty);
						}
						resp = new byte[12];
						resp[7] = 16;
						resp[8] = (byte)(addr >> 8);
						resp[9] = (byte)(addr & 0xFF);
						resp[10] = (byte)(qty >> 8);
						resp[11] = (byte)(qty & 0xFF);
						Console.WriteLine($"[SIM] FC16 写 {addr} 长度 {qty}{HintFor(addr)}");
						break;
					}
					default:
						resp = Exception(fc, 1);
						break;
				}

				resp[0] = (byte)(txId >> 8);
				resp[1] = (byte)(txId & 0xFF);
				resp[2] = 0;
				resp[3] = 0;
				resp[4] = (byte)((resp.Length - 6) >> 8);
				resp[5] = (byte)((resp.Length - 6) & 0xFF);
				resp[6] = unitId;
				stream.Write(resp);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[SIM] 连接异常: {ex.Message}");
		}
		finally
		{
			client.Close();
			Console.WriteLine("[SIM] 上位机断开");
		}
	}

	/// <summary>上位机写入时的提示（把关键地址翻译成人话，方便肉眼核对链路）。</summary>
	private static void OnWrite(int addr, int qty)
	{
		if (addr == A_MarkEcho)
			Console.WriteLine($"[SIM]   ← 上位机回写打码内容：{ReadAscii(A_MarkEcho, 40)}");
		else if (addr == A_ScanEcho)
			Console.WriteLine($"[SIM]   ← 上位机回写读到的码：{ReadAscii(A_ScanEcho, 20)}");
		else if (addr == A_MarkTrig && Regs[addr] == 0)
			Console.WriteLine("[SIM]   ← 上位机已复位打码触发位");
		else if (addr == A_ScanTrig && Regs[addr] == 0)
			Console.WriteLine("[SIM]   ← 上位机已复位读码触发位");
		else if (addr == A_UploadTrig && Regs[addr] == 0)
			Console.WriteLine("[SIM]   ← 上位机已复位上传触发位（数据区仍冻结，等回写 10532）");
		else if (addr == A_Heartbeat)
		{
			// 心跳每 0.5s 一次，不逐条打印
		}
	}

	private static string HintFor(int addr)
	{
		if (addr == A_MarkEcho) return "（打码内容回写）";
		if (addr == A_MarkRes) return "（打码结果）";
		if (addr == A_ScanEcho) return "（读码内容回写）";
		if (addr == A_ScanRes) return "（读码结果）";
		if (addr == A_UploadRes) return "（上传结果 → 解冻数据区）";
		if (addr == A_Heartbeat) return "（心跳）";
		if (addr == A_MarkTrig) return "（打码触发）";
		if (addr == A_ScanTrig) return "（读码触发）";
		if (addr == A_UploadTrig) return "（上传触发）";
		if (addr == A_PlcContent) return "（PLC 附带内容）";
		return "";
	}

	private static byte[] Exception(byte fc, byte code)
	{
		var r = new byte[9];
		r[7] = (byte)(fc + 0x80);
		r[8] = code;
		return r;
	}

	private static bool ReadExactly(NetworkStream s, byte[] buf, int offset, int count)
	{
		var read = 0;
		while (read < count)
		{
			var n = s.Read(buf, offset + read, count - read);
			if (n == 0) return false;
			read += n;
		}
		return true;
	}

	// ============================================================
	// 寄存器写入辅助
	// ============================================================

	private static void WriteU16(int addr, ushort v) => Regs[addr] = v;

	/// <summary>写 DINT（32 位有符号整数，高字在前 —— Modbus 大端标准）。</summary>
	private static void WriteInt32HiLo(int addr, int value)
	{
		Regs[addr] = (ushort)((value >> 16) & 0xFFFF);
		Regs[addr + 1] = (ushort)(value & 0xFFFF);
	}

	/// <summary>写单精度浮点，字序 CDAB（寄存器 0 = 低字 C,D；寄存器 1 = 高字 A,B）。
	/// 与上位机 Tha26003 默认口径 FloatCdab 严格对称，不能写成 ABCD。</summary>
	private static void WriteFloatCdab(int addr, float v)
	{
		var b = BitConverter.GetBytes(v);              // 小端 [D,C,B,A]
		Regs[addr] = (ushort)((b[1] << 8) | b[0]);     // C D（低字）
		Regs[addr + 1] = (ushort)((b[3] << 8) | b[2]); // A B（高字）
	}

	/// <summary>读 WORD 区 ASCII（高字节在前，与上位机 ParseAscii(swap=false) 对称）。</summary>
	private static string ReadAscii(int addr, int words)
	{
		var sb = new StringBuilder(words * 2);
		for (int w = 0; w < words; w++)
		{
			var v = Regs[addr + w];
			var hi = (byte)(v >> 8);
			var lo = (byte)(v & 0xFF);
			if (hi == 0) break;
			if (hi >= 0x20 && hi <= 0x7E) sb.Append((char)hi);
			if (lo == 0) break;
			if (lo >= 0x20 && lo <= 0x7E) sb.Append((char)lo);
		}
		return sb.ToString();
	}
}
