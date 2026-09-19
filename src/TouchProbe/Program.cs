using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TouchProbe.Descriptor;
using TouchProbe.Hid;
using TouchProbe.Native;
using TouchProbe.View;

namespace TouchProbe
{
    /// <summary>
    /// 【本文件是什么】
    /// 程序入口与四个命令：
    ///   list  列举系统里所有 HID 设备
    ///   caps  打印"系统解析出的触摸板结构"（描述符的语义），并推导位布局
    ///   parse 用我们自己的解析器解析报告描述符字节（内置两个教学示例，也可 --file 指定文件）
    ///   watch 实时读取触摸板的输入报告，解码出触点并画出来
    ///
    /// 【怎么跑】
    ///   命令行： dotnet run --project src/TouchProbe -- list
    ///   VS 里：  把 Program.cs 打开 → 调试 → 开始执行（也可以直接 F5）
    /// </summary>
    internal static class Program
    {
        // ================= 命令行的解析 =================

        /// <summary>命令行选项。</summary>
        private sealed class Options
        {
            public int? Device;      // --device N
            public string File;      // --file 路径
            public bool Raw;         // --raw 只看十六进制
            public bool Plain;       // --plain 不刷屏，逐行打印
            public bool Verify;      // --verify 打印"我们算的 vs 系统算的"对照表
            public bool Mouse;       // --mouse 用鼠标（而不是触摸板）
            public bool Async;       // --async 用 Task/Channel 异步管线（学 Task 用）

            public static Options Parse(string[] args)
            {
                var options = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i].ToLowerInvariant();
                    if (a == "--device" && i + 1 < args.Length)
                    {
                        int value;
                        if (int.TryParse(args[i + 1], out value)) options.Device = value;
                        i++;
                    }
                    else if (a == "--file" && i + 1 < args.Length)
                    {
                        options.File = args[i + 1];
                        i++;
                    }
                    else if (a == "--raw") options.Raw = true;
                    else if (a == "--plain") options.Plain = true;
                    else if (a == "--verify") options.Verify = true;
                    else if (a == "--mouse") options.Mouse = true;
                    else if (a == "--async") options.Async = true;
                }
                return options;
            }
        }

        private static async Task<int> Main(string[] args)
        {
            ConsoleUi.Setup();

            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            Options options = Options.Parse(args);

            try
            {
                switch (command)
                {
                    case "list": return CommandList();
                    case "caps": return CommandCaps(options);
                    case "parse": return CommandParse(options);
                    case "watch": return await CommandWatch(options);
                    default: PrintHelp(); return 0;
                }
            }
            catch (Exception ex)
            {
                ConsoleUi.Error(ex.Message);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            ConsoleUi.Title("TouchProbe — 用真实的 HID 设备学协议");
            Console.WriteLine();
            Console.WriteLine("  可用命令：");
            Console.WriteLine("    list                           列出系统里所有 HID 设备");
            Console.WriteLine("    caps   [--device N]            打印设备的“解析结果”（集合树 + 字段表 + 位布局）");
            Console.WriteLine("    parse  [--file 文件]           用自研解析器解析报告描述符字节（默认解析内置教学示例）");
            Console.WriteLine("    watch  [选项]                  实时读取并解码报告（重点）");
            Console.WriteLine();
            Console.WriteLine("  watch 的选项：");
            Console.WriteLine("    --mouse      用鼠标（相对位移，画指针轨迹）而不是触摸板");
            Console.WriteLine("    --async      用 Task + Channel 的异步管线（学习 C# Task 的重点示范）");
            Console.WriteLine("    --device N   指定 list 里的第 N 个设备");
            Console.WriteLine("    --raw        只打印原始十六进制，不解码");
            Console.WriteLine("    --plain      逐行滚动打印（适合重定向到文件）");
            Console.WriteLine("    --verify     额外打印“我们自己算的值 vs 系统解码的值”对照表");
            Console.WriteLine();
            Console.WriteLine("  常用组合：");
            Console.WriteLine("    dotnet run --project src/TouchProbe -- watch --mouse");
            Console.WriteLine("    dotnet run --project src/TouchProbe -- watch --mouse --async");
            Console.WriteLine("    dotnet run --project src/TouchProbe -- watch --mouse --plain --verify");
            Console.WriteLine();
        }

        // ================= 命令 1：list =================

        private static int CommandList()
        {
            ConsoleUi.Title("系统里的 HID 设备");
            ConsoleUi.Note("每一行是一个 HID 设备接口。带 ★ 的是“疑似触摸板”（按用途页/用途判断）。");

            List<HidDeviceInfo> devices = HidDeviceEnumerator.Enumerate();

            var rows = new List<string[]>();
            foreach (var d in devices)
            {
                string usage = "0x" + d.UsagePage.ToString("X2") + ":0x" + d.Usage.ToString("X2") + " " +
                               HidUsageNames.UsageName(d.UsagePage, d.Usage);
                string id = "0x" + d.VendorId.ToString("X4") + ":0x" + d.ProductId.ToString("X4");

                rows.Add(new[]
                {
                    d.Index.ToString(),
                    d.LooksLikeTouchpad ? "★" : "",
                    usage,
                    id,
                    d.InputReportBytes.ToString(),
                    d.HasReadAccess ? "可读" : "独占",
                    ConsoleUi.Truncate(string.IsNullOrEmpty(d.ProductName) ? d.InstanceId : d.ProductName, 30)
                });
            }

            ConsoleUi.Table(
                new[] { "#", "触摸板", "顶层用途", "VID:PID", "输入字节", "能否直接读", "名字" },
                rows,
                new[] { 3, 6, 30, 12, 8, 10, 32 });

            Console.WriteLine();
            ConsoleUi.Note("“能否直接读”= 我们能不能用 ReadFile 直接读它的报告。");
            ConsoleUi.Note("  标“独占”的设备被系统驱动独占打开了（鼠标、键盘、触摸板基本都是），");
            ConsoleUi.Note("  watch 命令会自动改用 Raw Input 通道拿原始报告，功能不受影响。");
            ConsoleUi.Note("提示：触摸板用 watch，鼠标用 watch --mouse，也可以 --device <序号> 手动指定。");
            return 0;
        }

        // ================= 选设备（caps / watch 共用） =================

        private static HidDeviceInfo SelectDevice(Options options)
        {
            List<HidDeviceInfo> devices = HidDeviceEnumerator.Enumerate();

            if (options.Device.HasValue)
            {
                HidDeviceInfo picked = devices.FirstOrDefault(d => d.Index == options.Device.Value);
                if (picked == null)
                    throw new InvalidOperationException("没有序号为 " + options.Device.Value + " 的设备，先跑 list 看看。");
                return picked;
            }

            // --mouse：挑"顶层用途是鼠标"的设备。
            // 注意：鼠标集合通常也被系统独占（mouhid 驱动），所以这里不要求 HasReadAccess，
            // 读不到就用 Raw Input —— Raw Input 对鼠标是官方支持的标准通道。
            if (options.Mouse)
            {
                HidDeviceInfo mouse = devices
                    .Where(d => d.UsagePage == 0x01 && d.Usage == 0x02 && d.InputReportBytes > 0)
                    .OrderByDescending(d => d.HasReadAccess)          // 能直接读的优先
                    .ThenByDescending(d => d.InputReportBytes)        // 报告字段多的优先
                    .FirstOrDefault();

                if (mouse == null)
                {
                    throw new InvalidOperationException(
                        "没有找到鼠标设备（用途页 0x01 / 用途 0x02）。\n" +
                        "      跑 list 看看有哪些设备，或用 --device N 指定一个。");
                }
                return mouse;
            }

            // 默认：自动挑触摸板，优先"顶层用途正好是 Digitizer/Touch Pad"的
            HidDeviceInfo best = devices
                .Where(d => d.LooksLikeTouchpad)
                .OrderByDescending(d => d.UsagePage == 0x0D && d.Usage == 0x05)
                .ThenByDescending(d => d.CapsAvailable)
                .FirstOrDefault();

            if (best == null)
                throw new InvalidOperationException("没找到触摸板。先跑 list，然后用 caps --device <序号> 指定一个设备。");

            return best;
        }

        // ================= 命令 2：caps =================

        private static int CommandCaps(Options options)
        {
            HidDeviceInfo info = SelectDevice(options);

            ConsoleUi.Title("触摸板的“解析结果”——它等价于报告描述符的内容");
            ConsoleUi.Note("说明：Windows 用户态拿不到描述符的原始字节（字节留在内核里），");
            ConsoleUi.Note("      但系统会把解析后的结构给我们，信息量是一样的。");

            ConsoleUi.Step(1, "设备身份");
            ConsoleUi.KeyValue("设备路径", info.DevicePath, 12);
            ConsoleUi.KeyValue("设备实例 ID", info.InstanceId, 12);
            ConsoleUi.KeyValue("厂商 / 产品", "0x" + info.VendorId.ToString("X4") + " / 0x" + info.ProductId.ToString("X4"), 12);
            ConsoleUi.KeyValue("名字", info.ProductName, 12);

            using (HidDevice device = HidDevice.Open(info))
            {
                NativeMethods.HIDP_CAPS caps = device.Caps;

                ConsoleUi.Step(2, "顶层能力（HIDP_CAPS）：我是谁、我送多长的报告");
                ConsoleUi.KeyValue("顶层用途", "0x" + caps.UsagePage.ToString("X2") + ":0x" + caps.Usage.ToString("X2") +
                                              " (" + HidUsageNames.Describe(caps.UsagePage, caps.Usage) + ")", 16);
                ConsoleUi.KeyValue("输入报告长度", caps.InputReportByteLength + " 字节", 16);
                ConsoleUi.KeyValue("输出报告长度", caps.OutputReportByteLength + " 字节", 16);
                ConsoleUi.KeyValue("功能报告长度", caps.FeatureReportByteLength + " 字节", 16);
                ConsoleUi.KeyValue("集合节点个数", caps.NumberLinkCollectionNodes.ToString(), 16);
                ConsoleUi.KeyValue("输入字段统计", caps.NumberInputButtonCaps + " 组按钮字段 / " +
                                                    caps.NumberInputValueCaps + " 组数值字段 / " +
                                                    caps.NumberInputDataIndices + " 个数据值", 16);

                // ---- 集合树 ----
                ConsoleUi.Step(3, "集合树（描述符里 Collection/EndCollection 形成的层级）");
                var nodes = HidCaps.GetLinkCollectionNodes(device.PreparsedData, caps);
                PrintCollectionTree(nodes);

                // ---- 字段表 ----
                ConsoleUi.Step(4, "字段表（每一行 = 描述符里的一条 Usage/Logical/Report/Input 组合）");
                var buttons = HidCaps.GetButtonCaps(device.PreparsedData, caps, NativeMethods.HidP_Input);
                var values = HidCaps.GetValueCaps(device.PreparsedData, caps, NativeMethods.HidP_Input);
                PrintFieldTable(buttons, values);

                // ---- 位布局推导 ----
                ConsoleUi.Step(5, "我们自己推导的位布局（这就是解码报告用的地图）");
                ReportLayout layout = ReportLayout.Build(caps, buttons, values);
                PrintLayout(layout);

                Console.WriteLine();
                ConsoleUi.Ok("以上就是这块触摸板的全部“协议内容”。下一步：watch 命令把它跑起来。");
            }

            return 0;
        }

        /// <summary>打印集合树（缩进表示层级）。</summary>
        private static void PrintCollectionTree(NativeMethods.HIDP_LINK_COLLECTION_NODE[] nodes)
        {
            if (nodes.Length == 0)
            {
                ConsoleUi.Note("（没有集合节点）");
                return;
            }

            var printed = new HashSet<int>();
            PrintNode(nodes, 0, 0, printed);
        }

        private static void PrintNode(
            NativeMethods.HIDP_LINK_COLLECTION_NODE[] nodes, int index, int depth, HashSet<int> printed)
        {
            if (index < 0 || index >= nodes.Length || printed.Contains(index)) return;
            printed.Add(index);

            var node = nodes[index];
            string usage = "0x" + node.LinkUsagePage.ToString("X2") + ":0x" + node.LinkUsage.ToString("X2") +
                           " (" + HidUsageNames.Describe(node.LinkUsagePage, node.LinkUsage) + ")";

            ConsoleUi.Info(new string(' ', depth * 2) + "[" + index + "] " + usage +
                           "  类型: " + HidCaps.CollectionTypeName(HidCaps.CollectionType(node.BitFields)));

            // 遍历子节点（FirstChild → NextSibling 形成链表）
            int child = node.FirstChild;
            int guard = 0;
            while (child != 0 && guard++ < nodes.Length)
            {
                PrintNode(nodes, child, depth + 1, printed);
                child = nodes[child].NextSibling;
            }
        }

        /// <summary>打印输入报告字段表。</summary>
        private static void PrintFieldTable(
            NativeMethods.HIDP_BUTTON_CAPS[] buttons, NativeMethods.HIDP_VALUE_CAPS[] values)
        {
            var rows = new List<string[]>();

            for (int i = 0; i < buttons.Length; i++)
            {
                var c = buttons[i];
                int usageMin = HidCaps.ButtonUsage(ref c);
                int usageMax = c.IsRange ? HidCaps.ButtonUsageMax(ref c) : usageMin;
                int count = c.IsRange ? HidCaps.ButtonDataIndexMax(ref c) - HidCaps.ButtonDataIndex(ref c) + 1 : 1;

                string usageText = usageMin == usageMax
                    ? HidUsageNames.Describe(c.UsagePage, usageMin)
                    : HidUsageNames.Describe(c.UsagePage, usageMin) + " ~ " + HidUsageNames.Describe(c.UsagePage, usageMax);

                rows.Add(new[]
                {
                    c.ReportID.ToString(),
                    "1 位 × " + count,
                    ConsoleUi.Truncate(usageText, 40),
                    "0 ~ 1",
                    "0x" + c.LinkUsagePage.ToString("X2") + ":0x" + c.LinkUsage.ToString("X2"),
                    HidCaps.ButtonDataIndex(ref c).ToString()
                });
            }

            for (int i = 0; i < values.Length; i++)
            {
                var c = values[i];
                int usageMin = HidCaps.ValueUsage(ref c);
                int usageMax = c.IsRange ? HidCaps.ValueUsageMax(ref c) : usageMin;

                string usageText = usageMin == usageMax
                    ? HidUsageNames.Describe(c.UsagePage, usageMin)
                    : HidUsageNames.Describe(c.UsagePage, usageMin) + " ~ " + HidUsageNames.Describe(c.UsagePage, usageMax);

                rows.Add(new[]
                {
                    c.ReportID.ToString(),
                    c.BitSize + " 位 × " + c.ReportCount,
                    ConsoleUi.Truncate(usageText, 40),
                    c.LogicalMin + " ~ " + c.LogicalMax,
                    "0x" + c.LinkUsagePage.ToString("X2") + ":0x" + c.LinkUsage.ToString("X2"),
                    HidCaps.ValueDataIndex(ref c).ToString()
                });
            }

            ConsoleUi.Table(
                new[] { "报告ID", "位宽×个数", "用途", "逻辑范围", "所属集合", "数据序号" },
                rows,
                new[] { 7, 12, 42, 14, 16, 9 });

            ConsoleUi.Note("“所属集合”指向集合树里的节点：触摸板通常每个触点一个逻辑集合（LinkUsage = Finger）。");
        }

        /// <summary>打印我们推导出的位布局。</summary>
        private static void PrintLayout(ReportLayout layout)
        {
            foreach (var pair in layout.ByReportId.OrderBy(p => p.Key))
            {
                Console.WriteLine();
                ConsoleUi.Info("报告 ID " + pair.Key + "（" + (layout.HasReportIds ? "报告第 1 字节 = " + pair.Key : "无报告 ID") + "）：");

                var rows = new List<string[]>();
                foreach (var f in pair.Value)
                {
                    string range = f.BitOffset + " ~ " + (f.BitOffset + f.TotalBits - 1);
                    string usage = f.IsRange
                        ? HidUsageNames.Describe(f.UsagePage, f.Usage) + " ~ " + HidUsageNames.Describe(f.UsagePage, f.UsageMax)
                        : HidUsageNames.Describe(f.UsagePage, f.Usage);

                    rows.Add(new[]
                    {
                        range,
                        f.BitSize + "×" + f.ReportCount,
                        ConsoleUi.Truncate(usage, 38),
                        f.LogicalMin + "~" + f.LogicalMax,
                        f.IsButtonClass ? "按钮类" : "数值类"
                    });
                }

                ConsoleUi.Table(
                    new[] { "位偏移", "位宽×个数", "用途", "逻辑范围", "类别" },
                    rows,
                    new[] { 12, 10, 40, 14, 8 });
            }

            Console.WriteLine();
            foreach (string note in layout.Notes) ConsoleUi.Warn(note);
        }

        // ================= 命令 3：parse =================

        private static int CommandParse(Options options)
        {
            if (!string.IsNullOrEmpty(options.File))
            {
                byte[] bytes = SampleDescriptors.LoadFromFile(options.File);
                ConsoleUi.Title("解析描述符文件：" + options.File);
                ParseAndPrint(bytes, null);
                return 0;
            }

            ConsoleUi.Title("用我们自己的解析器读报告描述符（教学示例）");
            ConsoleUi.Note("为什么用示例：Windows 用户态拿不到真实设备的描述符字节，原因见 docs/01 文档。");
            ConsoleUi.Note("如果你有真实描述符文件（例如 Linux 的 hidraw dump），用 --file 传进来。");

            SampleDescriptors.AnnotatedDescriptor mouse = SampleDescriptors.Mouse();
            PrintAnnotated(mouse);

            SampleDescriptors.AnnotatedDescriptor touchpad = SampleDescriptors.Touchpad();
            PrintAnnotated(touchpad);

            return 0;
        }

        private static void PrintAnnotated(SampleDescriptors.AnnotatedDescriptor sample)
        {
            ConsoleUi.Title(sample.Title);
            ConsoleUi.Note(sample.Story);

            ConsoleUi.Step(1, "逐段注释（这一段字节在说什么）");
            var rows = new List<string[]>();
            for (int i = 0; i < sample.Chunks.Count; i++)
            {
                rows.Add(new[]
                {
                    sample.OffsetOfChunk(i).ToString("X4"),
                    sample.Chunks[i].Hex,
                    sample.Chunks[i].Meaning
                });
            }
            ConsoleUi.Table(new[] { "偏移", "字节", "含义" }, rows, new[] { 6, 10, 80 });

            ParseAndPrint(sample.Bytes, null);
        }

        /// <summary>解析一段描述符并打印 item 表 / 集合 / 字段表。</summary>
        private static void ParseAndPrint(byte[] descriptor, string title)
        {
            ConsoleUi.Step(2, "原始字节（共 " + descriptor.Length + " 字节）");
            ConsoleUi.HexDump(descriptor, 16);

            ReportDescriptorParser.ParseResult result = ReportDescriptorParser.Parse(descriptor);

            ConsoleUi.Step(3, "逐条 item 解析（描述符是一串“指令”，这里一条条翻译）");
            var rows = new List<string[]>();
            foreach (var item in result.Items)
            {
                var bytes = new StringBuilder();
                for (int i = 0; i < item.ByteCount; i++) bytes.Append(descriptor[item.Offset + i].ToString("X2")).Append(' ');

                rows.Add(new[]
                {
                    item.Offset.ToString("X4"),
                    bytes.ToString().TrimEnd(),
                    item.ItemTypeName,
                    item.Name,
                    ConsoleUi.Truncate(item.Meaning ?? "", 52)
                });
            }
            ConsoleUi.Table(new[] { "偏移", "字节", "类型", "名字", "解释" }, rows, new[] { 6, 14, 8, 18, 54 });

            ConsoleUi.Step(4, "集合树（谁包含谁）");
            if (result.Collections.Count == 0) ConsoleUi.Note("（没有集合）");
            foreach (var c in result.Collections)
            {
                int depth = DepthOf(result, c);
                ConsoleUi.Info(new string(' ', depth * 2) + "[" + c.Index + "] " +
                               HidUsageNames.Describe(c.UsagePage, c.Usage <= 0 ? 0 : c.Usage) +
                               " → " + ReportDescriptorParser.CollectionTypeName(c.CollectionType));
            }

            ConsoleUi.Step(5, "字段表（第几位是什么数据）");
            var fieldRows = new List<string[]>();
            foreach (var f in result.Fields)
            {
                fieldRows.Add(new[]
                {
                    f.Kind,
                    f.ReportId.ToString(),
                    f.BitOffset + " ~ " + (f.BitOffset + f.BitSize * f.BitCount - 1),
                    f.BitSize + "×" + f.BitCount,
                    ConsoleUi.Truncate(f.UsageText(), 40),
                    f.FlagsText
                });
            }
            ConsoleUi.Table(new[] { "种类", "报告ID", "位偏移", "位宽×个数", "用途", "标志" },
                fieldRows, new[] { 8, 7, 12, 10, 42, 26 });

            if (result.UsesReportIds)
                ConsoleUi.Note("这份描述符用了报告 ID：每个输入报告的第 1 个字节是 ID，用来区分不同种类的报告。");

            foreach (string warning in result.Warnings) ConsoleUi.Warn(warning);
        }

        private static int DepthOf(ReportDescriptorParser.ParseResult result, ReportDescriptorParser.DescriptorCollection node)
        {
            int depth = 0;
            int current = node.ParentIndex;
            int guard = 0;
            while (current >= 0 && guard++ < 64)
            {
                depth++;
                current = result.Collections[current].ParentIndex;
            }
            return depth;
        }

        // ================= 命令 4：watch =================

        private static async Task<int> CommandWatch(Options options)
        {
            HidDeviceInfo info = SelectDevice(options);

            ConsoleUi.Title("实时解码 HID 报告");
            ConsoleUi.Info("设备：" + info.ShortDescription());
            ConsoleUi.Info("实例 ID：" + info.InstanceId);
            ConsoleUi.Info("设备路径：" + info.DevicePath);
            ConsoleUi.Note("现在动一动这个设备（鼠标：移动 + 按键；触摸板：滑动手指）。按 Ctrl+C 退出。");

            using (HidDevice device = HidDevice.Open(info))
            {
                NativeMethods.HIDP_CAPS caps = device.Caps;

                if (caps.InputReportByteLength == 0)
                    throw new InvalidOperationException("这个设备没有输入报告，换一个设备试试（--device N）。");

                // 建立位布局：我们解码报告用的"地图"
                var buttons = HidCaps.GetButtonCaps(device.PreparsedData, caps, NativeMethods.HidP_Input);
                var values = HidCaps.GetValueCaps(device.PreparsedData, caps, NativeMethods.HidP_Input);
                ReportLayout layout = ReportLayout.Build(caps, buttons, values);

                ConsoleUi.Info("报告长度：" + caps.InputReportByteLength + " 字节" +
                               (layout.HasReportIds ? "（第 1 字节是报告 ID）" : "（没有报告 ID）"));

                foreach (string note in layout.Notes) ConsoleUi.Warn(note);

                // 找到 X / Y 的逻辑范围，用来画地图
                FindCoordinateRange(layout, out int minX, out int maxX, out int minY, out int maxY);

                // 设备报的是"相对位移"（鼠标）还是"绝对坐标"（触摸板）？决定画什么图
                bool pointerMode = IsPointerDevice(layout);

                Console.WriteLine();
                ConsoleUi.Note("显示模式：" + (pointerMode ? "指针轨迹（设备报的是相对位移）"
                                                          : "触点位置（设备报的是绝对坐标）"));
                ConsoleUi.Note("输出模式：" + (options.Raw ? "只看原始十六进制"
                                             : options.Plain ? "逐行滚动" : "实时刷新（全屏重画）"));

                // ---------- 异步模式（学 Task 的重点） ----------
                MouseReportSynthesizer synthesizer = pointerMode
                    ? MouseReportSynthesizer.Create(layout, caps) : null;

                if (options.Async)
                {
                    await RunWatchLoopAsync(info, device, caps, layout, options, minX, maxX, minY, maxY,
                                            pointerMode, device.HasReadAccess, synthesizer);
                    return 0;
                }

                // ---------- 同步模式：选择"报告来源" ----------
                // 路线 A（直接读）：CreateFile + ReadFile。对一般的 HID 设备可用（比如鼠标）。
                // 路线 B（Raw Input）：设备被系统独占时用这条（触摸板），
                //                      系统会把原始报告通过 WM_INPUT 消息投递给我们。
                bool directRead = device.HasReadAccess;
                RawInputReader rawReader = null;

                if (directRead)
                {
                    ConsoleUi.Ok("报告来源：直接读取设备（ReadFile）");
                }
                else
                {
                    ConsoleUi.Warn("这个设备已被系统独占打开（申请读权限被拒绝），ReadFile 走不通。");
                    ConsoleUi.Note("改用 Raw Input：把窗口注册给系统，让它把原始报告投递给我们（字节内容一模一样）。");
                    if (synthesizer != null)
                    {
                        ConsoleUi.Note("注意：Windows 对鼠标/键盘只投递 RAWMOUSE（预处理过的 dx/dy/按键），不给原始报告字节。");
                        ConsoleUi.Note("      所以这里按描述符把 dx/dy/按键“编码”回报告字节，后面的解码流程完全一样 —— ");
                        ConsoleUi.Note("      正好能体会“描述符既是解码地图、也是编码地图”。");
                    }
                    rawReader = RawInputReader.Create(caps.UsagePage, caps.Usage, info.DevicePath, synthesizer);
                    ConsoleUi.Ok("报告来源：Raw Input（WM_INPUT）");
                }

                try
                {
                    RunWatchLoop(info, device, caps, layout, options, directRead, rawReader,
                                 minX, maxX, minY, maxY, pointerMode);
                }
                finally
                {
                    if (rawReader != null) rawReader.Dispose();
                }
            }

            return 0;
        }

        /// <summary>同步实时循环：取报告 → 解码 → 显示（单线程、阻塞式读取，最好理解）。</summary>
        private static void RunWatchLoop(
            HidDeviceInfo info, HidDevice device, NativeMethods.HIDP_CAPS caps, ReportLayout layout, Options options,
            bool directRead, RawInputReader rawReader, int minX, int maxX, int minY, int maxY, bool pointerMode)
        {
            // Ctrl+C 时恢复光标再退出（否则终端里光标会一直消失）
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                try { Console.CursorVisible = true; } catch { }
                Console.WriteLine();
                ConsoleUi.Note("已退出。");
                Environment.Exit(0);
            };

            if (!options.Plain && !options.Raw && !Console.IsOutputRedirected)
            {
                try { Console.CursorVisible = false; } catch { }
            }

            var tracker = new PointerTracker();
            var history = new List<string>();
            long reportCount = 0;
            int waitedSeconds = 0;

            while (true)
            {
                // 两条路线都返回"设备上报的原始字节"，后面的解码逻辑完全一样
                byte[] report;
                if (directRead)
                {
                    report = device.ReadInputReport();          // 阻塞：没数据就等
                }
                else
                {
                    // 最多等 1 秒，超时就回到循环顶部 —— 这样才有机会打印"等待输入"的提示
                    if (!rawReader.TryReadNext(1000, out report))
                    {
                        // 一个报告都还没收到时，给点反馈，免得让人以为程序卡死了
                        if (reportCount == 0 && !options.Raw)
                        {
                            waitedSeconds++;
                            ConsoleUi.Note("等待输入…（" + waitedSeconds + " 秒）" +
                                "  鼠标事件=" + rawReader.MouseEventCount +
                                " 筛掉=" + rawReader.SkippedCount +
                                " 注入来源=" + rawReader.InjectedEventCount +
                                " 无编码器丢弃=" + rawReader.DroppedMouseEvents +
                                " 见到设备=" + (string.IsNullOrEmpty(rawReader.DeviceName) ? "（还没有）" : rawReader.DeviceName));
                        }
                        continue;
                    }
                }
                reportCount++;

                if (options.Raw)
                {
                    Console.WriteLine(Hex(report));
                    continue;
                }

                // 系统解码（用于对照）+ 我们自己的解码
                Dictionary<ushort, uint> systemTable = HidCaps.GetDataTable(
                    device.PreparsedData, caps, NativeMethods.HidP_Input, report);
                List<DecodedField> fields = ReportDecoder.Decode(layout, report, systemTable);

                // ---------- 指针模式（鼠标） ----------
                if (pointerMode)
                {
                    string pointerSummary = UpdatePointer(tracker, fields);

                    if (options.Plain)
                    {
                        Console.WriteLine("#" + reportCount + "  " + pointerSummary);
                        Console.WriteLine("      " + Hex(report));
                        if (options.Verify) PrintVerifyTable(fields);
                        continue;
                    }

                    history.Add("#" + reportCount + " " + pointerSummary);
                    if (history.Count > 6) history.RemoveAt(0);
                    DrawPointerFrame(info, caps, reportCount, report, tracker, pointerSummary, history);
                    continue;
                }

                // ---------- 触点模式（触摸板） ----------
                List<ConsoleUi.TouchPoint> contacts = ExtractContacts(fields);
                int contactCount = ExtractContactCount(fields);

                if (options.Plain)
                {
                    PrintPlainLine(reportCount, report, contacts, contactCount, fields, options.Verify);
                    continue;
                }

                // 全屏刷新模式：把最近 6 行摘要留着，避免信息刷太快看不清
                history.Add(BuildSummary(reportCount, report, contacts, contactCount));
                if (history.Count > 6) history.RemoveAt(0);

                DrawFrame(info, caps, reportCount, report, contacts, fields, history,
                          minX, maxX, minY, maxY, options.Verify);
            }
        }

        /// <summary>
        /// 异步实时循环：生产者任务在后台读设备，主流程用 await foreach 消费。
        /// 【本项目学 C# Task 的重点】对照 Hid/ReportPipeline.cs 一起看：
        ///   Task.Run 起后台任务 → Channel 当传送带 → CancellationToken 负责停止 → await foreach 消费。
        /// </summary>
        private static async Task RunWatchLoopAsync(
            HidDeviceInfo info, HidDevice device, NativeMethods.HIDP_CAPS caps, ReportLayout layout, Options options,
            int minX, int maxX, int minY, int maxY, bool pointerMode, bool directRead,
            MouseReportSynthesizer synthesizer)
        {
            ConsoleUi.Note("异步模式：后台任务负责取数据，这里用 await foreach 消费 —— 学 Task 的重点示例");
            ConsoleUi.Note("（代码对照 Hid/ReportPipeline.cs：Task.Run / Channel / CancellationToken / await foreach）");
            ConsoleUi.Note(directRead
                ? "数据通道：ReadFile（后台线程阻塞读取）"
                : "数据通道：Raw Input（后台任务里创建窗口并接收 WM_INPUT）");

            // 被独占的设备走 Raw Input，能直接读的设备走 ReadFile —— 两条路对消费者完全一样
            using (ReportPipeline pipeline = directRead
                       ? ReportPipeline.StartForDevice(device)
                       : ReportPipeline.StartForRawInput(caps.UsagePage, caps.Usage, info.DevicePath, synthesizer))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                // Ctrl+C 只负责"触发取消"，真正的退出逻辑在下面 await foreach 之外
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    cancellation.Cancel();
                };

                if (!options.Plain && !options.Raw && !Console.IsOutputRedirected)
                {
                    try { Console.CursorVisible = false; } catch { }
                }

                var tracker = new PointerTracker();
                var history = new List<string>();
                long consumed = 0;
                long lastDraw = 0;

                try
                {
                    // await foreach：从传送带上一条条取数据，取不到时"让出线程"而不是死等
                    await foreach (byte[] report in pipeline.Reader.ReadAllAsync(cancellation.Token))
                    {
                        consumed++;
                        if (options.Raw)
                        {
                            Console.WriteLine(Hex(report));
                            continue;
                        }

                        Dictionary<ushort, uint> systemTable = HidCaps.GetDataTable(
                            device.PreparsedData, caps, NativeMethods.HidP_Input, report);
                        List<DecodedField> fields = ReportDecoder.Decode(layout, report, systemTable);

                        string summary = pointerMode
                            ? UpdatePointer(tracker, fields)
                            : BuildSummary(consumed, report, ExtractContacts(fields), ExtractContactCount(fields));

                        if (options.Plain)
                        {
                            Console.WriteLine("#" + consumed + "  " + summary);
                            Console.WriteLine("      " + Hex(report));
                            if (options.Verify) PrintVerifyTable(fields);
                            continue;
                        }

                        history.Add("#" + consumed + " " + summary);
                        if (history.Count > 6) history.RemoveAt(0);

                        // 帧率控制：每 50 毫秒重画一次。
                        // 注意这里只是"看看时间到了没"，不是 Thread.Sleep —— 异步代码里不要阻塞线程。
                        long now = Environment.TickCount64;
                        if (now - lastDraw >= 50)
                        {
                            lastDraw = now;
                            if (pointerMode)
                                DrawPointerFrame(info, caps, consumed, report, tracker, summary, history);
                            else
                                DrawFrame(info, caps, consumed, report, ExtractContacts(fields), fields, history,
                                          minX, maxX, minY, maxY, options.Verify);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 用户按了 Ctrl+C：正常退出路径
                }
                finally
                {
                    // 优雅收尾：取消 + 等生产者任务一会儿（不能无限等，见 ReportPipeline 的说明）
                    await pipeline.StopAsync(500);
                    try { Console.CursorVisible = true; } catch { }
                    Console.WriteLine();
                    ConsoleUi.Ok("已停止：消费 " + consumed + " 包，生产 " + pipeline.ProducedCount + " 包");
                    if (pipeline.ProducerError != null) ConsoleUi.Warn("生产者出错：" + pipeline.ProducerError.Message);
                    ConsoleUi.Note("（两个数字不同说明传送带满了丢过旧数据 —— 这是有界 Channel 的“背压”行为）");
                    ConsoleUi.Note("对比一下：同步模式用 while + 阻塞读，异步模式用 Task + Channel；功能一样，写法不同。");
                }
            }
        }

        /// <summary>判断设备是不是"指针类"：报的是相对位移（鼠标），而不是绝对坐标（触摸板）。</summary>
        private static bool IsPointerDevice(ReportLayout layout)
        {
            foreach (var list in layout.ByReportId.Values)
            {
                foreach (var f in list)
                {
                    if (f.UsagePage == 0x01 && f.Usage == 0x30) return !f.IsAbsolute;
                }
            }
            return false;
        }

        /// <summary>
        /// 指针模式：把这一包的位移/坐标累加进轨迹，并生成一行摘要。
        /// 鼠标报的是"我又移动了 dx,dy"，所以要自己累加才能得到"我在哪"。
        /// </summary>
        private static string UpdatePointer(PointerTracker tracker, List<DecodedField> fields)
        {
            int dx = 0, dy = 0, wheel = 0;
            bool absolute = false;
            long absX = 0, absY = 0, minX = 0, maxX = 0, minY = 0, maxY = 0;
            var pressed = new List<string>();

            foreach (var f in fields)
            {
                if (f.Field.UsagePage == 0x01 && f.Usage == 0x30)
                {
                    absolute = f.Field.IsAbsolute;
                    if (absolute) { absX = EffectiveValue(f); minX = f.Field.LogicalMin; maxX = f.Field.LogicalMax; }
                    else dx = (int)EffectiveValue(f);
                }
                else if (f.Field.UsagePage == 0x01 && f.Usage == 0x31)
                {
                    if (absolute) { absY = EffectiveValue(f); minY = f.Field.LogicalMin; maxY = f.Field.LogicalMax; }
                    else dy = (int)EffectiveValue(f);
                }
                else if (f.Field.UsagePage == 0x01 && f.Usage == 0x38)
                {
                    wheel = (int)EffectiveValue(f);
                }
                else if (f.Field.UsagePage == 0x09 && EffectiveValue(f) != 0)
                {
                    pressed.Add("Button " + f.Usage);       // 按键页 0x09 的字段：0/1 表示没按/按下
                }
            }

            if (absolute) tracker.ApplyAbsolute(absX, absY, minX, maxX, minY, maxY);
            else tracker.ApplyRelative(dx, dy);

            var text = new StringBuilder();
            text.Append("按键=").Append(pressed.Count == 0 ? "无" : string.Join("+", pressed));
            text.Append("   位移 dx=").Append(dx.ToString("+0;-0;0"));
            text.Append(" dy=").Append(dy.ToString("+0;-0;0"));
            if (wheel != 0) text.Append("   滚轮=").Append(wheel);
            text.Append("   虚拟位置=(").Append(tracker.X.ToString("F2")).Append(",").Append(tracker.Y.ToString("F2")).Append(")");
            return text.ToString();
        }

        /// <summary>指针模式的全屏刷新。</summary>
        private static void DrawPointerFrame(
            HidDeviceInfo info, NativeMethods.HIDP_CAPS caps, long reportCount, byte[] report,
            PointerTracker tracker, string summary, List<string> history)
        {
            var lines = new List<string>();

            lines.Add("TouchProbe · 指针模式    设备: " + ConsoleUi.Truncate(info.ShortDescription(), 40));
            lines.Add("报告长度 " + caps.InputReportByteLength + " 字节    已收报告 " + reportCount);
            lines.Add(new string('─', 78));
            lines.Add(summary);
            lines.Add("");

            int mapWidth = 60;
            int mapHeight = 12;
            try
            {
                int available = Console.WindowWidth - 6;
                if (available > 20 && available < mapWidth) mapWidth = available;
            }
            catch { }
            lines.AddRange(ConsoleUi.BuildTraceMap(tracker.Trail, tracker.X, tracker.Y, mapWidth, mapHeight));

            lines.Add("");
            lines.Add("最近一包：" + ConsoleUi.Truncate(Hex(report), 60));
            lines.Add(new string('─', 78));
            lines.Add("最近记录：");
            lines.AddRange(history);
            lines.Add("");
            lines.Add("按 Ctrl+C 退出");

            RedrawFrame(lines);
        }

        /// <summary>把一帧文字画到屏幕左上角（全屏刷新模式的公共部分）。</summary>
        private static void RedrawFrame(List<string> lines)
        {
            try
            {
                Console.SetCursorPosition(0, 0);
                int width;
                try { width = Math.Max(40, Console.WindowWidth - 1); } catch { width = 78; }

                foreach (string line in lines)
                {
                    Console.WriteLine(ConsoleUi.Truncate(line, width).PadRight(width));
                }
            }
            catch
            {
                // 终端不支持光标定位（或窗口太小）：退化成直接打印
                foreach (string line in lines) Console.WriteLine(line);
            }
        }

        /// <summary>从解码结果里找 X/Y 的逻辑范围（画地图要用）。</summary>
        private static void FindCoordinateRange(ReportLayout layout, out int minX, out int maxX, out int minY, out int maxY)
        {
            minX = 0; maxX = 4095; minY = 0; maxY = 4095;

            foreach (var list in layout.ByReportId.Values)
            {
                foreach (var f in list)
                {
                    if (f.UsagePage == 0x01 && f.Usage == 0x30) { minX = f.LogicalMin; maxX = f.LogicalMax; }
                    if (f.UsagePage == 0x01 && f.Usage == 0x31) { minY = f.LogicalMin; maxY = f.LogicalMax; }
                }
            }
        }

        /// <summary>
        /// 把"一串字段值"整理成"每个触点一个对象"：
        /// 同一个逻辑集合（LinkCollection）里的 X / Y / Tip Switch / Contact Identifier 属于同一个触点。
        /// </summary>
        private static List<ConsoleUi.TouchPoint> ExtractContacts(List<DecodedField> fields)
        {
            var contacts = new List<ConsoleUi.TouchPoint>();

            var groups = fields.GroupBy(f => f.Field.LinkCollection);
            foreach (var group in groups)
            {
                long? x = null, y = null, id = null;
                bool tip = false;
                bool hasTipField = false;

                foreach (var f in group)
                {
                    if (f.Field.UsagePage == 0x01 && f.Usage == 0x30) x = EffectiveValue(f);
                    else if (f.Field.UsagePage == 0x01 && f.Usage == 0x31) y = EffectiveValue(f);
                    else if (f.Field.UsagePage == 0x0D && f.Usage == 0x42) { tip = EffectiveValue(f) != 0; hasTipField = true; }
                    else if (f.Field.UsagePage == 0x0D && f.Usage == 0x51) id = EffectiveValue(f);
                }

                if (x.HasValue && y.HasValue)
                {
                    contacts.Add(new ConsoleUi.TouchPoint
                    {
                        Id = id.HasValue ? (int)id.Value : contacts.Count,
                        X = (int)x.Value,
                        Y = (int)y.Value,
                        // 如果描述符里没有 Tip Switch 字段，就认为"有坐标 = 有触点"
                        Tip = hasTipField ? tip : true,
                    });
                }
            }

            return contacts;
        }

        /// <summary>取报告里的"触点总数"（顶层集合里的 Contact Count）。</summary>
        private static int ExtractContactCount(List<DecodedField> fields)
        {
            foreach (var f in fields)
            {
                if (f.Field.UsagePage == 0x0D && f.Usage == 0x54) return (int)EffectiveValue(f);
            }
            return -1;
        }

        /// <summary>
        /// 选一个"可信的值"：我们自己算的和系统解码一致时用自己的，
        /// 不一致（多半是描述符里有我们看不见的填充位）时退回用系统的。
        /// </summary>
        private static long EffectiveValue(DecodedField field)
        {
            if (field.Matches == false && field.SystemValue.HasValue)
            {
                return field.Field.LogicalMin < 0
                    ? BitReader.ToSigned(field.SystemValue.Value, field.Field.BitSize)
                    : (long)field.SystemValue.Value;
            }
            return field.OurValue;
        }

        private static string Hex(byte[] data)
        {
            var sb = new StringBuilder();
            foreach (byte b in data) sb.Append(b.ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        private static string BuildSummary(
            long reportCount, byte[] report, List<ConsoleUi.TouchPoint> contacts, int contactCount)
        {
            var sb = new StringBuilder();
            sb.Append("#" + reportCount.ToString().PadLeft(6) + "  ");
            sb.Append("触点=" + (contactCount >= 0 ? contactCount.ToString() : "?") + "  ");
            foreach (var p in contacts)
            {
                if (!p.Tip) continue;
                sb.Append("[ID" + p.Id + " X=" + p.X + " Y=" + p.Y + "] ");
            }
            return sb.ToString();
        }

        private static void PrintPlainLine(
            long reportCount, byte[] report, List<ConsoleUi.TouchPoint> contacts, int contactCount,
            List<DecodedField> fields, bool verify)
        {
            Console.WriteLine("#" + reportCount + "  " + Hex(report));
            Console.WriteLine("      触点总数=" + (contactCount >= 0 ? contactCount.ToString() : "?") +
                              "  按下触点=" + contacts.Count(c => c.Tip));

            foreach (var p in contacts)
            {
                if (!p.Tip) continue;
                Console.WriteLine("      触点 ID=" + p.Id + "  X=" + p.X + "  Y=" + p.Y);
            }

            if (verify) PrintVerifyTable(fields);
        }

        private static void PrintVerifyTable(List<DecodedField> fields)
        {
            var rows = new List<string[]>();
            foreach (var f in fields)
            {
                rows.Add(new[]
                {
                    f.DataIndex.ToString(),
                    ConsoleUi.Truncate(f.Name, 34),
                    f.OurValue.ToString(),
                    f.SystemValue.HasValue ? f.SystemValue.Value.ToString() : "-",
                    f.Matches.HasValue ? (f.Matches.Value ? "一致" : "不一致") : "-"
                });
            }
            ConsoleUi.Table(new[] { "数据序号", "字段", "我们自己算", "系统解码", "对照" },
                rows, new[] { 8, 36, 12, 10, 8 });
        }

        /// <summary>全屏重画一帧。</summary>
        private static void DrawFrame(
            HidDeviceInfo info, NativeMethods.HIDP_CAPS caps, long reportCount, byte[] report,
            List<ConsoleUi.TouchPoint> contacts, List<DecodedField> fields, List<string> history,
            int minX, int maxX, int minY, int maxY, bool verify)
        {
            var lines = new List<string>();

            lines.Add("TouchProbe · 实时触摸解码    设备: " +
                      ConsoleUi.Truncate(info.ShortDescription(), 40));
            lines.Add("报告长度 " + caps.InputReportByteLength + " 字节    已收报告 " + reportCount +
                      "    当前触点 " + contacts.Count(c => c.Tip));
            lines.Add(new string('─', 78));

            // 触点明细（最多显示 5 个）
            int shown = 0;
            foreach (var p in contacts)
            {
                if (!p.Tip) continue;
                lines.Add("触点 ID=" + p.Id + "   X=" + p.X.ToString().PadLeft(5) + "  Y=" + p.Y.ToString().PadLeft(5) +
                          "   X条 " + ConsoleUi.Bar(p.X, minX, maxX, 24));
                if (++shown >= 5) break;
            }
            if (shown == 0) lines.Add("（当前没有按下触点 —— 请用手指滑动触摸板）");
            lines.Add("");

            // 位置地图
            int mapWidth = 60;
            int mapHeight = 12;
            try
            {
                int available = Console.WindowWidth - 6;
                if (available > 20 && available < mapWidth) mapWidth = available;
            }
            catch { }
            lines.AddRange(ConsoleUi.BuildTouchMap(contacts, minX, maxX, minY, maxY, mapWidth, mapHeight));

            lines.Add("");
            lines.Add("最近几包：" + ConsoleUi.Truncate(Hex(report), 60));
            lines.Add(new string('─', 78));
            lines.Add("最近记录：");
            lines.AddRange(history);
            lines.Add("");
            lines.Add("按 Ctrl+C 退出" + (verify ? "（另见 --plain --verify 对照表）" : ""));

            RedrawFrame(lines);
        }
    }
}