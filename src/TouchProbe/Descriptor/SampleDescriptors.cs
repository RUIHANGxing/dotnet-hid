using System;
using System.Collections.Generic;
using System.IO;

namespace TouchProbe.Descriptor
{
    /// <summary>
    /// 【本文件是什么】
    /// 两个"教学用"的报告描述符，都带逐条注释：
    ///
    ///   1. 经典鼠标（50 字节，规格书里的标准例子）—— 用来学 item 编码的最基本套路；
    ///   2. 仿触摸板（带报告 ID、两个触点、功能报告）—— 形状和真实触摸板一致，用来看懂真实设备的解析结果。
    ///
    /// 【为什么不直接解析我笔记本上触摸板的描述符？】
    /// 因为 Windows 把原始描述符字节留在了内核里，用户态程序拿不到（这是实测结论，见 docs/01）。
    /// 我们能拿到的是"系统解析后的结构"（caps/字段表），以及原始输入报告。
    /// 所以：字节级的学习用这两个示例；真实设备的数据用 caps + 实时报告来对照。
    ///
    /// 如果你以后在 Linux 上（/sys/class/hidraw/*/device/report_descriptor）或从厂商文档里
    /// 拿到了真实描述符的二进制文件，可以用 `parse --file 文件路径` 让这个解析器解析真货。
    /// </summary>
    public static class SampleDescriptors
    {
        /// <summary>描述符中的一小段字节 + 它的含义（用于逐条讲解）。</summary>
        public sealed class Chunk
        {
            public string Hex;      // 形如 "05 01"
            public string Meaning;  // 这一段在说什么

            public Chunk(string hex, string meaning)
            {
                Hex = hex;
                Meaning = meaning;
            }
        }

        /// <summary>一个带注释的示例描述符。</summary>
        public sealed class AnnotatedDescriptor
        {
            public string Title;
            public string Story;
            public List<Chunk> Chunks = new List<Chunk>();
            public byte[] Bytes;

            /// <summary>把 Chunk 列表拼成真正的字节数组（同时校验十六进制写法有没有错）。</summary>
            public static AnnotatedDescriptor FromChunks(string title, string story, params Chunk[] chunks)
            {
                var descriptor = new AnnotatedDescriptor { Title = title, Story = story };
                var bytes = new List<byte>();

                foreach (var chunk in chunks)
                {
                    descriptor.Chunks.Add(chunk);
                    string[] parts = chunk.Hex.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string part in parts)
                    {
                        bytes.Add(Convert.ToByte(part, 16));
                    }
                }

                descriptor.Bytes = bytes.ToArray();
                return descriptor;
            }

            /// <summary>每一段在字节流里的起始偏移（打印表格时用）。</summary>
            public int OffsetOfChunk(int index)
            {
                int offset = 0;
                for (int i = 0; i < index; i++)
                {
                    offset += Chunks[i].Hex.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                }
                return offset;
            }
        }

        /// <summary>
        /// 经典鼠标描述符（来自 USB HID 规范附录里的标准示例，50 字节）。
        /// 学会它，就看懂了描述符最核心的三件事：全局状态、局部信息、Main 条目。
        /// </summary>
        public static AnnotatedDescriptor Mouse()
        {
            return AnnotatedDescriptor.FromChunks(
                "经典鼠标描述符（52 字节）",
                "这段描述符告诉系统：我会送 1 个字节的按键状态（3 个按钮 + 5 位填充），" +
                "后面跟 3 个字节的 X/Y/滚轮位移。读懂它，就读懂了描述符的基本结构。",

                new Chunk("05 01", "Usage Page (0x01 = Generic Desktop 通用桌面)：下面说的用途都在这页里"),
                new Chunk("09 02", "Usage (0x02 = Mouse)：本设备是一只鼠标"),
                new Chunk("A1 01", "Collection (Application)：开始一个应用集合，即“这个设备的整体”"),

                new Chunk("09 01", "Usage (0x01 = Pointer)：下面这组数据描述的是“指针”"),
                new Chunk("A1 00", "Collection (Physical)：开始一个物理集合（把相关的输入放在一起）"),

                new Chunk("05 09", "Usage Page (0x09 = Button)：切换到按键页"),
                new Chunk("19 01", "Usage Minimum (0x01 = Button 1)：用途范围的起点"),
                new Chunk("29 03", "Usage Maximum (0x03 = Button 3)：用途范围的终点"),
                new Chunk("15 00", "Logical Minimum (0)：取值下限是 0"),
                new Chunk("25 01", "Logical Maximum (1)：取值上限是 1（也就是说每个值只有 0/1 两种）"),
                new Chunk("95 03", "Report Count (3)：这样的值连续出现 3 个"),
                new Chunk("75 01", "Report Size (1)：每个值占 1 位"),
                new Chunk("81 02", "Input (Data,Var,Abs)：产出 3 位输入数据 → 就是左/右/中键"),

                new Chunk("95 01", "Report Count (1)：再来 1 个值"),
                new Chunk("75 05", "Report Size (5)：占 5 位"),
                new Chunk("81 03", "Input (Const,Var,Abs)：常量（Const）表示这 5 位是“填充位”，没有含义，只是凑满 1 字节"),

                new Chunk("05 01", "Usage Page (0x01 = Generic Desktop)：切回通用桌面页"),
                new Chunk("09 30", "Usage (0x30 = X)：X 轴"),
                new Chunk("09 31", "Usage (0x31 = Y)：Y 轴"),
                new Chunk("09 38", "Usage (0x38 = Wheel)：滚轮"),
                new Chunk("15 81", "Logical Minimum (-127)：注意这条命令里 0x81 被解释成 -127（有符号数）"),
                new Chunk("25 7F", "Logical Maximum (127)：位移范围 -127 ~ 127"),
                new Chunk("75 08", "Report Size (8)：每个值占 8 位 = 1 字节"),
                new Chunk("95 03", "Report Count (3)：连续 3 个值 → X、Y、滚轮"),
                new Chunk("81 06", "Input (Data,Var,Rel)：Rel = 相对值（鼠标报的是“移动了多少”，不是“现在在哪”）"),

                new Chunk("C0", "End Collection：结束物理集合"),
                new Chunk("C0", "End Collection：结束应用集合"));
        }

        /// <summary>
        /// 仿触摸板描述符（形状与真实精确式触摸板一致，但为了教学写得很短）。
        /// 重点看三件事：报告 ID 是怎么用的、一个触点就是一个逻辑集合、填充位是怎么回事。
        /// </summary>
        public static AnnotatedDescriptor Touchpad()
        {
            return AnnotatedDescriptor.FromChunks(
                "仿触摸板描述符（带报告 ID，2 个触点）",
                "它演示真实触摸板的核心结构：一个应用集合下面挂若干个“Finger（手指）”逻辑集合，" +
                "每个逻辑集合里有触点编号、是否按下、X、Y。第一个字节是报告 ID，用来说明“这包报告属于哪一类”。",

                new Chunk("05 0D", "Usage Page (0x0D = Digitizer 数字化仪)：触摸类设备都在这页"),
                new Chunk("09 05", "Usage (0x05 = Touch Pad)：本设备是触摸板"),
                new Chunk("A1 01", "Collection (Application)：应用集合开始"),

                new Chunk("85 01", "Report ID (1)：下面这段输入数据属于“1 号报告”（报告的第 1 个字节会带 1）"),
                new Chunk("09 54", "Usage (0x54 = Contact Count)：当前有几个触点"),
                new Chunk("15 00", "Logical Minimum (0)"),
                new Chunk("25 05", "Logical Maximum (5)：最多 5 个触点"),
                new Chunk("75 08", "Report Size (8)：占 1 字节"),
                new Chunk("95 01", "Report Count (1)"),
                new Chunk("81 02", "Input (Data,Var,Abs) → 得到 1 字节“触点数量”"),

                new Chunk("09 22", "Usage (0x22 = Finger)：下面这组描述“一根手指”"),
                new Chunk("A1 02", "Collection (Logical)：逻辑集合开始 —— 这就是“一个触点”"),
                new Chunk("09 51", "Usage (0x51 = Contact Identifier)：触点编号（区分是哪根手指）"),
                new Chunk("15 00", "Logical Minimum (0)"),
                new Chunk("25 0F", "Logical Maximum (15)：编号 0~15"),
                new Chunk("75 08", "Report Size (8)"),
                new Chunk("95 01", "Report Count (1)"),
                new Chunk("81 02", "Input (Data,Var,Abs) → 1 字节触点编号"),

                new Chunk("09 42", "Usage (0x42 = Tip Switch)：手指是否按下"),
                new Chunk("09 47", "Usage (0x47 = Confidence)：这组数据是否可信"),
                new Chunk("15 00", "Logical Minimum (0)"),
                new Chunk("25 01", "Logical Maximum (1)：只有 0/1"),
                new Chunk("75 01", "Report Size (1)：每个值 1 位"),
                new Chunk("95 02", "Report Count (2)：两个值 → 共 2 位"),
                new Chunk("81 02", "Input (Data,Var,Abs) → 2 位：按下 + 可信度"),

                new Chunk("75 06", "Report Size (6)：接下来 6 位"),
                new Chunk("95 01", "Report Count (1)"),
                new Chunk("81 03", "Input (Const,Var,Abs) → 6 位填充，让“1 位字段”凑满整字节"),

                new Chunk("05 01", "Usage Page (0x01 = Generic Desktop)：坐标用通用桌面页的 X/Y"),
                new Chunk("09 30", "Usage (0x30 = X)：X 坐标"),
                new Chunk("09 31", "Usage (0x31 = Y)：Y 坐标"),
                new Chunk("15 00", "Logical Minimum (0)"),
                new Chunk("26 FF 1F", "Logical Maximum (8191)：0x1FFF，注意是 2 字节小端：FF 在前、1F 在后"),
                new Chunk("75 10", "Report Size (16)：每个坐标占 16 位"),
                new Chunk("95 02", "Report Count (2)：X 和 Y 各一个"),
                new Chunk("81 02", "Input (Data,Var,Abs) → 4 字节坐标（绝对值，说明是“现在在哪”）"),
                new Chunk("05 0D", "Usage Page (0x0D)：切回数字化仪页，准备下一个触点"),

                new Chunk("C0", "End Collection：结束第 1 个触点的逻辑集合"),

                new Chunk("09 22", "Usage (0x22 = Finger)：第 2 根手指"),
                new Chunk("A1 02", "Collection (Logical)"),
                new Chunk("09 51", "Usage (0x51 = Contact Identifier)"),
                new Chunk("15 00", "Logical Minimum (0)"),
                new Chunk("25 0F", "Logical Maximum (15)"),
                new Chunk("75 08", "Report Size (8)"),
                new Chunk("95 01", "Report Count (1)"),
                new Chunk("81 02", "Input (Data,Var,Abs)"),
                new Chunk("09 42", "Usage (0x42 = Tip Switch)"),
                new Chunk("09 47", "Usage (0x47 = Confidence)"),
                new Chunk("15 00", "Logical Minimum (0)"),
                new Chunk("25 01", "Logical Maximum (1)"),
                new Chunk("75 01", "Report Size (1)"),
                new Chunk("95 02", "Report Count (2)"),
                new Chunk("81 02", "Input (Data,Var,Abs)"),
                new Chunk("75 06", "Report Size (6)"),
                new Chunk("95 01", "Report Count (1)"),
                new Chunk("81 03", "Input (Const,Var,Abs)：填充位"),
                new Chunk("05 01", "Usage Page (0x01 = Generic Desktop)"),
                new Chunk("09 30", "Usage (0x30 = X)"),
                new Chunk("09 31", "Usage (0x31 = Y)"),
                new Chunk("15 00", "Logical Minimum (0)"),
                new Chunk("26 FF 1F", "Logical Maximum (8191)"),
                new Chunk("75 10", "Report Size (16)"),
                new Chunk("95 02", "Report Count (2)"),
                new Chunk("81 02", "Input (Data,Var,Abs)"),
                new Chunk("05 0D", "Usage Page (0x0D)"),
                new Chunk("C0", "End Collection：结束第 2 个触点"),

                new Chunk("85 02", "Report ID (2)：下面是一条“功能报告”（主机可以读写的配置项）"),
                new Chunk("09 55", "Usage (0x55 = Contact Count Maximum)：最多支持几个触点"),
                new Chunk("15 00", "Logical Minimum (0)"),
                new Chunk("25 0F", "Logical Maximum (15)"),
                new Chunk("75 08", "Report Size (8)"),
                new Chunk("95 01", "Report Count (1)"),
                new Chunk("B1 02", "Feature (Data,Var,Abs)：功能报告（不是输入报告）"),

                new Chunk("C0", "End Collection：结束应用集合"));
        }

        /// <summary>从文件读取一份描述符（如从 Linux 的 hidraw 里 dump 出来的真实描述符）。</summary>
        public static byte[] LoadFromFile(string path)
        {
            byte[] raw = File.ReadAllBytes(path);

            // 兼容"文本形式的十六进制"：如果不是二进制，就尝试按 hex 文本解析
            bool looksLikeText = true;
            foreach (byte b in raw)
            {
                bool ok = (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F')
                          || b == ' ' || b == '\r' || b == '\n' || b == '\t' || b == ',';
                if (!ok) { looksLikeText = false; break; }
            }

            if (!looksLikeText) return raw;

            var bytes = new List<byte>();
            string text = System.Text.Encoding.ASCII.GetString(raw);
            foreach (string token in text.Split(new[] { ' ', '\r', '\n', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                bytes.Add(Convert.ToByte(token, 16));
            }
            return bytes.ToArray();
        }
    }
}