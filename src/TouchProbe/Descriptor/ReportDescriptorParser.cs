using System;
using System.Collections.Generic;
using System.Text;
using TouchProbe.Hid;

namespace TouchProbe.Descriptor
{
    /// <summary>
    /// 【本文件是什么 —— HID 协议的心脏】
    /// 报告描述符（Report Descriptor）是一串"二进制指令"，由设备在启动时交给系统。
    /// 它回答两个问题：
    ///   1. 我上报的数据里，第几位是什么含义？（用途/位置/取值范围）
    ///   2. 我有哪些逻辑结构？（集合：应用集合 → 逻辑集合（每个触点）→ 字段）
    ///
    /// 本文件实现一个解析器，把这串字节翻译成人类能读的表格。
    ///
    /// 【item 的编码规则（记住这三行就懂一半）】
    /// 每个 item 由 1 个"前缀字节" + 0/1/2/4 个"数据字节"组成：
    ///
    ///      bit 7 6 5 4 | bit 3 2 | bit 1 0
    ///      └── tag ──┘   └ 类型 ┘  └ 长度 ┘
    ///
    ///   长度（bit1-0）：0 → 0 字节数据；1 → 1 字节；2 → 2 字节；3 → 4 字节
    ///   类型（bit3-2）：0 = Main（主条目，产生数据），1 = Global（全局状态），2 = Local（局部信息）
    ///   标签（bit7-4）：具体是哪一条指令（配合类型才有意义）
    ///
    /// 数据字节按"小端序"拼成一个整数（低字节在前）。
    /// 举例：05 01 →  前缀 0x05 = 0000 0101：类型 01（Global）、标签 0、长度 01（1 字节）
    ///              数据 0x01 → 含义："Usage Page = 1（通用桌面）"
    /// </summary>
    public static class ReportDescriptorParser
    {
        // =================================================================
        // 一、解析结果的数据模型
        // =================================================================

        /// <summary>一个解析出来的 item。</summary>
        public sealed class HidItem
        {
            public int Offset;          // 该 item 在描述符里的偏移（第几个字节开始）
            public int ByteCount;       // 该 item 总共占几个字节（含前缀）
            public int DataByteCount;   // 数据部分占几个字节（0/1/2/4）
            public int ItemType;        // 0=Main 1=Global 2=Local 3=保留
            public int Tag;             // 标签
            public uint Data;           // 数据（小端拼装后的原始值）
            public bool IsSignedData;   // 数据是否按有符号解释（逻辑最小/最大值是有符号的）
            public string Name;         // item 名字，如 "Usage Page"
            public string Meaning;      // 人类可读的解释，如 "0x01(Generic Desktop)"

            /// <summary>有符号解释后的数据（无符号时等于 Data）。</summary>
            public long SignedData
            {
                get
                {
                    if (IsSignedData)
                    {
                        // 按数据宽度做补码还原：15 81 → -127
                        switch (DataByteCount)
                        {
                            case 1: return unchecked((sbyte)Data);
                            case 2: return unchecked((short)Data);
                            default: return unchecked((int)Data);
                        }
                    }
                    return Data;
                }
            }

            public string ItemTypeName
            {
                get
                {
                    switch (ItemType)
                    {
                        case 0: return "Main";
                        case 1: return "Global";
                        case 2: return "Local";
                        default: return "Reserved";
                    }
                }
            }
        }

        /// <summary>集合节点（对应描述符里的 Collection / EndCollection 语句对）。</summary>
        public sealed class DescriptorCollection
        {
            public int Index;
            public int ParentIndex = -1;
            public int UsagePage;
            public int Usage;
            public int CollectionType;
            public int BitOffsetOfStart;   // 该集合从描述符第几字节开始
        }

        /// <summary>输入/输出/功能报告里的一个字段（对应描述符里的一条 Input/Output/Feature）。</summary>
        public sealed class DescriptorField
        {
            public string Kind;            // Input / Output / Feature
            public int ReportId;
            public int BitSize;            // Report Size
            public int BitCount;           // Report Count
            public int BitOffset;          // 在报告数据里的位偏移
            public bool IsConstant;        // 常量字段（填充位）
            public bool IsVariable;        // 可变字段（每个值独立含义）还是数组
            public bool IsRelative;        // 相对值（鼠标位移）还是绝对值（触摸坐标）
            public int UsagePage;
            public List<int> Usages = new List<int>();  // 局部状态里声明的用途列表
            public int UsageMin = -1;
            public int UsageMax = -1;
            public int LogicalMin;
            public int LogicalMax;
            public string FlagsText;       // "Data,Var,Abs" 这类

            /// <summary>把这条 item 展开成"第 i 个值用哪个用途"。</summary>
            public int UsageOfIndex(int i)
            {
                if (i < Usages.Count) return Usages[i];
                if (UsageMin >= 0) return UsageMin + i;
                return -1;
            }

            /// <summary>用途的可读描述（含范围写法）。</summary>
            public string UsageText()
            {
                if (IsConstant) return "（填充/常量位）";
                var parts = new List<string>();
                foreach (int u in Usages) parts.Add(HidUsageNames.Describe(UsagePage, u));
                if (UsageMin >= 0 && UsageMax >= 0)
                {
                    parts.Add(UsageMin == UsageMax
                        ? HidUsageNames.Describe(UsagePage, UsageMin)
                        : HidUsageNames.Describe(UsagePage, UsageMin) + " ~ " +
                          HidUsageNames.Describe(UsagePage, UsageMax));
                }
                if (parts.Count == 0) return "（未声明用途）";
                return string.Join(" / ", parts);
            }
        }

        /// <summary>整个描述符的解析结果。</summary>
        public sealed class ParseResult
        {
            public byte[] Descriptor;
            public List<HidItem> Items = new List<HidItem>();
            public List<DescriptorCollection> Collections = new List<DescriptorCollection>();
            public List<DescriptorField> Fields = new List<DescriptorField>();
            public bool UsesReportIds;
            public List<string> Warnings = new List<string>();

            /// <summary>某个报告（按类型 + ID）一共占多少位。</summary>
            public Dictionary<string, int> ReportBits = new Dictionary<string, int>();

            public int BitsOf(string kind, int reportId)
            {
                int bits;
                return ReportBits.TryGetValue(kind + "#" + reportId, out bits) ? bits : 0;
            }
        }

        // =================================================================
        // 二、全局状态与局部状态
        // =================================================================
        // HID 描述符是"有状态"的：Global 条目设置的值会一直生效，直到被同类型的下一条改写；
        // Local 条目只对紧随其后的那一条 Main 条目生效，用完就清空。
        // Push/Pop 可以把全局状态压栈/出栈，常用于复用一组设置。

        private sealed class GlobalState
        {
            public int UsagePage;
            public int LogicalMin;
            public int LogicalMax;
            public bool HasLogicalMin;
            public bool HasLogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public int UnitExponent;
            public int Unit;
            public int ReportSize;
            public int ReportCount;
            public int ReportId;

            public GlobalState Clone() { return (GlobalState)MemberwiseClone(); }
        }

        private sealed class LocalState
        {
            public List<int> Usages = new List<int>();
            public int UsageMin = -1;
            public int UsageMax = -1;
            public bool DelimiterOpen;

            public void Clear()
            {
                Usages.Clear();
                UsageMin = -1;
                UsageMax = -1;
                DelimiterOpen = false;
            }
        }

        // =================================================================
        // 三、解析主流程
        // =================================================================

        /// <summary>解析描述符字节，返回完整结果。</summary>
        public static ParseResult Parse(byte[] descriptor)
        {
            var result = new ParseResult { Descriptor = descriptor };

            var global = new GlobalState();
            var stack = new Stack<GlobalState>();
            var local = new LocalState();

            var collectionStack = new Stack<DescriptorCollection>();

            // 每个报告（类型 + ID）当前的位偏移
            var reportOffsets = new Dictionary<string, int>();

            int pos = 0;
            while (pos < descriptor.Length)
            {
                byte prefix = descriptor[pos];
                int sizeCode = prefix & 0x03;
                int dataBytes = sizeCode == 3 ? 4 : sizeCode;
                int itemType = (prefix >> 2) & 0x03;
                int tag = (prefix >> 4) & 0x0F;

                if (pos + 1 + dataBytes > descriptor.Length)
                {
                    result.Warnings.Add("描述符在偏移 " + pos + " 处被截断（item 数据不完整）");
                    break;
                }

                uint data = 0;
                for (int i = 0; i < dataBytes; i++)
                {
                    // 小端序：低字节在前
                    data |= (uint)descriptor[pos + 1 + i] << (8 * i);
                }

                var item = new HidItem
                {
                    Offset = pos,
                    DataByteCount = dataBytes,
                    ByteCount = 1 + dataBytes,
                    ItemType = itemType,
                    Tag = tag,
                    Data = data,
                };

                Interpret(item, global, local, result, collectionStack, reportOffsets, stack);

                result.Items.Add(item);
                pos += 1 + dataBytes;
            }

            if (collectionStack.Count > 0)
            {
                result.Warnings.Add("描述符结束时还有 " + collectionStack.Count + " 个集合没有关闭（缺少 End Collection）");
            }

            return result;
        }

        /// <summary>解释一条 item，并更新解析状态。</summary>
        private static void Interpret(
            HidItem item, GlobalState global, LocalState local, ParseResult result,
            Stack<DescriptorCollection> collectionStack, Dictionary<string, int> reportOffsets,
            Stack<GlobalState> globalStack)
        {
            if (item.ItemType == 1) InterpretGlobal(item, global, local, result, globalStack);
            else if (item.ItemType == 2) InterpretLocal(item, global, local, result);
            else if (item.ItemType == 0) InterpretMain(item, global, local, result, collectionStack, reportOffsets);
            else item.Name = "未知类型(保留)";
        }

        // ---------- Global 条目 ----------
        private static void InterpretGlobal(
            HidItem item, GlobalState global, LocalState local, ParseResult result, Stack<GlobalState> globalStack)
        {
            switch (item.Tag)
            {
                case 0x0:
                    item.Name = "Usage Page";
                    global.UsagePage = (int)item.Data;
                    item.Meaning = "0x" + item.Data.ToString("X4") + " (" + HidUsageNames.PageName(global.UsagePage) + ")";
                    break;

                case 0x1:
                    item.Name = "Logical Minimum";
                    item.IsSignedData = true;          // 逻辑最小/最大值按有符号解释
                    global.LogicalMin = (int)item.SignedData;
                    global.HasLogicalMin = true;
                    item.Meaning = item.SignedData.ToString();
                    break;

                case 0x2:
                    item.Name = "Logical Maximum";
                    item.IsSignedData = true;
                    global.LogicalMax = (int)item.SignedData;
                    global.HasLogicalMax = true;
                    item.Meaning = item.SignedData.ToString();
                    break;

                case 0x3:
                    item.Name = "Physical Minimum";
                    item.IsSignedData = true;
                    global.PhysicalMin = (int)item.SignedData;
                    item.Meaning = item.SignedData.ToString();
                    break;

                case 0x4:
                    item.Name = "Physical Maximum";
                    item.IsSignedData = true;
                    global.PhysicalMax = (int)item.SignedData;
                    item.Meaning = item.SignedData.ToString();
                    break;

                case 0x5:
                    item.Name = "Unit Exponent";
                    item.IsSignedData = true;
                    global.UnitExponent = (int)item.SignedData;
                    item.Meaning = item.SignedData.ToString();
                    break;

                case 0x6:
                    item.Name = "Unit";
                    global.Unit = (int)item.Data;
                    item.Meaning = "0x" + item.Data.ToString("X8");
                    break;

                case 0x7:
                    item.Name = "Report Size";
                    global.ReportSize = (int)item.Data;
                    item.Meaning = item.Data + " 位";
                    break;

                case 0x8:
                    item.Name = "Report ID";
                    global.ReportId = (int)item.Data;
                    if (item.Data != 0) result.UsesReportIds = true;
                    item.Meaning = item.Data.ToString();
                    break;

                case 0x9:
                    item.Name = "Report Count";
                    global.ReportCount = (int)item.Data;
                    item.Meaning = item.Data + " 个";
                    break;

                case 0xA:
                    item.Name = "Push";
                    item.Meaning = "保存当前全局状态（压栈）";
                    globalStack.Push(global.Clone());   // 压栈后继续用当前状态
                    break;

                case 0xB:
                    item.Name = "Pop";
                    item.Meaning = "恢复之前保存的全局状态（出栈）";
                    if (globalStack.Count > 0)
                    {
                        // 把栈顶状态的所有字段复制回当前状态
                        GlobalState saved = globalStack.Pop();
                        global.UsagePage = saved.UsagePage;
                        global.LogicalMin = saved.LogicalMin;
                        global.LogicalMax = saved.LogicalMax;
                        global.HasLogicalMin = saved.HasLogicalMin;
                        global.HasLogicalMax = saved.HasLogicalMax;
                        global.PhysicalMin = saved.PhysicalMin;
                        global.PhysicalMax = saved.PhysicalMax;
                        global.UnitExponent = saved.UnitExponent;
                        global.Unit = saved.Unit;
                        global.ReportSize = saved.ReportSize;
                        global.ReportCount = saved.ReportCount;
                        global.ReportId = saved.ReportId;
                    }
                    break;

                default:
                    item.Name = "Global 标签 0x" + item.Tag.ToString("X");
                    item.Meaning = "0x" + item.Data.ToString("X");
                    break;
            }
        }

        // ---------- Local 条目 ----------
        private static void InterpretLocal(HidItem item, GlobalState global, LocalState local, ParseResult result)
        {
            switch (item.Tag)
            {
                case 0x0:
                    item.Name = "Usage";
                    // 数据宽度为 4 时：低 16 位是用途，高 16 位是用途页（扩展用途）
                    int page = item.Data > 0xFFFF ? (int)(item.Data >> 16) : global.UsagePage;
                    int usage = (int)(item.Data & 0xFFFF);
                    local.Usages.Add(usage);
                    item.Meaning = HidUsageNames.Describe(page, usage);
                    break;

                case 0x1:
                    item.Name = "Usage Minimum";
                    local.UsageMin = (int)(item.Data & 0xFFFF);
                    item.Meaning = HidUsageNames.Describe(global.UsagePage, local.UsageMin) + "（范围起点）";
                    break;

                case 0x2:
                    item.Name = "Usage Maximum";
                    local.UsageMax = (int)(item.Data & 0xFFFF);
                    item.Meaning = HidUsageNames.Describe(global.UsagePage, local.UsageMax) + "（范围终点）";
                    break;

                case 0x7:
                    item.Name = "String Index";
                    item.Meaning = item.Data.ToString();
                    break;

                case 0xA:
                    item.Name = "Delimiter";
                    local.DelimiterOpen = item.Data == 1;
                    item.Meaning = item.Data == 1 ? "开始备用用途集" : "结束备用用途集";
                    break;

                default:
                    item.Name = "Local 标签 0x" + item.Tag.ToString("X");
                    item.Meaning = "0x" + item.Data.ToString("X");
                    break;
            }
        }

        // ---------- Main 条目 ----------
        private static void InterpretMain(
            HidItem item, GlobalState global, LocalState local, ParseResult result,
            Stack<DescriptorCollection> collectionStack, Dictionary<string, int> reportOffsets)
        {
            switch (item.Tag)
            {
                case 0x8: // Input
                case 0x9: // Output
                case 0xB: // Feature
                {
                    string kind = item.Tag == 0x8 ? "Input" : item.Tag == 0x9 ? "Output" : "Feature";
                    item.Name = kind;

                    int bitSize = global.ReportSize == 0 ? 1 : global.ReportSize;
                    int bitCount = global.ReportCount == 0 ? 1 : global.ReportCount;
                    string key = kind + "#" + global.ReportId;

                    int offset;
                    if (!reportOffsets.TryGetValue(key, out offset)) offset = 0;

                    var field = new DescriptorField
                    {
                        Kind = kind,
                        ReportId = global.ReportId,
                        BitSize = bitSize,
                        BitCount = bitCount,
                        BitOffset = offset,
                        IsConstant = (item.Data & 0x01) != 0,
                        IsVariable = (item.Data & 0x02) != 0,
                        IsRelative = (item.Data & 0x04) != 0,
                        UsagePage = global.UsagePage,
                        UsageMin = local.UsageMin,
                        UsageMax = local.UsageMax,
                        LogicalMin = global.LogicalMin,
                        LogicalMax = global.LogicalMax,
                        FlagsText = DescribeInputFlags(item.Data),
                    };
                    field.Usages.AddRange(local.Usages);
                    result.Fields.Add(field);

                    reportOffsets[key] = offset + bitSize * bitCount;
                    result.ReportBits[key] = reportOffsets[key];

                    item.Meaning = bitCount + " × " + bitSize + "位，" + DescribeInputFlags(item.Data) +
                                   " → 位偏移 " + offset + "~" + (offset + bitSize * bitCount - 1);
                    break;
                }

                case 0xA: // Collection
                {
                    item.Name = "Collection";
                    var node = new DescriptorCollection
                    {
                        Index = result.Collections.Count,
                        UsagePage = global.UsagePage,
                        Usage = local.Usages.Count > 0 ? local.Usages[0] : -1,
                        CollectionType = (int)item.Data,
                        BitOffsetOfStart = item.Offset,
                        ParentIndex = collectionStack.Count > 0 ? collectionStack.Peek().Index : -1,
                    };
                    result.Collections.Add(node);
                    collectionStack.Push(node);
                    item.Meaning = HidUsageNames.Describe(node.UsagePage, node.Usage <= 0 ? 0 : node.Usage) +
                                   "，" + CollectionTypeName(node.CollectionType);
                    break;
                }

                case 0xC: // End Collection
                {
                    item.Name = "End Collection";
                    if (collectionStack.Count > 0) collectionStack.Pop();
                    item.Meaning = "关闭当前集合";
                    break;
                }

                default:
                    item.Name = "Main 标签 0x" + item.Tag.ToString("X");
                    item.Meaning = "0x" + item.Data.ToString("X");
                    break;
            }

            // Main 条目执行后，局部状态必须清空（这是 HID 规范的要求，也是很多人第一次会踩的坑）
            local.Clear();
        }

        /// <summary>把 Input/Output/Feature 的"标志位"翻译成文字。</summary>
        private static string DescribeInputFlags(uint flags)
        {
            var sb = new StringBuilder();
            sb.Append((flags & 0x01) != 0 ? "Const" : "Data");
            sb.Append(',');
            sb.Append((flags & 0x02) != 0 ? "Var" : "Array");
            sb.Append(',');
            sb.Append((flags & 0x04) != 0 ? "Rel" : "Abs");
            if ((flags & 0x08) != 0) sb.Append(",Wrap");
            if ((flags & 0x10) != 0) sb.Append(",NonLinear");
            if ((flags & 0x20) != 0) sb.Append(",NoPreferred");
            if ((flags & 0x40) != 0) sb.Append(",NullState");
            if ((flags & 0x80) != 0) sb.Append(",Volatile");
            if ((flags & 0x100) != 0) sb.Append(",BufferedBytes");
            return sb.ToString();
        }

        /// <summary>集合类型的中文名。</summary>
        public static string CollectionTypeName(int type)
        {
            switch (type)
            {
                case 0: return "Physical（物理集合）";
                case 1: return "Application（应用集合）";
                case 2: return "Logical（逻辑集合）";
                case 3: return "Report";
                case 4: return "Named Array";
                case 5: return "Usage Switch";
                case 6: return "Usage Modifier";
                default: return "未知(" + type + ")";
            }
        }

        /// <summary>把描述符按 16 字节一行排版成十六进制文本（教学用）。</summary>
        public static List<string> ToHexLines(byte[] data, int bytesPerLine)
        {
            var lines = new List<string>();
            for (int i = 0; i < data.Length; i += bytesPerLine)
            {
                var sb = new StringBuilder();
                sb.Append(i.ToString("X4"));
                sb.Append("  ");
                for (int j = 0; j < bytesPerLine && i + j < data.Length; j++)
                {
                    sb.Append(data[i + j].ToString("X2"));
                    sb.Append(j == bytesPerLine / 2 - 1 ? "  " : " ");
                }
                lines.Add(sb.ToString().TrimEnd());
            }
            return lines;
        }
    }
}