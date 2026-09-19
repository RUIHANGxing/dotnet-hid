# iic-touch · TouchProbe

用**你笔记本上真实的触摸板**学通信协议的教学项目。

板子不用买、驱动不用装：这块触摸板内部走的是 I2C 总线 + **HID over I2C（HIDI2C）** 协议，
Windows 把它包装成标准 HID 设备暴露出来，我们就用这层"看得见的部分"来学 HID。

```
滑动手指  →  设备上报一包 30 字节的原始数据  →  我们自己按位解码  →  屏幕上的触点坐标
```

## 这个项目能学到什么

| 主题 | 在哪里体现 |
|---|---|
| HID 协议：报告描述符、用途页、位域、集合树 | `parse` 命令（字节级解析器）、`caps` 命令（真实设备） |
| 报告描述符 → 位布局的推导（哪一位是什么） | `Hid/ReportLayout.cs` |
| 按位提取、补码、位运算 | `Hid/ReportLayout.cs` 的 `BitReader` |
| Windows HID API（setupapi / hid.dll / kernel32） | `Native/NativeMethods.cs`、`Hid/*.cs` |
| P/Invoke、结构体内存布局、非托管内存管理 | `Native/NativeMethods.cs`、`Hid/HidDevice.cs` |
| Raw Input（设备被系统独占时怎么拿原始报告） | `Hid/RawInputReader.cs` |
| C# 基础：类/结构体/接口/集合/IDisposable | 全项目，注释里逐条讲 |

## 怎么跑（两条路，任选其一，效果完全一样）

### 路径 A：命令行 dotnet

```powershell
cd e:\All-Project\iic-touch
dotnet build                                   # 编译
dotnet run --project src/TouchProbe -- list    # 列出所有 HID 设备
dotnet run --project src/TouchProbe -- caps    # 打印触摸板的协议结构
dotnet run --project src/TouchProbe -- parse   # 用自研解析器解析描述符字节
dotnet run --project src/TouchProbe -- watch   # 实时解码（重点！滑动触摸板试试）
```

### 路径 B：Visual Studio

1. 双击 `IicTouch.sln` 打开解决方案；
2. 在解决方案资源管理器里把 `TouchProbe` 设为启动项目（一般默认就是）；
3. 调试 → 开始执行（F5）。默认执行的是 `watch`；
4. 想跑别的命令：项目属性 → 调试 → 命令行参数 里填 `list` / `caps` / `parse`。

> 提示：VS 的"输出"窗口和 cmd 表现略有差异；`watch` 的全屏刷新模式在 **Windows Terminal / cmd** 里效果最好。

## 命令一览

| 命令 | 作用 | 常用选项 |
|---|---|---|
| `list` | 列出系统里所有 HID 设备（带 ★ 的是疑似触摸板，并标出能否直接读） | |
| `caps` | 打印设备的集合树、字段表、我们推导出的位布局 | `--device N` 手动指定设备 |
| `parse` | 用自己写的解析器解析报告描述符字节 | `--file 文件` 解析真实描述符（二进制或十六进制文本） |
| `watch` | 实时读取并解码报告（默认触摸板） | `--mouse` 换鼠标、`--async` 用 Task 异步管线、`--plain` 逐行、`--raw` 只看十六进制、`--verify` 对照系统解码 |

**推荐的两条实操路线**：

```powershell
# ① 鼠标（数据到得最稳，推荐先用它把流程跑通）
dotnet run --project src/TouchProbe -- watch --mouse --plain --verify

# ② 异步版（学 Task：后台任务读设备 + Channel 传送带 + await foreach 消费）
dotnet run --project src/TouchProbe -- watch --mouse --async
```

> 鼠标如果是无线的，注意它会休眠：跑起来后动一下鼠标就能看到数据；实在没反应时程序会每秒打印
> 一行"等待输入"并附上收到的各类事件计数，方便判断卡在哪一步。

## 实测环境（你机器上的结论）

| 项目 | 值 | 怎么得到的 |
|---|---|---|
| 触摸板 | `ACPI\FTCS0038` → `HID\FTCS0038&COL02` | `list` 命令 / 设备管理器 |
| 厂商/产品 ID | `0x2808:0x0106`（FocalTech） | `HidD_GetAttributes` |
| 上层协议 | **HID over I2C（HIDI2C）**，驱动 `hidi2c.sys` | 见 `docs/01` |
| 报告 ID | `4` | `caps` 命令 |
| 输入报告长度 | 30 字节（1 字节报告 ID + 29 字节数据 = 232 位） | `HidP_GetCaps` |
| 触点数量 | 5 个（每个触点 = 1 个逻辑集合，40 位） | 集合树 + 位布局 |
| 坐标范围 | X: 0~3735，Y: 0~2297 | 字段表里的逻辑范围 |
| 能否直接读 | **不能**：设备被系统独占（错误码 32），只能走 Raw Input | `watch` 会自动告诉你 |
| 鼠标（Gaming 2.4G） | 8 字节报告（8 个按键位 + X + Y + 滚轮） | `caps --device 5` |

## 三条取数通道（这是本项目最"实战"的部分）

Windows 用户态想拿到设备的原始数据，实际上只有三条路，本项目全都实现了：

| 通道 | 适用 | 拿到的数据 | 代码 |
|---|---|---|---|
| **ReadFile** | 设备没被系统独占（如厂商自定义集合） | 真·原始报告字节 | `Hid/HidDevice.cs` |
| **Raw Input / RAWHID** | 非鼠标/键盘用途的集合（如触摸板 0x0D/0x05） | 真·原始报告字节 | `Hid/RawInputReader.cs` |
| **Raw Input / RAWMOUSE** | 鼠标、键盘 | **只有**预处理过的 dx/dy/按键标志 | 同上 + `Hid/MouseReportSynthesizer.cs` |

第三条是 Windows 的硬规定：订阅鼠标/键盘这一类集合时，系统**不给**原始 HID 报告字节。
本项目的做法是按描述符把 `dx/dy/按键` **重新编码**回报告字节，然后走同一套解码流程 ——
正好让人体会"描述符既是解码地图，也是编码地图"。

## 已知问题 / 下一步练习

1. **触摸板的 Raw Input 尚未实测成功**：需要手指配合动一下才能确认（也许是系统对精确式触摸板
   的原始输入有额外限制）。想验证就跑 `watch`，然后滑动触摸板。
2. **位偏移推断的局限**（很好的学习题）：对某些设备，系统给字段排的"数据序号"顺序
   和报告里的实际位顺序**不一致**（本例的鼠标就是这样：caps 里 Y 在 X 前面）。
   因为拿不到原始描述符，我们无法直接知道真实顺序 —— 所以：
   - `watch` 默认用**系统解码值**兜底显示（`Program.EffectiveValue`），保证数字是对的；
   - `--verify` 会把"我们自己按位算的"和"系统解码的"并排显示，不一致时立刻能看出来。
   想深入的话，可以试试：拿一份真实的鼠标描述符（Linux `hidraw` 或厂商文档），
   用 `parse --file` 解析，看看 X/Y 的声明顺序到底是什么。

## 目录结构

```
IicTouch.sln                 解决方案（VS 双击这个）
src/TouchProbe/
  Program.cs                 程序入口 + 四个命令 + 实时显示
  Native/NativeMethods.cs    C# 与 Windows 原生 API 之间的桥（P/Invoke）+ 详细注释
  Hid/HidDeviceEnumerator.cs 枚举 HID 设备（SetupAPI）
  Hid/HidDevice.cs           打开设备、读能力、读报告、释放资源
  Hid/HidCaps.cs             把系统的"解析结果"翻译成 C# 结构（含结构体标定的说明）
  Hid/ReportLayout.cs        ★ 自己推导位布局 + 按位提取 + 与系统解码对照
  Hid/HidUsageNames.cs       用途表（0x0D/0x42 = 笔尖开关 这类编码的字典）
  Hid/RawInputReader.cs      设备被独占时的取数通道（WM_INPUT）
  Descriptor/*.cs            字节级描述符解析器 + 两个带注释的教学示例
  View/ConsoleUi.cs          控制台表格、十六进制排版、字符地图
docs/
  01-HID协议基础.md          先读这篇：HID 是什么、Windows 怎么分层、为什么拿不到原始描述符
  02-报告描述符详解.md        描述符的字节级规则 + 逐条练习
  03-代码导读与C#要点.md      零基础也能读的代码说明 + C# 语法点 + 结构体标定方法
```

## 下一步可以做什么

- **M2（可视化）**：把字符地图换成 WinForms/WPF 窗口，并把读报告的循环改成 `Task` + `Channel`
  （这是练 C# 异步的第一手素材：阻塞 IO 放后台线程，数据推给界面）。
- **换设备练手**：`list` 里那些 `0xFF00:0x01` 的厂商自定义集合、鼠标、键盘，描述符结构各不相同，
  用 `caps --device N` 一个个看过去，就知道 HID 的"自描述"到底有多灵活。
- **拿真实描述符字节**：在 Linux 上 `cat /sys/class/hidraw/hidrawN/device/report_descriptor`，
  然后用 `parse --file 文件` 解析。（I2C 触摸板不能 usbipd 转发，USB 设备可以。）