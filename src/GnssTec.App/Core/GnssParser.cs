using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace GnssTec.App.Core;

/// <summary>
/// BFN 二进制观测数据解析。逐行对照原始 PySide6 实现移植，
/// 位域宽度、字节偏移、回退分支都保持完全一致，以便逐值对拍验证。
/// </summary>
public static class GnssParser
{
    /// <summary>记录同步字：AA 44 12 1C。</summary>
    public static readonly byte[] Sync = { 0xAA, 0x44, 0x12, 0x1C };

    public const int HeaderSize = 28;

    /// <summary>信号码 → 频点。GPS/BDS 的映射表与原始实现一致，其余系统一律 OTHER。</summary>
    public static Sig Tran(Sys s, int sigcode)
    {
        if (s == Sys.GPS)
        {
            if (sigcode is 0 or 16) return Sig.L1;
            if (sigcode is 5 or 9 or 17) return Sig.L2;
            if (sigcode == 14) return Sig.OTHER;
            return Sig.OTHER;
        }

        if (s == Sys.Beidou)
        {
            if (sigcode is 0 or 4 or 7) return Sig.B1;
            if (sigcode is 1 or 5 or 9 or 11) return Sig.B2;
            if (sigcode is 2 or 6) return Sig.B3;
            return Sig.OTHER;
        }

        return Sig.OTHER;
    }

    /// <summary>读 28 字节记录头，返回新偏移。越界返回 false。</summary>
    public static bool ReadHeader(ReadOnlySpan<byte> data, int offset, out Header h, out int next)
    {
        h = default;
        next = offset;
        if (offset < 0 || offset + HeaderSize > data.Length) return false;

        h.A = data[offset];
        h.B = data[offset + 1];
        h.C = data[offset + 2];
        h.D = data[offset + 3];
        h.ID = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
        h.Length = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 8)..]);
        h.Week = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 14)..]);
        h.Ms = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 16)..]);
        next = offset + HeaderSize;
        return true;
    }

    /// <summary>BESTPOS 载荷：lat@8、lon@16、hgt@24，均为小端 double。</summary>
    public static bool ReadBestPos(ReadOnlySpan<byte> data, int offset, out BestPos pos, out int next)
    {
        pos = default;
        next = offset;
        if (offset + 32 > data.Length) return false;
        pos.Lat = BinaryPrimitives.ReadDoubleLittleEndian(data[(offset + 8)..]);
        pos.Lon = BinaryPrimitives.ReadDoubleLittleEndian(data[(offset + 16)..]);
        pos.Hgt = BinaryPrimitives.ReadDoubleLittleEndian(data[(offset + 24)..]);
        next = offset + 76;
        return true;
    }

    /// <summary>
    /// RANGE 载荷：前 4 字节为观测条数，其后每条 44 字节
    /// （slot u16@0、psr f64@4、adr f64@16、status u32@40）。
    /// obs×44+8 != length 时回退为 length/44，与原始实现一致。
    /// </summary>
    public static List<Range> ReadRange(ReadOnlySpan<byte> data, int offset, int length,
                                        int targetId = -1, Sys targetSys = Sys.ALL)
    {
        var results = new List<Range>();
        if (offset + 4 > data.Length) return results;

        uint obs = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        if ((long)obs * 44 + 8 != length) obs = (uint)(length / 44);
        offset += 4;

        for (uint j = 0; j < obs; j++)
        {
            if (offset + 44 > data.Length) break;

            var r = new Range
            {
                Slot = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]),
                Psr = BinaryPrimitives.ReadDoubleLittleEndian(data[(offset + 4)..]),
                Adr = BinaryPrimitives.ReadDoubleLittleEndian(data[(offset + 16)..]),
                Status = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 40)..]),
            };

            int sysVal = (int)((r.Status & 0x00070000u) >> 16);
            r.Sys = sysVal < 8 ? (Sys)sysVal : Sys.ALL;

            if (r.Sys != targetSys && targetSys != Sys.ALL) { offset += 44; continue; }
            if (targetId != -1 && r.Slot != targetId) { offset += 44; continue; }

            r.Grouped = ((r.Status & 0x00100000u) >> 20) != 0;
            int sigcode = (int)((r.Status & 0x03E00000u) >> 21);
            r.SigType = Tran(r.Sys, sigcode);
            results.Add(r);
            offset += 44;
        }

        return results;
    }

    /// <summary>
    /// SATVIS2 载荷：sys 取首字节并按 &gt;3 减 2、&gt;6 再减 1 校正；
    /// sat_count@12；每条 40 字节（ID u16@0、el f64@8、az f64@16）。
    /// </summary>
    public static SatVis2 ReadSatVis2Entry(ReadOnlySpan<byte> data, int offset, int targetId, Sys targetSys)
    {
        var sv = new SatVis2 { ID = -1 };
        if (offset + 16 > data.Length) return sv;

        uint satCount = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 12)..]);
        int sysVal = data[offset];
        if (sysVal > 3) sysVal -= 2;
        if (sysVal > 6) sysVal -= 1;
        sv.Sys = sysVal < 8 ? (Sys)sysVal : Sys.ALL;
        offset += 16;

        for (uint j = 0; j < satCount; j++)
        {
            if (offset + 40 > data.Length) break;
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            if (id == targetId || targetId == -1)
            {
                if (sv.Sys == targetSys || targetSys == Sys.ALL)
                {
                    sv.ID = id;
                    sv.El = BinaryPrimitives.ReadDoubleLittleEndian(data[(offset + 8)..]);
                    sv.Az = BinaryPrimitives.ReadDoubleLittleEndian(data[(offset + 16)..]);
                    return sv;
                }
            }

            offset += 40;
        }

        return sv;
    }

    /// <summary>
    /// GPS 周 + 周内毫秒 → UTC。基准 1980-01-06，逐年逐月递减，
    /// 与原始实现的循环结构完全一致（含闰年规则）。
    /// </summary>
    public static UtcTime ToUtc(ushort week, uint ms)
    {
        long totalS = (long)week * 604800 + ms / 1000;
        long days = totalS / 86400;
        long seconds = totalS % 86400;

        int y = 1980;
        long d = 6 + days;
        int[] mdays = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };

        while (true)
        {
            int leap = IsLeap(y) ? 1 : 0;
            int daysInYear = 365 + leap;
            if (d > daysInYear) { d -= daysInYear; y++; }
            else break;
        }

        int month = 0;
        while (month < 12)
        {
            int leap = (month == 1 && IsLeap(y)) ? 1 : 0;
            int daysInMonth = mdays[month] + leap;
            if (d > daysInMonth) { d -= daysInMonth; month++; }
            else break;
        }

        return new UtcTime
        {
            Y = y,
            M = month + 1,
            D = (int)d,
            h = (int)(seconds / 3600),
            m = (int)((seconds % 3600) / 60),
            s = (int)(seconds % 60),
        };
    }

    private static bool IsLeap(int y) => (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;

    /// <summary>从 offset 起寻找下一个同步字；找不到返回 -1。</summary>
    public static int FindSync(ReadOnlySpan<byte> data, int offset)
    {
        for (int i = offset; i + 4 <= data.Length; i++)
            if (data[i] == Sync[0] && data[i + 1] == Sync[1] &&
                data[i + 2] == Sync[2] && data[i + 3] == Sync[3])
                return i;
        return -1;
    }
}
