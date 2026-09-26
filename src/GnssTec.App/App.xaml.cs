using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace GnssTec.App;

public partial class App : Application
{
    private Window? _window;

    /// <summary>启动异常落盘路径。WinExe 没有控制台，靠这个抓崩溃原因。</summary>
    internal static string CrashLogPath => Path.Combine(AppContext.BaseDirectory, "startup-error.log");

    internal static void LogCrash(string label, object? ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {label}\n{ex}\n\n");
        }
        catch { /* 日志失败不能掩盖原始异常 */ }
    }

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);

        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogCrash("Application.UnhandledException", e.Exception);
            System.Diagnostics.Debug.WriteLine($"[未处理异常] {e.Exception}");
            e.Handled = false;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs();

        // --selftest：不开窗口跑完整解析流程，用于脚本化验证。
        // 参数形式：--selftest <mode> <input> <outputDir> [sys] [satId] [start] [end]
        int flag = Array.IndexOf(argv, "--selftest");
        if (flag >= 0)
        {
            Environment.Exit(SelfTest.Run(argv[(flag + 1)..]));
            return;
        }

        // 窗口必须留强引用，否则会被 GC 回收、进程直接退出。
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            LogCrash("MainWindow 构造/激活失败", ex);
            throw;
        }
    }
}

