namespace ZC.MyCustomLib;

/// <summary>
/// 自定义类库示例
/// </summary>
public class MyCustomClass
{
    /// <summary>
    /// 示例方法
    /// </summary>
    public string GetMessage()
    {
        return "Hello from MyCustomLib!";
    }

    /// <summary>
    /// 带参数的示例方法
    /// </summary>
    public int Calculate(int a, int b)
    {
        return a + b;
    }
}

/// <summary>
/// 工具类示例
/// </summary>
public static class MyCustomHelper
{
    /// <summary>
    /// 静态辅助方法
    /// </summary>
    public static string FormatMessage(string message)
    {
        return $"[MyCustomLib] {message}";
    }
}
