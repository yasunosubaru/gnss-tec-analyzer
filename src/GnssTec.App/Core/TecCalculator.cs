using System;
using System.Collections.Generic;

namespace GnssTec.App.Core;

/// <summary>
/// TEC（电离层总电子含量）计算。逐行对照原始实现移植：
/// 伪距差值 TECA、载波相位差值 TECR、按 &gt;100 s 间断分段求平均，
/// 最终 TEC = TECR + 分段平均(deltaT)。
/// </summary>
public static class TecCalculator
{
    /// <summary>
    /// 就地写入每条的 TECA / TECR / DeltaT / Ave / TEC，返回分段边界数组
    /// （长度为分段数+1，首元素恒为 0，末元素为总条数）。
    /// </summary>
    public static int[] Compute(List<SatData> satData, Sys sysType)
    {
        uint lastMs = 0;
        ushort lastWeek = 0;
        var ave = new List<double> { 0.0 };
        int a = 0;
        var gap = new List<int> { 0 };

        foreach (var sd in satData)
        {
            long elapsed = (long)(sd.Week - lastWeek) * 604800000 + sd.Ms - lastMs;
            if (elapsed > 100000 && a != 0)
            {
                gap.Add(a);
                ave.Add(0.0);
            }
            lastMs = sd.Ms;
            lastWeek = sd.Week;

            if (sysType == Sys.GPS)
            {
                sd.TECA = 9.5196 * (sd.Psr2 - sd.Psr1);
                sd.TECR = 279.17 * (sd.Adr2 / 120 - sd.Adr1 / 154);
            }
            else if (sysType == Sys.Beidou)
            {
                sd.TECA = 8.9932 * (sd.Psr2 - sd.Psr1);
                sd.TECR = 263.73 * (sd.Adr2 / 118 - sd.Adr1 / 152.6);
            }

            sd.DeltaT = sd.TECA - sd.TECR;
            ave[^1] += sd.DeltaT;
            a++;
        }

        gap.Add(a);

        for (int i = 0; i < ave.Count; i++)
        {
            int span = gap[i + 1] - gap[i];
            ave[i] = span != 0 ? ave[i] / span : 0.0;
        }

        int j = 0;
        for (int i = 0; i < satData.Count; i++)
        {
            var sd = satData[i];
            if (j + 1 < gap.Count - 1 && i > gap[j + 1] && i != satData.Count - 1)
                j++;

            sd.Ave = ave[j];
            sd.TEC = sd.TECR + ave[j];
        }

        return gap.ToArray();
    }
}
