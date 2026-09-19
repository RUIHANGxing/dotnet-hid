using System;
using System.Collections.Generic;
using System.Linq;
using TouchProbe.Native;

namespace TouchProbe.Hid
{
    /// <summary>
    /// 【本文件是什么 —— 整个项目最核心的一段】
    /// 描述符的语义是："报告里第几个 bit 是什么东西"。
    /// 系统只给了我们一堆"字段说明"（ButtonCaps / ValueCaps），但没有直接给"每个字段从第几位开始"。
    /// 所以我们自己把位布局推导出来：
    ///
    ///   1. 每个字段按"数据序号"（DataIndex）排序 —— 数据序号就是字段在报告里的出场顺序；
    ///   2. 从第 0 位开始，依次给每个字段分配 BitOffset，分配完就往后挪 位宽 × 个数；
    ///   3. 得到"报告 ID → 字段表（含位偏移）"这张总表。
    ///
    /// 然后用手写的位提取函数（ExtractBits）从原始报告里把值抠出来，
    /// 再和系统解码结果（HidP_GetData）逐字段对照 —— 对得上，说明我们完全理解了协议；
    /// 对不上，说明描述符里有我们看不见的东西（通常是"填充位"），程序会提示。
    /// </summary>
    public sealed class ReportField
    {
        /// <summary>属于哪个报告（报告 ID；0 表示描述符没有使用报告 ID）。</summary>
        public int ReportId;

        public ushort UsagePage;
        /// <summary>用途（单值用途，或用途范围的下限）。</summary>
        public ushort Usage;
        /// <summary>用途范围的上限（IsRange = true 时有效）。</summary>
        public ushort UsageMax;
        public bool IsRange;

        /// <summary>是否来自"按钮类"（1 位宽的字段，比如 Tip Switch）。</summary>
        public bool IsButtonClass;

        /// <summary>每个值占多少位。</summary>
        public int BitSize;
        /// <summary>重复几个值。</summary>
        public int ReportCount;

        /// <summary>【我们自己推导出来的】本字段在"报告数据部分"里的起始位（0 = 第 1 个数据字节的最低位）。</summary>
        public int BitOffset;

        /// <summary>属于哪个逻辑集合（下标）。触摸板通常"一个触点"= 一个逻辑集合。</summary>
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;

        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;

        /// <summary>第一个数据序号（对应系统解码表里的 DataIndex）。</summary>
        public ushort DataIndex;

        public bool IsAbsolute;
        public bool HasNull;

        /// <summary>本字段总共占多少位。</summary>
        public int TotalBits { get { return BitSize * ReportCount; } }

        /// <summary>人类可读的名字（如 Digitizer:Tip Switch）。</summary>
        public string Name { get { return HidUsageNames.Describe(UsagePage, Usage); } }
    }

    /// <summary>字段布局表。</summary>
    public sealed class ReportLayout
    {
        /// <summary>报告 ID → 该报告的字段列表（已按位偏移升序排好）。</summary>
        public Dictionary<int, List<ReportField>> ByReportId = new Dictionary<int, List<ReportField>>();

        /// <summary>描述符是否使用了报告 ID（true 表示每个报告的第 1 个字节是报告 ID）。</summary>
        public bool HasReportIds;

        /// <summary>我们推导出的数据位数（不含填充位）。</summary>
        public int KnownDataBits;

        /// <summary>报告实际位数 - 已知数据位数 = 填充位数（我们无法定位的常量位）。</summary>
        public int UnknownPaddingBits;

        /// <summary>推导过程中的说明文字（给使用者看的人类可读提示）。</summary>
        public List<string> Notes = new List<string>();

        /// <summary>按"系统的字段描述"推导出完整的位布局。</summary>
        public static ReportLayout Build(
            NativeMethods.HIDP_CAPS caps,
            NativeMethods.HIDP_BUTTON_CAPS[] buttons,
            NativeMethods.HIDP_VALUE_CAPS[] values)
        {
            var layout = new ReportLayout();
            var allFields = new List<ReportField>();

            // ---------- 第 1 步：把按钮类字段翻译成统一的字段模型 ----------
            // 注意：这里用下标循环而不是 foreach，因为下面要按 ref 传结构体给辅助函数，
            // 而 C# 的 foreach 迭代变量是只读的，不能取 ref（这是新手常踩的坑）。
            for (int i = 0; i < buttons.Length; i++)
            {
                var c = buttons[i];
                var field = new ReportField();
                field.IsButtonClass = true;
                field.ReportId = c.ReportID;
                field.UsagePage = c.UsagePage;
                field.Usage = HidCaps.ButtonUsage(ref c);
                field.UsageMax = c.IsRange ? HidCaps.ButtonUsageMax(ref c) : field.Usage;
                field.IsRange = c.IsRange;
                field.BitSize = 1;                                   // 按钮类永远是 1 位
                field.DataIndex = HidCaps.ButtonDataIndex(ref c);
                field.ReportCount = c.IsRange
                    ? HidCaps.ButtonDataIndexMax(ref c) - field.DataIndex + 1
                    : 1;
                field.LinkCollection = c.LinkCollection;
                field.LinkUsage = c.LinkUsage;
                field.LinkUsagePage = c.LinkUsagePage;
                field.LogicalMin = 0;                                // 按钮只有"没按(0)/按下(1)"
                field.LogicalMax = 1;
                field.PhysicalMin = 0;
                field.PhysicalMax = 0;
                field.IsAbsolute = c.IsAbsolute;
                allFields.Add(field);
            }

            // ---------- 第 2 步：把数值类字段翻译成统一的字段模型 ----------
            for (int i = 0; i < values.Length; i++)
            {
                var c = values[i];
                var field = new ReportField();
                field.IsButtonClass = false;
                field.ReportId = c.ReportID;
                field.UsagePage = c.UsagePage;
                field.Usage = HidCaps.ValueUsage(ref c);
                field.UsageMax = c.IsRange ? HidCaps.ValueUsageMax(ref c) : field.Usage;
                field.IsRange = c.IsRange;
                field.BitSize = c.BitSize;
                field.ReportCount = c.ReportCount;
                field.DataIndex = HidCaps.ValueDataIndex(ref c);
                field.LinkCollection = c.LinkCollection;
                field.LinkUsage = c.LinkUsage;
                field.LinkUsagePage = c.LinkUsagePage;
                field.LogicalMin = c.LogicalMin;
                field.LogicalMax = c.LogicalMax;
                field.PhysicalMin = c.PhysicalMin;
                field.PhysicalMax = c.PhysicalMax;
                field.IsAbsolute = c.IsAbsolute;
                field.HasNull = c.HasNull;
                allFields.Add(field);
            }

            // ---------- 第 3 步：按报告分组、按数据序号排序、累加位偏移 ----------
            layout.HasReportIds = allFields.Any(f => f.ReportId != 0);

            var groups = allFields.GroupBy(f => f.ReportId);
            foreach (var group in groups)
            {
                var list = group.OrderBy(f => f.DataIndex).ToList();
                int bitOffset = 0;
                foreach (var field in list)
                {
                    field.BitOffset = bitOffset;
                    bitOffset += field.TotalBits;
                }
                layout.ByReportId[group.Key] = list;
                layout.KnownDataBits += bitOffset;
            }

            // ---------- 第 4 步：和报告实际长度比对，检查有没有"看不见的填充位" ----------
            int dataBytes = caps.InputReportByteLength - (layout.HasReportIds ? 1 : 0);
            int actualBits = dataBytes * 8;
            layout.UnknownPaddingBits = actualBits - layout.KnownDataBits;

            if (layout.UnknownPaddingBits > 0)
            {
                layout.Notes.Add(
                    "注意：报告实际有 " + actualBits + " 位数据，我们只知道其中 " + layout.KnownDataBits +
                    " 位的用途，剩下 " + layout.UnknownPaddingBits +
                    " 位是描述符里的“填充位”（常量字段）。填充位系统不会告诉我们具体位置，");
                layout.Notes.Add(
                    "      所以如果它在中间，后面字段的位偏移就会整体偏移，与系统的解码结果对不上 —— 这正是“拿不到原始描述符”带来的真实麻烦。");
            }
            else if (layout.UnknownPaddingBits < 0)
            {
                layout.Notes.Add("警告：推导出的位数比报告实际长度还大，说明我们的推导假设有误。");
            }

            return layout;
        }

        /// <summary>取某个报告 ID 的字段表（找不到返回空表）。</summary>
        public List<ReportField> FieldsOf(int reportId)
        {
            List<ReportField> list;
            return ByReportId.TryGetValue(reportId, out list) ? list : new List<ReportField>();
        }

        /// <summary>数据部分从第几个字节开始（有报告 ID 时要跳过第 1 个字节）。</summary>
        public int DataStartByte { get { return HasReportIds ? 1 : 0; } }
    }

    /// <summary>
    /// 【位提取】从字节数组里抠出一段位。
    ///
    /// HID 规定：报告是一串连续的位流，字节内部"低位数在前"（小端序的位序）。
    /// 比如位偏移 0、位宽 3，就是第 1 个字节的第 0、1、2 位组成的数。
    /// </summary>
    public static class BitReader
    {
        /// <summary>
        /// 从 data 的第 dataStartByte 个字节起，取 bitOffset 处开始的 bitSize 位（低位在前）。
        /// 返回值是"无符号原始值"，符号解释请用 ToSigned。
        /// </summary>
        public static ulong ExtractBits(byte[] data, int dataStartByte, int bitOffset, int bitSize)
        {
            ulong result = 0;
            for (int i = 0; i < bitSize; i++)
            {
                int bitIndex = bitOffset + i;
                int byteIndex = dataStartByte + (bitIndex / 8);   // 第几个字节
                int bitInByte = bitIndex % 8;                     // 字节里的第几位
                ulong bit = (ulong)((data[byteIndex] >> bitInByte) & 0x01);
                result |= bit << i;                               // 放到结果的第 i 位
            }
            return result;
        }

        /// <summary>
        /// 把原始位解释成有符号数（补码）。
        /// 判断依据：如果描述符里的逻辑最小值是负数，这个字段就是有符号的。
        /// </summary>
        public static long ToSigned(ulong raw, int bitSize)
        {
            if (bitSize >= 64) return unchecked((long)raw);
            ulong signBit = 1UL << (bitSize - 1);
            if ((raw & signBit) != 0)
            {
                // 最高位是 1 → 负数：减去 2^位数
                return unchecked((long)(raw - (1UL << bitSize)));
            }
            return (long)raw;
        }
    }

    /// <summary>
    /// 【位写入】ExtractBits 的逆操作：把一段值写进字节数组的指定位。
    /// 鼠标演示里用它把 RAWMOUSE 的 dx/dy/按键"编码"回报告字节（见 MouseReportSynthesizer）。
    /// </summary>
    public static class BitWriter
    {
        public static void WriteBits(byte[] data, int dataStartByte, int bitOffset, int bitSize, ulong value)
        {
            for (int i = 0; i < bitSize; i++)
            {
                int bitIndex = bitOffset + i;
                int byteIndex = dataStartByte + (bitIndex / 8);
                int bitInByte = bitIndex % 8;
                ulong bit = (value >> i) & 0x01UL;

                if (bit != 0) data[byteIndex] |= (byte)(1 << bitInByte);
                else data[byteIndex] &= (byte)~(1 << bitInByte);
            }
        }
    }

    /// <summary>一个字段被解码后的结果（含"我们算的"和"系统算的"两个值，用于对照）。</summary>
    public sealed class DecodedField
    {
        /// <summary>属于哪个字段。</summary>
        public ReportField Field;

        /// <summary>这是该字段的第几个重复值（0 开始）。</summary>
        public int SubIndex;

        /// <summary>这个值对应的用途（范围字段每个值用途不同）。</summary>
        public ushort Usage;

        /// <summary>这个值的数据序号（和系统对照用）。</summary>
        public ushort DataIndex;

        /// <summary>我们抠出来的原始位值。</summary>
        public ulong RawBits;

        /// <summary>我们解释出来的数值（考虑符号）。</summary>
        public long OurValue;

        /// <summary>系统解码出来的值（取不到就是 null）。</summary>
        public uint? SystemValue;

        /// <summary>两者是否一致（系统没给值时为 null）。</summary>
        public bool? Matches;

        /// <summary>字段名（如 Digitizer:Tip Switch）。</summary>
        public string Name { get { return HidUsageNames.Describe(Field.UsagePage, Usage); } }
    }

    /// <summary>报告解码器：把一包原始报告变成"一串可读的字段值"。</summary>
    public static class ReportDecoder
    {
        /// <summary>
        /// 解码一个报告：
        ///   - 用我们自己的位提取算一遍（OurValue）
        ///   - 把系统解码结果（systemTable）也附上，逐字段对照
        /// </summary>
        public static List<DecodedField> Decode(
            ReportLayout layout, byte[] report, Dictionary<ushort, uint> systemTable)
        {
            int reportId = layout.HasReportIds ? report[0] : 0;
            var result = new List<DecodedField>();

            foreach (var field in layout.FieldsOf(reportId))
            {
                for (int i = 0; i < field.ReportCount; i++)
                {
                    var item = new DecodedField();
                    item.Field = field;
                    item.SubIndex = i;

                    // 用途：范围字段按顺序递增，单值字段重复同样的用途
                    item.Usage = field.IsRange ? (ushort)(field.Usage + i) : field.Usage;

                    // 数据序号：连续的
                    item.DataIndex = (ushort)(field.DataIndex + i);

                    // 位偏移：第 i 个值在字段起点之后再往后挪 i × 位宽
                    int bitOffset = field.BitOffset + i * field.BitSize;

                    item.RawBits = BitReader.ExtractBits(
                        report, layout.DataStartByte, bitOffset, field.BitSize);

                    // 逻辑最小值为负 → 按有符号解释
                    item.OurValue = field.LogicalMin < 0
                        ? BitReader.ToSigned(item.RawBits, field.BitSize)
                        : (long)item.RawBits;

                    uint systemValue;
                    if (systemTable != null && systemTable.TryGetValue(item.DataIndex, out systemValue))
                    {
                        item.SystemValue = systemValue;

                        // 系统给的是"原始值"，和我们的原始位值对比；
                        // 有符号字段系统给的是补码形式，这里做一次等价转换再比。
                        long systemAsLong = field.LogicalMin < 0
                            ? BitReader.ToSigned(systemValue, field.BitSize)
                            : (long)systemValue;
                        item.Matches = (systemAsLong == item.OurValue);
                    }

                    result.Add(item);
                }
            }
            return result;
        }
    }
}