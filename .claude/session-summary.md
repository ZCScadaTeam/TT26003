# 会话摘要 - N23X 上位机改造

## 项目背景
N23X 是从 DT-DJJ 旧项目改造的同公司新产品上位机。Avalonia UI + .NET 10，PLC 通信用 HslCommunication（汇川 H5U）。

## 已完成改造

### 1. 默认管理员账号
- `App.cs` 新增 `EnsureDefaultAdminUser()` — Users 表为空时自动创建 admin/admin

### 2. MES 上传重构
- `MesService.SendBinDataAsync` → `SendProductDataAsync(sn, bushingCode)`
- JSON 格式: `{ sn, bushingCode, dateTime, workPlace, user }`
- `MesVM`/`MesPage` 重写 — 只有 SN 码 + Bushing 码扫码输入
- 配置页标签改为"产品数据接口URL"

### 3. 主页简化
- `MainPage.axaml` — 去掉工单号，改为 PPID + Bushing 码输入
- `MainVM` — 去掉工单相关字段和方法

### 4. OK 品自动上传 MES
- `PlcServiceNew` 新增 `CurrentBushingCode` 属性
- `HandleCountTriggers` 在 OK 品计数时调用 `UploadOkToMes()`
- `UploadOkToMes` 检查 SN + Bushing 码都不为空才发 MES

### 5. 快速编辑模式禁用
- 搬入 `ConsoleHelper.cs`，`App.cs` 启动时调用 `DisableQuickEditMode()`

### 6. 报警读取
- `PlcServiceNew` 新增 `ReadAlarms()` — 读 B1000-B1999（异常报警）+ B2000-B2999（警告）
- 每 100ms 主循环读取，显示在主页面 DataGrid
- 当前显示通用名，Excel 中文描述待导入

## 未完成
- PLC 点位表待按新产品重写（目前只有 6 个基本点）
- 报警中文描述未导入（Excel 2000 条 B 区报警）
- 相机读取未实现（两个项目都没有）
