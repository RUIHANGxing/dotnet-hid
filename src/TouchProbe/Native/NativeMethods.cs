using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TouchProbe.Native
{
    /// <summary>
    /// 【本文件是什么】
    /// 这里是 C# 代码和 Windows 原生 API（C 语言写的系统函数）之间的"桥"。
    ///
    /// 【为什么需要这座桥】
    /// Windows 的核心功能（枚举设备、打开设备、读报告）都是用 C 写的，放在几个 DLL 里：
    ///   - setupapi.dll  ：设备枚举（"有哪些设备？它们的身份证号是什么？"）
    ///   - hid.dll       ：HID 专用函数（"这个 HID 设备有什么能力？"）
    ///   - kernel32.dll  ：文件/句柄操作（打开设备、读取数据、关闭句柄）
    /// C# 不能直接调用它们，必须先把 C 的函数"声明"一遍（下面这些 [DllImport] 标记的方法），
    /// 这个技术叫 P/Invoke（Platform Invoke，平台调用）。运行时 .NET 会在 DLL 里按名字找到函数，
    /// 把 C# 的参数翻译成 C 的参数。
    ///
    /// 【关键翻译规则】
    ///   C 的 int/unsigned int/char* 等类型要一一对应到 C# 类型，见每个声明旁的注释。
    ///   结构体（struct）必须"内存布局一致"，否则字段会错位读到垃圾数据 —— 见下面的
    ///   [StructLayout(LayoutKind.Sequential)] 说明。
    /// </summary>
    public static class NativeMethods
    {
        // =====================================================================
        // 一、常量（这些数字是 Windows 规定的，改不得；原始定义在 Windows SDK 头文件里）
        // =====================================================================

        /// <summary>DIGCF_PRESENT：只列出"当前插着/在线"的设备。</summary>
        public const uint DIGCF_PRESENT = 0x02;

        /// <summary>DIGCF_DEVICEINTERFACE：按"设备接口"枚举（我们要的就是 HID 接口路径）。</summary>
        public const uint DIGCF_DEVICEINTERFACE = 0x10;

        /// <summary>GENERIC_READ：申请"可读"权限。</summary>
        public const uint GENERIC_READ = 0x80000000;

        /// <summary>GENERIC_WRITE：申请"可写"权限。</summary>
        public const uint GENERIC_WRITE = 0x40000000;

        /// <summary>FILE_SHARE_READ：别的程序也能同时读（触摸板已被系统驱动打开，必须允许共享）。</summary>
        public const uint FILE_SHARE_READ = 0x01;

        /// <summary>FILE_SHARE_WRITE：别的程序也能同时写。</summary>
        public const uint FILE_SHARE_WRITE = 0x02;

        /// <summary>OPEN_EXISTING：打开一个"已经存在"的设备（不是创建文件）。</summary>
        public const uint OPEN_EXISTING = 3;

        /// <summary>CreateFile 失败时返回的句柄值：全 1（0xFFFFFFFFFFFFFFFF）。</summary>
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        /// <summary>枚举到头时 GetLastError() 会给出这个错误码，属于正常结束。</summary>
        public const int ERROR_NO_MORE_ITEMS = 259;

        /// <summary>HIDP_STATUS_SUCCESS：HidP_* 系列函数成功时的返回值。</summary>
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        /// <summary>报告类型：HID 报告分三种（输入=设备→电脑，输出=电脑→设备，功能=可读可写配置项）。</summary>
        public const int HidP_Input = 0;
        public const int HidP_Output = 1;
        public const int HidP_Feature = 2;

        // =====================================================================
        // 二、结构体（C 的 struct 在 C# 里的对应物）
        // =====================================================================
        //
        // 【StructLayout(LayoutKind.Sequential) 是什么意思】
        // 它告诉 .NET：字段按声明顺序、一个接一个地摆放在内存里，不要优化、不要重排。
        // 这样 C# 结构体在内存中的字节排列就和 C 那边的结构体一模一样，
        // 原生函数按偏移量访问字段时才能读对。
        //
        // 【为什么字段类型要"斤斤计较"】
        // C 的 USHORT 是 2 字节、ULONG 在 Windows 上是 4 字节、UCHAR 是 1 字节、BOOLEAN 是 1 字节。
        // C# 里就是 ushort / uint / byte / bool（bool 要标明按 1 字节编组）。
        // 错一个字段，后面所有字段都会整体错位 —— 这是 P/Invoke 最常见的坑。

        /// <summary>SP_DEVICE_INTERFACE_DATA：一个"设备接口"的描述信息（由 SetupAPI 填充）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            /// <summary>结构体自身大小（调用前必须填，系统靠它判断版本）。</summary>
            public int cbSize;
            /// <summary>接口所属的类 GUID（HID 设备都挂在同一个 GUID 下）。</summary>
            public Guid InterfaceClassGuid;
            /// <summary>标志位（是否有设备实例等）。</summary>
            public int Flags;
            /// <summary>系统保留字段，用不到。</summary>
            public IntPtr Reserved;
        }

        /// <summary>SP_DEVINFO_DATA：一个"设备实例"的描述信息。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            /// <summary>设备安装类 GUID（HIDClass 类）。</summary>
            public Guid ClassGuid;
            /// <summary>设备实例句柄（一个整数编号，用来查询设备信息）。</summary>
            public int DevInst;
            public IntPtr Reserved;
        }

        /// <summary>HIDD_ATTRIBUTES：设备的"身份卡"——厂商 ID、产品 ID、版本号。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public uint Size;
            /// <summary>Vendor ID：厂商编号（如 FocalTech = 0x2808）。</summary>
            public ushort VendorID;
            /// <summary>Product ID：产品编号。</summary>
            public ushort ProductID;
            /// <summary>设备版本号（BCD 编码，如 0x0106 表示 1.06）。</summary>
            public ushort VersionNumber;
        }

        /// <summary>
        /// HIDP_CAPS：系统对"这个 HID 设备能干什么"的总结。
        /// 它来自报告描述符，但注意：它是"解析结果"，不是原始字节。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            /// <summary>顶层集合的用途（Usage），如 0x05 = Touch Pad。</summary>
            public ushort Usage;
            /// <summary>顶层集合的用途页（Usage Page），如 0x0D = Digitizer（数字化仪）。</summary>
            public ushort UsagePage;

            /// <summary>每个输入报告占多少字节（含 1 字节报告 ID，如果有的话）。</summary>
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;

            /// <summary>系统保留的 17 个 ushort，占位用，保证后面字段偏移正确。</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;

            /// <summary>集合节点个数（描述符里 Collection 语句的数量）。</summary>
            public ushort NumberLinkCollectionNodes;

            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            /// <summary>输入报告里"数据字段"的总个数（不含填充位）。</summary>
            public ushort NumberInputDataIndices;

            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;

            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        /// <summary>
        /// HIDP_BUTTON_CAPS：描述"一组 1 位字段"（按键、开关、触摸的 Tip Switch 等）。
        /// 注意 Windows 的规则：描述符里"1 位宽的可变字段"统统被归到按钮类。
        ///
        /// 【重要：这个布局是实测标定出来的，不是照抄网上资料】
        /// 网上很多 C# 版本把它写成 52 字节（保留区 18 字节），但在本机（Windows 26100）实测
        /// 真实结构体是 72 字节，保留区有 38 字节。写小了会导致系统往我们的缓冲区里多写数据，
        /// 直接踩坏堆内存（表现为程序莫名崩溃）。
        /// 标定方法：用 byte[] 当缓冲区调用 HidP_GetButtonCaps，再从返回的字节里反推出步长与字段偏移。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_BUTTON_CAPS
        {
            public ushort UsagePage;          // 偏移 +0
            public byte ReportID;             // +2
            [MarshalAs(UnmanagedType.U1)] public bool IsAlias;      // +3
            public ushort BitField;           // +4
            /// <summary>属于哪个逻辑集合（下标，对应 HIDP_LINK_COLLECTION_NODE 数组）。</summary>
            public ushort LinkCollection;     // +6
            public ushort LinkUsage;          // +8
            public ushort LinkUsagePage;      // +10
            /// <summary>true 表示这是一段"用途范围"（Usage Minimum ~ Usage Maximum）。</summary>
            [MarshalAs(UnmanagedType.U1)] public bool IsRange;          // +12
            [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;    // +13
            [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;// +14
            /// <summary>true = 绝对值（触摸坐标），false = 相对值（鼠标位移）。</summary>
            [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;       // +15
            /// <summary>+16 的标志位。含义未经验证（实测恒为 1），我们不使用它。</summary>
            public byte Flag16;
            /// <summary>+17 的对齐填充，保证后面的数组从偶数偏移开始。</summary>
            public byte Padding17;
            /// <summary>系统保留区：+18 ~ +55（共 38 字节）。</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 19)]
            public ushort[] Reserved;
            /// <summary>
            /// C 语言里的联合体（union）：同一块 16 字节内存，按 IsRange 有两种解读方式：
            ///   IsRange = true  → UsageMin / UsageMax / … / DataIndexMin / DataIndexMax
            ///   IsRange = false → Usage / … / DataIndex
            /// 位置在 +56 ~ +71（见 HidCaps.cs 里的读取函数）。
            /// </summary>
            public HIDP_UNION16 Union;
        }

        /// <summary>HIDP_BUTTON_CAPS 的期望大小（实测标定值，用于运行时自检）。</summary>
        public const int ButtonCapsSize = 72;

        /// <summary>HIDP_VALUE_CAPS 的期望大小（实测标定值，用于运行时自检）。</summary>
        public const int ValueCapsSize = 72;

        /// <summary>
        /// HIDP_VALUE_CAPS：描述"一组多位字段"（坐标、压力、计数等）。
        /// 布局同样是实测标定的：总长 72 字节，联合体在 +56 ~ +71（与 HIDP_BUTTON_CAPS 一致）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;          // +0
            public byte ReportID;             // +2
            [MarshalAs(UnmanagedType.U1)] public bool IsAlias;          // +3
            public ushort BitField;           // +4
            public ushort LinkCollection;     // +6
            public ushort LinkUsage;          // +8
            public ushort LinkUsagePage;      // +10

            [MarshalAs(UnmanagedType.U1)] public bool IsRange;          // +12
            [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;    // +13
            [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;// +14
            /// <summary>绝对值/相对值（触摸板的 X/Y 是绝对值）。</summary>
            [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;       // +15
            /// <summary>+16 的标志位（HID 规范里叫 HasNull，实测恒为 1，我们不使用）。</summary>
            [MarshalAs(UnmanagedType.U1)] public bool HasNull;          // +16
            public byte Reserved;             // +17

            /// <summary>每个值占多少位（比如 16 表示 16 位坐标）。</summary>
            public ushort BitSize;            // +18
            /// <summary>这个字段重复出现几次。</summary>
            public ushort ReportCount;        // +20
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public ushort[] Reserved2;        // +22 ~ +31

            /// <summary>单位指数（HID 的单位编码，暂时用不到，留着理解结构）。</summary>
            public uint UnitsExp;             // +32
            public uint Units;                // +36

            /// <summary>逻辑最小值：报告里数字的取值下限（决定有无符号、如何换算）。</summary>
            public int LogicalMin;            // +40
            /// <summary>逻辑最大值：报告里数字的取值上限。</summary>
            public int LogicalMax;            // +44
            /// <summary>物理最小值（换算成物理量后的下限，如毫米）。</summary>
            public int PhysicalMin;           // +48
            public int PhysicalMax;           // +52

            /// <summary>同 HIDP_BUTTON_CAPS 的联合体，位置 +56 ~ +71。</summary>
            public HIDP_UNION16 Union;
        }

        /// <summary>
        /// 占位结构体：模拟 C 里的 union（16 字节 = 8 个 ushort）。
        /// 具体哪个位置代表什么，取决于 IsRange，见 HidCaps.cs 里的读取代码。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_UNION16
        {
            public ushort W0, W1, W2, W3, W4, W5, W6, W7;
        }

        /// <summary>
        /// HIDP_LINK_COLLECTION_NODE：集合树的一个节点。
        /// 描述符里的 Collection/EndCollection 会形成一棵树，这里就是树的结构。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_LINK_COLLECTION_NODE
        {
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            /// <summary>父节点的下标。</summary>
            public ushort Parent;
            public ushort NumberOfChildren;
            public ushort NextSibling;
            public ushort FirstChild;
            /// <summary>
            /// C 里的位域：低 8 位是集合类型（0=Physical 1=Application 2=Logical 3=Report …），
            /// 第 8 位是 IsAlias。C# 里用一个 uint 表示，需要自己做位运算取出（见 HidCaps.cs）。
            /// </summary>
            public uint BitFields;
            public IntPtr UserContext;
        }

        /// <summary>
        /// HIDP_DATA：系统帮我们解码后的"一个字段的值"。
        /// DataIndex 是字段在报告里的序号，RawValue 是原始数值（按钮则是 0/1）。
        /// 我们用它来校验"自己按位算出来的值"是否正确。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_DATA
        {
            public ushort DataIndex;
            public ushort Reserved;
            public uint RawValue;
        }

        // =====================================================================
        // 三、原生函数声明（P/Invoke）
        // =====================================================================

        // ---------- 3.1 hid.dll：HID 专用 API ----------

        /// <summary>拿到"HID 设备类"的 GUID（枚举设备时要用它当过滤条件）。</summary>
        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        /// <summary>读取设备的 VID/PID/版本号。</summary>
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        /// <summary>
        /// 取"预解析数据"（preparsed data）：系统把报告描述符解析后的结果打包成的内存块。
        /// 注意：它是不透明的一大坨二进制，需要配合 HidP_* 函数使用。
        /// 用完必须用 HidD_FreePreparsedData 释放，否则内存泄漏。
        /// </summary>
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        /// <summary>释放预解析数据。</summary>
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        /// <summary>读取产品名（返回的是 UTF-16 字符串，所以要用 byte[] 接再解码）。</summary>
        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetProductString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);

        /// <summary>读取厂商名。</summary>
        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetManufacturerString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);

        /// <summary>读取序列号。</summary>
        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetSerialNumberString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);

        /// <summary>取"能力汇总"（顶层用途、各报告长度、各类字段个数）。</summary>
        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

        /// <summary>取"按钮类字段"清单（1 位宽的字段）。</summary>
        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetButtonCaps(
            int reportType, [Out] HIDP_BUTTON_CAPS[] buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);

        /// <summary>取"数值类字段"清单（多位宽的字段，如坐标、压力）。</summary>
        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetValueCaps(
            int reportType, [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

        /// <summary>取"集合树"（描述符里的 Collection 层级结构）。</summary>
        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetLinkCollectionNodes(
            [Out] HIDP_LINK_COLLECTION_NODE[] linkCollectionNodes, ref uint linkCollectionNodesLength, IntPtr preparsedData);

        /// <summary>
        /// 让系统按描述符"按位解包"一个输入报告，返回一连串 (DataIndex, RawValue)。
        /// 我们用它的结果来交叉验证自己写的位提取逻辑。
        /// </summary>
        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetData(
            int reportType, [Out] HIDP_DATA[] dataList, ref uint dataLength,
            IntPtr preparsedData, byte[] report, uint reportLength);

        /// <summary>
        /// 让系统从报告里取"某一个用途"的值。
        /// （我们主要用 HidP_GetData；这个留着做单字段对照，也是官方推荐用法之一。）
        /// </summary>
        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetUsageValue(
            int reportType, ushort usagePage, ushort linkCollection, ushort usage,
            out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

        // ---------- 3.2 setupapi.dll：设备枚举 ----------

        /// <summary>获取"设备信息集合"句柄（枚举的起点）。</summary>
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        /// <summary>按序号取出一个设备接口（序号从 0 开始，直到函数返回 false）。</summary>
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
            uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        /// <summary>
        /// 取设备接口的详细信息。第一次调用传 detail = IntPtr.Zero 用来问"需要多大缓冲区"，
        /// 第二次才真正把数据填进我们分配的缓冲区（这是 Windows API 常见的两步套路）。
        /// </summary>
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
            out uint requiredSize, IntPtr deviceInfoData);

        /// <summary>同上，但顺带把"设备实例信息"也取回来（我们要用它查设备实例 ID）。</summary>
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetail")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SetupDiGetDeviceInterfaceDetailWithInfo(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
            out uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

        /// <summary>取设备实例 ID（形如 HID\FTCS0038&COL02\4&3a5b98db&0&0001）。</summary>
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SetupDiGetDeviceInstanceId(
            IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
            [Out] char[] deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

        /// <summary>释放设备信息集合（枚举结束后必须调用）。</summary>
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        // ---------- 3.3 kernel32.dll：句柄与读写 ----------

        /// <summary>
        /// 打开设备（"设备路径"当文件名用）。返回"句柄"——一个由系统分配的整数编号，
        /// 之后所有读写都用这个句柄指代这个设备。
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        /// <summary>从设备读数据。HID 设备很特别：没有新报告时会"阻塞"（一直等）。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool ReadFile(
            IntPtr file, byte[] buffer, uint numberOfBytesToRead, out uint numberOfBytesRead, IntPtr overlapped);

        /// <summary>关闭句柄（相当于 C 里的 CloseHandle）。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool CloseHandle(IntPtr handle);

        // =====================================================================
        // 四、Raw Input（原始输入）相关：当设备被系统独占时，靠它拿原始报告
        // =====================================================================

        /// <summary>窗口类描述（RegisterClassEx 用）。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        /// <summary>窗口消息（GetMessage 用）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        /// <summary>订阅说明：我要哪一类设备的原始输入、投递到哪个窗口。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        /// <summary>原始输入的头部：类型、大小、设备句柄。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType;      // 1=鼠标 2=键盘 2=HID（RIM_TYPEHID = 2）
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX wndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool RegisterRawInputDevices(
            RAWINPUTDEVICE[] devices, uint count, uint structureSize);

        /// <summary>取出原始数据。第一次用空指针问大小，第二次真正取内容。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputData(
            IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        /// <summary>查询原始输入设备的信息（我们要它的设备名）。</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetRawInputDeviceInfo(
            IntPtr device, uint command, IntPtr data, ref uint size);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMessage(out MSG message, IntPtr hWnd, uint min, uint max);

        /// <summary>PeekMessage：看一眼有没有消息，不阻塞（用来做"等一会儿看看"的循环）。</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool PeekMessage(out MSG message, IntPtr hWnd, uint min, uint max, uint removeMessage);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool TranslateMessage(ref MSG message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DispatchMessage(ref MSG message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        // =====================================================================
        // 五、小工具
        // =====================================================================

        /// <summary>把"最后一次 Windows 错误码"翻译成人能读的文字。</summary>
        public static string LastErrorText()
        {
            int code = Marshal.GetLastWin32Error();
            try
            {
                return "错误码 " + code + "（" + new Win32Exception(code).Message + "）";
            }
            catch
            {
                return "错误码 " + code;
            }
        }
    }
}