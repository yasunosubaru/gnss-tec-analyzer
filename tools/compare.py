"""WinUI 3 版与参考 Python 版的输出对拍（逐字节）。

策略：
  1. 先用 C# 版探测哪些卫星真的有 L1+L2 观测（用输出行数判断）。
  2. 只对这些卫星逐字节对拍 —— 因为原始 PySide6 实现存在一个除零 bug：
     当某卫星没有任何有效观测时，compute_TEC 里 gap = [0, 0]，
     `ave[i] /= (gap[i+1] - gap[i])` 直接抛 ZeroDivisionError，
     文件根本不会写出。C# 版对此做了保护（输出只含表头的文件）。
     这类情形单独归类为 ORIG-BUG，不计入失败。

数据文件不进仓库（最小的也有 1.2 MB，大的 395 MB），需自行提供：

    python tools/compare.py --small test.bin --gps BFN18083110_....gps
    python tools/compare.py --gps xxx.gps --big Data_1Hz.bin

也可用环境变量 GNSS_TEC_SAMPLE_BIN / _GPS / _BIG，
或把文件放进仓库的 samples/ 目录（按扩展名与体积自动识别）。
缺哪个就跳过哪组测试，并在结尾汇总提示。
"""

import argparse
import hashlib
import os
import subprocess
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SAMPLES = os.path.join(ROOT, "samples")

EXE = os.path.join(
    ROOT, "src", "GnssTec.App", "bin", "x64", "Release",
    "net8.0-windows10.0.19041.0", "GnssTec.exe",
)
PYREF = os.path.join(HERE, "py_reference.py")

WORK = os.path.join(os.environ.get("TEMP", "."), "opencode", "gnss_compare")

ORIG_BUG = "float division by zero"

stats = {"pass": 0, "fail": 0, "origbug": 0}
failures = []
skipped = []


def pick(explicit, env_name, exts, min_mb=0):
    """定位一个数据文件：显式参数 > 环境变量 > samples/ 目录。"""
    if explicit:
        return os.path.abspath(explicit)
    env = os.environ.get(env_name)
    if env:
        return os.path.abspath(env)
    if os.path.isdir(SAMPLES):
        for f in sorted(os.listdir(SAMPLES)):
            p = os.path.join(SAMPLES, f)
            if not os.path.isfile(p):
                continue
            if os.path.splitext(f)[1].lower() in exts and \
               os.path.getsize(p) >= min_mb * 1048576:
                return p
    return None


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def run(tag, cmd, quiet=True):
    r = subprocess.run(cmd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=7200)
    if r.returncode != 0 and not quiet:
        print(f"    [{tag}] 退出码 {r.returncode}")
        for line in ((r.stdout or "") + (r.stderr or "")).strip().splitlines()[-6:]:
            print("        " + line[:160])
    return r


def clean(d):
    if os.path.isdir(d):
        for f in os.listdir(d):
            try:
                os.remove(os.path.join(d, f))
            except OSError:
                pass
    os.makedirs(d, exist_ok=True)
    return d


def snapshot(d):
    out = {}
    if not os.path.isdir(d):
        return out
    for name in sorted(os.listdir(d)):
        p = os.path.join(d, name)
        if os.path.isfile(p) and not name.startswith("_"):
            out[name] = (md5(p), os.path.getsize(p))
    return out


def record(label, ok, note=""):
    if ok:
        stats["pass"] += 1
        print(f"  [PASS] {label:40s} {note}")
    else:
        stats["fail"] += 1
        failures.append(label)
        print(f"  [FAIL] {label:40s} {note}")


def record_origbug(label, note=""):
    stats["origbug"] += 1
    print(f"  [ORIG-BUG] {label:36s} {note}")


def compare(label, args, allow_origbug=False):
    cs_dir = clean(os.path.join(WORK, "cs_" + label))
    py_dir = clean(os.path.join(WORK, "py_" + label))
    cs_args = [cs_dir if a == "-" else a for a in args]
    py_args = [py_dir if a == "-" else a for a in args]

    r_cs = run("C#", [EXE, "--selftest", *cs_args])
    r_py = run("PY", [sys.executable, PYREF, *py_args])

    cs, py = snapshot(cs_dir), snapshot(py_dir)

    # 原版除零 bug：Python 崩、C# 正常产出。
    py_bug = r_py.returncode != 0 and ORIG_BUG in (r_py.stdout + r_py.stderr)
    if allow_origbug and py_bug and r_cs.returncode == 0:
        msgs = []
        for name, (h, sz) in cs.items():
            msgs.append(f"{name}({sz}B)")
        record_origbug(label, "原版除零崩溃；C# 正常输出 " + ",".join(msgs))
        return True

    ok = r_cs.returncode == 0 and r_py.returncode == 0 and cs == py
    note = f"C#={len(cs)}文件 PY={len(py)}文件"
    record(label, ok, note)

    if not ok:
        if set(cs) != set(py):
            print(f"         仅C#={sorted(set(cs)-set(py))} 仅PY={sorted(set(py)-set(cs))}")
        for name in sorted(set(cs) & set(py)):
            if cs[name] != py[name]:
                cp, pp = os.path.join(cs_dir, name), os.path.join(py_dir, name)
                print(f"         内容不同: {name}  C#={cs[name][1]}B/{cs[name][0]}")
                print(f"                              PY={py[name][1]}B/{py[name][0]}")
                with open(cp, encoding="utf-8", errors="replace") as f1, \
                     open(pp, encoding="utf-8", errors="replace") as f2:
                    a, b = f1.readlines(), f2.readlines()
                for i in range(max(len(a), len(b))):
                    la = a[i].rstrip("\n") if i < len(a) else "<缺行>"
                    lb = b[i].rstrip("\n") if i < len(b) else "<缺行>"
                    if la != lb:
                        print(f"            第{i+1}行 C#: {la[:110]}")
                        print(f"            第{i+1}行 PY: {lb[:110]}")
                        break
    return ok


def probe_sats(input_path, sys_arg, lo, hi):
    """用 C# 版探出真的有观测的卫星号（只含表头的文件视为无数据）。"""
    found = []
    probe_root = os.path.join(WORK, "_probe")
    for sat in range(lo, hi + 1):
        d = os.path.join(probe_root, f"{sys_arg}_{sat}")
        clean(d)
        run("probe", [EXE, "--selftest", "1", input_path, d, sys_arg, str(sat)])
        name = ("GPS" if sys_arg == "GPS" else "BDS") + f"-{sat}.txt"
        f = os.path.join(d, name)
        if os.path.isfile(f):
            with open(f, encoding="utf-8", errors="replace") as fh:
                nlines = sum(1 for _ in fh)
            if nlines > 7:          # 表头 6 行 + 空行
                found.append((sat, nlines))
    return found


def main():
    os.makedirs(WORK, exist_ok=True)
    if not os.path.isfile(EXE):
        print("找不到 EXE，请先构建：", EXE)
        return 2

    ap = argparse.ArgumentParser(description="C# 版与参考 Python 版逐字节对拍")
    ap.add_argument("--small", help="小样本 .bin（提供则跑 BESTPOS 与批量导出）")
    ap.add_argument("--gps", help="样本 .gps（提供则跑全部三组模式）")
    ap.add_argument("--big", help="大数据集 .bin（提供则额外跑大数据集测试）")
    a = ap.parse_args()

    SMALL = pick(a.small, "GNSS_TEC_SAMPLE_BIN", {".bin"}, min_mb=0)
    GPS1 = pick(a.gps, "GNSS_TEC_SAMPLE_GPS", {".gps"}, min_mb=0)
    BIG = pick(a.big, "GNSS_TEC_SAMPLE_BIG", {".bin"}, min_mb=100)

    for name, p in (("small(.bin)", SMALL), ("gps(.gps)", GPS1), ("big(.bin)", BIG)):
        if p:
            print(f"数据 {name:12s} = {p}  ({os.path.getsize(p)/1048576.0:.1f} MB)")
        else:
            print(f"数据 {name:12s} = (未提供，对应测试将跳过)")
            skipped.append(name)
    print()

    if SMALL is None and GPS1 is None:
        print("没有任何数据文件，无法对拍。请用 --small/--gps 指定，")
        print("或设置环境变量 GNSS_TEC_SAMPLE_BIN / GNSS_TEC_SAMPLE_GPS，")
        print("或把文件放进仓库的 samples/ 目录。")
        return 2

    print("=" * 78)
    print("模式 0：BESTPOS 平均位置")
    print("=" * 78)
    for label, path in (("m0-test.bin", SMALL), ("m0-1.2MB.gps", GPS1)):
        if path and os.path.isfile(path):
            compare(label, ["0", path, "-"])

    print()
    print("=" * 78)
    print("模式 1：TEC 分析（只对真有观测的卫星对拍）")
    print("=" * 78)
    targets = []
    if SMALL and os.path.isfile(SMALL):
        targets.append(("test.bin", SMALL, 1, 40))
    if GPS1 and os.path.isfile(GPS1):
        targets.append(("1.2MB.gps", GPS1, 1, 40))

    for tag, path, lo, hi in targets:
        sats = probe_sats(path, "GPS", lo, hi)
        print(f"  {tag}: 探到 {len(sats)} 颗有观测的卫星 -> "
              f"{[s for s, _ in sats]}")
        for sat, nlines in sats:
            compare(f"m1-{tag}-GPS-{sat}", ["1", path, "-", "GPS", str(sat)],
                    allow_origbug=True)
        # 再抽查两颗无观测的，确认是原版 bug 而非移植差异
        empties = [s for s in range(lo, hi + 1) if s not in [x for x, _ in sats]][:2]
        for sat in empties:
            compare(f"m1-{tag}-GPS-{sat}(空)", ["1", path, "-", "GPS", str(sat)],
                    allow_origbug=True)

    print()
    print("=" * 78)
    print("模式 2：批量导出")
    print("=" * 78)
    if SMALL and os.path.isfile(SMALL):
        compare("m2-test-1-32", ["2", SMALL, "-", "GPS", "1", "1", "32"])
        compare("m2-test-1-8", ["2", SMALL, "-", "GPS", "1", "1", "8"])
    if GPS1 and os.path.isfile(GPS1):
        compare("m2-gps-1-8", ["2", GPS1, "-", "GPS", "1", "1", "8"])
        compare("m2-gps-20-24", ["2", GPS1, "-", "GPS", "1", "20", "24"])

    if BIG and os.path.isfile(BIG):
        print()
        print("=" * 78)
        print("大数据集：Data_1Hz.bin（395 MB）")
        print("=" * 78)
        compare("m0-Data1Hz", ["0", BIG, "-"])
        sats = probe_sats(BIG, "GPS", 1, 20)
        print(f"  Data_1Hz.bin: 探到 {len(sats)} 颗有观测的卫星 -> "
              f"{[(s, l) for s, l in sats]}")
        for sat, nlines in sats:
            compare(f"m1-Data1Hz-GPS-{sat}", ["1", BIG, "-", "GPS", str(sat)],
                    allow_origbug=True)
        compare("m2-Data1Hz-1-4", ["2", BIG, "-", "GPS", "1", "1", "4"])

    print()
    print("=" * 78)
    total = stats["pass"] + stats["fail"]
    print(f"对拍 {total} 项：PASS {stats['pass']}，FAIL {stats['fail']}")
    print(f"另记录原版除零 bug 场景 {stats['origbug']} 项（C# 已修复，不计失败）")
    if skipped:
        print("跳过的数据组：", "、".join(skipped))
    if failures:
        print("失败项：", failures)
    if total == 0:
        print("注意：没有任何对拍项被执行")
    print("=" * 78)
    return 0 if stats["fail"] == 0 and total > 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
