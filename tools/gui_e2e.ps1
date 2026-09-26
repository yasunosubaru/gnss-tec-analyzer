# 端到端 GUI 测试：用 UI Automation 真实驱动 WinUI 3 界面跑一遍处理流程。
# 走的路径与"用户点按钮"完全一致：填控件 -> 点开始处理 -> 读日志/产物。
#
# 用法：powershell -File tools\gui_e2e.ps1 -InputFile <f> -OutDir <d> -Mode <0|1|2> [-SatId 5] [-Sys GPS|BDS]

param(
    [Parameter(Mandatory=$true)][string]$InputFile,
    [Parameter(Mandatory=$true)][string]$OutDir,
    [Parameter(Mandatory=$true)][int]$Mode,
    [int]$SatId = 5,
    [int]$IdStart = 1,
    [int]$IdEnd = 8,
    [string]$Sys = "GPS"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing

# 用于 PrintWindow / SetForegroundWindow 的 P/Invoke
if (-not ("NativeMethods" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class NativeMethods {
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);
}
"@
}

$Exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\src\GnssTec.App\bin\x64\Release\net8.0-windows10.0.19041.0\GnssTec.exe"))
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

Write-Host "启动: $Exe"
$proc = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 9

$root = $AE::FromHandle($proc.MainWindowHandle)
if (-not $root) { Write-Error "取不到主窗口"; exit 1 }
Write-Host "主窗口: $($root.Current.Name)"

function Find-ById([string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    return $root.FindFirst($TS::Descendants, $cond)
}

function Set-Text([string]$id, [string]$value) {
    $e = Find-ById $id
    if (-not $e) { throw "找不到控件 $id" }
    $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Write-Host "  设置 $id = $value"
}

function Set-Number([string]$id, [double]$value) {
    $e = Find-ById $id
    if (-not $e) { throw "找不到控件 $id" }
    if (-not $e.Current.IsEnabled) {
        # 控件可能因模式切换尚未启用，短暂等待
        for ($i = 0; $i -lt 20; $i++) {
            Start-Sleep -Milliseconds 200
            if ($e.Current.IsEnabled) { break }
        }
    }
    if (-not $e.Current.IsEnabled) { throw "控件 $id 仍处于禁用状态" }
    $rv = $e.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $rv.SetValue($value)
    Start-Sleep -Milliseconds 250
    Write-Host "  设置 $id = $value (回读 $($rv.Current.Value))"
}

function Invoke-ById([string]$id) {
    $e = Find-ById $id
    if (-not $e) { throw "找不到控件 $id" }
    $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "  点击 $id"
}

function Get-Log {
    try {
        $e = Find-ById "LogBox"
        return $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    } catch { return "" }
}

# --- 填输入/输出 ---
Set-Text "InputPathBox" $InputFile
Set-Text "OutputDirBox" $OutDir

# --- 先选模式，控件启用状态依赖它 ---
$radioId = "Mode${Mode}Radio"
$r = Find-ById $radioId
if (-not $r) { throw "找不到 $radioId" }
$r.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Write-Host "  选中模式 $Mode"
Start-Sleep -Milliseconds 1200

# --- 卫星系统 ---
if ($Mode -ge 1) {
    $cb = Find-ById "SysCombo"
    $want = if ($Sys -eq "BDS") { "BeiDou (BDS)" } else { "GPS" }
    $items = $cb.FindAll($TS::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)))
    foreach ($it in $items) {
        if ($it.Current.Name -eq $want) {
            $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Write-Host "  选中系统 $want"
            break
        }
    }
    Start-Sleep -Milliseconds 400
}

# --- 卫星参数 ---
if ($Mode -eq 1) { Set-Number "SatIdBox" $SatId }
if ($Mode -eq 2) { Set-Number "IdStartBox" $IdStart; Set-Number "IdEndBox" $IdEnd }

# --- 清空日志以便判断新输出 ---
$before = Get-Log

# --- 开始处理 ---
Invoke-ById "RunButton"

# --- 等待完成 ---
$deadline = (Get-Date).AddMinutes(15)
$done = $false; $err = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 800
    $txt = Get-Log
    if ($txt -match "处理完成。") { $done = $true; break }
    if ($txt -match "\[错误\]") { $err = $true; break }
}

$final = Get-Log
Write-Host "`n--- 界面输出日志 ---"
Write-Host $final

if ($err) { Write-Error "界面报错"; exit 2 }
if (-not $done) { Write-Error "超时未检测到完成"; exit 3 }

# --- 截图（PrintWindow：不受其它窗口遮挡影响） ---
$br = $root.Current.BoundingRectangle
$w = [int]$br.Width; $h = [int]$br.Height
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$PW_RENDERFULLCONTENT = 0x00000002
$ok = [NativeMethods]::PrintWindow($proc.MainWindowHandle, $hdc, $PW_RENDERFULLCONTENT)
$g.ReleaseHdc($hdc)
if (-not $ok) {
    # 回退：激活窗口后整屏抓取
    Write-Host "  PrintWindow 失败，改用前置抓屏"
    $g2 = [System.Drawing.Graphics]::FromImage($bmp)
    [NativeMethods]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 900
    $br = $root.Current.BoundingRectangle
    $g2.CopyFromScreen([int]$br.X, [int]$br.Y, 0, 0, $bmp.Size)
    $g2.Dispose()
}
$shot = Join-Path $OutDir "_gui_screenshot.png"
$bmp.Save($shot); $g.Dispose(); $bmp.Dispose()
Write-Host "`n截图: $shot"

Start-Sleep -Milliseconds 500
if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2 }
if (-not $proc.HasExited) { $proc.Kill() }
Write-Host "GUI 端到端测试完成"
exit 0
