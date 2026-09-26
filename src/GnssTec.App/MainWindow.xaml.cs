using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GnssTec.App.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GnssTec.App;

public sealed partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private bool _running;

    /// <summary>把后台线程的日志/进度搬回 UI 线程的 sink。</summary>
    private sealed class UiSink : IProgressSink
    {
        private readonly MainWindow _owner;
        public UiSink(MainWindow owner) => _owner = owner;

        public void Log(string message) =>
            _owner.DispatcherQueue.TryEnqueue(() => _owner.AppendLog(message));

        public void Progress(int percent, string message) =>
            _owner.DispatcherQueue.TryEnqueue(() => _owner.SetProgress(percent, message));
    }

    public MainWindow()
    {
        InitializeComponent();

        // 本机 WinUI 运行时无法在 XAML 里给 ToggleButton.IsChecked 赋值
        // （XamlParseException: Failed to assign to property ... IsChecked），
        // 与 IsVisible / Cursor 属同一类问题。默认选中改在代码里设置。
        Mode0Radio.IsChecked = true;

        // Mica 背景，与系统主题一致。
        try { SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); }
        catch { /* 旧系统不支持则退回默认背景 */ }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // 按显示器工作区自适应尺寸并居中。
        // 本机工作区仅 1440×863，若直接 Resize(1040,900)，窗口底部会落到
        // 屏幕外 63px，日志区整块不可见。工作区已扣除任务栏，可安全贴合。
        var wa = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        int winW = Math.Min(1100, Math.Max(860, (int)(wa.Width * 0.74)));
        int winH = Math.Min(940, Math.Max(620, (int)(wa.Height * 0.94)));
        appWindow.Resize(new Windows.Graphics.SizeInt32(winW, winH));
        appWindow.Move(new Windows.Graphics.PointInt32(
            wa.X + Math.Max(0, (wa.Width - winW) / 2),
            wa.Y + Math.Max(0, (wa.Height - winH) / 2)));
        appWindow.Title = "GNSS 数据分析工具 · 电离层 TEC";

        // 标题栏图标必须用绝对路径：给相对路径时 SetIcon 按 CWD 去找，
        // 从资源管理器或快捷方式启动时 CWD 各不相同，找不到就静默失败。
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "GnssTec.ico");
        if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);

        AppendLog("GNSS 数据分析工具已就绪。");
        AppendLog("支持 BFN 二进制观测数据（记录 ID 42=BESTPOS / 43=RANGE / 1043=SATVIS2）。");
        AppendLog("");
        UpdateParamStates();
    }

    // ------------------------------------------------------------------
    // 界面辅助
    // ------------------------------------------------------------------
    internal void AppendLog(string message)
    {
        LogBox.Text += message + Environment.NewLine;
        LogBox.SelectionStart = LogBox.Text.Length;
        LogBox.SelectionLength = 0;
    }

    internal void SetProgress(int percent, string message)
    {
        Progress.Value = Math.Clamp(percent, 0, 100);
        ProgressText.Text = message;
    }

    private WorkMode CurrentMode()
    {
        if (Mode1Radio.IsChecked == true) return WorkMode.TecAnalysis;
        if (Mode2Radio.IsChecked == true) return WorkMode.BulkExport;
        return WorkMode.BestPosAverage;
    }

    private Sys CurrentSys() => SysCombo.SelectedIndex == 1 ? Sys.Beidou : Sys.GPS;

    private static int ToInt(NumberBox box, int fallback)
    {
        double v = box.Value;
        if (double.IsNaN(v) || double.IsInfinity(v)) return fallback;
        return (int)Math.Round(v);
    }

    private void UpdateParamStates()
    {
        var mode = CurrentMode();
        bool isMode0 = mode == WorkMode.BestPosAverage;
        bool isMode1 = mode == WorkMode.TecAnalysis;
        bool isMode2 = mode == WorkMode.BulkExport;

        SysCombo.IsEnabled = !isMode0 && !_running;
        SatIdBox.IsEnabled = isMode1 && !_running;
        IdStartBox.IsEnabled = isMode2 && !_running;
        IdEndBox.IsEnabled = isMode2 && !_running;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e) => UpdateParamStates();

    // ------------------------------------------------------------------
    // 文件 / 目录选择（unpackaged 应用必须 InitializeWithWindow）
    // ------------------------------------------------------------------
    private async void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".bin");
        picker.FileTypeFilter.Add(".gps");
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is not null) InputPathBox.Text = file.Path;
    }

    private async void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) OutputDirBox.Text = folder.Path;
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        var dir = OutputDirBox.Text?.Trim();
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ------------------------------------------------------------------
    // 运行 / 停止
    // ------------------------------------------------------------------
    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        string input = InputPathBox.Text?.Trim() ?? "";
        if (input.Length == 0)
        {
            AppendLog("[警告] 请选择输入文件");
            await ShowMessage("警告", "请选择输入文件");
            return;
        }
        if (!File.Exists(input))
        {
            AppendLog("[警告] 输入文件不存在: " + input);
            await ShowMessage("警告", $"输入文件不存在:\n{input}");
            return;
        }

        string outputDir = OutputDirBox.Text?.Trim() ?? "";
        if (outputDir.Length == 0)
        {
            AppendLog("[警告] 请选择输出目录");
            await ShowMessage("警告", "请选择输出目录");
            return;
        }
        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception ex)
        {
            AppendLog("[警告] 无法创建输出目录: " + ex.Message);
            await ShowMessage("警告", "无法创建输出目录");
            return;
        }

        var mode = CurrentMode();
        var sys = CurrentSys();
        int satId = ToInt(SatIdBox, 1);
        int start = ToInt(IdStartBox, 1);
        int end = ToInt(IdEndBox, 32);

        _running = true;
        _cts = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        OpenOutputButton.IsEnabled = false;
        Progress.Value = 0;
        LogBox.Text = "";
        StatusText.Text = "处理中...";
        UpdateParamStates();

        var sink = new UiSink(this);
        var token = _cts.Token;

        try
        {
            await Task.Run(() =>
            {
                var proc = new GnssProcessor(sink);
                proc.ProcessFile(input, outputDir, mode, sys, satId, start, end, token);
            }, token);

            AppendLog("\n处理完成。");
            StatusText.Text = "处理完成";
            OpenOutputButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            AppendLog("\n[用户终止]");
            StatusText.Text = "已终止";
        }
        catch (Exception ex)
        {
            AppendLog($"\n[错误] {ex.Message}");
            StatusText.Text = "出错";
            await ShowMessage("错误", ex.Message);
        }
        finally
        {
            _running = false;
            RunButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            UpdateParamStates();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_cts is { IsCancellationRequested: false })
        {
            _cts.Cancel();
            StatusText.Text = "正在终止...";
        }
    }

    private async Task ShowMessage(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "确定",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
