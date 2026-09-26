"""用原始 PySide6 实现做参考输出，供与 WinUI 3 版对拍。

**原始 PySide6 实现（gnss_gui.py）不随本仓库分发** —— 那是课程作业代码，
著作权归原作者。因此本脚本需要你自行提供该文件的位置：

    python tools/py_reference.py <mode> <input> <outDir> [sys] [satId] [start] [end]

原版路径按以下顺序查找：

  1) 环境变量 ``GNSS_TEC_ORIG_PYSIDE6`` 指向 gnss_gui.py
  2) 命令行 ``--gui <path>``（须放在位置参数之前）
  3) 仓库内的 ``reference/gnss_gui.py``（若你自行放入）
  4) 仓库上一级目录的 ``pyside6/gnss_gui.py``（本机开发布局的兼容）

找不到时会明确报错并返回 2，而不是静默跳过 —— 静默跳过会让对拍假通过。

依赖：PySide6（仅本脚本需要，应用本体无 Python 依赖）
    pip install PySide6
"""

import argparse
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)            # 仓库根
CANDIDATES = [
    os.path.join(ROOT, "reference", "gnss_gui.py"),
    os.path.join(os.path.dirname(ROOT), "pyside6", "gnss_gui.py"),
]


def find_gui(explicit=None):
    if explicit:
        return os.path.abspath(explicit) if os.path.isfile(explicit) else None
    env = os.environ.get("GNSS_TEC_ORIG_PYSIDE6")
    if env and os.path.isfile(env):
        return os.path.abspath(env)
    for p in CANDIDATES:
        if os.path.isfile(p):
            return p
    return None


def main() -> int:
    ap = argparse.ArgumentParser(description="用原始 PySide6 实现生成参考输出")
    ap.add_argument("--gui", help="原始 gnss_gui.py 的路径")
    ap.add_argument("rest", nargs="*",
                    help="mode input outDir [sys] [satId] [start] [end]")
    a = ap.parse_args()

    argv = a.rest
    if len(argv) < 3:
        print(__doc__)
        return 2

    GUI = find_gui(a.gui)
    if not GUI:
        print("找不到原始 PySide6 实现 gnss_gui.py。")
        print("已查找：")
        print("  环境变量 GNSS_TEC_ORIG_PYSIDE6 = (未设置)")
        for p in CANDIDATES:
            print("  " + p + ("  <- 存在" if os.path.isfile(p) else ""))
        print("")
        print("请用 --gui <path> 指定，或设置环境变量 GNSS_TEC_ORIG_PYSIDE6。")
        print("该文件不随本仓库分发（课程作业代码，著作权归原作者）。")
        return 2

    mode = int(argv[0])
    input_path = argv[1]
    out_dir = argv[2]
    sys_arg = argv[3] if len(argv) > 3 else "GPS"
    sat_id = int(argv[4]) if len(argv) > 4 else 1
    start = int(argv[5]) if len(argv) > 5 else 1
    end = int(argv[6]) if len(argv) > 6 else 32

    os.makedirs(out_dir, exist_ok=True)

    # QThread/信号需要 QCoreApplication 存在；不建窗口。
    from PySide6.QtCore import QCoreApplication
    _app = QCoreApplication(sys.argv)

    spec = importlib.util.spec_from_file_location("gnss_gui_ref", GUI)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    sys_type = mod.Sys.Beidou if sys_arg.upper() in ("BDS", "BEIDOU") else mod.Sys.GPS

    thread = mod.ProcessThread(input_path, out_dir, mode, sys_type, sat_id, start, end)

    logs = []
    thread.log.connect(lambda s: logs.append(s))
    errors = []
    thread.error.connect(lambda s: errors.append(s))
    thread.progress.connect(lambda p, s: None)

    # 同步执行，不启动事件循环。
    thread.run()

    log_path = os.path.join(out_dir, "_selftest.log")
    with open(log_path, "w", encoding="utf-8", newline="") as fh:
        fh.write("# py-reference ok mode=%d sys=%s\n" % (mode, sys_arg))
        for line in logs:
            fh.write(line + "\n")
        for e in errors:
            fh.write("[错误] " + str(e) + "\n")

    print("py-reference 完成: mode=%d sys=%s outDir=%s (参考实现: %s)"
          % (mode, sys_arg, out_dir, GUI))
    if errors:
        print("有错误:", errors[:3])
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
