# GNSS 电离层 TEC 分析工具

[![build](https://github.com/yasunosubaru/gnss-tec-analyzer/actions/workflows/build.yml/badge.svg)](https://github.com/yasunosubaru/gnss-tec-analyzer/actions/workflows/build.yml)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![platform](https://img.shields.io/badge/platform-Windows%2010%2019041%2B-0078D4?logo=windows)
![framework](https://img.shields.io/badge/WinUI%203-.NET%208-512BD4?logo=dotnet)

解析 BFN 接收机的二进制观测流（`.bin` / `.gps`），计算**电离层总电子含量（TEC）**、
平均定位坐标，并按卫星批量导出原始观测。

用 **WinUI 3 (C# / .NET 8)** 重写自一份 PySide6 课程作业程序，
输出已用**逐字节对拍**验证与原版一致。

---

## 快速开始

**下载**：Releases 页取 `GnssTec-<版本>.zip`，解压后运行 `GnssTec.exe`。
无需安装 .NET 运行时（Windows App SDK 随包分发）。

**数据**：本仓库**不含**任何真实观测数据（最小的也有 1.2 MB，大的 395 MB，
且属监测网数据不宜再分发）。请自行准备 BFN 格式文件，用「浏览」选择，
或拖到 exe 上。

```powershell
# 启动后选文件、选模式、点「开始处理」

# 无界面模式（便于脚本化）
GnssTec.exe --selftest <mode> <input> <outDir> [sys] [satId] [start] [end]
#   mode : 0 = BESTPOS 平均位置
#          1 = 指定卫星的 TEC 分析
#          2 = 按卫星 ID 范围批量导出原始观测
#   sys  : GPS | BDS
```

> 姊妹项目：[bfn-signal-analyzer](https://github.com/yasunosubaru/bfn-signal-analyzer)
> （C/NO 载噪比信号分析仪，源自同课程另一份作业）。

### 从源码构建

需要 Windows 10 19041+、.NET 8 SDK、Visual Studio 2022 或 Build Tools
（含「使用 C++ 的桌面开发」工作负载，WinUI 3 需要 MSIX 工具链）。

```powershell
git clone https://github.com/yasunosubaru/gnss-tec-analyzer.git
cd gnss-tec-analyzer
python tools\add_bom.py          # 必须先跑，见 §5
dotnet build src\GnssTec.App -c Release -p:Platform=x64
dotnet publish src\GnssTec.App -c Release -p:Platform=x64 `
  -r win-x64 --self-contained false -o dist\GnssTec
```

> 目标框架固定为 `net8.0-windows10.0.19041.0`，且必须指定 `-p:Platform=x64`。

---

## 1. 功能

解析 BFN 接收机的二进制观测流（`.bin` / `.gps`），提供三种模式：

| 模式 | 功能 | 输出 |
|---|---|---|
| **0** | 计算 BESTPOS 平均位置（经度 / 纬度 / 高度） | `bestpos_avg.txt` |
| **1** | 指定卫星的 TEC 分析（电离层总电子含量） | `{系统}-{卫星号}.txt` |
| **2** | 按卫星 ID 范围批量导出原始观测（不含 TEC） | `{系统}-{卫星号}.txt` × N |

支持 GPS 与北斗（BDS）两个系统。

### 二进制格式

| 项 | 说明 |
|---|---|
| 同步字 | `AA 44 12 1C`（4 字节） |
| 记录头 | 28 字节：ID `u16@4`、length `u16@8`、week `u16@14`、ms `u32@16` |
| ID 42 BESTPOS | 载荷 76 字节，lat/lon/hgt 为 `f64@8 / @16 / @24` |
| ID 43 RANGE | 前 4 字节为观测条数，其后每条 44 字节：slot `u16@0`、psr `f64@4`、adr `f64@16`、status `u32@40` |
| ID 1043 SATVIS2 | 首字节为系统（`>3` 减 2、`>6` 再减 1 校正），sat_count `u32@12`；每条 40 字节：ID `u16@0`、el `f64@8`、az `f64@16` |

`status` 位域：系统 `(status & 0x00070000) >> 16`、grouped `>> 20`、信号码 `(status & 0x03E00000) >> 21`。

### TEC 计算

```
GPS : TECA = 9.5196 × (psr2 − psr1)      TECR = 279.17 × (adr2/120 − adr1/154)
BDS : TECA = 8.9932 × (psr2 − psr1)      TECR = 263.73 × (adr2/118 − adr1/152.6)
deltaT = TECA − TECR
TEC   = TECR + 分段平均(deltaT)          # 按 >100 s 间隔分段
```

时间换算：GPS 周 + 周内毫秒 → UTC，基准 1980-01-06（GPS 真实历元）+ 18 秒闰秒。

---

## 2. 构建与运行

```powershell
python tools\add_bom.py                                       # 必须先跑，见 §5
dotnet build src\GnssTec.App\GnssTec.App.csproj -c Release -p:Platform=x64
```

产物：`src\GnssTec.App\bin\x64\Release\net8.0-windows10.0.19041.0\GnssTec.exe`

**前置条件**：.NET 8 SDK、Windows 10 19041+。`nuget.config` 默认走 nuget.org；
若网络受限可按文件内注释启用镜像。

启动异常会写入 exe 同目录的 `startup-error.log`（WinExe 没有控制台）。

---

## 3. 界面

顶部为输入/输出选择与参数区（模式、星座、卫星 ID 或 ID 范围），
中部为「开始处理 / 停止 / 打开输出目录」与进度显示，
下部为只读日志框，深色底浅色字在任何系统主题下都可读。

---

## 4. 验证

### 4.1 与原始 PySide6 实现逐字节对拍

`tools/compare.py` 会先用 C# 版探出真有观测的卫星，再对每个卫星跑两边并比对 MD5。

**需要自行提供两样东西**（都不随本仓库分发）：

| 需要的东西 | 怎么指定 |
|---|---|
| BFN 数据文件 | `--small` / `--gps` / `--big`，或环境变量 `GNSS_TEC_SAMPLE_BIN` / `_GPS` / `_BIG`，或放进 `samples/` |
| 原始 `gnss_gui.py` | `--gui <path>`，或环境变量 `GNSS_TEC_ORIG_PYSIDE6` |

```powershell
python tools\compare.py --gps .\BFN18083110_....gps `
    --gui ..\pyside6\gnss_gui.py
python tools\compare.py --gps .\xxx.gps --big .\Data_1Hz.bin   # 追加 395 MB 数据集
```

缺少数据或参考实现时脚本会**明确报错并返回 2**，不会静默跳过 ——
静默跳过会让对拍假通过。

**本机实测结果：43 项 PASS / 0 FAIL**（含 `--big`），另记录 4 项原版除零 bug 场景。

| 测试集 | 覆盖 | 结果 |
|---|---|---|
| 模式 0 | `test.bin`、1.2 MB `.gps`、`Data_1Hz.bin` | 全部逐字节一致 |
| 模式 1 · GPS | `test.bin` 11 颗、`.gps` 4 颗、`Data_1Hz.bin` **20 颗** | 全部逐字节一致 |
| 模式 1 · BDS | `test.bin` 12 颗 | 11 PASS / 0 FAIL（1 项原版 bug） |
| 模式 2 | 批量导出 1–32、1–8、20–24 | 全部逐字节一致 |

`Data_1Hz.bin` 单颗卫星输出 **13–17 万行** TEC 数据，20 颗合计约 300 万行，全部一致。

### 4.2 GUI 端到端测试

`tools/gui_e2e.ps1` 用 UI Automation **真实驱动界面**（填控件 → 点「开始处理」→ 读日志），
走的是与用户点击完全相同的代码路径。

```powershell
powershell -File tools\gui_e2e.ps1 -InputFile <f> -OutDir <d> -Mode 1 -SatId 5
```

**结果：模式 0 / 1 / 2 的 GUI 产物与 Python 参考输出逐字节一致。**
界面日志正确显示完整流程与数据摘要（起止时间、平均 TEC）。

---

## 5. 本机 WinUI 3 踩坑记录

以下都是实测踩过的，且报错信息**完全没有指向真实原因**，记录备查。

| 现象 | 原因 | 处理 |
|---|---|---|
| 编译报无提示的「错误: 1」 | Windows App SDK 1.8 的 XamlCompiler 不支持 `net10.0` TFM | TFM 固定为 `net8.0-windows10.0.19041.0` |
| `XamlParseException: Failed to assign to property '...ToggleButton.IsChecked'` | 本机运行时无法在 XAML 里给 `IsChecked` 赋值（与 `IsVisible`、`Cursor` 同类） | 默认选中改在代码里 `Mode0Radio.IsChecked = true` |
| csc 报 `CS1056`「意外字符」，位置指向无关的 `{` | 无 BOM 时 csc 按 GBK 解码，中文注释字节被误判 | **所有 `.cs`/`.xaml`/`.csproj` 必须带 UTF-8 BOM**，用 `tools/add_bom.py` |
| 窗口底部内容不可见 | 直接 `Resize(1040,900)`，而本机工作区仅 1440×863，窗口超出屏幕 63 px | 按 `DisplayArea.WorkArea` 自适应尺寸并居中 |
| 标题栏图标不显示 | `AppWindow.SetIcon` 传相对路径时按 CWD 查找，找不到**静默失败** | 传绝对路径 `Path.Combine(AppContext.BaseDirectory, ...)` |
| exe 启动即报「应用程序无法启动」 | unpackaged 应用缺少引导器入口点 | `<WindowsAppSdkBootstrapInitialize>true</WindowsAppSdkBootstrapInitialize>` |
| `dotnet publish` 后窗口图标丢失 | 只写 `<Content Include>` 时资源不进 publish 输出 | 同时写 `CopyToOutputDirectory` 与 `CopyToPublishDirectory` |
| `.ps1` 里中文报解析错误 | PowerShell 5.1 无 BOM 时按 GBK 读脚本 | 脚本也要加 BOM |
| NumberBox 通过 UIA 设值无效 | 用内部 Edit 的 `ValuePattern` 只改显示文本，不改 `Value` | 用 `RangeValuePattern.SetValue`，并先确认控件已启用 |

---

## 6. 相对原版的改进与偏差

### 修复的 bug

**原版 `compute_TEC` 在无有效观测时除零崩溃。** 当某卫星没有任何 L1+L2 观测时，
`gap = [0, 0]`，`ave[i] /= (gap[i+1] - gap[i])` 抛 `ZeroDivisionError`，
文件根本不会写出。本版加了零长度保护，此时输出只含表头的文件。

对拍中触发该 bug 的场景共 6 项（GPS 4 + BDS 2），归类为 `ORIG-BUG` 而非失败。

### 行为一致的部分

字段顺序、小数位数（6 位）、空行位置、分段边界、分段平均算法、时间换算、
信号码映射表、`obs×44+8 != length` 的回退分支 —— 均与原版逐字节一致。

### 原版存在但本版未复现的问题（仅记录，未改）

原始作业目录里另有两个 C++ 侧程序：一个把输出目录硬编码成 `D:\GNSS_Output\`
（在任何正常机器上都不存在），另一个把 `.gps` 文件名写死在源码数组里。
这两者属于 C++ 侧遗留，与本次重写无关，故未复现。

---

## 7. 目录结构

```
gnss-tec-analyzer/
├── README.md
├── LICENSE                          MIT
├── nuget.config                     包源（默认 nuget.org，镜像按注释启用）
├── .github\workflows\build.yml      CI：构建 + 冒烟测试 + 打包 + 发 Release
├── tools\
│   ├── add_bom.py                   给源文件补 UTF-8 BOM（构建前必跑，见 §5）
│   ├── py_reference.py              调原始 PySide6 实现做参考输出（需自行提供原版）
│   ├── compare.py                   逐字节对拍（数据与原版均需自行提供）
│   └── gui_e2e.ps1                  UI Automation 端到端 GUI 测试
├── dist\GnssTec\                    发布产物（不入库）
└── src\GnssTec.App\
    ├── GnssTec.App.csproj
    ├── app.manifest                 DPI / UTF-8 代码页 / longPathAware
    ├── App.xaml(.cs)                入口 + --selftest + 崩溃日志
    ├── MainWindow.xaml(.cs)         界面
    ├── SelfTest.cs                  无界面自检
    ├── Assets\                      GnssTec.ico + 各尺寸 PNG
    └── Core\                        不依赖 UI，可单独复用
        ├── Models.cs                枚举与数据结构
        ├── GnssParser.cs            二进制解析（同步字/记录头/RANGE/SATVIS2/时间换算）
        ├── TecCalculator.cs         TEC 计算与分段
        └── GnssProcessor.cs         三种模式 + 输出文件
```

核心库 `Core/` 不依赖任何 UI，可单独复用。

---

## 致谢与许可

本仓库的**代码**以 [MIT](LICENSE) 许可发布。

`.bin` / `.gps` 是 BFN（北斗卫星导航系统质量监测网）接收机的私有二进制格式，
本仓库**不含**任何真实观测数据，也**不合成**替代样本 —— TEC 需要成对的
L1+L2 伪距与载波相位，且 `Adr` 字段语义未经独立确认，造一个量级可能失真的
假样本比没有样本更容易误导人。CI 因此只做「缺数据时应明确报错」的冒烟测试，
逐字节对拍需自行提供真实数据后运行 `tools/compare.py`。

原始 PySide6 作业代码的著作权归原作者所有，本仓库未包含、未修改、
未重新分发它，仅在此说明本项目的来源关系。若你也是相关作业的作者并希望
调整措辞或补充致谢，请开 issue。

数据来源：BFN 监测网（公开 GNSS 质量监测网络）。
