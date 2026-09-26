using System;

namespace GnssTec.App.Core;

/// <summary>卫星系统。取值与原始 Python/C++ 实现一一对应。</summary>
public enum Sys
{
    GPS = 0,
    GLONASS = 1,
    SBAS = 2,
    Galileo = 3,
    Beidou = 4,
    QZSS = 5,
    NavIC = 6,
    Other = 7,
    ALL = 8,
}

/// <summary>信号类型（频点）。</summary>
public enum Sig
{
    L1 = 0,
    L2 = 1,
    B1 = 2,
    B2 = 3,
    B3 = 4,
    OTHER = 5,
}

/// <summary>BFN 记录头，固定 28 字节。</summary>
public struct Header
{
    public byte A, B, C, D;
    public ushort ID;
    public ushort Length;
    public ushort Week;
    public uint Ms;
}

/// <summary>BESTPOS（ID=42）有效载荷中的位置。</summary>
public struct BestPos
{
    public double Lat;
    public double Lon;
    public double Hgt;
}

/// <summary>RANGE（ID=43）单条观测，固定 44 字节。</summary>
public struct Range
{
    public ushort Slot;
    public double Psr;
    public double Adr;
    public uint Status;
    public Sys Sys;
    public bool Grouped;
    public Sig SigType;
}

/// <summary>SATVIS2（ID=1043）可见性记录。</summary>
public struct SatVis2
{
    public Sys Sys;
    public int ID;
    public double Az;
    public double El;
    public uint Ms;
    public ushort Week;
}

/// <summary>单个卫星单个历元的合并观测 + TEC 结果。</summary>
public sealed class SatData
{
    public ushort Week;
    public uint Ms;
    public double Az;
    public double El;
    public double Psr1;
    public double Adr1;
    public double Psr2;
    public double Adr2;
    public double Ave;
    public double TEC;
    public double TECA;
    public double TECR;
    public double DeltaT;
}

/// <summary>由 GPS 周/毫秒换算出的 UTC 时间。</summary>
public struct UtcTime
{
    public int Y, M, D, h, m, s;

    public override string ToString() => $"{Y}-{M:00}-{D:00} {h:00}:{m:00}:{s:00}";
}

/// <summary>名称映射，输出文件与日志用。</summary>
public static class Names
{
    public static string SysName(Sys s) => s switch
    {
        Sys.GPS => "GPS",
        Sys.GLONASS => "GLONASS",
        Sys.SBAS => "SBAS",
        Sys.Galileo => "Galileo",
        Sys.Beidou => "BDS",
        Sys.QZSS => "QZSS",
        Sys.NavIC => "NavIC",
        _ => "Unknown",
    };

    public static string SigName(Sig s) => s switch
    {
        Sig.L1 => "L1",
        Sig.L2 => "L2",
        Sig.B1 => "B1",
        Sig.B2 => "B2",
        Sig.B3 => "B3",
        _ => "other",
    };
}

/// <summary>日志/进度回调，由 UI 层注入；核心库不依赖任何 UI。</summary>
public interface IProgressSink
{
    void Log(string message);
    void Progress(int percent, string message);
}
