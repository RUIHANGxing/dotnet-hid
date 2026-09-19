using System;
using System.Collections.Generic;

namespace TouchProbe.View
{
    /// <summary>
    /// 【本文件是什么】
    /// 鼠标报的是"相对位移"（我又向右移动了 3 格），不是"我现在在哪"。
    /// 所以想画出鼠标轨迹，就得自己把位移**累加**起来 —— 这是很多图形程序的第一步。
    ///
    /// 位置用 0~1 的归一化坐标保存：0 = 最左/最上，1 = 最右/最下。
    /// 画图时再按字符网格的大小换算成格子坐标。
    /// </summary>
    public sealed class PointerTracker
    {
        /// <summary>当前指针位置（归一化 0~1）。</summary>
        public double X { get; private set; } = 0.5;
        public double Y { get; private set; } = 0.5;

        /// <summary>最近一段轨迹（用来画"尾巴"）。</summary>
        public List<(double X, double Y)> Trail { get; } = new List<(double X, double Y)>();

        /// <summary>一次位移换算成多少归一化距离。数值越大，指针动得越快。</summary>
        private const double UnitsPerScreen = 600.0;

        /// <summary>单次位移的上限：某些设备会报很大的值，不夹住会让指针"瞬移"。</summary>
        private const int MaxDeltaPerEvent = 120;

        /// <summary>轨迹最多保留多少个点（防止列表无限增长）。</summary>
        private const int MaxTrailLength = 120;

        /// <summary>累加一次相对位移（鼠标模式用）。</summary>
        public void ApplyRelative(int deltaX, int deltaY)
        {
            X = Clamp01(X + Clamp(deltaX) / UnitsPerScreen);
            Y = Clamp01(Y + Clamp(deltaY) / UnitsPerScreen);

            Trail.Add((X, Y));
            if (Trail.Count > MaxTrailLength) Trail.RemoveAt(0);
        }

        private static int Clamp(int delta)
        {
            if (delta > MaxDeltaPerEvent) return MaxDeltaPerEvent;
            if (delta < -MaxDeltaPerEvent) return -MaxDeltaPerEvent;
            return delta;
        }

        /// <summary>直接设置绝对位置（触摸板模式用，把逻辑坐标映射到 0~1）。</summary>
        public void ApplyAbsolute(long valueX, long valueY, long minX, long maxX, long minY, long maxY)
        {
            X = maxX > minX ? Clamp01((double)(valueX - minX) / (maxX - minX)) : 0.5;
            Y = maxY > minY ? Clamp01((double)(valueY - minY) / (maxY - minY)) : 0.5;

            Trail.Add((X, Y));
            if (Trail.Count > MaxTrailLength) Trail.RemoveAt(0);
        }

        /// <summary>把指针拉回屏幕中央（按一下回车之类可以调用）。</summary>
        public void Reset()
        {
            X = 0.5;
            Y = 0.5;
            Trail.Clear();
        }

        private static double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }
    }
}