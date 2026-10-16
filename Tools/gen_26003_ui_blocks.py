"""生成 / 替换 THA26003 改造中的机械性长块。

覆盖三处：
  1) App/UI/Pages/Main/ThaHistoryRow.cs      —— 行模型（全字段，可空显示）
  2) App/UI/Pages/Date/DatePage.axaml        —— 明细表 / 追溯表 列块
  3) App/UI/Pages/Main/MainPage.axaml        —— 主页产品记录表 列块（与明细同列，解决"显示不全"）
  4) App/UI/Pages/Date/DateVM.cs             —— 导出投影 + 导出行

口径：上/下针高各 15 点；上/下位置度各 15 点（PIN16 为协议预留，不采集、不建列）；
      所有数值字段可空，null 显示 `--`（不伪装成 0.000）。

幂等：按内容标记定位，不依赖上一次生成的列名，可重复执行。
"""
from pathlib import Path

ROOT = Path(r"D:/Ver pro/DataCollection -26003")

PIN_N = 15
POS_N = 15

COLS = [
    ("Time", "下料时间"),
    ("ProductCode", "产品码"),
    ("MarkResult", "打码结果"),
    ("ReadResult", "读码结果"),
    ("Overall", "总结果"),
    ("Withstand", "导通耐压结果"),
    ("PinHeight", "针高结果"),
    ("Position", "位置度结果"),
    ("IsRetest", "是否复检"),
    ("MarkTime", "打码时间"),
    ("ScanTime", "读码时间"),
] \
    + [(f"Top{i}", f"上针高 Pin{i}(mm)") for i in range(1, PIN_N + 1)] \
    + [(f"Bottom{i}", f"下针高 Pin{i}(mm)") for i in range(1, PIN_N + 1)] \
    + [(f"TopPosX{i}", f"上位置度 PIN{i}_X") for i in range(1, POS_N + 1)] \
    + [(f"TopPosY{i}", f"上位置度 PIN{i}_Y") for i in range(1, POS_N + 1)] \
    + [(f"BottomPosX{i}", f"下位置度 PIN{i}_X") for i in range(1, POS_N + 1)] \
    + [(f"BottomPosY{i}", f"下位置度 PIN{i}_Y") for i in range(1, POS_N + 1)]

# 主页记录表与统计页明细同列（全字段，横向滚动查看）
MAIN_COLS = COLS


def gen_history_row() -> str:
    L = [
        "using AppZC.Models.DB;",
        "using AppZC.Services;",
        "using ZC.Mvvm;",
        "",
        "namespace AppZC.UI.Pages;",
        "",
        "/// <summary>",
        "/// THA26003 产品记录行（主页记录表 / 统计页明细表 / 数据追溯表 共用）。",
        "/// 上/下针高各 15 点；上/下位置度各 15 点（PIN16 为协议预留，不采集）。",
        "/// **未采集的字段一律显示 `--`**（源数据为 NULL，不伪装成 0.000）。",
        "/// </summary>",
        "[ObservableObject]",
        "public partial class ThaHistoryRow",
        "{",
    ]
    for name, header in COLS:
        default = '""' if name == "ProductCode" else '"--"'
        L.append(f"\t/// <summary>{header}。</summary>")
        L.append(f"\tpublic partial string {name} {{ get; set; }} = {default};")
        L.append("")
    L += [
        "\t/// <summary>总结果颜色（OK 绿 / NG 红 / 未判定灰）。</summary>",
        '\tpublic partial string OverallColor { get; set; } = "#9E9E9E";',
        "",
        "\t/// <summary>从数据库记录构建行。</summary>",
        "\tpublic static ThaHistoryRow From(ThaProductRecord r)",
        "\t{",
        "\t\tvar row = new ThaHistoryRow",
        "\t\t{",
        '\t\t\tTime = r.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),',
        '\t\t\tProductCode = r.ProductCode ?? "",',
        "\t\t\tMarkResult = Tha26003.MarkResultText(r.MarkResult),",
        "\t\t\tReadResult = Tha26003.ScanResultText(r.ReadResult),",
        "\t\t\tOverall = Tha26003.ResultText(r.OverallResult),",
        "\t\t\tWithstand = Tha26003.ResultText(r.WithstandResult),",
        "\t\t\tPinHeight = Tha26003.ResultText(r.PinHeightResult),",
        "\t\t\tPosition = Tha26003.ResultText(r.PositionResult),",
        '\t\t\tIsRetest = r.IsRetest switch { 1 => "是", 2 => "否", _ => "--" },',
        '\t\t\tMarkTime = r.MarkTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "--",',
        '\t\t\tScanTime = r.ScanTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "--",',
        '\t\t\tOverallColor = r.OverallResult switch { 1 => "#2E7D32", 2 => "#D32F2F", _ => "#9E9E9E" },',
        "\t\t};",
    ]
    for i in range(1, PIN_N + 1):
        L.append(f"\t\trow.Top{i} = F(r.TopPinHeight{i});")
    for i in range(1, PIN_N + 1):
        L.append(f"\t\trow.Bottom{i} = F(r.BottomPinHeight{i});")
    for i in range(1, POS_N + 1):
        L.append(f"\t\trow.TopPosX{i} = F(r.TopPinPosX{i});")
        L.append(f"\t\trow.TopPosY{i} = F(r.TopPinPosY{i});")
    for i in range(1, POS_N + 1):
        L.append(f"\t\trow.BottomPosX{i} = F(r.BottomPinPosX{i});")
        L.append(f"\t\trow.BottomPosY{i} = F(r.BottomPinPosY{i});")
    L += [
        "\t\treturn row;",
        "\t}",
        "",
        "\t/// <summary>数值显示：null（未采集）一律 `--`，不伪装成 0.000。</summary>",
        '\tprivate static string F(double? v) => v?.ToString("F3") ?? "--";',
        "}",
        "",
    ]
    return "\n".join(L)


def cols_xaml(cols, indent: str) -> str:
    return "\n".join(
        f'{indent}<DataGridTextColumn Header="{h}" Binding="{{Binding {n}}}" Width="Auto" />'
        for n, h in cols
    )


def gen_export_projection() -> str:
    L = [
        "\t/// <summary>导出数据投影（26003 全字段；未采集字段导出为空字符串）。</summary>",
        "\tprivate List<ThaExportRow> ProjectExportData()",
        "\t{",
        "\t\treturn _detailRecords",
        "\t\t\t.Select(r => new ThaExportRow",
        "\t\t\t{",
        '\t\t\t\t产品码 = r.ProductCode ?? "",',
        "\t\t\t\t打码结果 = Tha26003.MarkResultText(r.MarkResult),",
        "\t\t\t\t读码结果 = Tha26003.ScanResultText(r.ReadResult),",
        "\t\t\t\t总结果 = Tha26003.ResultText(r.OverallResult),",
        "\t\t\t\t导通耐压结果 = Tha26003.ResultText(r.WithstandResult),",
        "\t\t\t\t针高结果 = Tha26003.ResultText(r.PinHeightResult),",
        "\t\t\t\t位置度结果 = Tha26003.ResultText(r.PositionResult),",
        '\t\t\t\t是否复检 = r.IsRetest switch { 1 => "是", 2 => "否", _ => "" },',
    ]
    for i in range(1, PIN_N + 1):
        L.append(f"\t\t\t\t上针高{i} = F(r.TopPinHeight{i}),")
    for i in range(1, PIN_N + 1):
        L.append(f"\t\t\t\t下针高{i} = F(r.BottomPinHeight{i}),")
    for i in range(1, POS_N + 1):
        L.append(f"\t\t\t\t上位置度X{i} = F(r.TopPinPosX{i}),")
        L.append(f"\t\t\t\t上位置度Y{i} = F(r.TopPinPosY{i}),")
    for i in range(1, POS_N + 1):
        L.append(f"\t\t\t\t下位置度X{i} = F(r.BottomPinPosX{i}),")
        L.append(f"\t\t\t\t下位置度Y{i} = F(r.BottomPinPosY{i}),")
    L += [
        '\t\t\t\t打码时间 = r.MarkTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",',
        '\t\t\t\t读码时间 = r.ScanTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",',
        '\t\t\t\t创建时间 = r.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),',
        '\t\t\t\t更新时间 = r.UpdateTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",',
        "\t\t\t})",
        "\t\t\t.ToList();",
        "\t}",
    ]
    return "\n".join(L)


def gen_export_class() -> str:
    W = "[MiniExcelLibs.Attributes.ExcelColumnWidth(180)]"
    L = ["\t/// <summary>导出行（列头/列宽由 MiniExcel 特性控制）。</summary>",
         "\tprivate sealed class ThaExportRow", "\t{"]

    def col(name: str, wide: bool = False):
        L.append(f'\t\t[MiniExcelLibs.Attributes.ExcelColumnName("{name}")]')
        if wide:
            L.append(f"\t\t{W}")
        L.append(f'\t\tpublic string {name} {{ get; set; }} = "";')

    col("产品码", wide=True)
    for n in ["打码结果", "读码结果", "总结果", "导通耐压结果", "针高结果", "位置度结果", "是否复检"]:
        col(n)
    for i in range(1, PIN_N + 1):
        col(f"上针高{i}")
    for i in range(1, PIN_N + 1):
        col(f"下针高{i}")
    for i in range(1, POS_N + 1):
        col(f"上位置度X{i}")
        col(f"上位置度Y{i}")
    for i in range(1, POS_N + 1):
        col(f"下位置度X{i}")
        col(f"下位置度Y{i}")
    for n in ["打码时间", "读码时间", "创建时间", "更新时间"]:
        col(n, wide=True)
    L.append("\t}")
    return "\n".join(L)


def replace_grid_columns(path: Path, cols, tag: str) -> None:
    """把文件里每处 '连续 DataGridTextColumn 行' 的列块整段替换成 cols。"""
    lines = path.read_text(encoding="utf-8").split("\n")
    # 只替换"明细表"这类目标：以 Time 绑定开头的连续列块
    n = 0
    i = 0
    while i < len(lines):
        if "<DataGridTextColumn" in lines[i] and 'Binding="{Binding Time}"' in lines[i]:
            indent = lines[i][: len(lines[i]) - len(lines[i].lstrip())]
            j = i
            while j + 1 < len(lines) and "<DataGridTextColumn" in lines[j + 1]:
                j += 1
            lines[i:j + 1] = cols_xaml(cols, indent).split("\n")
            n += 1
            i += 1
            continue
        i += 1
    path.write_text("\n".join(lines), encoding="utf-8")
    print(f"{path.name} 替换列块 {n} 处（{tag}）")


def main():
    p = ROOT / "App/UI/Pages/Main/ThaHistoryRow.cs"
    p.write_text(gen_history_row(), encoding="utf-8")
    print("wrote", p.name)

    replace_grid_columns(ROOT / "App/UI/Pages/Date/DatePage.axaml", COLS, "明细全字段")
    replace_grid_columns(ROOT / "App/UI/Pages/Main/MainPage.axaml", MAIN_COLS, "主页关键列")

    p = ROOT / "App/UI/Pages/Date/DateVM.cs"
    text = p.read_text(encoding="utf-8")
    a = text.find("\t/// <summary>导出数据投影")
    b = text.find("\t/// <summary>导出行")
    c = text.find("\tprivate static string ResultText(int v)")
    assert min(a, b, c) > 0, (a, b, c)
    p.write_text(text[:a] + gen_export_projection() + "\n\n" + gen_export_class() + "\n\n" + text[c:],
                 encoding="utf-8")
    print("DateVM.cs 导出块已替换")

    print(f"明细列数 = {len(COLS)}；主页列数 = {len(MAIN_COLS)}")


if __name__ == "__main__":
    main()
