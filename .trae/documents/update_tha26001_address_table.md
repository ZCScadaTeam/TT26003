# THA26001 地址表更新实施计划

## Summary

按用户上传的最新 `THA26001_PLC_ModbusTCP地址表.md`，将采集协议从「66 WORD / 4 针 / 单位置度」升级为「116 WORD / 8 针 / 双位置度（鱼眼+框口）」，产品码偏移由 60 改为 110。不采集 CCD 段（3000~3126）。同步更新 ModbusSim 模拟器。

## Current State Analysis

**现有程序读取布局**（[ThaPlcService.cs:333-352](file:///d:/Ver pro/DataCollection/App/Services/ThaPlcService.cs#L333-L352)）：
- 整块读 66 WORD（132 字节），基址 %MW1000 / %MW2000
- 状态字 offset 0-6（7 WORD）
- 鱼眼针高 offset 8/12/16/20（4 针 × 2 WORD = 8 WORD）
- 框口针高 offset 10/14/18/22（4 针 × 2 WORD = 8 WORD）
- 位置度 offset 24 起，X/Y 交替 8 组（32 WORD = 16 float）
- 产品码 offset 60-65（6 WORD = 12 字节 ASCII）
- 块总长 66 WORD = 132 字节

**新地址表差异**（[地址表](../)）：
- 块总长 116 WORD = 232 字节（范围 1000~1115 / 2000~2115）
- 鱼眼针高 8 针：offset 8/12/16/20/**24/28/32/36**
- 框口针高 8 针：offset 10/14/18/22/**26/30/34/38**
- 鱼眼位置 8 PIN X/Y：offset 40-70（**不变**）
- **框口位置 8 PIN X/Y（新增）**：offset 72-103（32 WORD = 16 float）
- 产品码：offset 60-65 → **offset 110-115**
- CCD 段 3000-3126：**不读**

**数据模型字段现状**（[ThaProductRecord.cs:55-85](file:///d:/Ver pro/DataCollection/App/Ext/DbModels/ThaProductRecord.cs#L55-L85)）：
- 4 鱼眼 + 4 框口针高 = 8 列
- 8 位置 X + 8 位置 Y = 16 列
- 需扩展为 8 鱼眼 + 8 框口 = 16 列，新增 8 框口位置 X + 8 框口位置 Y = 16 列，**净增 24 列**

**UI 列现状**：
- [MainPage.axaml:179-202](file:///d:/Ver pro/DataCollection/App/UI/Pages/Main/Main/MainPage.axaml#L179-L202) 主页历史表 33 列（含 8+8 针高 + 16 位置）
- [DatePage.axaml:159-182](file:///d:/Ver pro/DataCollection/App/UI/Pages/Date/DatePage.axaml#L159-L182) 详细数据表 33 列
- [DatePage.axaml:222-245](file:///d:/Ver pro/DataCollection/App/UI/Pages/Date/DatePage.axaml#L222-L245) 追溯查询表 33 列
- [DateVM.cs:577-657](file:///d:/Ver pro/DataCollection/App/UI/Pages/Date/DateVM.cs#L577-L657) 导出 29 列
- [MonitorVM.cs:108-149](file:///d:/Ver pro/DataCollection/App/UI/Pages/Monitor/MonitorVM.cs#L108-L149) 监控页每通道 32 行

**模拟器现状**（[ModbusSim/Program.cs](file:///d:/Ver pro/DataCollection/Tools/ModbusSim/Program.cs)）：
- `Regs = new ushort[3000]`
- 产品 1/2 写 4 针鱼眼 + 4 针框口 + 8 位置 X/Y + 产品码（offset 1060/2060）
- 每 1 秒触发一次

## Proposed Changes

### 1. 协议解析 [ThaPlcService.cs](file:///d:/Ver pro/DataCollection/App/Services/ThaPlcService.cs)

- 改块大小常量 `66` → `116`（`ReadBlockWithRetryAsync` 调用与 `raw.Length` 校验同步改）
- 改偏移常量：
  - `WEyelet` 扩展为 `{ 8, 12, 16, 20, 24, 28, 32, 36 }`（8 针）
  - `WFrame` 扩展为 `{ 10, 14, 18, 22, 26, 30, 34, 38 }`（8 针）
  - 新增 `WFramePosBase = 72`（%MW1072 / %MW2072 框口位置度 X1 起）
  - `WCodeBase` 60 → 110
- 改 `ThaProtocol.Parse` 方法体：
  - 添加 `EyeletPinHeight5..8 = Float(raw, WEyelet[4..7])`
  - 添加 `FramePinHeight5..8 = Float(raw, WFrame[4..7])`
  - 添加 `FramePositionX1..8 / FramePositionY1..8`（按 `WFramePosBase + i*4` / `+2`）
- 改 `ThaChannelSnapshot` record 添加 `FramePositionX` / `FramePositionY`（保留 `EyeletPinHeights` / `FramePinHeights` 数组长度由 4 升 8）
- 改 `ToChannelSnapshot` 适配新字段映射

### 2. 数据库模型 [ThaProductRecord.cs](file:///d:/Ver pro/DataCollection/App/Ext/DbModels/ThaProductRecord.cs)

新增 24 列（CodeFirst 自动迁移，旧数据新增列默认为 0）：
- `EyeletPinHeight5..8`（4 列）
- `FramePinHeight5..8`（4 列）
- `FramePositionX1..8`（8 列）
- `FramePositionY1..8`（8 列）

### 3. 主页/统计行模型 [ThaHistoryRow.cs](file:///d:/Ver pro/DataCollection/App/UI/Pages/Main/ThaHistoryRow.cs)

新增 24 个 string 属性 + `From()` 转换：
- `Eyelet5..8`
- `Frame5..8`
- `FramePosX1..8` / `FramePosY1..8`

### 4. UI 列更新（3 个 XAML）

**主页历史表** [MainPage.axaml:179-202](file:///d:/Ver pro/DataCollection/App/UI/Pages/Main/MainPage.axaml#L179-L202) — 在现有 Pin4 鱼眼后追加 Pin5-8 鱼眼、Pin5-8 框口、框口位置度 X1-Y8 共 24 列。

**详细数据表 + 追溯查询表** [DatePage.axaml:159-245](file:///d:/Ver pro/DataCollection/App/UI/Pages/Date/DatePage.axaml#L159-L245) — 同样追加 24 列 × 2 表 = 48 列绑定。

**排序**：鱼眼 Pin1-8、框口 Pin1-8、位置度 PIN1_X/Y PIN2_X/Y … PIN8_X/Y（鱼眼在前），框口位置 PIN1_X/Y … PIN8_X/Y（框口在后）。沿用现有 `Width="Auto"` 模式，横向滚动展示。

### 5. 导出模型 [DateVM.cs:577-657](file:///d:/Ver pro/DataCollection/App/UI/Pages/Date/DateVM.cs#L577-L657)

- `ThaExportRow` 新增 24 个 `string` 属性 + `ExcelColumnName` 特性（沿用 `鱼眼针高N` / `框口针高N` / `框口位置度XN` / `框口位置度YN` 列头）
- `ProjectExportData` 填充新字段

### 6. 监控页 [MonitorVM.cs:108-149](file:///d:/Ver pro/DataCollection/App/UI/Pages/Monitor/MonitorVM.cs#L108-L149)

- 针高循环 4 → 8（鱼眼 + 框口）
- 新增「框口位置度 PIN1-8 X/Y」段（基于 `WFramePosBase`）
- 产品码地址 `1060` → `1110`，`2060` → `2110`

### 7. 模拟器 [ModbusSim/Program.cs](file:///d:/Ver pro/DataCollection/Tools/ModbusSim/Program.cs)

- `Regs` 数组大小 `3000` → `3200`（容纳 2115 与 3126）
- 写 8 针鱼眼 + 8 针框口（offset 8..38）
- 写 8 鱼眼位置 + 8 框口位置（offset 40..103）
- `WriteCode(1060/2060)` → `WriteCode(1110/2110)`
- 触发循环中 `WriteCode` 调用同步更新地址

### 8. 文档与项目记忆

- [AGENTS.md](file:///d:/Ver pro/DataCollection/AGENTS.md) 规约中的工程约定「产品1数据块位于寄存器1000-1065」同步改为「1000-1115」，项目记忆相关行更新。

## Assumptions & Decisions

- **CCD 段不读**：地址表明示「CCD To PLC」方向，是 PLC 内部缓存，无业务需求
- **不删除旧列**：保留 4 针高/16 位置列，向后兼容旧记录（CodeFirst 加列不删列，旧记录新字段为 0）
- **UI 全部展开**：现有 `Width="Auto"` + 横向滚动已支持，新增 24 列不强制折叠
- **监控页加位置段**：与 TAS 数据展示一致
- **块大小 116 WORD**：基址 1000 + 长度 116 = 末端 1115（恰好包含产品码），无需二次读
- **CDAB 字节序保持**：上一轮已切换为 CDAB，新扩展字段统一沿用

## Verification Steps

1. `dotnet build App/App.csproj -c Debug` 0 错误
2. 启动 ModbusSim + 主程序（debug 模式），触发 `%MW100=1/2/3` 三种场景
3. 主页历史表展示新字段（Pin5-8 鱼眼/框口、框口位置度 X/Y）
4. 监控页展示 8 针 + 双位置度
5. 统计页详细数据 + 追溯查询展示新字段
6. CSV 导出含新列，UTF-8 BOM 正常
7. 旧数据兼容：CodeFirst 加列后旧记录新字段为 0
