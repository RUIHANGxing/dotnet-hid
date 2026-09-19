using System.Collections.Generic;

namespace TouchProbe.Hid
{
    /// <summary>
    /// 【本文件是什么】
    /// 一张"用途表"：HID 协议用两个数字表示"这东西是干什么的"——
    ///   用途页（Usage Page）：大类，比如 0x01 = 通用桌面设备，0x0D = 数字化仪（触摸/手写）
    ///   用途（Usage）      ：小类，比如 0x30 = X 轴，0x42 = 笔尖开关（Tip Switch）
    /// 例如：0x0D/0x05 = 数字化仪类里的"触摸板"；0x01/0x30 = 通用桌面类的"X 轴"。
    ///
    /// 这张表只收录了常用的一部分（够读懂触摸板）。遇到表里没有的用途，
    /// 程序会直接打印 0xPPPP:0xUUUU 这样的十六进制编码，你可以去 USB-IF 的
    /// 《HID Usage Tables》文档里查（文档编号 hut1_12v2.pdf）。
    /// </summary>
    public static class HidUsageNames
    {
        /// <summary>用途页名字（key = 用途页编号）。</summary>
        private static readonly Dictionary<int, string> Pages = new Dictionary<int, string>
        {
            { 0x01, "Generic Desktop" },   // 通用桌面：鼠标、键盘、X/Y 轴…
            { 0x02, "Simulation" },
            { 0x03, "VR" },
            { 0x04, "Sport" },
            { 0x05, "Game" },
            { 0x06, "Generic Device" },
            { 0x08, "LED" },
            { 0x09, "Button" },            // 按键页：Button 1、Button 2…
            { 0x0A, "Ordinal" },
            { 0x0C, "Consumer" },          // 多媒体键
            { 0x0D, "Digitizer" },         // 数字化仪：触摸板/触摸屏/手写板
            { 0x0E, "Haptics" },
            { 0x0F, "PID" },
            { 0x10, "Unicode" },
            { 0x14, "Alphanumeric" },
            { 0x15, "Medical" },
            { 0x16, "Monitor" },
            { 0x20, "Sensor" },
            { 0x84, "Power" },
            { 0xFF00, "Vendor 0xFF00" },   // 厂商自定义
            { 0xFF01, "Vendor 0xFF01" },
        };

        /// <summary>具体用途名字（key = 用途页 &lt;&lt; 16 | 用途）。</summary>
        private static readonly Dictionary<int, string> Usages = new Dictionary<int, string>
        {
            // ---------- 0x01 通用桌面 ----------
            { Key(0x01, 0x01), "Pointer" },
            { Key(0x01, 0x02), "Mouse" },
            { Key(0x01, 0x04), "Joystick" },
            { Key(0x01, 0x05), "Game Pad" },
            { Key(0x01, 0x06), "Keyboard" },
            { Key(0x01, 0x07), "Keypad" },
            { Key(0x01, 0x30), "X" },
            { Key(0x01, 0x31), "Y" },
            { Key(0x01, 0x32), "Z" },
            { Key(0x01, 0x38), "Wheel" },
            { Key(0x01, 0x39), "Hat Switch" },

            // ---------- 0x09 按键页 ----------
            { Key(0x09, 0x01), "Button 1" },
            { Key(0x09, 0x02), "Button 2" },
            { Key(0x09, 0x03), "Button 3" },

            // ---------- 0x0D 数字化仪（触摸板的重点） ----------
            { Key(0x0D, 0x01), "Digitizer" },
            { Key(0x0D, 0x02), "Pen" },
            { Key(0x0D, 0x03), "Light Pen" },
            { Key(0x0D, 0x04), "Touch Screen" },
            { Key(0x0D, 0x05), "Touch Pad" },              // ← 我们的触摸板顶层用途
            { Key(0x0D, 0x06), "White Board" },
            { Key(0x0D, 0x07), "Coordinate Measuring Machine" },
            { Key(0x0D, 0x08), "3D Digitizer" },
            { Key(0x0D, 0x20), "Stylus" },
            { Key(0x0D, 0x22), "Finger" },                 // ← “一个手指”的逻辑集合
            { Key(0x0D, 0x30), "Tip Pressure" },           // 压力
            { Key(0x0D, 0x32), "In Range" },               // 是否进入感应范围
            { Key(0x0D, 0x33), "Touch" },
            { Key(0x0D, 0x3D), "Azimuth" },
            { Key(0x0D, 0x3E), "Altitude" },
            { Key(0x0D, 0x41), "Twist" },
            { Key(0x0D, 0x42), "Tip Switch" },             // 笔尖/手指是否“按下”
            { Key(0x0D, 0x43), "Secondary Tip Switch" },
            { Key(0x0D, 0x44), "Barrel Switch" },
            { Key(0x0D, 0x45), "Eraser" },
            { Key(0x0D, 0x46), "Tablet Pick" },
            { Key(0x0D, 0x47), "Confidence" },             // 这组数据可信吗
            { Key(0x0D, 0x48), "Width" },
            { Key(0x0D, 0x49), "Height" },
            { Key(0x0D, 0x51), "Contact Identifier" },     // 触点编号（哪根手指）
            { Key(0x0D, 0x52), "Device Mode" },
            { Key(0x0D, 0x53), "Device Identifier" },
            { Key(0x0D, 0x54), "Contact Count" },          // 当前有几个触点
            { Key(0x0D, 0x55), "Contact Count Maximum" },  // 最多支持几个触点（在功能报告里）
            { Key(0x0D, 0x56), "Scan Time" },              // 扫描时间
            { Key(0x0D, 0x5A), "Pad Type" },
        };

        /// <summary>把"用途页"和"用途"打包成一个 key。</summary>
        private static int Key(int page, int usage)
        {
            return (page << 16) | (usage & 0xFFFF);
        }

        /// <summary>用途页的英文名（查不到就返回 0xFFFF 形式）。</summary>
        public static string PageName(int page)
        {
            string name;
            if (Pages.TryGetValue(page, out name)) return name;
            return "0x" + page.ToString("X4");
        }

        /// <summary>用途的名字（查不到就返回 0xPPPP:0xUUUU）。</summary>
        public static string UsageName(int page, int usage)
        {
            string name;
            if (Usages.TryGetValue(Key(page, usage), out name)) return name;
            return "0x" + page.ToString("X4") + ":0x" + ((ushort)usage).ToString("X4");
        }

        /// <summary>组合成可读的一行，如 "Digitizer(0x0D) / Tip Switch(0x42)"。</summary>
        public static string Describe(int page, int usage)
        {
            return PageName(page) + "/" + UsageName(page, usage);
        }
    }
}