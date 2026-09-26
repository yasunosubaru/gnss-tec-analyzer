using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace GnssTec.App.Core;

/// <summary>处理模式。</summary>
public enum WorkMode
{
    /// <summary>计算 BESTPOS 平均位置。</summary>
    BestPosAverage = 0,

    /// <summary>指定卫星的 TEC 分析。</summary>
    TecAnalysis = 1,

    /// <summary>按卫星 ID 范围批量导出原始观测（不含 TEC）。</summary>
    BulkExport = 2,
}

/// <summary>
/// 三种处理模式的实现。输出文本的字段顺序、小数位、空行位置与
/// 原始 PySide6 版本逐字节对齐，便于结果对拍。
/// 核心库不依赖 UI，进度通过 <see cref="IProgressSink"/> 回报。
/// </summary>
public sealed class GnssProcessor
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IProgressSink _sink;

    public GnssProcessor(IProgressSink sink) => _sink = sink;

    private static string F6(double v) => v.ToString("F6", Inv);

    /// <summary>读入整个文件后按模式分发，与原始实现一致（整文件驻留内存）。</summary>
    public void ProcessFile(string inputPath, string outputDir, WorkMode mode,
                            Sys sysType, int satId, int satRangeStart, int satRangeEnd,
                            CancellationToken token = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"文件不存在: {inputPath}", inputPath);

        byte[] data = File.ReadAllBytes(inputPath);
        token.ThrowIfCancellationRequested();

        switch (mode)
        {
            case WorkMode.BestPosAverage:
                ModeBestPosAverage(data, outputDir, token);
                break;
            case WorkMode.TecAnalysis:
                ModeTecAnalysis(data, outputDir, sysType, satId, token);
                break;
            case WorkMode.BulkExport:
                ModeBulkExport(data, outputDir, sysType, satRangeStart, satRangeEnd, token);
                break;
        }
    }

    // ------------------------------------------------------------------
    // 模式 0：BESTPOS 平均位置
    // ------------------------------------------------------------------
    private void ModeBestPosAverage(byte[] data, string outputDir, CancellationToken token)
    {
        _sink.Log(new string('=', 50));
        _sink.Log("模式0: 计算 BESTPOS 平均位置");
        _sink.Log(new string('=', 50));

        var bestPosList = new List<BestPos>();
        int offset = 0;
        int totalSize = data.Length;
        int lastPct = 0;
        var span = new ReadOnlySpan<byte>(data);

        while (offset < data.Length - GnssParser.HeaderSize)
        {
            token.ThrowIfCancellationRequested();
            if (data[offset] != GnssParser.Sync[0]) { offset++; continue; }
            if (!MatchesSync(span, offset)) { offset++; continue; }

            if (!GnssParser.ReadHeader(span, offset, out var head, out offset)) break;
            int recEnd = offset + head.Length;

            if (head.ID == 42)
            {
                if (GnssParser.ReadBestPos(span, offset, out var pos, out _))
                    bestPosList.Add(pos);
            }

            offset = recEnd;

            int pct = (int)((long)offset * 100 / totalSize);
            if (pct > lastPct)
            {
                lastPct = pct;
                _sink.Progress(pct, $"正在读取... {pct}%");
            }
        }

        if (bestPosList.Count > 0)
        {
            double avgLat = 0, avgLon = 0, avgHgt = 0;
            foreach (var p in bestPosList) { avgLat += p.Lat; avgLon += p.Lon; avgHgt += p.Hgt; }
            avgLat /= bestPosList.Count; avgLon /= bestPosList.Count; avgHgt /= bestPosList.Count;

            _sink.Log($"\n共读取 {bestPosList.Count} 条 BESTPOS 记录");
            _sink.Log($"平均纬度 (lat): {F6(avgLat)}");
            _sink.Log($"平均经度 (lon): {F6(avgLon)}");
            _sink.Log($"平均高度 (hgt): {F6(avgHgt)}");

            if (!string.IsNullOrEmpty(outputDir))
            {
                string outPath = Path.Combine(outputDir, "bestpos_avg.txt");
                using var w = new StreamWriter(outPath, false, new UTF8Encoding(false));
                w.WriteLine("BESTPOS 平均结果");
                w.WriteLine($"记录数: {bestPosList.Count}");
                w.WriteLine($"纬度 (lat): {F6(avgLat)}");
                w.WriteLine($"经度 (lon): {F6(avgLon)}");
                w.WriteLine($"高度 (hgt): {F6(avgHgt)}");
                _sink.Log($"\n结果已保存到: {outPath}");
            }
        }
        else
        {
            _sink.Log("未找到 BESTPOS 记录");
        }

        _sink.Progress(100, "完成");
    }

    // ------------------------------------------------------------------
    // 模式 1：指定卫星 TEC 分析
    // ------------------------------------------------------------------
    private void ModeTecAnalysis(byte[] data, string outputDir, Sys sysType, int satId, CancellationToken token)
    {
        string sysName = Names.SysName(sysType);
        _sink.Log(new string('=', 50));
        _sink.Log($"模式1: TEC 分析 - {sysName} 卫星 {satId}");
        _sink.Log(new string('=', 50));

        var satData = new List<SatData>();
        var satVisList = new List<SatVis2>();
        var span = new ReadOnlySpan<byte>(data);
        int totalSize = data.Length;
        int offset = 0;
        int count = 0;

        // 阶段 1：读取全部记录
        while (offset < data.Length - GnssParser.HeaderSize)
        {
            token.ThrowIfCancellationRequested();
            if (data[offset] != GnssParser.Sync[0]) { offset++; continue; }
            if (!MatchesSync(span, offset)) { offset++; continue; }

            if (!GnssParser.ReadHeader(span, offset, out var head, out offset)) break;
            int recEnd = offset + head.Length;

            if (head.ID == 43)
            {
                var ranges = GnssParser.ReadRange(span, offset, head.Length, satId, sysType);
                if (ranges.Count > 0)
                {
                    var sd = new SatData { Week = head.Week, Ms = head.Ms };
                    bool hasL1 = false, hasL2 = false;
                    foreach (var r in ranges)
                    {
                        if (r.SigType is Sig.L1 or Sig.B1)
                        { sd.Adr1 = r.Adr; sd.Psr1 = r.Psr; hasL1 = true; }
                        else if (r.SigType is Sig.L2 or Sig.B2)
                        { sd.Adr2 = r.Adr; sd.Psr2 = r.Psr; hasL2 = true; }
                    }

                    if (sd.Adr2 == 0 && sd.Psr2 == 0)
                    {
                        foreach (var r in ranges)
                        {
                            if (r.SigType == Sig.OTHER)
                            { sd.Adr2 = r.Adr; sd.Psr2 = r.Psr; hasL2 = true; break; }
                        }
                    }

                    if (hasL1 && hasL2 && sd.Psr1 != 0 && sd.Psr2 != 0 && sd.Adr1 != 0 && sd.Adr2 != 0)
                        satData.Add(sd);
                }
            }
            else if (head.ID == 1043)
            {
                var sv = GnssParser.ReadSatVis2Entry(span, offset, satId, sysType);
                if (sv.ID == satId && sv.Sys == sysType)
                {
                    sv.Week = head.Week;
                    sv.Ms = head.Ms;
                    satVisList.Add(sv);
                }
            }

            offset = recEnd;
            count++;
            int pct = (int)((long)offset * 100 / totalSize);
            if (count % 500 == 0)
                _sink.Progress(Math.Min(pct, 50), $"读取中... {pct}%");
        }

        _sink.Log($"读取完成: {satData.Count} 条有效观测, {satVisList.Count} 条可见性记录");

        // 阶段 2：按时间关联可见性
        _sink.Log("\n正在关联可见性数据...");
        int lastIdx = 0, matched = 0;
        for (int i = 0; i < satData.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var sd = satData[i];
            for (int j = lastIdx; j < satVisList.Count; j++)
            {
                long diff = (long)sd.Ms - satVisList[j].Ms;
                if (Math.Abs(diff) < 1000)
                {
                    sd.Az = satVisList[j].Az;
                    sd.El = satVisList[j].El;
                    lastIdx = j;
                    matched++;
                    break;
                }
            }

            int pct = 50 + (int)((long)i * 25 / Math.Max(satData.Count, 1));
            if (i % 100 == 0)
                _sink.Progress(pct, $"关联数据... {(int)((long)i * 100 / Math.Max(satData.Count, 1))}%");
        }

        _sink.Log($"关联完成: {matched} 条匹配");

        // 阶段 3：计算 TEC
        _sink.Log("\n正在计算 TEC...");
        _sink.Progress(75, "计算 TEC...");
        int[] gap = TecCalculator.Compute(satData, sysType);
        _sink.Log("TEC 计算完成");

        // 与原始实现一致：模式 1 即使没有任何匹配记录也会写出只含表头的文件。
        if (!string.IsNullOrEmpty(outputDir))
        {
            string fileName = $"{sysName}-{satId}.txt";
            string outPath = Path.Combine(outputDir, fileName);

            using (var w = new StreamWriter(outPath, false, new UTF8Encoding(false)))
            {
                w.WriteLine($"{sysName}-{satId}");
                w.WriteLine("Y M D h m s");
                w.WriteLine("az  el");
                w.WriteLine("psr1  adr1");
                w.WriteLine("psr2  adr2");
                w.WriteLine("TECA  TECR  ave-deltaT  TEC");
                w.WriteLine();

                if (gap.Length > 2)
                {
                    w.WriteLine("存在超过100s间隔的数据段，分段时间点:");
                    for (int gi = 1; gi < gap.Length - 1; gi++)
                    {
                        var t = GnssParser.ToUtc(satData[gap[gi] - 1].Week, satData[gap[gi] - 1].Ms);
                        w.WriteLine(t.ToString());
                    }
                    w.WriteLine();
                }

                for (int i = 0; i < satData.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var sd = satData[i];
                    var t = GnssParser.ToUtc(sd.Week, sd.Ms);
                    w.WriteLine($"{t.Y} {t.M} {t.D} {t.h} {t.m} {t.s}");
                    w.WriteLine($"{F6(sd.Az)}  {F6(sd.El)}");
                    w.WriteLine($"{F6(sd.Psr1)}  {F6(sd.Adr1)}");
                    w.WriteLine($"{F6(sd.Psr2)}  {F6(sd.Adr2)}");
                    w.WriteLine($"{F6(sd.TECA)}  {F6(sd.TECR)}  {F6(sd.Ave)}  {F6(sd.TEC)}");
                    w.WriteLine();

                    int pct = 75 + (int)((long)(i + 1) * 25 / Math.Max(satData.Count, 1));
                    if (i % 200 == 0)
                        _sink.Progress(Math.Min(pct, 100), $"写入中... {(int)((long)(i + 1) * 100 / Math.Max(satData.Count, 1))}%");
                }
            }

            _sink.Log($"\n结果已保存到: {outPath}");
        }

        if (satData.Count > 0)
        {
            double sum = 0;
            foreach (var sd in satData) sum += sd.TEC;
            _sink.Log("\n--- 数据摘要 ---");
            _sink.Log($"有效数据点: {satData.Count}");
            _sink.Log($"起点: {GnssParser.ToUtc(satData[0].Week, satData[0].Ms)}");
            _sink.Log($"终点: {GnssParser.ToUtc(satData[^1].Week, satData[^1].Ms)}");
            _sink.Log($"平均 TEC: {sum / satData.Count:F4}");
        }

        _sink.Progress(100, "完成");
    }

    // ------------------------------------------------------------------
    // 模式 2：批量导出原始观测
    // ------------------------------------------------------------------
    private void ModeBulkExport(byte[] data, string outputDir, Sys sysType,
                               int idStart, int idEnd, CancellationToken token)
    {
        string sysName = Names.SysName(sysType);
        _sink.Log(new string('=', 50));
        _sink.Log($"模式2: 批量导出原始数据 - {sysName}");
        _sink.Log(new string('=', 50));

        var span = new ReadOnlySpan<byte>(data);

        for (int satId = idStart; satId <= idEnd; satId++)
        {
            token.ThrowIfCancellationRequested();
            _sink.Log($"\n>>> 处理 {sysName} 卫星 {satId}...");

            var satData = new List<SatData>();
            var satVisList = new List<SatVis2>();
            int offset = 0;

            while (offset < data.Length - GnssParser.HeaderSize)
            {
                token.ThrowIfCancellationRequested();
                if (data[offset] != GnssParser.Sync[0]) { offset++; continue; }
                if (!MatchesSync(span, offset)) { offset++; continue; }

                if (!GnssParser.ReadHeader(span, offset, out var head, out offset)) break;
                int recEnd = offset + head.Length;

                if (head.ID == 43)
                {
                    var ranges = GnssParser.ReadRange(span, offset, head.Length, satId, sysType);
                    if (ranges.Count > 0)
                    {
                        var sd = new SatData { Week = head.Week, Ms = head.Ms };
                        foreach (var r in ranges)
                        {
                            if (r.SigType is Sig.L1 or Sig.B1) { sd.Adr1 = r.Adr; sd.Psr1 = r.Psr; }
                            else if (r.SigType is Sig.L2 or Sig.B2) { sd.Adr2 = r.Adr; sd.Psr2 = r.Psr; }
                        }
                        if (sd.Adr2 == 0 && sd.Psr2 == 0)
                        {
                            foreach (var r in ranges)
                            {
                                if (r.SigType == Sig.OTHER)
                                { sd.Adr2 = r.Adr; sd.Psr2 = r.Psr; break; }
                            }
                        }
                        satData.Add(sd);
                    }
                }
                else if (head.ID == 1043)
                {
                    var sv = GnssParser.ReadSatVis2Entry(span, offset, satId, sysType);
                    if (sv.ID == satId && sv.Sys == sysType)
                    {
                        sv.Week = head.Week;
                        sv.Ms = head.Ms;
                        satVisList.Add(sv);
                    }
                }

                offset = recEnd;
            }

            int lastIdx = 0;
            foreach (var sd in satData)
            {
                for (int j = lastIdx; j < satVisList.Count; j++)
                {
                    long diff = (long)sd.Ms - satVisList[j].Ms;
                    if (Math.Abs(diff) < 1000)
                    {
                        sd.Az = satVisList[j].Az;
                        sd.El = satVisList[j].El;
                        lastIdx = j;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(outputDir))
            {
                string fileName = $"{sysName}-{satId}.txt";
                string outPath = Path.Combine(outputDir, fileName);
                using var w = new StreamWriter(outPath, false, new UTF8Encoding(false));
                w.WriteLine($"{sysName}-{satId}");
                w.WriteLine("Y M D h m s");
                w.WriteLine("az  el");
                w.WriteLine("psr1  adr1");
                w.WriteLine("psr2  adr2");
                w.WriteLine();
                foreach (var sd in satData)
                {
                    var t = GnssParser.ToUtc(sd.Week, sd.Ms);
                    w.WriteLine($"{t.Y} {t.M} {t.D} {t.h} {t.m} {t.s}");
                    w.WriteLine($"{F6(sd.Az)}  {F6(sd.El)}");
                    w.WriteLine($"{F6(sd.Psr1)}  {F6(sd.Adr1)}");
                    w.WriteLine($"{F6(sd.Psr2)}  {F6(sd.Adr2)}");
                    w.WriteLine();
                }

                _sink.Log($"  已保存: {outPath}  ({satData.Count} 条)");
            }

            satData.Clear();
            satVisList.Clear();

            int pct = (int)((long)(satId - idStart + 1) * 100 / (idEnd - idStart + 1));
            _sink.Progress(pct, $"处理中... {pct}%");
        }

        _sink.Progress(100, "完成");
    }

    private static bool MatchesSync(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 4 > data.Length) return false;
        return data[offset] == GnssParser.Sync[0]
            && data[offset + 1] == GnssParser.Sync[1]
            && data[offset + 2] == GnssParser.Sync[2]
            && data[offset + 3] == GnssParser.Sync[3];
    }
}
