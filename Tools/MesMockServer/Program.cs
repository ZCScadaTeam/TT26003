var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var app = builder.Build();

// 新接口：UploadDataByKey（N23X组装上传）
app.MapPost("/api/upload", async (HttpContext context) =>
{
    using var sr = new StreamReader(context.Request.Body);
    var body = await sr.ReadToEndAsync();

    var border = new string('-', 50);
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"\n{border}");
    Console.WriteLine("[MES MOCK] POST /api/upload - N23X组装上传(UploadDataByKey)");
    Console.WriteLine($"请求时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"请求体:");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(body);
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.WriteLine($"{border}\n");

    return Results.Ok(new { status = true, message = "接收成功" });
});

// 旧接口：产品数据上传（保留兼容）
app.MapPost("/api/product", async (HttpContext context) =>
{
    using var sr = new StreamReader(context.Request.Body);
    var body = await sr.ReadToEndAsync();

    var border = new string('-', 50);
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"\n{border}");
    Console.WriteLine("[MES MOCK] POST /api/product - 产品数据上传(旧)");
    Console.WriteLine($"请求时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"请求体:");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(body);
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.WriteLine($"{border}\n");

    return Results.Ok(new { state = true, description = "接收成功" });
});

app.MapPost("/api/device/status", async (HttpContext context) =>
{
    using var sr = new StreamReader(context.Request.Body);
    var body = await sr.ReadToEndAsync();

    var border = new string('-', 50);
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine($"\n{border}");
    Console.WriteLine("[MES MOCK] POST /api/device/status - 设备状态上报");
    Console.WriteLine($"请求时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"请求体:");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(body);
    Console.ForegroundColor = ConsoleColor.Gray;
    Console.WriteLine($"{border}\n");

    return Results.Ok(new { state = true, description = "接收成功" });
});

app.MapGet("/", () => Results.Ok(new
{
    server = "MES Mock Server",
    status = "running",
    endpoints = new[] { "POST /api/upload", "POST /api/product", "POST /api/device/status" }
}));

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║        MES Mock Server 已启动           ║");
Console.WriteLine("╠══════════════════════════════════════════╣");
Console.WriteLine("║  http://localhost:5073                  ║");
Console.WriteLine("║  POST /api/upload     N23X组装上传(新)  ║");
Console.WriteLine("║  POST /api/product    产品数据上传(旧)  ║");
Console.WriteLine("║  POST /api/device/status 设备状态上报   ║");
Console.WriteLine("╚══════════════════════════════════════════╝");
Console.ResetColor();

app.Run("http://localhost:5073");
