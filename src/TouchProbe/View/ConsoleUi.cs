using System;
using System.Collections.Generic;
using System.Text;

namespace TouchProbe.View
{
    /// <summary>
    /// 【本文件是什么】
    /// 控制台"美工"：把结果排成好看的表格、十六进制块、和一张能看见手指位置的字符地图。
    ///
    /// 控制台本身只能打印文字，所以"画图"是用字符拼的：
    ///   · 用 Console.ForegroundColor 换颜色
    ///   · 用 Console.SetCursorPosition 把光标挪回左上角重画（动画就是这么来的）
    /// </summary>
    public static class ConsoleUi
    {
        /// <summary>一个触点（画图用的小数据结构）。</summary>
        public sealed class TouchPoint
        {
            public int Id;
            public int X;
            public int Y;
            public bool Tip;
        }

        /// <summary>把控制台切成 UTF-8 输出，避免中文乱码。</summary>
        public static void Setup()
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
                // 某些重定向场景下设置编码会失败，忽略即可
            }
        }

        // ---------------- 标题与提示 ----------------

        public static void Title(string text)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=== " + text + " ===");
            Console.ResetColor();
        }

        public static void Step(int number, string text)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("【第 " + number + " 步】");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        public static void Info(string text)
        {
            Console.WriteLine("  " + text);
        }

        public static void Note(string text)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  · " + text);
            Console.ResetColor();
        }

        public static void Ok(string text)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  ✔ " + text);
            Console.ResetColor();
        }

        public static void Warn(string text)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ! " + text);
            Console.ResetColor();
        }

        public static void Error(string text)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ✘ " + text);
            Console.ResetColor();
        }

        /// <summary>打印 "名字 : 值" 形式的一行。</summary>
        public static void KeyValue(string key, string value, int keyWidth)
        {
            string pad = key.PadRight(keyWidth);
            Console.Write("  " + pad + " : ");
            Console.WriteLine(value);
        }

        // ---------------- 十六进制 ----------------

        /// <summary>把字节数组按固定宽度排版成十六进制 + ASCII 对照（教学最常用的展示形式）。</summary>
        public static void HexDump(byte[] data, int bytesPerLine)
        {
            for (int i = 0; i < data.Length; i += bytesPerLine)
            {
                var hex = new StringBuilder();
                var ascii = new StringBuilder();

                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (i + j < data.Length)
                    {
                        byte b = data[i + j];
                        hex.Append(b.ToString("X2"));
                        hex.Append(' ');
                        // 可打印字符才显示，否则显示点
                        ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
                    }
                    else
                    {
                        hex.Append("   ");
                        ascii.Append(' ');
                    }
                }

                Console.Write("  " + i.ToString("X4") + "  ");
                Console.Write(hex.ToString());
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(" |" + ascii + "|");
                Console.ResetColor();
            }
        }

        // ---------------- 表格 ----------------

        /// <summary>打印一张对齐的表格（用等宽字符算宽度，中文按 2 个字符宽处理）。</summary>
        public static void Table(string[] headers, List<string[]> rows, int[] widths)
        {
            PrintRow(headers, widths, ConsoleColor.Cyan);

            var separator = new StringBuilder("  ");
            for (int i = 0; i < widths.Length; i++) separator.Append(new string('-', widths[i] + 2));
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(separator.ToString());
            Console.ResetColor();

            foreach (var row in rows) PrintRow(row, widths, null);
        }

        private static void PrintRow(string[] cells, int[] widths, ConsoleColor? color)
        {
            if (color.HasValue) Console.ForegroundColor = color.Value;

            var sb = new StringBuilder("  ");
            for (int i = 0; i < cells.Length; i++)
            {
                string text = cells[i] ?? "";
                sb.Append(text);
                sb.Append(new string(' ', Math.Max(1, widths[i] - DisplayWidth(text) + 2)));
            }
            Console.WriteLine(sb.ToString().TrimEnd());

            if (color.HasValue) Console.ResetColor();
        }

        /// <summary>算字符串显示宽度：中文/全角字符算 2，其余算 1。</summary>
        public static int DisplayWidth(string text)
        {
            int width = 0;
            foreach (char c in text) width += c > 0x7F ? 2 : 1;
            return width;
        }

        /// <summary>按显示宽度截断字符串（防止表格被撑爆）。</summary>
        public static string Truncate(string text, int maxWidth)
        {
            if (DisplayWidth(text) <= maxWidth) return text;

            var sb = new StringBuilder();
            int width = 0;
            foreach (char c in text)
            {
                int w = c > 0x7F ? 2 : 1;
                if (width + w > maxWidth - 1) break;
                sb.Append(c);
                width += w;
            }
            return sb.ToString() + "…";
        }

        /// <summary>画一条进度条（用来直观显示坐标范围）。</summary>
        public static string Bar(long value, long min, long max, int width)
        {
            if (max <= min) return "";

            double ratio = (double)(value - min) / (max - min);
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            int filled = (int)Math.Round(ratio * width);
            return new string('#', filled) + new string('.', width - filled);
        }

        // ---------------- 触摸位置地图 ----------------

        /// <summary>
        /// 用字符画一张触摸板示意图，手指按下去的位置会显示触点的编号。
        /// 坐标会按设备的逻辑范围（minX~maxX）映射到字符网格上。
        /// </summary>
        public static List<string> BuildTouchMap(
            List<TouchPoint> points, int minX, int maxX, int minY, int maxY, int width, int height)
        {
            var lines = new List<string>();

            // 先造一张空网格
            var grid = new char[height, width];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    grid[y, x] = ' ';

            // 画背景参考点，方便看出手指移动
            for (int y = 1; y < height - 1; y += 2)
                for (int x = 1; x < width - 1; x += 4)
                    grid[y, x] = '·';

            // 把每个触点放进网格
            foreach (var p in points)
            {
                if (!p.Tip) continue;

                double rx = maxX > minX ? (double)(p.X - minX) / (maxX - minX) : 0.5;
                double ry = maxY > minY ? (double)(p.Y - minY) / (maxY - minY) : 0.5;

                if (rx < 0 || rx > 1 || ry < 0 || ry > 1) continue;

                int gx = 1 + (int)Math.Round(rx * (width - 3));
                int gy = 1 + (int)Math.Round(ry * (height - 3));

                // 注意：触摸板的 Y 是"上小下大"还是"上大下小"取决于设备，
                // 我们这里直接把逻辑值映射过来；如果反了，说明设备坐标原点在左下角。
                char mark = (char)('0' + (p.Id % 10));
                grid[gy, gx] = mark;
            }

            // 加上边框（注意：二维数组不能直接当字符串用，要逐行拼出来）
            lines.Add("┌" + new string('─', width - 2) + "┐");
            for (int y = 0; y < height; y++)
            {
                var row = new StringBuilder(width);
                for (int x = 0; x < width; x++) row.Append(grid[y, x]);
                lines.Add("│" + row + "│");
            }
            lines.Add("└" + new string('─', width - 2) + "┘");

            return lines;
        }

        /// <summary>
        /// 用字符画一张"指针轨迹图"（鼠标模式用）：
        /// 走过的路径显示为小点，当前位置显示为 @。
        /// 坐标是 0~1 的归一化值。
        /// </summary>
        public static List<string> BuildTraceMap(
            List<(double X, double Y)> trail, double currentX, double currentY, int width, int height)
        {
            var lines = new List<string>();

            var grid = new char[height, width];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    grid[y, x] = ' ';

            // 背景参考点
            for (int y = 1; y < height - 1; y += 2)
                for (int x = 1; x < width - 1; x += 4)
                    grid[y, x] = '·';

            // 轨迹
            foreach (var point in trail)
            {
                int gx = 1 + (int)Math.Round(point.X * (width - 3));
                int gy = 1 + (int)Math.Round(point.Y * (height - 3));
                if (gx > 0 && gx < width - 1 && gy > 0 && gy < height - 1) grid[gy, gx] = '░';
            }

            // 当前位置
            int cx = 1 + (int)Math.Round(currentX * (width - 3));
            int cy = 1 + (int)Math.Round(currentY * (height - 3));
            if (cx > 0 && cx < width - 1 && cy > 0 && cy < height - 1) grid[cy, cx] = '@';

            lines.Add("┌" + new string('─', width - 2) + "┐");
            for (int y = 0; y < height; y++)
            {
                var row = new StringBuilder(width);
                for (int x = 0; x < width; x++) row.Append(grid[y, x]);
                lines.Add("│" + row + "│");
            }
            lines.Add("└" + new string('─', width - 2) + "┘");

            return lines;
        }
    }
}