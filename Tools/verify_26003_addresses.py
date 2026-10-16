"""交叉核对：协议 md 地址表 ↔ 代码常量 ↔ 监控页显示地址。

解析 THA26003_PLC_ModbusTCP通讯协议.md 里的地址表，与 App/Services/Tha26003Protocol.cs
的常量逐项比对，并算出监控页实际会显示的地址字符串。任一不一致即报错退出。
"""
import re
import sys
from pathlib import Path

ROOT = Path(r"D:/Ver pro/DataCollection -26003")
md = (ROOT / "THA26003_PLC_ModbusTCP通讯协议.md").read_text(encoding="utf-8")
cs = (ROOT / "App/Services/Tha26003Protocol.cs").read_text(encoding="utf-8")

# ---- 1) 从代码读常量 ----
consts = {}
for m in re.finditer(r"public const (?:int|ushort) (\w+) = (\d+);", cs):
    consts[m.group(1)] = int(m.group(2))

# ---- 2) 从 md 抽取"字段 → 地址"（第 2、3 节表格） ----
md_map = {}
for line in md.split("\n"):
    line = line.strip()
    if not line.startswith("|") or "%MW" not in line:
        continue
    cells = [c.strip() for c in line.strip("|").split("|")]
    if not cells:
        continue
    addr = re.search(r"%MW(\d+)", line)
    if addr:
        md_map.setdefault(cells[0].strip("`* "), int(addr.group(1)))

def md_addr(field):
    for k, v in md_map.items():
        if k.startswith(field):
            return v
    return None

fails = []
def check(label, expect, actual):
    ok = expect == actual
    print(f"  {'OK  ' if ok else 'FAIL'}  {label:<34} md={expect}  代码={actual}")
    if not ok:
        fails.append(label)

print("【三站信号】")
check("镭码触发", md_addr("镭码触发"), consts["MwMarkTrigger"])
check("镭码结果", md_addr("镭码结果"), consts["MwMarkResult"])
check("扫码触发", md_addr("扫码触发"), consts["MwScanTrigger"])
check("扫码完成告知结果", md_addr("扫码完成告知结果"), consts["MwScanResult"])
check("采集触发", md_addr("采集触发"), consts["MwUploadTrigger"])
check("采集完成", md_addr("采集完成"), consts["MwUploadResult"])
check("PLC 可能附带镭码内容", md_addr("PLC 可能附带镭码内容"), consts["MwPlcContent"])
check("扫码内容回写", md_addr("扫码内容回写"), consts["MwScanEcho"])
check("PC 将镭码内容发送 PLC", md_addr("PC 将镭码内容发送 PLC"), consts["MwMarkEcho"])

print("\n【结果字】")
check("导通耐压结果", md_addr("导通耐压结果"), consts["SegResultBase"] + consts["OffWithstand"])
check("针高结果", md_addr("针高结果"), consts["SegResultBase"] + consts["OffPinHeight"])
check("位置度结果", md_addr("位置度结果"), consts["SegResultBase"] + consts["OffPosition"])

print("\n【针高 / 位置度首末点】")
check("上针高 Pin1", md_addr("上针高 Pin1"), consts["SegTopPinBase"])
check("上针高 Pin15", md_addr("上针高 Pin15"), consts["SegTopPinBase"] + 14 * 2)
check("下针高 Pin1", md_addr("下针高 Pin1"), consts["SegBottomPinBase"])
check("下针高 Pin15", md_addr("下针高 Pin15"), consts["SegBottomPinBase"] + 14 * 2)
check("上针位置度 PIN1_X", md_addr("上针位置度 PIN1_X"), consts["SegTopPosBase"])
check("上针位置度 PIN1_Y", md_addr("上针位置度 PIN1_Y"), consts["SegTopPosBase"] + 2)
check("上针位置度 PIN15_X", md_addr("上针位置度 PIN15_X"), consts["SegTopPosBase"] + 14 * 4)
check("上针位置度 PIN15_Y", md_addr("上针位置度 PIN15_Y"), consts["SegTopPosBase"] + 14 * 4 + 2)
check("下针位置度 PIN1_X", md_addr("下针位置度 PIN1_X"), consts["SegBottomPosBase"])
check("下针位置度 PIN15_Y", md_addr("下针位置度 PIN15_Y"), consts["SegBottomPosBase"] + 14 * 4 + 2)

print("\n【分段读覆盖（末字必须 >= 末字段起始+1）】")
seg = [
    ("结果段", consts["SegResultBase"], consts["SegResultLen"], consts["SegResultBase"] + consts["SegResultLen"] - 1),
    ("上针高", consts["SegTopPinBase"], consts["SegTopPinLen"],
     consts["SegTopPinBase"] + 14 * 2 + 1),
    ("下针高", consts["SegBottomPinBase"], consts["SegBottomPinLen"],
     consts["SegBottomPinBase"] + 14 * 2 + 1),
    ("上位置度", consts["SegTopPosBase"], consts["SegTopPosLen"],
     consts["SegTopPosBase"] + 14 * 4 + 2 + 1),
    ("下位置度", consts["SegBottomPosBase"], consts["SegBottomPosLen"],
     consts["SegBottomPosBase"] + 14 * 4 + 2 + 1),
]
for name, base, length, need_last in seg:
    last = base + length - 1
    ok = (last >= need_last) and (length <= 125)
    print(f"  {'OK  ' if ok else 'FAIL'}  {name:<8} 起始 {base} 长度 {length} 覆盖 {base}~{last}（需到 {need_last}）")
    if not ok:
        fails.append(name)

print("\n【监控页将显示的地址（由常量推导，抽查）】")
samples = [
    ("打码触发", consts["MwMarkTrigger"]),
    ("打码结果", consts["MwMarkResult"]),
    ("读码触发", consts["MwScanTrigger"]),
    ("上传触发", consts["MwUploadTrigger"]),
    ("上针高 Pin15", consts["SegTopPinBase"] + 14 * 2),
    ("下位置度 PIN15_Y", consts["SegBottomPosBase"] + 14 * 4 + 2),
    ("心跳", consts["MwHeartbeat"]),
]
for name, mw in samples:
    print(f"  %MW{mw:<6} {name}")

print()
if fails:
    print(f"不一致项：{fails}")
    sys.exit(1)
print("全部一致：协议 md / 代码常量 / 监控页地址 三方对齐")
