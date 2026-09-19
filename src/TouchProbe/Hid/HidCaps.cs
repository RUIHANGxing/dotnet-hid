using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TouchProbe.Native;

namespace TouchProbe.Hid
{
    /// <summary>
    /// 【本文件是什么】
    /// 把"预解析数据"（系统解析报告描述符后的结果）翻译成我们能读的 C# 结构：
    ///   - 按钮类字段清单（ButtonCaps）
    ///   - 数值类字段清单（ValueCaps）
    ///   - 集合树（LinkCollectionNodes）
    ///   - 以及"让系统替我们解码一个报告"的结果（HidP_GetData）
    ///
    /// 【为什么这些信息等价于报告描述符】
    /// 报告描述符是一串"指令"，告诉系统：第几个 bit 是什么用途、取值范围多少、属于哪个集合。
    /// 系统解析完，就得到这里的三张表。表里每一行，都能对应回描述符里的若干条 item
    /// （格式：Usage Page / Usage / Logical Min / Logical Max / Report Size / Report Count / Input）。
    /// 所以虽然拿不到原始字节，我们依然能完整理解这块触摸板的协议内容。
    /// </summary>
    public static class HidCaps
    {
        /// <summary>
        /// 自检：我们声明的 C# 结构体必须和系统里真实的结构体一样大。
        /// 不一致的话，系统会往我们的缓冲区里多写数据 → 踩坏内存 → 程序莫名其妙崩溃。
        /// 这是把"实测标定值"写进代码里当保险丝。
        /// </summary>
        private static void EnsureStructSize(Type type, int expectedSize, string name)
        {
            int actual = Marshal.SizeOf(type);
            if (actual != expectedSize)
            {
                throw new InvalidOperationException(
                    name + " 在 C# 里声明为 " + actual + " 字节，但本机实测的系统结构体是 " +
                    expectedSize + " 字节。请按 docs/03 文档里的“结构体标定”方法重新标定，否则会踩坏内存。");
            }
        }

        /// <summary>取按钮类字段清单（描述符里所有 1 位宽的可变字段，包括触摸板的 Tip Switch）。</summary>
        public static NativeMethods.HIDP_BUTTON_CAPS[] GetButtonCaps(
            IntPtr preparsedData, NativeMethods.HIDP_CAPS caps, int reportType)
        {
            EnsureStructSize(typeof(NativeMethods.HIDP_BUTTON_CAPS), NativeMethods.ButtonCapsSize, "HIDP_BUTTON_CAPS");
            ushort count = reportType == NativeMethods.HidP_Input ? caps.NumberInputButtonCaps
                         : reportType == NativeMethods.HidP_Output ? caps.NumberOutputButtonCaps
                         : caps.NumberFeatureButtonCaps;

            if (count == 0) return new NativeMethods.HIDP_BUTTON_CAPS[0];

            var array = new NativeMethods.HIDP_BUTTON_CAPS[count];
            ushort length = count;
            int status = NativeMethods.HidP_GetButtonCaps(reportType, array, ref length, preparsedData);
            if (status != NativeMethods.HIDP_STATUS_SUCCESS)
            {
                throw new InvalidOperationException("HidP_GetButtonCaps 失败，状态码 0x" + status.ToString("X8"));
            }

            if (length < count) Array.Resize(ref array, length);
            return array;
        }

        /// <summary>取数值类字段清单（多位宽字段，如触点 ID、X、Y、压力）。</summary>
        public static NativeMethods.HIDP_VALUE_CAPS[] GetValueCaps(
            IntPtr preparsedData, NativeMethods.HIDP_CAPS caps, int reportType)
        {
            EnsureStructSize(typeof(NativeMethods.HIDP_VALUE_CAPS), NativeMethods.ValueCapsSize, "HIDP_VALUE_CAPS");
            ushort count = reportType == NativeMethods.HidP_Input ? caps.NumberInputValueCaps
                         : reportType == NativeMethods.HidP_Output ? caps.NumberOutputValueCaps
                         : caps.NumberFeatureValueCaps;

            if (count == 0) return new NativeMethods.HIDP_VALUE_CAPS[0];

            var array = new NativeMethods.HIDP_VALUE_CAPS[count];
            ushort length = count;
            int status = NativeMethods.HidP_GetValueCaps(reportType, array, ref length, preparsedData);
            if (status != NativeMethods.HIDP_STATUS_SUCCESS)
            {
                throw new InvalidOperationException("HidP_GetValueCaps 失败，状态码 0x" + status.ToString("X8"));
            }

            if (length < count) Array.Resize(ref array, length);
            return array;
        }

        /// <summary>取集合树（描述符里 Collection/EndCollection 形成的层级）。</summary>
        public static NativeMethods.HIDP_LINK_COLLECTION_NODE[] GetLinkCollectionNodes(
            IntPtr preparsedData, NativeMethods.HIDP_CAPS caps)
        {
            uint count = caps.NumberLinkCollectionNodes;
            if (count == 0) return new NativeMethods.HIDP_LINK_COLLECTION_NODE[0];

            var array = new NativeMethods.HIDP_LINK_COLLECTION_NODE[count];
            uint length = count;
            int status = NativeMethods.HidP_GetLinkCollectionNodes(array, ref length, preparsedData);
            if (status != NativeMethods.HIDP_STATUS_SUCCESS)
            {
                throw new InvalidOperationException("HidP_GetLinkCollectionNodes 失败，状态码 0x" + status.ToString("X8"));
            }

            if (length < count) Array.Resize(ref array, (int)length);
            return array;
        }

        /// <summary>
        /// 让系统替我们"按描述符解包"一个报告，返回 数据序号 → 原始值 的字典。
        /// 返回值同时包含了按钮（0/1）和数值字段。
        /// </summary>
        public static Dictionary<ushort, uint> GetDataTable(
            IntPtr preparsedData, NativeMethods.HIDP_CAPS caps, int reportType, byte[] report)
        {
            var table = new Dictionary<ushort, uint>();
            int count = reportType == NativeMethods.HidP_Input ? caps.NumberInputDataIndices
                      : reportType == NativeMethods.HidP_Output ? caps.NumberOutputDataIndices
                      : caps.NumberFeatureDataIndices;
            if (count <= 0) return table;

            var dataList = new NativeMethods.HIDP_DATA[count];
            uint length = (uint)count;
            int status = NativeMethods.HidP_GetData(
                reportType, dataList, ref length, preparsedData, report, (uint)report.Length);
            if (status != NativeMethods.HIDP_STATUS_SUCCESS) return table;

            for (uint i = 0; i < length && i < dataList.Length; i++)
            {
                table[dataList[i].DataIndex] = dataList[i].RawValue;
            }
            return table;
        }

        // =================================================================
        // 读取联合体（union）里的字段
        // =================================================================
        // C 的联合体让同一块内存有两种读法，C# 没有这种语法，
        // 所以我们按 IsRange 判断后，手动取对应位置的 ushort（见 NativeMethods.HIDP_UNION16）。

        /// <summary>按钮字段：取用途最小值（IsRange 时）。</summary>
        public static ushort ButtonUsage(ref NativeMethods.HIDP_BUTTON_CAPS c)
        {
            return c.Union.W0; // 范围时 = UsageMin，单值时 = Usage
        }

        public static ushort ButtonUsageMax(ref NativeMethods.HIDP_BUTTON_CAPS c)
        {
            return c.Union.W1; // 仅 IsRange = true 时有意义
        }

        public static ushort ButtonDataIndex(ref NativeMethods.HIDP_BUTTON_CAPS c)
        {
            return c.Union.W6; // 范围时 = DataIndexMin，单值时 = DataIndex
        }

        public static ushort ButtonDataIndexMax(ref NativeMethods.HIDP_BUTTON_CAPS c)
        {
            return c.Union.W7; // 仅 IsRange = true 时有意义
        }

        /// <summary>数值字段：取用途（或用途范围起点）。</summary>
        public static ushort ValueUsage(ref NativeMethods.HIDP_VALUE_CAPS c)
        {
            return c.Union.W0;
        }

        public static ushort ValueUsageMax(ref NativeMethods.HIDP_VALUE_CAPS c)
        {
            return c.Union.W1;
        }

        public static ushort ValueDataIndex(ref NativeMethods.HIDP_VALUE_CAPS c)
        {
            return c.Union.W6;
        }

        public static ushort ValueDataIndexMax(ref NativeMethods.HIDP_VALUE_CAPS c)
        {
            return c.Union.W7;
        }

        /// <summary>集合类型（描述符里 Collection 后面的那个数字：0=Physical 1=Application 2=Logical …）。</summary>
        public static int CollectionType(uint bitFields)
        {
            // 低 8 位是类型，其余位是 IsAlias 等标志
            return (int)(bitFields & 0xFF);
        }

        /// <summary>集合类型的中文说明。</summary>
        public static string CollectionTypeName(int type)
        {
            switch (type)
            {
                case 0: return "Physical（物理集合）";
                case 1: return "Application（应用集合，顶层）";
                case 2: return "Logical（逻辑集合，如“一个触点”）";
                case 3: return "Report（报告集合）";
                case 4: return "NamedArray（命名数组）";
                case 5: return "UsageSwitch";
                case 6: return "UsageModifier";
                default: return "未知(" + type + ")";
            }
        }
    }
}