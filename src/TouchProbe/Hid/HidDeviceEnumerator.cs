using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using TouchProbe.Native;

namespace TouchProbe.Hid
{
    /// <summary>
    /// 【本文件是什么】
    /// 把"系统里有哪些 HID 设备"问一遍，并把每个设备的关键信息收集起来。
    ///
    /// 【枚举流程（Windows 固定套路，值得记住）】
    ///   1. 问系统要 HID 类的 GUID（相当于班级编号）；
    ///   2. SetupDiGetClassDevs 拿到一个"名单句柄"；
    ///   3. 循环 SetupDiEnumDeviceInterfaces 按序号 0,1,2… 取每个成员，直到取不到为止；
    ///   4. 对每个成员，用 SetupDiGetDeviceInterfaceDetail 拿到它的"设备路径"字符串；
    ///   5. 打开这个路径，问它的 VID/PID、名字、能力（HIDP_CAPS）；
    ///   6. 关句柄、销毁名单。
    ///
    /// 【"设备路径"长什么样】
    ///   \\?\hid#ftcs0038&col02#4&3a5b98db&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}
    ///   它其实就是设备的"文件名"，CreateFile 打开它就能读写这个设备。
    ///   注意路径里已经包含了设备实例信息，而且大小写是小写的。
    /// </summary>
    public sealed class HidDeviceInfo
    {
        /// <summary>在列表里的序号（给 --device 参数用，从 1 开始，方便人读）。</summary>
        public int Index { get; set; }

        /// <summary>设备路径（CreateFile 用）。</summary>
        public string DevicePath { get; set; }

        /// <summary>设备实例 ID，形如 HID\FTCS0038&COL02\4&3a5b98db&0&0001（设备管理器里也看得到）。</summary>
        public string InstanceId { get; set; }

        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }
        public ushort VersionNumber { get; set; }

        public string Manufacturer { get; set; }
        public string ProductName { get; set; }
        public string SerialNumber { get; set; }

        /// <summary>顶层集合用途页（HID 用它说明"我是什么类型的设备"）。</summary>
        public ushort UsagePage { get; set; }
        /// <summary>顶层集合用途。0x05 = Touch Pad，0x02 = Mouse，0x06 = Keyboard。</summary>
        public ushort Usage { get; set; }

        /// <summary>输入报告长度（字节，含报告 ID 字节）。</summary>
        public ushort InputReportBytes { get; set; }
        public ushort OutputReportBytes { get; set; }
        public ushort FeatureReportBytes { get; set; }

        /// <summary>是否成功读到了上面的能力信息（打开失败的设备会是 false）。</summary>
        public bool CapsAvailable { get; set; }

        /// <summary>
        /// 打开设备时是否拿到了"读权限"。
        /// 很多设备的 HID 集合会被系统驱动独占打开，这时只能申请到"无读写权限"的句柄：
        /// 还能查询能力（IOCTL），但 ReadFile 会被拒绝（错误码 5 / 32）。
        /// 遇到这种情况，watch 命令会自动改用 Raw Input 来取原始报告。
        /// </summary>
        public bool HasReadAccess { get; set; }

        /// <summary>打开设备时遇到的错误（用于排查）。</summary>
        public string OpenError { get; set; }

        /// <summary>判断"这看起来像不像触摸板"：优先看用途页/用途，退而看名字。</summary>
        public bool LooksLikeTouchpad
        {
            get
            {
                // 标准答案：Digitizer(0x0D) / Touch Pad(0x05)
                if (UsagePage == 0x0D && Usage == 0x05) return true;
                // 有些设备把自己的顶层集合声明成 Digitizer/Finger(0x22)
                if (UsagePage == 0x0D && Usage == 0x22) return true;
                // 兜底：靠名字判断（不同厂商命名不规范时的保命手段）
                string s = (InstanceId ?? "") + " " + (ProductName ?? "");
                return s.IndexOf("touchpad", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("触摸板", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("TPD", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        /// <summary>给列表用的简短描述。</summary>
        public string ShortDescription()
        {
            string name = !string.IsNullOrEmpty(ProductName) ? ProductName
                        : !string.IsNullOrEmpty(InstanceId) ? InstanceId
                        : "(未知设备)";
            return name;
        }
    }

    /// <summary>设备枚举器。</summary>
    public static class HidDeviceEnumerator
    {
        /// <summary>枚举当前系统上所有在线的 HID 设备接口。</summary>
        public static List<HidDeviceInfo> Enumerate()
        {
            var result = new List<HidDeviceInfo>();

            // 第 1 步：拿到 HID 设备类的 GUID
            Guid hidGuid;
            NativeMethods.HidD_GetHidGuid(out hidGuid);

            // 第 2 步：拿"名单句柄"。DIGCF_PRESENT 只列在线设备；
            // DIGCF_DEVICEINTERFACE 表示我们按"设备接口"来枚举（HID 接口是它的子集）。
            IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
                ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == NativeMethods.INVALID_HANDLE_VALUE)
            {
                throw new InvalidOperationException("枚举设备失败：" + NativeMethods.LastErrorText());
            }

            try
            {
                // 第 3 步：按序号一个个取
                var interfaceData = new NativeMethods.SP_DEVICE_INTERFACE_DATA();
                // cbSize 必须填结构体自身大小，系统靠它识别版本
                interfaceData.cbSize = Marshal.SizeOf(typeof(NativeMethods.SP_DEVICE_INTERFACE_DATA));

                for (uint index = 0; ; index++)
                {
                    bool ok = NativeMethods.SetupDiEnumDeviceInterfaces(
                        deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData);

                    if (!ok)
                    {
                        // 取完了会报 ERROR_NO_MORE_ITEMS，这是正常结束
                        if (Marshal.GetLastWin32Error() == NativeMethods.ERROR_NO_MORE_ITEMS) break;
                        // 其它错误就跳过这一项继续
                        continue;
                    }

                    HidDeviceInfo info = ReadOneDevice(deviceInfoSet, ref interfaceData);
                    if (info != null)
                    {
                        info.Index = result.Count + 1;
                        result.Add(info);
                    }
                }
            }
            finally
            {
                // 第 6 步：无论中途是否出错，都要销毁名单
                NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return result;
        }

        /// <summary>读取单个设备的详细信息（路径、实例 ID、身份卡、能力）。</summary>
        private static HidDeviceInfo ReadOneDevice(
            IntPtr deviceInfoSet, ref NativeMethods.SP_DEVICE_INTERFACE_DATA interfaceData)
        {
            // 第 4 步之一：先问"需要多大缓冲区"
            uint requiredSize;
            NativeMethods.SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);

            if (requiredSize == 0) return null;

            // 第 4 步之二：分配一块非托管内存，第二次调用把数据填进去。
            // Marshal.AllocHGlobal 分配的是"堆外内存"，C# 的垃圾回收管不到它，
            // 所以用完必须 FreeHGlobal —— 这是和原生代码打交道的常见责任。
            IntPtr buffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA 的第一个字段是 cbSize（4 字节），
                // 64 位系统要填 8，32 位要填 6（这是历史遗留的坑，记下来即可）。
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

                var devInfoData = new NativeMethods.SP_DEVINFO_DATA();
                devInfoData.cbSize = Marshal.SizeOf(typeof(NativeMethods.SP_DEVINFO_DATA));

                bool ok = NativeMethods.SetupDiGetDeviceInterfaceDetailWithInfo(
                    deviceInfoSet, ref interfaceData, buffer, requiredSize, out requiredSize, ref devInfoData);
                if (!ok) return null;

                // 设备路径字符串紧跟在 cbSize 之后，偏移 4 字节处开始（UTF-16 字符串）
                string devicePath = Marshal.PtrToStringUni(new IntPtr(buffer.ToInt64() + 4));
                if (string.IsNullOrEmpty(devicePath)) return null;

                var info = new HidDeviceInfo { DevicePath = devicePath };

                // 顺带把"设备实例 ID"问出来（设备管理器里的那个编号）
                info.InstanceId = GetInstanceId(deviceInfoSet, ref devInfoData);

                // 第 5 步：打开设备，问它的身份与能力
                FillDeviceDetails(info);
                return info;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>取设备实例 ID（例如 HID\FTCS0038&COL02\4&3a5b98db&0&0001）。</summary>
        private static string GetInstanceId(IntPtr deviceInfoSet, ref NativeMethods.SP_DEVINFO_DATA devInfoData)
        {
            var chars = new char[512];
            uint required;
            bool ok = NativeMethods.SetupDiGetDeviceInstanceId(
                deviceInfoSet, ref devInfoData, chars, (uint)chars.Length, out required);
            if (!ok) return "";

            int len = Array.IndexOf(chars, '\0');
            if (len < 0) len = chars.Length;
            return new string(chars, 0, len);
        }

        /// <summary>打开设备，读取 VID/PID、名字、HID 能力。</summary>
        private static void FillDeviceDetails(HidDeviceInfo info)
        {
            // 打开设备。权限优先级：读写 → 只读 → 最保守的 0（只能查询）。
            // 触摸板已被系统驱动打开，所以必须用"共享模式"，否则会被拒绝。
            bool readAccess;
            IntPtr handle = OpenDeviceHandle(info.DevicePath, out readAccess);
            if (handle == NativeMethods.INVALID_HANDLE_VALUE)
            {
                info.OpenError = NativeMethods.LastErrorText();
                return;
            }
            info.HasReadAccess = readAccess;

            IntPtr preparsed = IntPtr.Zero;
            try
            {
                // 身份卡
                var attributes = new NativeMethods.HIDD_ATTRIBUTES();
                attributes.Size = (uint)Marshal.SizeOf(typeof(NativeMethods.HIDD_ATTRIBUTES));
                if (NativeMethods.HidD_GetAttributes(handle, ref attributes))
                {
                    info.VendorId = attributes.VendorID;
                    info.ProductId = attributes.ProductID;
                    info.VersionNumber = attributes.VersionNumber;
                }

                // 名字（返回 UTF-16 字节流，需要自己解码；长度按字节算，512 表示最多 255 个字符）
                info.Manufacturer = ReadUnicodeString((byte[] buf) => NativeMethods.HidD_GetManufacturerString(handle, buf, (uint)buf.Length));
                info.ProductName = ReadUnicodeString((byte[] buf) => NativeMethods.HidD_GetProductString(handle, buf, (uint)buf.Length));
                info.SerialNumber = ReadUnicodeString((byte[] buf) => NativeMethods.HidD_GetSerialNumberString(handle, buf, (uint)buf.Length));

                // 能力汇总：先拿预解析数据，再问 HidP_GetCaps
                if (NativeMethods.HidD_GetPreparsedData(handle, out preparsed) && preparsed != IntPtr.Zero)
                {
                    var caps = new NativeMethods.HIDP_CAPS();
                    if (NativeMethods.HidP_GetCaps(preparsed, ref caps) == NativeMethods.HIDP_STATUS_SUCCESS)
                    {
                        info.UsagePage = caps.UsagePage;
                        info.Usage = caps.Usage;
                        info.InputReportBytes = caps.InputReportByteLength;
                        info.OutputReportBytes = caps.OutputReportByteLength;
                        info.FeatureReportBytes = caps.FeatureReportByteLength;
                        info.CapsAvailable = true;
                    }
                }
            }
            finally
            {
                // 预解析数据是系统分配的非托管内存，必须手动释放
                if (preparsed != IntPtr.Zero) NativeMethods.HidD_FreePreparsedData(preparsed);
                NativeMethods.CloseHandle(handle);
            }
        }

        /// <summary>打开设备句柄（带权限降级策略）。readAccess 会告诉我们最终有没有拿到读权限。</summary>
        internal static IntPtr OpenDeviceHandle(string devicePath, out bool readAccess)
        {
            readAccess = true;

            // 试 1：读写权限
            IntPtr handle = NativeMethods.CreateFile(
                devicePath, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle != NativeMethods.INVALID_HANDLE_VALUE) return handle;

            // 试 2：只读权限
            handle = NativeMethods.CreateFile(
                devicePath, NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle != NativeMethods.INVALID_HANDLE_VALUE) return handle;

            // 试 3：无读写权限（只能做查询类操作，读报告会失败 → 此时改用 Raw Input）
            readAccess = false;
            handle = NativeMethods.CreateFile(
                devicePath, 0,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            return handle;
        }

        /// <summary>读取 UTF-16 字符串（HID 的字符串 API 返回的是宽字符字节流）。</summary>
        private static string ReadUnicodeString(Func<byte[], bool> reader)
        {
            var buffer = new byte[512];
            if (!reader(buffer)) return "";
            // 用 UTF-16（小端）解码字节数组 —— 这就是 .NET 里的字符串编码转换
            string text = Encoding.Unicode.GetString(buffer);
            int end = text.IndexOf('\0');
            return end >= 0 ? text.Substring(0, end) : text;
        }
    }
}