using Microsoft.Data.Sqlite;
using MiniExcelLibs;
using MiniExcelLibs.Attributes;

var db = args.Length > 0 ? args[0] : @"D:\Ver pro\DataCollection\App\bin\Debug\AppData\THA26001.db";
using var conn = new SqliteConnection($"Data Source={db}");
conn.Open();

using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT ProductCode, CreateTime, UpdateTime FROM ThaProductRecord ORDER BY Id LIMIT 5";
using var reader = cmd.ExecuteReader();

var rows = new List<TestRow>();
while (reader.Read())
{
	rows.Add(new TestRow
	{
		产品码 = reader.GetString(0),
		创建时间 = reader.GetDateTime(1).ToString("yyyy-MM-dd HH:mm:ss"),
		更新时间 = reader.IsDBNull(2) ? "" : reader.GetDateTime(2).ToString("yyyy-MM-dd HH:mm:ss"),
	});
}
Console.WriteLine($"行数: {rows.Count}");

var xlsxPath = Path.Combine(Path.GetTempPath(), "THA26001_width_test.xlsx");
using (var stream = File.Create(xlsxPath))
{
	await MiniExcel.SaveAsAsync(stream, rows);
}
Console.WriteLine($"已导出: {xlsxPath}");

// 与主程序 ThaExportRow 相同的列宽特性验证类
internal sealed class TestRow
{
	[ExcelColumnName("产品码")]
	[ExcelColumnWidth(180)]
	public string 产品码 { get; set; } = "";
	[ExcelColumnName("创建时间")]
	[ExcelColumnWidth(180)]
	public string 创建时间 { get; set; } = "";
	[ExcelColumnName("更新时间")]
	[ExcelColumnWidth(180)]
	public string 更新时间 { get; set; } = "";
}