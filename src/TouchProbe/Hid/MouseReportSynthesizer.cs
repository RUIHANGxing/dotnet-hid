using System;
using System.Collections.Generic;
using TouchProbe.Native;

namespace TouchProbe.Hid
{
    /// <summary>
    /// 【本文件是什么 —— 一个重要的现实妥协，也是一个很好的教学点】
    ///
    /// Windows 的 Raw Input 有一条硬规定：
    ///   · 如果订阅的是"鼠标"或"键盘"这类顶层集合，系统只给你 **RAWMOUSE / RAWKEYBOARD**
    ///     （预处理过的 dx/dy/按键标志），**不给**原始 HID 报告字节；
    ///   · 只有其它用途（比如触摸板的 0x0D/0x05、厂商自定义页）才会给 RAWHID（真正的报告字节）。
    ///
    /// 所以鼠标这条路拿不到"设备发的原始字节"。但我们可以做一件很有教学价值的事：
    /// **按描述符把 dx/dy/按键重新"编码"成报告字节**，然后走完全相同的解码流程。
    /// 这样你能亲眼看到"描述符既是解码的地图，也是编码的地图"—— 两者是互逆的。
    ///
    /// （对照：触摸板 / 厂商集合走的是真·原始报告字节，见 RawInputReader 和 HidDevice。）
    /// </summary>
    public sealed class MouseReportSynthesizer
    {
        private readonly ReportLayout _layout;
        private readonly int _reportId;
        private readonly int _reportLength;

        private readonly ReportField _fieldX;
        private readonly ReportField _fieldY;
        private readonly ReportField _fieldWheel;

        /// <summary>鼠标按键字段（用途页 0x09 的那些 1 位字段），按用途号排序。</summary>
        private readonly List<ReportField> _buttons = new List<ReportField>();

        // 按键的"按下状态"要自己维护：RAWMOUSE 只告诉你"这一下是按了还是松了"
        private bool _leftDown, _rightDown, _middleDown;

        public MouseReportSynthesizer(ReportLayout layout, NativeMethods.HIDP_CAPS caps, int reportId)
        {
            _layout = layout;
            _reportId = reportId;
            _reportLength = caps.InputReportByteLength;

            foreach (var field in layout.FieldsOf(reportId))
            {
                if (field.UsagePage == 0x01 && field.Usage == 0x30) _fieldX = field;
                else if (field.UsagePage == 0x01 && field.Usage == 0x31) _fieldY = field;
                else if (field.UsagePage == 0x01 && field.Usage == 0x38) _fieldWheel = field;
                else if (field.UsagePage == 0x09) _buttons.Add(field);
            }
        }

        /// <summary>自动找出"装着 X/Y 的那个报告"，为它建一个编码器。</summary>
        public static MouseReportSynthesizer Create(ReportLayout layout, NativeMethods.HIDP_CAPS caps)
        {
            int reportId = 0;
            bool found = false;

            foreach (var pair in layout.ByReportId)
            {
                foreach (var field in pair.Value)
                {
                    if (field.UsagePage == 0x01 && (field.Usage == 0x30 || field.Usage == 0x31))
                    {
                        reportId = pair.Key;
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }

            return new MouseReportSynthesizer(layout, caps, reportId);
        }

        /// <summary>
        /// 用一次 RAWMOUSE 事件的内容生成一包"报告字节"。
        /// rawButtonFlags 的低位含义（Windows 规定）：
        ///   0x0001 左键按下 / 0x0002 左键抬起
        ///   0x0004 右键按下 / 0x0008 右键抬起
        ///   0x0010 中键按下 / 0x0020 中键抬起
        ///   0x0400 滚轮事件（滚动量在 buttonData 里）
        /// </summary>
        public byte[] Build(int deltaX, int deltaY, ushort rawButtonFlags, short wheelDelta)
        {
            var report = new byte[_reportLength];
            if (_layout.HasReportIds) report[0] = (byte)_reportId;

            int dataStart = _layout.DataStartByte;

            if (_fieldX != null) WriteField(report, dataStart, _fieldX, deltaX);
            if (_fieldY != null) WriteField(report, dataStart, _fieldY, deltaY);
            if (_fieldWheel != null && wheelDelta != 0) WriteField(report, dataStart, _fieldWheel, wheelDelta);

            // 更新按键状态
            if ((rawButtonFlags & 0x0001) != 0) _leftDown = true;
            if ((rawButtonFlags & 0x0002) != 0) _leftDown = false;
            if ((rawButtonFlags & 0x0004) != 0) _rightDown = true;
            if ((rawButtonFlags & 0x0008) != 0) _rightDown = false;
            if ((rawButtonFlags & 0x0010) != 0) _middleDown = true;
            if ((rawButtonFlags & 0x0020) != 0) _middleDown = false;

            // 把状态写进按键字段（按用途号 1=左、2=右、3=中）
            for (int i = 0; i < _buttons.Count; i++)
            {
                var field = _buttons[i];
                bool pressed = field.Usage == 1 ? _leftDown
                             : field.Usage == 2 ? _rightDown
                             : field.Usage == 3 ? _middleDown
                             : false;
                WriteField(report, dataStart, field, pressed ? 1 : 0);
            }

            return report;
        }

        /// <summary>把一个数值写进报告指定位域（ExtractBits 的逆操作）。</summary>
        private static void WriteField(byte[] report, int dataStartByte, ReportField field, long value)
        {
            // 有符号字段：负数要转成补码形式再写
            ulong bits = unchecked((ulong)value) & MaskFor(field.BitSize);
            BitWriter.WriteBits(report, dataStartByte, field.BitOffset, field.BitSize, bits);
        }

        private static ulong MaskFor(int bitSize)
        {
            if (bitSize >= 64) return ulong.MaxValue;
            return (1UL << bitSize) - 1;
        }
    }
}