using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TouchProbe.Hid
{
    /// <summary>
    /// 【本文件是什么 —— 学 C# Task 的第一手素材】
    ///
    /// 读设备报告是"阻塞式"的：调用 ReadFile 后没有数据就一直等。
    /// 如果直接在界面上等，界面就卡住了。标准解法是把"读"和"处理"拆成两个任务：
    ///
    ///     生产者任务（后台线程）            Channel（传送带）        消费者（主流程）
    ///     Task.Run { ReadFile 循环 }  →→→  Channel&lt;byte[]&gt;  →→→  await foreach { 解码 + 显示 }
    ///
    /// 这就是最经典的**生产者-消费者模型**，也是 Task / async-await / CancellationToken 的标准用法。
    ///
    /// 用到的知识点（对照看代码）：
    ///   · Task.Run           把阻塞代码丢到线程池线程上跑
    ///   · Channel<T>         .NET 自带的异步队列，生产者写、消费者读，自动处理线程安全
    ///   · CancellationToken  取消令牌：想让循环停下来时，把它"触发"即可
    ///   · await foreach      异步地一个个取数据，不阻塞线程
    ///   · Interlocked        多线程下安全地做计数（普通 ++ 在多线程里会数错）
    /// </summary>
    public sealed class ReportPipeline : IDisposable
    {
        /// <summary>
        /// 传送带。设成"有界 + 满了丢最旧"：
        /// 万一显示跟不上采集速度（触摸板一秒上百包），内存不会无限涨，只丢旧数据。
        /// 这叫"背压（backpressure）"，是做数据管道时必须考虑的问题。
        /// </summary>
        private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        /// <summary>生产者任务（读设备的那条后台循环）。</summary>
        public Task ProducerTask { get; private set; }

        /// <summary>总共读到了多少包（多线程计数，用 Interlocked 保证正确）。</summary>
        private long _producedCount;

        /// <summary>生产者出过的错（比如设备被拔了）。</summary>
        public Exception ProducerError { get; private set; }

        /// <summary>消费者从这条传送带取数据。</summary>
        public ChannelReader<byte[]> Reader
        {
            get { return _channel.Reader; }
        }

        /// <summary>Raw Input 接收器（只有用 Raw Input 的管线才有；由生产者任务里创建并赋值）。</summary>
        public RawInputReader RawReader { get; private set; }

        /// <summary>已经采集到的包数。</summary>
        public long ProducedCount
        {
            get { return Interlocked.Read(ref _producedCount); }
        }

        /// <summary>取消令牌（传给消费者，也用来通知生产者停下来）。</summary>
        public CancellationToken Token
        {
            get { return _cancellation.Token; }
        }

        /// <summary>
        /// 为"可以直接读的设备"（比如鼠标）建立管线。
        /// 这里故意用 Task.Run 包住阻塞的 ReadFile：这是把同步 IO 变异步的最朴素做法，
        /// 也是理解 async/await 的第一课 —— 你不是"让 IO 变快"，而是"别让主线程等它"。
        /// </summary>
        public static ReportPipeline StartForDevice(HidDevice device)
        {
            var pipeline = new ReportPipeline();

            pipeline.ProducerTask = Task.Run(async () =>
            {
                try
                {
                    while (!pipeline._cancellation.IsCancellationRequested)
                    {
                        byte[] report = device.ReadInputReport();          // ← 阻塞在这里等数据
                        await pipeline._channel.Writer.WriteAsync(report, pipeline._cancellation.Token);

                        // 多线程下 ++ 不是原子操作，必须用 Interlocked
                        Interlocked.Increment(ref pipeline._producedCount);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，不用管
                }
                catch (Exception ex)
                {
                    // 例如设备被拔掉、句柄失效
                    pipeline.ProducerError = ex;
                }
                finally
                {
                    // 关闭传送带：消费者那边的 await foreach 会自然结束
                    pipeline._channel.Writer.TryComplete(pipeline.ProducerError);
                }
            });

            return pipeline;
        }

        /// <summary>
        /// 停止采集。
        /// 注意一个真实的坑：ReadFile 阻塞在上面的线程里，取消令牌**叫不醒它**，
        /// 所以这里用 WhenAny + 超时"等一会儿就走"，不做无限等待。
        /// 想让 IO 真正可取消，得改用重叠 IO（Overlapped）或设置读超时 —— 这是进阶话题。
        /// </summary>
        public async Task StopAsync(int timeoutMilliseconds)
        {
            _cancellation.Cancel();
            _channel.Writer.TryComplete();

            if (ProducerTask != null)
            {
                await Task.WhenAny(ProducerTask, Task.Delay(timeoutMilliseconds));
            }
        }

        /// <summary>
        /// 为"被系统独占的设备"（鼠标、键盘、触摸板）建立管线。
        ///
        /// 和 StartForDevice 的区别只在生产者任务里干什么：
        ///   直接读      → ReadFile（阻塞等设备给数据）
        ///   Raw Input   → 收窗口消息（系统把报告"投递"过来）
        ///
        /// 这里必须**在生产者任务内部**创建接收器：窗口消息是按线程分发的，
        /// 哪个线程创建的窗口，就必须由哪个线程来收消息。这是 Windows 消息机制的一条硬规则。
        /// </summary>
        public static ReportPipeline StartForRawInput(
            ushort usagePage, ushort usage, string devicePathFilter, MouseReportSynthesizer mouseSynthesizer)
        {
            var pipeline = new ReportPipeline();

            pipeline.ProducerTask = Task.Run(async () =>
            {
                RawInputReader reader = null;
                try
                {
                    reader = RawInputReader.Create(usagePage, usage, devicePathFilter, mouseSynthesizer);
                    pipeline.RawReader = reader;

                    while (!pipeline._cancellation.IsCancellationRequested)
                    {
                        byte[] report;
                        if (!reader.TryReadNext(out report)) break;      // 阻塞在消息循环里
                        await pipeline._channel.Writer.WriteAsync(report, pipeline._cancellation.Token);
                        Interlocked.Increment(ref pipeline._producedCount);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消
                }
                catch (Exception ex)
                {
                    pipeline.ProducerError = ex;
                }
                finally
                {
                    if (reader != null) reader.Dispose();
                    pipeline._channel.Writer.TryComplete(pipeline.ProducerError);
                }
            });

            return pipeline;
        }

        public void Dispose()
        {
            try { _cancellation.Cancel(); } catch { }
            _cancellation.Dispose();
        }
    }
}