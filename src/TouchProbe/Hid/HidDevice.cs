using System;
using System.Runtime.InteropServices;
using TouchProbe.Native;

namespace TouchProbe.Hid
{
    /// <summary>
    /// 【本文件是什么】
    /// 一个"已打开的 HID 设备"的封装：打开句柄、取能力、读输入报告、释放资源。
    ///
    /// 【为什么要包成一个类】
    /// 打开设备会占用系统资源（句柄 + 预解析数据内存），必须配对释放。
    /// C# 提供了 IDisposable 机制：用 using 包裹后，代码块结束时自动调用 Dispose()，
    /// 哪怕中途抛异常也不会漏掉 —— 这是 C# 里管理"非托管资源"的标准做法。
    ///
    /// 【什么是句柄】
    /// 句柄（handle）就是操作系统给你的一个编号，代表"你打开的那个东西"。
    /// 它不是指针，不能直接解引用；所有操作都要把编号还给系统，让系统去办。
    /// 用完不还 = 资源泄漏（Windows 里叫句柄泄漏）。
    /// </summary>
    public sealed class HidDevice : IDisposable
    {
        /// <summary>设备句柄（CreateFile 的返回值）。</summary>
        private IntPtr _handle = NativeMethods.INVALID_HANDLE_VALUE;

        /// <summary>预解析数据指针（系统解析报告描述符后的结果，HidP_* 函数都要用它）。</summary>
        private IntPtr _preparsedData = IntPtr.Zero;

        private bool _disposed;

        /// <summary>被打开设备的基本信息。</summary>
        public HidDeviceInfo Info { get; }

        /// <summary>能力汇总（顶层用途、报告长度、字段个数）。</summary>
        public NativeMethods.HIDP_CAPS Caps { get; private set; }

        /// <summary>是否有读权限（没有的话读报告会失败，需要改用 Raw Input）。</summary>
        public bool HasReadAccess { get; private set; }

        private HidDevice(HidDeviceInfo info)
        {
            Info = info;
        }

        /// <summary>打开设备并读取能力信息。</summary>
        public static HidDevice Open(HidDeviceInfo info)
        {
            var device = new HidDevice(info);

            device._handle = HidDeviceEnumerator.OpenDeviceHandle(info.DevicePath, out bool readAccess);
            if (device._handle == NativeMethods.INVALID_HANDLE_VALUE)
            {
                throw new InvalidOperationException(
                    "打不开设备：" + info.DevicePath + "，" + NativeMethods.LastErrorText());
            }
            device.HasReadAccess = readAccess;

            if (!NativeMethods.HidD_GetPreparsedData(device._handle, out device._preparsedData)
                || device._preparsedData == IntPtr.Zero)
            {
                string err = NativeMethods.LastErrorText();
                device.Dispose();
                throw new InvalidOperationException("读取设备能力失败（HidD_GetPreparsedData）：" + err);
            }

            var caps = new NativeMethods.HIDP_CAPS();
            if (NativeMethods.HidP_GetCaps(device._preparsedData, ref caps) != NativeMethods.HIDP_STATUS_SUCCESS)
            {
                device.Dispose();
                throw new InvalidOperationException("读取设备能力失败（HidP_GetCaps）");
            }
            device.Caps = caps;

            return device;
        }

        /// <summary>预解析数据指针（给 HidCaps 里的解析函数用）。</summary>
        internal IntPtr PreparsedData
        {
            get { return _preparsedData; }
        }

        /// <summary>
        /// 读一个输入报告（会阻塞：没有新报告就一直等，直到设备上报或出错）。
        /// 返回的字节数组长度 = 能力里的 InputReportByteLength。
        /// 若描述符定义了报告 ID，则第 1 个字节就是报告 ID，后面才是数据。
        /// </summary>
        public byte[] ReadInputReport()
        {
            int length = Caps.InputReportByteLength;
            if (length <= 0) throw new InvalidOperationException("这个设备没有输入报告");

            var buffer = new byte[length];
            uint read;
            bool ok = NativeMethods.ReadFile(_handle, buffer, (uint)length, out read, IntPtr.Zero);
            if (!ok)
            {
                throw new InvalidOperationException("读取报告失败：" + NativeMethods.LastErrorText());
            }
            if (read != (uint)length)
            {
                // 正常情况不会发生；真发生了说明设备行为不合规范
                throw new InvalidOperationException("报告长度异常：期望 " + length + "，实际 " + read);
            }
            return buffer;
        }

        /// <summary>关闭句柄与预解析数据。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_preparsedData != IntPtr.Zero)
            {
                NativeMethods.HidD_FreePreparsedData(_preparsedData);
                _preparsedData = IntPtr.Zero;
            }

            if (_handle != NativeMethods.INVALID_HANDLE_VALUE)
            {
                NativeMethods.CloseHandle(_handle);
                _handle = NativeMethods.INVALID_HANDLE_VALUE;
            }
        }
    }
}