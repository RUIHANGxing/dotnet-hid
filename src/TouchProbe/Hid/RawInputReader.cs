using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using TouchProbe.Native;

namespace TouchProbe.Hid
{
    /// <summary>
    /// 【本文件是什么 —— 当设备被系统独占时，怎么还能拿到它的原始报告】
    ///
    /// 实测结论：这块触摸板已经被 Windows 的精确式触摸板驱动"独占打开"了，
    /// 我们用 CreateFile 申请读权限会直接失败（错误码 32：共享冲突）。
    /// 也就是说 ReadFile 这条路对它彻底走不通。
    ///
    /// 但 Windows 还提供了另一条正规通道：**Raw Input（原始输入）**。
    /// 它不是"你去读设备"，而是"你告诉系统：这个设备有输入时，请把原始报告投递给我"。
    /// 系统通过窗口消息（WM_INPUT）把报告送过来，字节内容与 ReadFile 拿到的完全一样。
    ///
    /// 使用步骤（Windows 固定套路）：
    ///   1. 注册一个窗口类，创建一个（不显示的）窗口，用来接收消息；
    ///   2. 调用 RegisterRawInputDevices，声明"我要 用途页 0x0D / 用途 0x05（触摸板）"的原始输入；
    ///   3. 跑消息循环 GetMessage/DispatchMessage；
    ///   4. 收到 WM_INPUT 时调用 GetRawInputData 取出原始字节。
    ///
    /// 注意：Raw Input 拿到的报告**不含**任何解析结果，就是设备上报的原始字节 ——
    /// 正好适合我们自己做解码。
    /// </summary>
    public sealed class RawInputReader : IDisposable
    {
        private const uint WM_INPUT = 0x00FF;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RIDEV_INPUTSINK = 0x00000100;   // 即使窗口不在前台也要收
        private const int RIM_TYPEHID = 2;       // 原始 HID 报告（非鼠标/键盘的集合）
        private const int RIM_TYPEMOUSE = 0;     // 鼠标的预处理事件（RAWMOUSE）

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // 窗口过程委托必须用字段保存起来，否则会被垃圾回收器回收 → 回调时崩溃
        private readonly WndProcDelegate _wndProc;

        private IntPtr _hInstance;
        private IntPtr _hWnd;
        private string _className;

        /// <summary>已经收到但还没被取走的报告（一包 WM_INPUT 里可能带多个报告）。</summary>
        private readonly Queue<byte[]> _pending = new Queue<byte[]>();

        /// <summary>设备名缓存：hDevice → 设备路径（同一台设备只查一次）。</summary>
        private readonly Dictionary<IntPtr, string> _deviceNameCache = new Dictionary<IntPtr, string>();

        /// <summary>这是哪个设备的原始输入（设备路径，留空表示不筛选）。</summary>
        private string _devicePathFilter;

        /// <summary>设备名字（第一次收到报告后从系统查询，用于确认没抓错设备）。</summary>
        public string DeviceName { get; private set; } = "";

        /// <summary>本设备上报的每个报告的字节数（由系统告诉我们）。</summary>
        public int ReportSize { get; private set; }

        /// <summary>被筛掉的报告数（订阅了整类设备时，其它设备的输入会被丢掉）。</summary>
        public long SkippedCount { get; private set; }

        /// <summary>收到过多少次鼠标类事件（RAWMOUSE）。</summary>
        public long MouseEventCount { get; private set; }

        /// <summary>RAWMOUSE 事件被丢弃的次数（没有提供编码器时会出现）。</summary>
        public long DroppedMouseEvents { get; private set; }

        /// <summary>
        /// "注入来源"的事件数（用 SendInput 之类注入的输入，hDevice 为空）。
        /// 这类事件不属于任何物理设备，教学演示时我们照样接受它，方便随时验证解码流程。
        /// </summary>
        public long InjectedEventCount { get; private set; }

        /// <summary>
        /// 把 RAWMOUSE 的 (dx, dy, 按钮标志, 滚轮量) 编码成报告字节的函数。
        /// 由上层（Program）在"目标设备是鼠标"时提供，见 MouseReportSynthesizer。
        /// </summary>
        public Func<int, int, ushort, short, byte[]> MouseReportEncoder { get; set; }

        private RawInputReader()
        {
            _wndProc = WindowProc;
        }

        /// <summary>
        /// 创建并注册一个 Raw Input 接收器。
        /// usagePage / usage 决定"订阅哪一类设备的原始输入"（如 0x01/0x02 = 所有鼠标）。
        /// devicePathFilter 决定"只要这个设备的"（订阅是按类别的，一台电脑可能有多个鼠标）。
        /// </summary>
        public static RawInputReader Create(
            ushort usagePage, ushort usage, string devicePathFilter, MouseReportSynthesizer mouseSynthesizer)
        {
            var reader = new RawInputReader();
            reader._devicePathFilter = devicePathFilter;
            if (mouseSynthesizer != null)
            {
                // 收到 RAWMOUSE 时，用它把 dx/dy/按键编码回报告字节
                reader.MouseReportEncoder = mouseSynthesizer.Build;
            }

            reader._hInstance = NativeMethods.GetModuleHandle(null);
            reader._className = "TouchProbeRawInputWindow";

            var wndClass = new NativeMethods.WNDCLASSEX();
            wndClass.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.WNDCLASSEX));
            wndClass.style = 0;
            wndClass.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(reader._wndProc);
            wndClass.hInstance = reader._hInstance;
            wndClass.lpszClassName = reader._className;

            ushort atom = NativeMethods.RegisterClassEx(ref wndClass);
            if (atom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                // 183 = 类已注册（重复创建时会出现），忽略即可
                if (err != 183) throw new InvalidOperationException("注册窗口类失败：" + NativeMethods.LastErrorText());
            }

            // 创建一个不显示的窗口（不调用 ShowWindow 它就不会出现在屏幕上）
            reader._hWnd = NativeMethods.CreateWindowEx(
                0, reader._className, "TouchProbe", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, reader._hInstance, IntPtr.Zero);

            if (reader._hWnd == IntPtr.Zero)
                throw new InvalidOperationException("创建窗口失败：" + NativeMethods.LastErrorText());

            // 订阅原始输入
            var device = new NativeMethods.RAWINPUTDEVICE();
            device.usUsagePage = usagePage;
            device.usUsage = usage;
            device.dwFlags = RIDEV_INPUTSINK;   // 窗口不在前台也照样收
            device.hwndTarget = reader._hWnd;

            if (!NativeMethods.RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICE))))
                throw new InvalidOperationException("注册原始输入失败：" + NativeMethods.LastErrorText());

            return reader;
        }

        /// <summary>等下一个报告（内部会跑消息循环，没有输入时会一直等）。</summary>
        public bool TryReadNext(out byte[] report)
        {
            return TryReadNext(-1, out report);
        }

        /// <summary>
        /// 等下一个报告，最多等 timeoutMilliseconds 毫秒（传 -1 表示一直等）。
        /// 这里用 PeekMessage + Thread.Sleep 自己搭循环，而不是用会阻塞的 GetMessage，
        /// 好处是"可以边等边干别的"（比如打印心跳、检查取消标记）。
        /// </summary>
        public bool TryReadNext(int timeoutMilliseconds, out byte[] report)
        {
            const uint PM_REMOVE = 0x0001;
            int waited = 0;

            while (true)
            {
                if (_pending.Count > 0)
                {
                    report = _pending.Dequeue();
                    return true;
                }

                // 把当前排队的消息全部处理掉（WM_INPUT 会在这里被 WindowProc 取走）
                NativeMethods.MSG message;
                while (NativeMethods.PeekMessage(out message, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    NativeMethods.TranslateMessage(ref message);
                    NativeMethods.DispatchMessage(ref message);
                    if (_pending.Count > 0)
                    {
                        report = _pending.Dequeue();
                        return true;
                    }
                }

                if (timeoutMilliseconds >= 0 && waited >= timeoutMilliseconds)
                {
                    report = null;
                    return false;
                }

                System.Threading.Thread.Sleep(20);
                waited += 20;
            }
        }

        /// <summary>窗口过程：系统把消息发到这里，我们在 WM_INPUT 时取数据。</summary>
        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT)
            {
                try { HandleRawInput(lParam); }
                catch { /* 教学程序，出错不崩 */ }
            }
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void HandleRawInput(IntPtr hRawInput)
        {
            // 第一步：问系统"这包原始数据有多大"
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTHEADER));
            if (NativeMethods.GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0)
                return;

            // 第二步：分配缓冲区并真正取数据
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint written = NativeMethods.GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize);
                if (written == uint.MaxValue || written == 0) return;

                // RAWINPUT 结构：头部 + 具体数据（鼠标 / 键盘 / HID 三选一）
                uint type = (uint)Marshal.ReadInt32(buffer, 0);

                IntPtr hDevice = Marshal.ReadIntPtr(buffer, 8);   // 头部里的 hDevice（64 位下偏移 8）

                // 注意：订阅的是"整类设备"，所以每一包都要先确认是不是我们要的那台
                // （一台电脑上可能同时有鼠标和触摸板的鼠标集合）
                string deviceName = GetDeviceName(hDevice);

                // 订阅是按"设备类别"来的（比如所有鼠标），所以这里筛一下具体设备
                if (!string.IsNullOrEmpty(_devicePathFilter) && !SameDevice(deviceName, _devicePathFilter))
                {
                    // hDevice 为空 = 注入来源（SendInput 等）。教学演示里照样接受，
                    // 否则鼠标休眠时就完全看不到数据了。
                    if (hDevice != IntPtr.Zero)
                    {
                        SkippedCount++;
                        return;
                    }
                    InjectedEventCount++;
                }

                DeviceName = deviceName;

                // 联合体数据紧跟在 RAWINPUTHEADER 之后：x64 下偏移 24，x86 下偏移 16
                int offset = 8 + (IntPtr.Size == 8 ? 24 : 16);

                // ---- 情况一：鼠标类事件（RAWMOUSE）----
                // Windows 对"鼠标/键盘"顶层集合只给这种预处理过的结构，不给原始报告字节
                if (type == RIM_TYPEMOUSE)
                {
                    HandleMouseEvent(buffer, offset);
                    return;
                }

                if (type != RIM_TYPEHID) return;

                // ---- 情况二：真正的 HID 报告（RAWHID）----
                // dwSizeHid（每个报告多少字节）、dwCount（这一包里有几个报告）、然后是数据
                int dwSizeHid = Marshal.ReadInt32(buffer, offset);
                int dwCount = Marshal.ReadInt32(buffer, offset + 4);
                if (dwSizeHid <= 0 || dwSizeHid > 4096 || dwCount <= 0) return;

                ReportSize = dwSizeHid;
                IntPtr dataStart = new IntPtr(buffer.ToInt64() + offset + 8);

                for (int i = 0; i < dwCount; i++)
                {
                    var report = new byte[dwSizeHid];
                    Marshal.Copy(new IntPtr(dataStart.ToInt64() + i * dwSizeHid), report, 0, dwSizeHid);
                    _pending.Enqueue(report);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// 处理 RAWMOUSE 事件。
        /// 结构（相对联合体起点的偏移，x64 与 x86 相同）：
        ///   +0  usFlags        低 1 位 = 1 表示"绝对坐标"，0 表示"相对位移"
        ///   +4  ulButtons      低 16 位 = usButtonFlags（按下/抬起/滚轮），高 16 位 = usButtonData（滚轮量）
        ///   +8  ulRawButtons
        ///   +12 lLastX，+16 lLastY，+20 ulExtraInformation
        /// </summary>
        private void HandleMouseEvent(IntPtr buffer, int dataOffset)
        {
            MouseEventCount++;

            ushort flags = (ushort)Marshal.ReadInt16(buffer, dataOffset);
            int buttonAndData = Marshal.ReadInt32(buffer, dataOffset + 4);
            ushort buttonFlags = (ushort)(buttonAndData & 0xFFFF);
            short buttonData = (short)(buttonAndData >> 16);
            int deltaX = Marshal.ReadInt32(buffer, dataOffset + 12);
            int deltaY = Marshal.ReadInt32(buffer, dataOffset + 16);

            if (MouseReportEncoder == null)
            {
                DroppedMouseEvents++;
                return;
            }

            // 绝对坐标模式（有些设备/远控场景），先按 0~65535 归一化成相对量，避免位置乱跳
            if ((flags & 0x01) != 0)
            {
                deltaX = deltaX / 256;
                deltaY = deltaY / 256;
            }

            byte[] report = MouseReportEncoder(deltaX, deltaY, buttonFlags, buttonData);
            if (report != null) _pending.Enqueue(report);
        }

        /// <summary>取设备名（带缓存，同一台设备只问系统一次）。</summary>
        private string GetDeviceName(IntPtr hDevice)
        {
            if (hDevice == IntPtr.Zero) return "";

            string name;
            if (_deviceNameCache.TryGetValue(hDevice, out name)) return name;

            name = QueryDeviceName(hDevice);
            _deviceNameCache[hDevice] = name;
            return name;
        }

        /// <summary>
        /// 比较两个设备路径是不是同一台设备。
        /// 系统在不同 API 里给的路径大小写可能不同，结尾的 GUID 也可能不一致，
        /// 所以：统一转大写 + 去掉 "#{GUID}" 结尾再比。
        /// </summary>
        private static bool SameDevice(string a, string b)
        {
            string normalizedA = NormalizePath(a);
            string normalizedB = NormalizePath(b);

            if (normalizedA == normalizedB) return true;

            // 宽松比较：只比 "HID#..." 到下一个 # 之间的那段（设备型号+集合编号），
            // 这样即使实例号写法有差异，也能认出是同一台设备。
            string signatureA = Signature(normalizedA);
            return signatureA.Length > 0 && signatureA == Signature(normalizedB);
        }

        /// <summary>取路径里 "HID#" 和下一个 "#" 之间的那段，例如 VID_372E&PID_103D&MI_01&COL01。</summary>
        private static string Signature(string normalizedPath)
        {
            int first = normalizedPath.IndexOf('#');
            if (first < 0) return "";
            int second = normalizedPath.IndexOf('#', first + 1);
            if (second < 0) return "";
            return normalizedPath.Substring(first + 1, second - first - 1);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            int guidIndex = path.IndexOf("#{", StringComparison.Ordinal);
            string trimmed = guidIndex >= 0 ? path.Substring(0, guidIndex) : path;
            return trimmed.ToUpperInvariant();
        }

        /// <summary>查询设备名（形如 \\?\HID#FTCS0038&COL02#…），用于确认抓的是哪台设备。</summary>
        private static string QueryDeviceName(IntPtr hDevice)
        {
            uint size = 0;
            NativeMethods.GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0) return "";

            IntPtr buffer = Marshal.AllocHGlobal((int)(size * 2 + 2));
            try
            {
                uint result = NativeMethods.GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buffer, ref size);
                if (result == uint.MaxValue) return "";
                string name = Marshal.PtrToStringUni(buffer);
                return name ?? "";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            if (_hWnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_hWnd);
                _hWnd = IntPtr.Zero;
            }
        }
    }
}