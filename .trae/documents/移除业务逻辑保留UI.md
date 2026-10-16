# N23X 上位机（App）：移除全部业务逻辑，保留整体 UI

## Context（背景）

App 是 N23X 光栅传感器产线上位机（Avalonia + .NET 10，MVVM 依赖闭源 ZC.Core 框架 DLL），核心业务是 PLC/耐压仪/光栅/KGK 喷码机/MES 通信 + PostgreSQL 读写（统计/配方/登录/日志）。用户要求：**移除全部业务逻辑，仅保留整体 UI**（页面/导航/布局/主题/弹窗），且程序**必须可编译运行**，业务数据显示占位/假数据。

现状：`App\UI\Pages\Devices` 目录已空（设备页源文件已被删），但 `AppVM.cs`/`App.cs` 仍引用已删类型，项目**当前不可编译**（`App\bin\Debug` 为空）。

范围约定（已与用户确认）：仅处理 App 项目；不含 GratingSensor.Lab 等。

## 实施阶段

以下依赖顺序保证每批改完，残留编译错误只来自"下一批尚未改的文件"。

### 阶段 0 — 新增演示基础件（可独立编译，不引用任何待删类型）

| 文件 | 内容 |
|---|---|
| `App\UI\Ext\DemoSession.cs`（新增） | 静态演示会话：`CurrentUserName/Role/IsLoggedIn/RoleName/Login/Logout`；角色用整型常量（0=未登录，2=技术员，4=管理员），随 UserAuthService 删除后不再依赖 `UserRole` 枚举 |
| `App\UI\Ext\DemoData.cs`（新增） | 静态假数据源（统一单一数据源）：① 假测量（`NextMeasurement()`：每 1s 生成 10 个测量字段 + 递增产品号 + OK/NG 累加计数）；② PLC 显示（`PlcStatus="演示"`、TotalOk/TotalNg）；③ 机位行状态轮转（`RandomizeStatus()`）；④ **内存配方库**（`ObservableCollection<RecipeItem> Recipes` 种子 3~5 条 + 六分组传感器清单 `SensorsByGroup`，MainVM 下拉 / RecipeVM / RecipeCreateDialog 读写同一集合）；⑤ 统计假序列（按小时/按班组/按天 OK/NG 数组 + 明细行生成）；⑥ `MaxRecipeNameLength` 等常量 |

迁移（保持原命名空间不变，避免改所有 `using`）：
- `Services\ConsoleHelper.cs`、`Services\LogMgr.cs` → `App\Infra\`（日志基础设施，NLog 文件日志无 DB 依赖，保留）
- `Services\ProductOutputDetailFormatter.cs` → `App\UI\Ext\`（纯显示映射，DateVM/MainVM 仍在用，命名空间 `AppZC.UI.Pages` 不变）

### 阶段 1 — 无设备依赖 VM（先消掉编译错误大头）

1. **`UI\Pages\Permission\PermissionVM.cs`**：删 `[Inject] UserAuthService`；`Login()` 改 非空密码→`DemoSession.Login("演示管理员", 4)`；`UpdateLoginStatus()` 读 DemoSession；`Logout()` 清会话。
2. **`UI\Pages\Permission\SetVM.cs`**：删 `[Inject] UserAuthService`；`ChangeVendorPassword` 改内存演示（两次密码一致→提示"修改成功（演示）"）。
3. **`UI\Pages\Config\ConfigVM.cs`**：仅删 `CalibrationDimensionCalculator.OutlierFilterEnabled = ...` 两行静态写；其余（AppConfig 读写 + json5 落盘）保留。
4. **`UI\Pages\Config\RecipeVM.cs`**：删 `[Inject] RecipeService` 与其全部调用，改为读写 `DemoData.Recipes` / `DemoData.SensorsByGroup`（内存 CRUD）；`RecipeService.MaxRecipeNameLength` → 本地/DemoData 常量；保留 `RecipeItem`/`SensorItem` 类与全部 UI 属性（MainVM 也用它）。
5. **`UI\Dialogs\RecipeCreateDialog.axaml.cs`**：`App.Current.IOC.Get<RecipeService>()` → 直接写 `DemoData.Recipes`（内存重复检查 + Add），成功后置 `RecipeId/RecipeName` 并 `Close(true)`；`MaxRecipeNameLength` 同样换常量。

### 阶段 2 — 统计页三 VM（共享 DemoData 假序列）

- **`UI\Pages\Date\DateVM.cs`**：删 `required ISqlSugarClient DbClient`、`using AppZC.Models.DB`/`SqlSugar`、全部 DB 查询与 `WithCountRecordPageDbGateAsync`；`Query()`/按小时/按班组/按天 → 从 `DemoData` 取假 OK/NG 序列填充 XLabels/Series/白晚班表/明细表；`PlcServiceNew.GetShiftByTime` → 本地私有静态实现；**保留** LiveCharts 图表、ChartTheme、Excel/CSV 导出。
- **`UI\Pages\Date\OEEDateVM.cs`**、**`OEEVM.cs`**：同上处理（删 DB 查询换假序列），三页数字由同一 DemoData 序列驱动，视觉自洽。

### 阶段 3 — 监控与主页（工作量最大）

- **`UI\Pages\Monitor\MonitorVM.cs`**：删 `[Inject] Plc`、PlcStructNew 字段访问器、`PlcAddress` 计算、`_updaters` struct getter、`RequestMonitorProductCodeRefresh`。把 `BuildInvSignals/BuildDimSignals/BuildMarkSignals` 现有的**硬编码地址/名称/说明文案**搬为静态行清单，构造时建行；保留 `MonitorSignalRow`、`Signals`、`PlcStatus/StationModeText/LastRefreshTime`、`StartAutoRefresh/StopAutoRefresh` 生命周期（页面 .axaml.cs 调用的契约不变）；500ms 定时器对每行随机赋 `Value`；`PlcStatus="演示"`。
- **`UI\Pages\Main\MainVM.cs`**（最大）：删 6 个 `[Inject] 业务服务`、ProductionMonitor 事件订阅、全部 `InvStation/DimStation/MarkStation` 调用、`CalibrationDimensionCalculator`、`ProductOutputDetailFormatter`（如有）、RecipeService 加载、传感器清零控制器调用。**保留全部 XAML 绑定属性** + 3 张机位表（机位名硬编码 "1A"~"4B"/"1A"~"2B"/"工位1~2"，不再用 `PlcServiceNew.InvSlotName/DimSlotName`）；构造器保留 1s `_updateTimer`，回调改 `UpdateDemoData()`（每轮：填 PlcStatus/OK-NG 计数/InvPhase/10 个 Meas/轮转一行机位状态/刷新 CurrentTime，首轮触发启动确认 Overlay）；标定流程用 DemoData 假尺寸 + RecipeCreateDialog 内存写入；传感器清零改为模拟延迟+演示结果提示、权限读 `DemoSession.Role >= 2`；`ShowTriggerConfirm/ConfirmAllow/ConfirmDeny` 保留 Overlay UI 交互（按钮仅置状态）。

### 阶段 4 — 壳与启动

- **`UI\AppVM.cs`**：删 `/设备` 顶部导航项、`GetDeviceNavigations/GetFirstDeviceViewModel`、`LeftNavigations["设备"]`；删 `[Inject] UserAuthService`；`OnSelectedNavDataChanged/OnSelectedLeftNavDataChanged` 权限守卫改"直接放行"（不再弹权限对话框）；`UpdateCurrentUserDisplay()` 读 `DemoSession` 拼 `CurrentUserDisplay`。
- **`UI\AvaApp.cs`**：退出清理块删 `PlcServiceNew.CleanupAsync`（服务已删），保留 `App.Destroy` 与 LogMgr 清理。
- **`App\App.cs`**：顶层删 `new PlcStructNew().GetStructInfo()` 与 `iBinaryStructInfo`（`PlcStructNew` 模型要删）；构建链去掉 `.UseDatabase(config.Database).UseDbKeyValueStorage().UseWebServer()`；`OnInitialize` 删 DB 相关全部调用（等待连库/补列/建表/Seed/设备预创建），仅保留 `StartUi()` + `SetProgress(40,500)`；`OnStart` 删 `TcpStreamServerSocket/HttpServer` 块，保留 `StartTaskServices()` 与 `SetProgress(100,500)`；`CleanupBeforeExitAsync` 删 `PlcServiceNew` 引用（服务已删）；类内全部 SQL/表/分表/SQL 日志方法连同 `using AppZC.Models.DB` 一并删除。

### 阶段 5 — 删除叶子（阶段 4 全部完成后）

- 删 `App\Services\`：`PlcAdapters\`、`CalibrationDimensionCalculator.cs`、`DbLogConfig.cs`、`PlcAddress.cs`、`PlcServiceNew.cs`、`ProductionMonitor.cs`、`ProductTestRecordService.cs`、`RecipeService.cs`、`TableRetentionService.cs`、`UserAuthService.cs`、`ZxEnT6ControllerService.cs`
- 删 `App\Ext\BinStructs\`（PlcStructNew.cs 等）与 `App\Ext\DbModels\`（User/Recipe/Sensor/CountRecord/ProductTestRecord/DeviceStatusRecord）
- 清空 `App\Ext\.generated\`（源生成器残留，`<None>` 不入编译，但清掉避免混淆）

## 保留不动（UI 壳）

- `UI\AppStartUpVM.cs`（工位选择 + json5 写入，无 DB）
- `UI\Pages\Warning\LogVM.cs`（读 `D:\Logs\DXX` 文件）、`UI\Pages\Help\HelpVM.cs`
- 全部 `.axaml`：AppView / AppWindow / AvaApp / TopAppBarLayout / DefaultLayout / StartUpLayout / 各页面与弹窗（RecipeCreateDialog 仅改 .cs 逻辑）/ Styles/Converters/ChartTheme
- 行模型：`StationPositionRow.cs`、`MonitorSignalRow.cs`、`ProductOutputDetailRows.cs`
- `AppConfig`（Assets\Configs\AppConfig.cs）与 `Config\AppConfig.json5`
- 无需处理：csproj 外链 glob（`..\Rj6836H.Lab\**` 等目录不存在，glob 空匹配无副作用）

## 验证

1. 构建：
   ```
   dotnet build "d:\Ver pro\DataCollection\App\App.csproj" -c Debug
   ```
   （csproj 的 `$(DotnetTargetFrameworkVersion)` 等属性来自根 `Directory.Build.props`；输出落在 `App\bin\Debug`）
2. 运行 + 逐页核对：启动工位选择 → 主页 10 测量字段每秒跳动/PLC 状态"演示"/OK-NG 计数/三表行轮转/启动确认 Overlay → 监控 INV-DIM-Mark 三区信号值 500ms 刷新 → 统计三页假曲线 + Excel/CSV 导出 → 配置连接设置保存双写 json5 → 配方增删改查/尺寸/传感器内存自洽 + 手动标定→弹窗建配方后主页下拉出现 → 人员登录/改密演示 + 顶栏 CurrentUserDisplay 同步 → 主题切换/窗口按钮/退出确认弹窗可正常关闭进程。
3. 若框架启动异常：`initialize/start` 均 `UnwarpAsync(errThrow:false)` 吞异常，查 `D:\Logs\DXX` 日志三处（初始化失败/启动失败/未处理异常）定位；大概率是**遗漏重写的 `[Inject]` 指向已删类型**或残留 `using AppZC.Services` 引用已删名。

## 风险

- XAML 绑定属性遗漏（Avalonia 静默不报编译错）→ 按阶段 3 保留清单逐页核对，重点 MainVM 三张表列绑定与统计页图表系列
- 框架闭源：`UseDatabase/UseWebServer/StartTaskServices` 行为不可修改；如 `StartTaskServices` 空转拖慢启动（日志观察）可直接删该调用
- 配方单一数据源：务必统一走 `DemoData.Recipes`，避免主页下拉与配方页/弹窗不同步
- 「设备」顶栏项移除后 StationMode 仍用于主页面板/监控三区/配置页卡片可见性，逻辑自洽，StationMode 字段保留