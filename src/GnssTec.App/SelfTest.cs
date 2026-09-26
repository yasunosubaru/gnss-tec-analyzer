using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GnssTec.App.Core;

namespace GnssTec.App;

/// <summary>
/// 无界面自检入口，用于脚本化验证核心解析逻辑。
/// 用法：GnssTec.exe --selftest &lt;mode&gt; &lt;input&gt; &lt;outDir&gt; [sys] [satId] [start] [end]
///   mode : 0=BESTPOS 平均, 1=TEC 分析, 2=批量导出
///   sys  : GPS | BDS（默认 GPS）
/// 同时把日志写到 &lt;outDir&gt;\_selftest.log，便于与 Python 参考实现对拍。
/// </summary>
internal static class SelfTest
{
    private sealed class Sink : IProgressSink
    {
        private readonly List<string> _lines = new();
        private readonly bool _verbose;
        private int _lastPct = -1;

        public Sink(bool verbose) => _verbose = verbose;

        public IReadOnlyList<string> Lines => _lines;

        public void Log(string message)
        {
            _lines.Add(message);
            if (_verbose) Console.Out.WriteLine(message);
        }

        public void Progress(int percent, string message)
        {
            // 只记录百分比变化，避免刷屏（与 Python 版日志量级一致）。
            if (percent != _lastPct)
            {
                _lastPct = percent;
                if (_verbose) Console.Out.WriteLine($"[进度] {percent}% {message}");
            }
        }
    }

    public static int Run(string[] args)
    {
        var sb = new StringBuilder();
        try
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("用法: --selftest <mode> <input> <outDir> [sys] [satId] [start] [end]");
                return 2;
            }

            int mode = int.Parse(args[0]);
            string input = args[1];
            string outDir = args[2];
            string sysArg = args.Length > 3 ? args[3] : "GPS";
            int satId = args.Length > 4 ? int.Parse(args[4]) : 1;
            int start = args.Length > 5 ? int.Parse(args[5]) : 1;
            int end = args.Length > 6 ? int.Parse(args[6]) : 32;

            Sys sys = sysArg.Equals("BDS", StringComparison.OrdinalIgnoreCase)
                   || sysArg.Equals("BeiDou", StringComparison.OrdinalIgnoreCase)
                ? Sys.Beidou : Sys.GPS;

            Directory.CreateDirectory(outDir);

            var sink = new Sink(verbose: true);
            var proc = new GnssProcessor(sink);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            proc.ProcessFile(input, outDir, (WorkMode)mode, sys, satId, start, end);
            sw.Stop();

            Console.Out.WriteLine($"\n[selftest] 完成，用时 {sw.Elapsed.TotalSeconds:F3} s");
            Console.Out.WriteLine($"[selftest] mode={mode} sys={sysArg} input={input}");
            Console.Out.WriteLine($"[selftest] outDir={outDir}");

            sb.AppendLine($"# selftest ok mode={mode} sys={sysArg} elapsed={sw.Elapsed.TotalSeconds:F3}s");
            foreach (var l in sink.Lines) sb.AppendLine(l);

            File.WriteAllText(Path.Combine(outDir, "_selftest.log"), sb.ToString(), new UTF8Encoding(false));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[selftest] 失败: {ex}");
            try
            {
                File.WriteAllText(Path.Combine(args.Length > 2 ? args[2] : ".", "_selftest.log"),
                    $"# selftest FAILED\n{ex}", new UTF8Encoding(false));
            }
            catch { /* 忽略次要错误 */ }
            return 1;
        }
    }
}
