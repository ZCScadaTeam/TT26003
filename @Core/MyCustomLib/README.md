# 如何创建和使用自定义 DLL 包

## 一、创建 DLL 项目

### 1. 创建项目结构
```
@Core/
  └── MyCustomLib/
      ├── MyCustomLib.csproj    # 项目配置文件
      ├── MyCustomClass.cs      # 你的代码文件
      └── README.md             # 说明文档
```

### 2. 配置 .csproj 文件

参考 `UI.Theme.csproj` 的写法，关键配置项：

```xml
<PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>$(DotnetLangVersion)</LangVersion>
    <AssemblyName>ZC.MyCustomLib</AssemblyName>
    <RootNamespace>ZC.MyCustomLib</RootNamespace>
    <CoreLibPath>../../-/libs/Core</CoreLibPath>
</PropertyGroup>
```

### 3. 引用依赖

**引用现有的 Core DLL：**
```xml
<ItemGroup Condition="'$(CoreLibPath)' !=''">
    <Reference Include="$(CoreLibPath)/ZC.Core.dll"/>
    <Reference Include="$(CoreLibPath)/ZC.Core.UI.dll"/>
</ItemGroup>
```

**引用 NuGet 包：**
```xml
<ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="$(NewtonsoftJsonVersion)"/>
</ItemGroup>
```

## 二、编译 DLL

### 方法 1：使用 Visual Studio
1. 在解决方案资源管理器中右键点击项目
2. 选择"生成"或"重新生成"
3. 编译后的 DLL 在 `bin/Debug/net10.0/` 或 `bin/Release/net10.0/` 目录

### 方法 2：使用命令行
```bash
cd @Core/MyCustomLib
dotnet build -c Release
```

## 三、在 App 项目中使用 DLL

### 方法 1：项目引用（开发阶段推荐）

在 `App/App.csproj` 中添加：
```xml
<ItemGroup>
    <ProjectReference Include="../@Core/MyCustomLib/MyCustomLib.csproj"/>
</ItemGroup>
```

### 方法 2：DLL 引用（发布阶段推荐）

1. 将编译好的 DLL 复制到 `-/libs/Core/` 目录
2. 在 `App/App.csproj` 中添加：
```xml
<ItemGroup Condition="'$(CoreLibDllPath)'!=''">
    <Reference Include="$(CoreLibDllPath)/ZC.MyCustomLib.dll"/>
</ItemGroup>
```

### 方法 3：条件引用（灵活切换）

参考 `App.csproj` 的写法，同时支持项目引用和 DLL 引用：

```xml
<!-- 定义路径变量 -->
<PropertyGroup>
    <MyCustomLibProjectPath>../@Core/MyCustomLib</MyCustomLibProjectPath>
    <MyCustomLibDllPath>../-/libs/Core</MyCustomLibDllPath>
</PropertyGroup>

<!-- 项目引用 -->
<ItemGroup Condition="'$(MyCustomLibProjectPath)'!='' AND Exists('$(MyCustomLibProjectPath)')">
    <ProjectReference Include="$(MyCustomLibProjectPath)/MyCustomLib.csproj"/>
</ItemGroup>

<!-- DLL 引用 -->
<ItemGroup Condition="'$(MyCustomLibDllPath)'!='' AND !Exists('$(MyCustomLibProjectPath)')">
    <Reference Include="$(MyCustomLibDllPath)/ZC.MyCustomLib.dll"/>
</ItemGroup>
```

## 四、在代码中使用

```csharp
using ZC.MyCustomLib;

// 使用自定义类
var myClass = new MyCustomClass();
string message = myClass.GetMessage();
int result = myClass.Calculate(10, 20);

// 使用静态辅助类
string formatted = MyCustomHelper.FormatMessage("测试消息");
```

## 五、发布 DLL 到 libs 目录

### 手动复制
```bash
# 编译项目
dotnet build -c Release

# 复制 DLL 到 libs 目录
copy @Core\MyCustomLib\bin\Release\net10.0\ZC.MyCustomLib.dll -\libs\Core\
copy @Core\MyCustomLib\bin\Release\net10.0\ZC.MyCustomLib.pdb -\libs\Core\
```

### 自动复制（在 .csproj 中添加）
```xml
<Target Name="CopyToLibs" AfterTargets="Build" Condition="'$(Configuration)' == 'Release'">
    <ItemGroup>
        <DllFiles Include="$(OutDir)$(AssemblyName).dll"/>
        <PdbFiles Include="$(OutDir)$(AssemblyName).pdb"/>
    </ItemGroup>
    <Copy SourceFiles="@(DllFiles)" DestinationFolder="../../-/libs/Core/" OverwriteReadOnlyFiles="true"/>
    <Copy SourceFiles="@(PdbFiles)" DestinationFolder="../../-/libs/Core/" OverwriteReadOnlyFiles="true"/>
    <Message Text="已复制 DLL 到 libs 目录" Importance="high"/>
</Target>
```

## 六、添加到解决方案

1. 在 Visual Studio 中右键点击解决方案
2. 选择"添加" -> "现有项目"
3. 选择 `@Core/MyCustomLib/MyCustomLib.csproj`

或者直接编辑 `ZC.CORE.Templates.sln` 文件添加项目引用。

## 七、最佳实践

1. **命名规范**：遵循 `ZC.{模块名}` 的命名规范
2. **版本管理**：使用 `Directory.Build.props` 统一管理版本号
3. **依赖管理**：尽量使用变量引用版本号，如 `$(NewtonsoftJsonVersion)`
4. **文档注释**：为公共 API 添加 XML 文档注释
5. **调试符号**：同时发布 .pdb 文件以便调试
6. **条件编译**：使用条件引用支持开发和发布两种模式

## 八、常见问题

### Q: 如何在 Avalonia 项目中使用？
A: 如果需要 UI 相关功能，参考 `UI.Theme.csproj`，添加 Avalonia 包引用：
```xml
<PackageReference Include="Avalonia" Version="$(AvaloniaVersion)"/>
```

### Q: 如何引用其他自定义 DLL？
A: 在 .csproj 中添加引用：
```xml
<Reference Include="$(CoreLibPath)/ZC.OtherCustomLib.dll"/>
```

### Q: 编译错误找不到依赖？
A: 检查 `CoreLibPath` 路径是否正确，确保引用的 DLL 存在。
