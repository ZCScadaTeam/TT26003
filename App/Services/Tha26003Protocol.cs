using System.Text;
using AppZC.Models.DB;
using HslCommunication.Core;

namespace AppZC.Services;

/// <summary>
/// THA26003 PLC ModbusTCP 协议常量与解析（依据《THA26003_PLC_ModbusTCP通讯协议.md》Rev.2）。
///
/// 地址模型（Rev.2，全部 ≤ 65535，可直接作 Modbus 寄存器号；AddressStartWithZero=true 时字符串地址 == %MW 编号）：
/// 通讯库：HslCommunication 13.0.1，设备类 InovanceTcpNet（汇川，派生自 ModbusTcpNet）。
///   信号区  %MW10500 打码触发 / 10502 打码结果 / 10520 读码触发 / 10522 读码结果
///           %MW10530 上传触发 / 10532 上传结果 / 10560 读码回写(20WORD)
///   回写区  %MW10420 打码内容回写(40WORD)
///   数据区  %MW10002 结果字(7WORD) / 10022 上针高15 / 10122 下针高15 / 10220 上位置度15X/Y / 10320 下位置度15X/Y
///   心跳    %MW20502（0.5s 写 1）
///
/// Modbus FC03 单请求最多 125 个寄存器，而数据区跨 %MW10002~10379（378 WORD），因此必须分 5 段读（共 187 WORD）；
/// 信号区三站触发位可一次读 10500 起 41 WORD 全覆盖。
/// </summary>
public static class Tha26003
{
	// ===== 信号区 PLC 编程地址（%MW 编号；Rev.2 全部 ≤65535，默认直传为 Modbus 寄存器号）=====
	public const int MwMarkTrigger = 10500;   // PLC→PC 打码触发（写 1）
	public const int MwMarkResult = 10502;    // PC→PLC 打码结果码
	public const int MwScanTrigger = 10520;   // PLC→PC 读码触发（写 1）
	public const int MwScanResult = 10522;    // PC→PLC 读码结果码
	public const int MwUploadTrigger = 10700; // PLC→PC 采集(上传)触发（写 1），**独立于读码站**：前面 NG 不扫码的产品同样要上传
	public const int MwUploadResult = 10702;  // PC→PLC 采集(上传)完成（写 1=成功；异常写 2/3）
	public const int MwScanEcho = 10560;      // PC→PLC 读码内容回写（20WORD）
	public const int MwMarkEcho = 10420;      // PC→PLC 打码内容回写（40WORD）
	public const int MwHeartbeat = 20502;     // 心跳（0.5s 写 1）

	/// <summary>单个 %MW 地址的显示文本（UI/日志用，避免各处手写地址而漂移）。</summary>
	public static string MwText(int mw) => $"%MW{mw}";

	/// <summary>WORD 区段地址的显示文本，如 <c>%MW10420~10459</c>。</summary>
	public static string MwRangeText(int mw, int words) => $"%MW{mw}~{mw + words - 1}";

	// ===== PLC 附带前站镭码内容（供读码站比对）=====

	/// <summary>PLC 附带前站镭码内容：%MW10580~10619（40 WORD = 80 字节 ASCII）。</summary>
	public const int MwPlcCode = 10580;
	/// <summary>该区 WORD 数（协议表标 40WORD，长度不够可增加）。</summary>
	public const int PlcCodeWords = 40;

	/// <summary>信号区一次读的长度：%MW10500~10540（打码/读码两站触发位与结果位；10530~10540 为旧预留，一并读回便于对账）。</summary>
	public const ushort SignalBlockLength = 41;

	/// <summary>上传站信号区起点（%MW10700 采集触发 / %MW10702 采集完成）。</summary>
	public const int MwUploadBlockBase = 10700;

	/// <summary>上传站信号区长度：10700~10702 共 3 WORD。</summary>
	public const ushort UploadBlockLength = 3;

	/// <summary>上传站信号块内的 WORD 偏移（0 == %MW10700）。</summary>
	public const int OffUploadTrig = 0;   // 10700
	public const int OffUploadRes = 2;    // 10702

	/// <summary>信号块基址（%MW 编号）。</summary>
	public const int MwSignalBlockBase = 10500;

	/// <summary>信号块内的 WORD 偏移（0 == %MW10500）。</summary>
	public const int OffMarkTrigger = 0;    // 10500
	public const int OffMarkResult = 2;     // 10502
	public const int OffScanTrigger = 20;   // 10520
	public const int OffScanResult = 22;    // 10522
	public const int OffUploadTrigger = 30; // 预留（上传位已独立到 %MW10700 区，不在此块内）
	public const int OffUploadResult = 32;  // 预留

	public const int MarkEchoWords = 40;   // 80 字符
	public const int ScanEchoWords = 20;   // 40 字符

	// ===== 数据区分段读（FC03 ≤125 WORD）=====

	/// <summary>结果字段：10002~10008（导通耐压 / 针高 / 位置度）。</summary>
	public const int SegResultBase = 10002;
	public const ushort SegResultLen = 7;

	/// <summary>上针高 Pin1~15：%MW10022~10051。</summary>
	/// <remarks>协议 md 列的是每个字段的**起始**地址（Pin15=%MW10050），
	/// 每点 2WORD，故 15 点实占 30 WORD（末字 = 10051）。</remarks>
	public const int SegTopPinBase = 10022;
	public const ushort SegTopPinLen = 30;

	/// <summary>下针高 Pin1~15：%MW10122~10151。</summary>
	public const int SegBottomPinBase = 10122;
	public const ushort SegBottomPinLen = 30;

	/// <summary>上针位置度 PIN1~15 X/Y：%MW10220~10279（15 点 × 2 字段 × 2WORD = 60 WORD）。
	/// PIN16（%MW10280/10282）为协议预留，**目前无数据，不读**。</summary>
	public const int SegTopPosBase = 10220;
	public const ushort SegTopPosLen = 60;

	/// <summary>下针位置度 PIN1~15 X/Y：%MW10320~10379。</summary>
	public const int SegBottomPosBase = 10320;
	public const ushort SegBottomPosLen = 60;

	/// <summary>结果字段在结果段内的 WORD 偏移。</summary>
	public const int OffWithstand = 0;  // 10002
	public const int OffPinHeight = 2;  // 10004
	public const int OffPosition = 4;   // 10006

	/// <summary>针位数：上/下针高各 15 点。</summary>
	public const int PinHeightCount = 15;

	/// <summary>位置度**有效**点数：上/下各 15 点（X/Y 交替）。
	/// 协议表虽列到 PIN16，但现场目前只有 15 个 Pin 有效，PIN16 为预留；
	/// PLC 启用 PIN16 后把本常量改回 16、分段长度改回 64、并补上 PIN16 字段赋值即可。</summary>
	public const int PositionCount = 15;

	/// <summary>
	/// 解析后的测量数据（不含打码/读码站信息，那两项由工位作业补齐）。
	/// **全部字段可空**：分段读失败 / PLC 未写该区时为 null，与"读到 0"区分开。
	/// 数组的元素也可能为 null（同一段内个别字段异常）。
	/// </summary>
	public sealed record Measurement(
		int? WithstandResult,
		int? PinHeightResult,
		int? PositionResult,
		double?[] TopPinHeights,
		double?[] BottomPinHeights,
		double?[] TopPosX,
		double?[] TopPosY,
		double?[] BottomPosX,
		double?[] BottomPosY);

	/// <summary>结果字归一：1/2 为有效，其余（含 0 = 未采集）一律 null。</summary>
	private static int? NormResult(int word) => word is 1 or 2 ? word : null;

	/// <summary>
	/// 解析数据区（5 个分段的原始字节）为测量数据。
	/// <paramref name="numeric"/> 决定数值字/浮点口径：
	///   FloatCdab（默认，与现场 Modicon/汇川系一致）、FloatAbcd、RawDiv1000。
	/// </summary>
	public static Measurement ParseMeasurement(
		byte[]? segResult, byte[]? segTopPin, byte[]? segBottomPin,
		byte[]? segTopPos, byte[]? segBottomPos,
		PinHeightFormat pinFormat, PositionFormat posFormat)
	{
		// 结果字：整段读失败 → null；段内取值非 1/2 → null
		int? withstand = segResult != null ? NormResult(Word(segResult, OffWithstand)) : null;
		int? pinHeight = segResult != null ? NormResult(Word(segResult, OffPinHeight)) : null;
		int? position = segResult != null ? NormResult(Word(segResult, OffPosition)) : null;

		// 上/下针高：15 点，DINT ÷1000，步进 2 WORD
		var topPin = new double?[PinHeightCount];
		var botPin = new double?[PinHeightCount];
		for (int i = 0; i < PinHeightCount; i++)
		{
			topPin[i] = ReadPinHeight(segTopPin, i * 2, pinFormat);
			botPin[i] = ReadPinHeight(segBottomPin, i * 2, pinFormat);
		}

		// 位置度：15 点，REAL，X/Y 交替，步进 4 WORD
		var topX = new double?[PositionCount];
		var topY = new double?[PositionCount];
		var botX = new double?[PositionCount];
		var botY = new double?[PositionCount];
		for (int i = 0; i < PositionCount; i++)
		{
			topX[i] = ReadPosition(segTopPos, i * 4, posFormat);
			topY[i] = ReadPosition(segTopPos, i * 4 + 2, posFormat);
			botX[i] = ReadPosition(segBottomPos, i * 4, posFormat);
			botY[i] = ReadPosition(segBottomPos, i * 4 + 2, posFormat);
		}

		return new Measurement(withstand, pinHeight, position, topPin, botPin, topX, topY, botX, botY);
	}

	/// <summary>安全读一个针高值（DINT÷1000）：段缺失/越界/异常值 → null。</summary>
	private static double? ReadPinHeight(byte[]? seg, int wordOffset, PinHeightFormat format)
	{
		if (seg == null || (wordOffset + 2) * 2 > seg.Length)
			return null;
		var v = Int32Div1000(seg, wordOffset, format);
		return Sanitize(v);
	}

	/// <summary>安全读一个位置度值（REAL）：段缺失/越界/NaN/哨兵 → null。</summary>
	private static double? ReadPosition(byte[]? seg, int wordOffset, PositionFormat format)
	{
		if (seg == null || (wordOffset + 2) * 2 > seg.Length)
			return null;
		var v = Float(seg, wordOffset, format);
		return Sanitize(v);
	}

	/// <summary>异常值过滤：NaN / 无穷 / 哨兵量级 → null（视为未采集，不落脏值）。</summary>
	private static double? Sanitize(double v)
		=> double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 1e30 ? null : v;

	/// <summary>把测量数据写入产品记录（打码/读码站字段由工位作业单独填）。</summary>
	public static void Apply(ThaProductRecord r, Measurement m)
	{
		r.WithstandResult = m.WithstandResult;
		r.PinHeightResult = m.PinHeightResult;
		r.PositionResult = m.PositionResult;

		r.TopPinHeight1 = m.TopPinHeights[0];
		r.TopPinHeight2 = m.TopPinHeights[1];
		r.TopPinHeight3 = m.TopPinHeights[2];
		r.TopPinHeight4 = m.TopPinHeights[3];
		r.TopPinHeight5 = m.TopPinHeights[4];
		r.TopPinHeight6 = m.TopPinHeights[5];
		r.TopPinHeight7 = m.TopPinHeights[6];
		r.TopPinHeight8 = m.TopPinHeights[7];
		r.TopPinHeight9 = m.TopPinHeights[8];
		r.TopPinHeight10 = m.TopPinHeights[9];
		r.TopPinHeight11 = m.TopPinHeights[10];
		r.TopPinHeight12 = m.TopPinHeights[11];
		r.TopPinHeight13 = m.TopPinHeights[12];
		r.TopPinHeight14 = m.TopPinHeights[13];
		r.TopPinHeight15 = m.TopPinHeights[14];

		r.BottomPinHeight1 = m.BottomPinHeights[0];
		r.BottomPinHeight2 = m.BottomPinHeights[1];
		r.BottomPinHeight3 = m.BottomPinHeights[2];
		r.BottomPinHeight4 = m.BottomPinHeights[3];
		r.BottomPinHeight5 = m.BottomPinHeights[4];
		r.BottomPinHeight6 = m.BottomPinHeights[5];
		r.BottomPinHeight7 = m.BottomPinHeights[6];
		r.BottomPinHeight8 = m.BottomPinHeights[7];
		r.BottomPinHeight9 = m.BottomPinHeights[8];
		r.BottomPinHeight10 = m.BottomPinHeights[9];
		r.BottomPinHeight11 = m.BottomPinHeights[10];
		r.BottomPinHeight12 = m.BottomPinHeights[11];
		r.BottomPinHeight13 = m.BottomPinHeights[12];
		r.BottomPinHeight14 = m.BottomPinHeights[13];
		r.BottomPinHeight15 = m.BottomPinHeights[14];

		r.TopPinPosX1 = m.TopPosX[0];		r.TopPinPosY1 = m.TopPosY[0];
		r.TopPinPosX2 = m.TopPosX[1];		r.TopPinPosY2 = m.TopPosY[1];
		r.TopPinPosX3 = m.TopPosX[2];		r.TopPinPosY3 = m.TopPosY[2];
		r.TopPinPosX4 = m.TopPosX[3];		r.TopPinPosY4 = m.TopPosY[3];
		r.TopPinPosX5 = m.TopPosX[4];		r.TopPinPosY5 = m.TopPosY[4];
		r.TopPinPosX6 = m.TopPosX[5];		r.TopPinPosY6 = m.TopPosY[5];
		r.TopPinPosX7 = m.TopPosX[6];		r.TopPinPosY7 = m.TopPosY[6];
		r.TopPinPosX8 = m.TopPosX[7];		r.TopPinPosY8 = m.TopPosY[7];
		r.TopPinPosX9 = m.TopPosX[8];		r.TopPinPosY9 = m.TopPosY[8];
		r.TopPinPosX10 = m.TopPosX[9];		r.TopPinPosY10 = m.TopPosY[9];
		r.TopPinPosX11 = m.TopPosX[10];		r.TopPinPosY11 = m.TopPosY[10];
		r.TopPinPosX12 = m.TopPosX[11];		r.TopPinPosY12 = m.TopPosY[11];
		r.TopPinPosX13 = m.TopPosX[12];		r.TopPinPosY13 = m.TopPosY[12];
		r.TopPinPosX14 = m.TopPosX[13];		r.TopPinPosY14 = m.TopPosY[13];
		r.TopPinPosX15 = m.TopPosX[14];		r.TopPinPosY15 = m.TopPosY[14];

		r.BottomPinPosX1 = m.BottomPosX[0];	r.BottomPinPosY1 = m.BottomPosY[0];
		r.BottomPinPosX2 = m.BottomPosX[1];	r.BottomPinPosY2 = m.BottomPosY[1];
		r.BottomPinPosX3 = m.BottomPosX[2];	r.BottomPinPosY3 = m.BottomPosY[2];
		r.BottomPinPosX4 = m.BottomPosX[3];	r.BottomPinPosY4 = m.BottomPosY[3];
		r.BottomPinPosX5 = m.BottomPosX[4];	r.BottomPinPosY5 = m.BottomPosY[4];
		r.BottomPinPosX6 = m.BottomPosX[5];	r.BottomPinPosY6 = m.BottomPosY[5];
		r.BottomPinPosX7 = m.BottomPosX[6];	r.BottomPinPosY7 = m.BottomPosY[6];
		r.BottomPinPosX8 = m.BottomPosX[7];	r.BottomPinPosY8 = m.BottomPosY[7];
		r.BottomPinPosX9 = m.BottomPosX[8];	r.BottomPinPosY9 = m.BottomPosY[8];
		r.BottomPinPosX10 = m.BottomPosX[9];	r.BottomPinPosY10 = m.BottomPosY[9];
		r.BottomPinPosX11 = m.BottomPosX[10];	r.BottomPinPosY11 = m.BottomPosY[10];
		r.BottomPinPosX12 = m.BottomPosX[11];	r.BottomPinPosY12 = m.BottomPosY[11];
		r.BottomPinPosX13 = m.BottomPosX[12];	r.BottomPinPosY13 = m.BottomPosY[12];
		r.BottomPinPosX14 = m.BottomPosX[13];	r.BottomPinPosY14 = m.BottomPosY[13];
		r.BottomPinPosX15 = m.BottomPosX[14];	r.BottomPinPosY15 = m.BottomPosY[14];
	}
	/// <summary>
	/// 总结果（MES 自算，协议明确「上位机可自行判断」）：
	///   **其余结果里没有 NG(2) → 总结果 OK(1)；只要有一项 NG → 总结果 NG(2)**。
	/// 打码/读码结果用的是各自的结果码空间（1~6），只有 ==1 才算通过
	/// （否则重码 3、错码 4、枪异常 5 会被"没有 2 就 OK"的朴素判断误判成 OK）。
	/// </summary>
	/// <summary>
	/// 总结果（MES 自算）：**四项结果（导通耐压 / 针高 / 位置度 / 扫码）里没有 NG(2) → OK(1)**；
	/// 有任何一项 NG → 2；测量三项缺项 → null（无法判定，不猜 OK）；扫码为 null（未扫码）不阻塞 OK。
	/// </summary>
	public static int? ComputeOverall(int? withstand, int? pinHeight, int? position, int? scan)
	{
		// 结果口径：只有 1=OK / 2=NG 有效，其余（0=未采集、异常值）视为"未知"
		static int? NormResult(int? v) => v is 1 or 2 ? v : null;

		var w = NormResult(withstand);
		var p = NormResult(pinHeight);
		var s = NormResult(position);
		var c = NormResult(scan);

		// 四项里任何一项 NG → 总结果 NG
		if (w == 2 || p == 2 || s == 2 || c == 2)
			return 2;

		// 测量三项（耐压/针高/位置度）缺一 → 无法判定（不猜 OK）
		if (w == null || p == null || s == null)
			return null;

		// 扫码为 null = 该件未扫码（前面 NG 不扫码 / 无码件）→ 不阻塞 OK
		return 1;
	}

	/// <summary>测量数据合法性：NaN / 无穷 / 未初始化哨兵（1e38）才算异常；null（未采集）不算异常。
	/// 返回 false 表示数据是"读到了但明显是脏值"，应判数据 NG 且不落库。</summary>
	public static bool IsMeasurementSane(Measurement m)
	{
		foreach (var v in AllNumbers(m))
		{
			if (v == null) continue;
			if (double.IsNaN(v.Value) || double.IsInfinity(v.Value))
				return false;
			if (Math.Abs(v.Value) > 1e30)
				return false;
		}
		return true;
	}

	private static IEnumerable<double?> AllNumbers(Measurement m)
	{
		foreach (var v in m.TopPinHeights) yield return v;
		foreach (var v in m.BottomPinHeights) yield return v;
		foreach (var v in m.TopPosX) yield return v;
		foreach (var v in m.TopPosY) yield return v;
		foreach (var v in m.BottomPosX) yield return v;
		foreach (var v in m.BottomPosY) yield return v;
	}

	// ===== 底层解码 =====

	/// <summary>HSL 字节变换（与现场 PLC 字序同源）。默认 CDAB（Modicon / 汇川系常见）。</summary>
	private static readonly RegularByteTransform BtCdab = new() { DataFormat = DataFormat.CDAB };
	private static readonly RegularByteTransform BtAbcd = new() { DataFormat = DataFormat.ABCD };

	/// <summary>状态字：大端（高字节在前）。</summary>
	public static int Word(byte[] raw, int wordOffset)
		=> (raw[wordOffset * 2] << 8) | raw[wordOffset * 2 + 1];

	/// <summary>
	/// 针高：DINT（2WORD）÷1000。
	/// 转换走 HSL 的 <see cref="RegularByteTransform.TransInt32"/>（**与 `plc.ReadInt32("MWxxxxx")` 同一套字节变换**），
	/// 字序由 <paramref name="format"/> 选 CDAB（低字在前，默认）/ ABCD（高字在前）。
	/// 注意：DINT 没有 NaN 语义，PLC 未写该区时读到 0 → 0.000（与真实 0 无法区分），
	/// 「该件未做针高检测」要靠整段读失败或结果字（%MW10004）判断。
	/// </summary>
	public static double Int32Div1000(byte[] raw, int wordOffset, PinHeightFormat format)
	{
		var bt = format == PinHeightFormat.Int32Abcd ? BtAbcd : BtCdab;
		return bt.TransInt32(raw, wordOffset * 2) / 1000.0;
	}

	/// <summary>位置度：REAL（2WORD 单精度浮点），字序由 <paramref name="format"/> 决定。</summary>
	public static double Float(byte[] raw, int wordOffset, PositionFormat format)
		=> format == PositionFormat.FloatAbcd
			? BtAbcd.TransSingle(raw, wordOffset * 2)
			: BtCdab.TransSingle(raw, wordOffset * 2);
	/// <summary>是否对产品码做 WORD 内字节交换（true = 每 WORD 按 [低字节, 高字节] 拼字符串）。
	/// 仅影响 ASCII 字符串区解析，状态字/数值字段不受影响。</summary>
	public const bool ProductCodeSwapWords = false;

	/// <summary>
	/// ASCII 字符串区解码。Modbus 大端下每个 WORD 为 [高字节, 低字节]；
	/// <paramref name="swapInWord"/> = true 时按 [低字节, 高字节]。遇 0x00 视为结束，过滤不可打印字符。
	/// </summary>
	public static string ParseAscii(byte[] raw, int byteOffset, int byteLength, bool swapInWord)
	{
		var sb = new System.Text.StringBuilder(byteLength);
		for (int i = 0; i + 1 < byteLength; i += 2)
		{
			var hi = raw[byteOffset + i];
			var lo = raw[byteOffset + i + 1];
			var first = swapInWord ? lo : hi;
			var second = swapInWord ? hi : lo;
			if (first == 0) break;
			if (first >= 0x20 && first <= 0x7E) sb.Append((char)first);
			if (second == 0) break;
			if (second >= 0x20 && second <= 0x7E) sb.Append((char)second);
		}
		return sb.ToString().Trim();
	}

	/// <summary>
	/// 把 ASCII 字符串编码为定长 WORD 数组（缺省补 0）。
	/// 每个 WORD 值 = (首字符 &lt;&lt; 8) | 次字符，与 <see cref="ParseAscii"/>(swapInWord=false) 对称。
	/// 用 short[] 写寄存器，避免 HSL DataFormat 对 byte[] 写法的字序影响。
	/// </summary>
	public static short[] EncodeAsciiWords(string text, int wordCount)
	{
		var s = text ?? "";
		var buf = new short[wordCount];
		for (int w = 0; w < wordCount; w++)
		{
			var i = w * 2;
			int c1 = i < s.Length ? s[i] : 0;
			int c2 = i + 1 < s.Length ? s[i + 1] : 0;
			buf[w] = (short)((c1 << 8) | (c2 & 0xFF));
		}
		return buf;
	}

	// ===== 显示映射（可空：null = 未采集，一律显示 --）=====

	/// <summary>结果字显示：1→OK 2→NG 其他/null→--。</summary>
	public static string ResultText(int? v) => v switch { 1 => "OK", 2 => "NG", _ => "--" };

	/// <summary>打码结果码显示：PLC 侧只有 1=OK / 2=NG。</summary>
	public static string MarkResultText(int? v) => v switch
	{
		1 => "OK",
		2 => "NG",
		_ => "--",
	};

	/// <summary>
	/// 打码完成信号 → 写 %MW10502 的值：**只有成功才是 1**；
	/// 失败返回 null（**不写**，PLC 侧保持 0），原因走弹窗 / 日志 / 打码流水表。
	/// </summary>
	public static int? MarkDoneCode(MarkResultCode c) => c == MarkResultCode.Ok ? 1 : null;

	/// <summary>打码内部细分原因文本（弹窗/日志用，不上 PLC）。</summary>
	public static string MarkReasonText(MarkResultCode c) => c switch
	{
		MarkResultCode.Ok => "OK",
		MarkResultCode.LaserNg => "打标NG（回执不带 OK）",
		MarkResultCode.Timeout => "回执超时",
		MarkResultCode.GenerateFailed => "码生成失败",
		MarkResultCode.InternalError => "MES 异常",
		MarkResultCode.EchoMismatch => "回执内容不符",
		_ => "--",
	};

	/// <summary>读码结果码显示：PLC 侧只有 1=OK / 2=NG。</summary>
	public static string ScanResultText(int? v) => v switch
	{
		1 => "OK",
		2 => "NG",
		_ => "--",
	};

	/// <summary>
	/// 读码结果码 → 写 PLC 的值：**1=OK，其余全部 2=NG**（重码/错码/未扫到/枪异常不再细分）。
	/// </summary>
	public static int ScanPlcCode(ScanResultCode c) => c == ScanResultCode.Ok ? 1 : 2;

	/// <summary>读码内部细分原因文本（弹窗/日志用，不上 PLC）。</summary>
	public static string ScanReasonText(ScanResultCode c) => c switch
	{
		ScanResultCode.Ok => "OK",
		ScanResultCode.NoRead => "未扫到",
		ScanResultCode.Duplicate => "重码",
		ScanResultCode.BadFormat => "错码",
		ScanResultCode.CommError => "枪异常",
		ScanResultCode.InternalError => "MES异常",
		_ => "--",
	};

	/// <summary>上传（采集）结果码显示：只有 1=OK / 2=失败（落库结果）。</summary>
	public static string UploadResultText(int? v) => v switch
	{
		1 => "OK",
		2 => "失败",
		_ => "--",
	};
}

/// <summary>针高口径：PLC 侧数据类型 **DINT（32 位有符号整数）**，收到值需 ÷1000 显示（2115 → 2.115）。</summary>
/// <summary>针高口径：PLC 侧数据类型 **DINT（32 位有符号整数）**，读取后 ÷1000 显示（2115 → 2.115 mm）。</summary>
public enum PinHeightFormat
{
	/// <summary>字序 CDAB：**低字在前**（汇川 AM/H5U、三菱风格 PLC 的 DINT 排列）。默认。</summary>
	Int32Cdab = 0,
	/// <summary>字序 ABCD：**高字在前**（部分 PLC/旧版本配置）。</summary>
	Int32Abcd = 1,
}

/// <summary>位置度口径：PLC 侧数据类型 **REAL（32 位浮点）**。</summary>
public enum PositionFormat
{
	/// <summary>IEEE754 单精度，字序 CDAB（默认）。</summary>
	FloatCdab = 0,
	/// <summary>IEEE754 单精度，字序 ABCD。</summary>
	FloatAbcd = 1,
}

/// <summary>
/// 打码站结果码。**%MW10502 是「打码完成」信号，只写 1（成功完成）；失败不写（保持 0）+ 弹窗提示**。
/// 本枚举的细分值仅作内部诊断（弹窗标题 / 日志 / %ThaMarkLog），不上 PLC。
/// </summary>
public enum MarkResultCode
{
	NotDone = 0,
	Ok = 1,
	/// <summary>NG（MES 内部记录用；不会写到 PLC）。</summary>
	Ng = 2,
	/// <summary>内部诊断：打标机回执不带 OK（对外仅弹窗）。</summary>
	LaserNg = 2,
	/// <summary>内部诊断：回执超时 / 通讯异常。</summary>
	Timeout = 3,
	/// <summary>内部诊断：取不到唯一时间码。</summary>
	GenerateFailed = 4,
	/// <summary>内部诊断：MES 内部异常。</summary>
	InternalError = 5,
	/// <summary>内部诊断：回执内容与下发不一致。</summary>
	EchoMismatch = 6,
}

/// <summary>
/// 读码站结果码。
/// **写入 %MW10522 只有两个值：1=OK / 2=NG**（与 PLC 约定）；
/// 其余细分值仅作内部诊断（弹窗原因 / 流水表），统一由 <see cref="Tha26003.ScanPlcCode"/> 归为 2。
/// </summary>
public enum ScanResultCode
{
	NotDone = 0,
	Ok = 1,
	/// <summary>NG（写 PLC 的唯一非 OK 值）。</summary>
	Ng = 2,
	/// <summary>内部诊断：未扫到 / 扫码枪回 error（对外也是 2）。</summary>
	NoRead = 7,
	/// <summary>内部诊断：重码（对外也是 2）。</summary>
	Duplicate = 3,
	/// <summary>内部诊断：错码/格式不符（对外也是 2）。</summary>
	BadFormat = 4,
	/// <summary>内部诊断：扫码枪通讯异常（对外也是 2）。</summary>
	CommError = 5,
	/// <summary>内部诊断：MES 内部异常（对外也是 2）。</summary>
	InternalError = 6,
}

/// <summary>
/// 上传（采集）站结果码（写入 %MW10702）：**只区分"落库成功/失败"两个值** ——
/// 1=落库 OK（数据已入本地库）；2=落库失败（读数据区失败 / 数据异常未落库 / 数据库写入失败）。
/// 「数据 NG」是产品质量判定，由 ProductRecord.OverallResult 承担，不再占用本结果码。
/// </summary>
public enum UploadResultCode
{
	NotDone = 0,
	Ok = 1,
	Failed = 2,
}

/// <summary>码索引状态机（ThaCodeIndex.Status）。</summary>
public enum CodeStatus
{
	/// <summary>已打码，未读码。</summary>
	Marked = 0,
	/// <summary>已读码，未上传。</summary>
	Scanned = 1,
	/// <summary>已上传完成。</summary>
	Uploaded = 2,
	/// <summary>NG 冻结（判 NG 报废的码不得复用）。</summary>
	NgLocked = 3,
}

/// <summary>
/// %MW 编程地址 → Modbus 寄存器号 的映射规则（保留为现场保险机制）。
/// 背景：Modbus 保持寄存器地址只有 16 位（0~65535）。协议 Rev.2 已把全部地址压到
/// %MW10702 以内，因此**默认直传、无需换算**；若现场 PLC 的映射表另有偏移
/// （例如 %MW10002 实际暴露在寄存器 0），在此加一条规则即可，代码不用改。
/// 规则语义：%MW ∈ [MwFrom, MwFrom+MwSpan) → 寄存器 = RegisterFrom + (%MW - MwFrom)。
/// </summary>
public sealed class ModbusAddressRule
{
	/// <summary>%MW 起始编号。</summary>
	public int MwFrom { get; set; }

	/// <summary>对应的 Modbus 寄存器起始号。</summary>
	public int RegisterFrom { get; set; }

	/// <summary>本规则覆盖的 %MW 宽度。</summary>
	public int MwSpan { get; set; } = 10000;
}

/// <summary>
/// 地址换算器：把协议 md 里的 %MW 编程地址换算成实际下发的设备地址。
/// 汇川 InovanceTcpNet 只接受软元件名（实测 M/MW 前缀均可用，且**号 == Modbus 寄存器号 1:1**，
/// 官方示例也用 <c>MW1000</c>），故默认前缀 "MW"：<c>%MW10500 → "MW10500" → 寄存器 10500</c>。
/// 改用 ModbusTcpNet 直连时把前缀配成空串即输出纯数字地址。
/// </summary>
public sealed class ThaAddressMap
{
	private readonly List<ModbusAddressRule> _rules;
	private readonly string _prefix;

	private ThaAddressMap(List<ModbusAddressRule> rules, string prefix)
	{
		_rules = rules;
		_prefix = prefix;
	}

	/// <summary>默认：直传寄存器号 + 汇川 MW 软元件前缀。</summary>
	public static ThaAddressMap Default { get; } = new(new List<ModbusAddressRule>(), "MW");

	/// <summary>按配置构造；规则为空时直传（现场若还有超出 65535 的 %MW，加规则即可）。</summary>
	public static ThaAddressMap Create(Configs.AppConfig cfg)
	{
		var rules = cfg.ModbusAddressRules?.Where(r => r.MwSpan > 0).ToList() ?? new List<ModbusAddressRule>();
		return new ThaAddressMap(rules, cfg.PlcAddressPrefix ?? "");
	}

	/// <summary>换算为 Modbus 寄存器号。</summary>
	public int Resolve(int mw)
	{
		foreach (var r in _rules)
		{
			if (mw >= r.MwFrom && mw < r.MwFrom + r.MwSpan)
				return r.RegisterFrom + (mw - r.MwFrom);
		}
		return mw;
	}

	/// <summary>换算为设备地址字符串（HSL 接口用）：<c>前缀 + 寄存器号</c>。越界时记错误日志，避免静默读到错误地址。</summary>
	public string R(int mw)
	{
		var reg = Resolve(mw);
		if (reg is < 0 or > 65535)
		{
			LogMgr.Instance.Error($"[THA-ADDR] %MW{mw} 换算后寄存器号 {reg} 超出 Modbus 范围(0~65535)，请检查 ModbusAddressRules 配置");
		}
		return _prefix + reg;
	}

	/// <summary>可读描述（日志/UI 用）。</summary>
	public string Describe(int mw) => $"%MW{mw} → 设备地址 {R(mw)}（寄存器 {Resolve(mw)}）";
}
