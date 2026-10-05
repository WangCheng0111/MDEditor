# 模块边界（步骤 2）

现有 `MDEditor/MDEditor.csproj` 是应用模块。保留项目名、程序集名、包身份、XAML 命名空间和标题栏实现，不为了命名统一而重建应用。

## 依赖关系

箭头表示“引用”。不得反向引用，也不得循环引用。

```text
MDEditor（WinUI 应用） ──→ Core、Typesetting、Native
Native（Windows 适配） ──→ Core、Typesetting
MathWorker（独立进程） ──→ Typesetting
Typesetting（纯排版）  ──→ Core
Core（纯文档核心）      ──→ 无其他项目

Core.Tests（普通 .NET） ──→ Core、Typesetting
```

| 模块 | 目标框架 | 职责与限制 |
| --- | --- | --- |
| MDEditor | net10.0-windows10.0.19041.0 | WinUI Views、CommunityToolkit.Mvvm ViewModels、命令、应用服务、窗口与画布宿主 |
| MDEditor.Core | net10.0 | 原文、文档版本、编辑事务、解析与源码映射；不引用 WinUI、原生绘制或应用 |
| MDEditor.Typesetting | net10.0 | 排版算法、布局契约与快照；只引用 Core，不调用 Windows API |
| MDEditor.Native | net10.0-windows10.0.19041.0 | Windows 字体、绘制及文本服务适配；不管理文件或应用状态 |
| MDEditor.MathWorker | net10.0-windows10.0.19041.0 | 独立数学进程；TeX 最小语法/盒布局宿主与系统 DirectWrite 字形解析；不引用 WinUI、App 或 Native |
| MDEditor.Core.Tests | net10.0 | 核心、纯排版及模块边界测试；不引用或加载应用、Native |

Core 与 Typesetting 的契约使用自己的逻辑坐标、源码位置等数据，不使用 XAML、DirectWrite 或 COM 类型。平台相关能力通过纯模块定义的接口传入，由 Native 实现；后续按实际步骤引入接口，当前不预设尚未验证的 API。

MVVM 仍在应用层。逐字形数据、光标及输入法组合状态不做成 ViewModel 的逐帧通知。步骤 11 已引入 TeX 数学工作进程，保持在应用/原生适配边界之外；步骤 12 只通过 JSON 消费纯布局数据，再由 Native 将精确 glyph/rule 绑定到当前 CanvasDevice。步骤 13 在应用服务层加入协商式持久 IPC、请求串行化、有界 Worker 缓存、取消流撤销与崩溃恢复；应用仍不加载 Worker 程序集，Worker 也不引用 App、Native 或 WinUI。步骤 14 的源码扫描与 Knuth–Plass 数学盒排版位于纯 Typesetting，DirectWrite 塑形和 Win2D 绑定位于 Native，异步 Worker 调度仍位于应用层。步骤 15 的 piece-table 文本缓冲、版本化快照和编辑事务只位于 Core。步骤 16 的源码位置与几何映射位于纯 Typesetting，指针及光标绘制位于应用。步骤 17 的可编辑样张采用 Core 中的版本化文本/样式快照、Typesetting 中的字素安全编辑操作、Native 中的按内容纪元重排与应用层的键盘事件。步骤 18 的文本与样式历史由 Core/Typesetting 管理，WinUI 层只负责键盘意图与 Windows 剪贴板；步骤 19 由 WinUI TextBox 桥接桌面 TSF 组合事件与候选窗口，正文快照和组合撤销仍由 Core/Typesetting 管理。步骤 20 的 CommonMark/约定 GFM 子集解析和 UTF-16 节点范围位于纯 Core；此时尚未接入应用画布的实时 Markdown 投影。

步骤 21 的原文到显示文本投影、隐藏语法的双侧边界、删除范围和不可变显示快照位于 Core；从显示文本几何回映射到原文光标的适配位于 Typesetting。两者均不依赖 WinUI。步骤 22 将投影后的显示快照接入 Native 的真实字体塑形与排版，将原生几何回映射到原文光标；格式源码替换规则仍在 Core，WinUI 只接入命令和交互。投影状态改变但原文版本未变时，不能只按版本号复用旧布局。步骤 23 的容器前缀识别、原文结构事务和投影仍位于 Core；Typesetting 保证折叠行的内容侧光标；Native 依据同一段落行宽绘制列表/引用标记及缩进，WinUI 只路由键盘、按钮和复选框点击。

Native 是 C# Windows 类库，由它封装 DirectWrite / Direct2D；步骤 19 的 TSF 输入由应用层 WinUI TextBox 桥接，后续若需直接实现 TSF 文本存储，可在 Native 增加对应适配工程。步骤 3 接入 Win2D 后，Native 与应用同步使用 x86 / x64 / ARM64，纯核心和测试仍使用 AnyCPU。参见 [原生画布](step-03-canvas.md)。

## 本步骤的范围

步骤 2 时三个类库只包含模块元数据和项目依赖，没有虚构的编辑、布局或绘制实现。步骤 3 在 Native 和应用层新增原生画布；步骤 4 新增 Native DirectWrite 塑形、字形簇/真实宽度与字形重放。步骤 5 在 Core 新增只读源文/版本与 UTF-16 范围，在 Typesetting 新增不含 Windows 类型的不可变布局快照，并由 Native 独立绑定字体后按快照绘制。步骤 6 在 Typesetting 新增纯算法 Knuth–Plass；步骤 7 在 Native 提供真实候选行重新塑形，通过纯度量回调参与最优路径评分，在 Typesetting 做源码映射与整字形簇间距分配，再用共享快照绘制原生多行两端对齐。步骤 8 新增中文禁则、实测白边与有限间距优先分配；步骤 9 新增离线 Liang en-US 断词与生成连字符源码锚点；步骤 10 新增正文宽度重排、编辑缩放、滚动、DPI 复用、最新请求提交及完成帧缓存；步骤 11 新增受限 TeX 数学盒模型、独立 Worker、系统 DirectWrite glyph 解析与跨进程布局数据；步骤 12 新增 OpenType MATH constants/variants/assembly、Native 固定 glyph 重放、rule 绘制和多 DPI 像素验证；步骤 13 新增持久 Worker 会话、协议协商、公式缓存、错误隔离、取消安全与崩溃恢复；步骤 14 新增 Markdown 数学源码扫描、行内盒与正文共同断行和块级盒绘制；步骤 15 新增纯 Core 文本缓冲、原子编辑事务与精确保留换行的行索引；步骤 16 新增原生画布光标与选区；步骤 17 新增键盘编辑及对当前版本正文的原生重排；步骤 18 新增撤销/重做/剪贴板和提前接入的最小可编辑公式链路；步骤 19 新增 TSF 输入桥接；步骤 20 新增 Core 中的 Markdown 解析配置及测试；步骤 21 新增编辑投影与双向源码映射；步骤 22 接入原生实时 Markdown 样式和格式命令。完整 GFM/数学语法与文件持久化仍未实施。参见 [字形塑形](step-04-shaping.md)、[统一布局快照](step-05-layout-snapshot.md)、[纯算法断行](step-06-knuth-plass.md)、[原生段落](step-07-justified-paragraphs.md)、[中文细调](step-08-cjk-typography.md)、[英文断词](step-09-english-hyphenation.md)、[视口重排](step-10-viewport-reflow.md)、[数学工作进程](step-11-tex-math-worker.md)、[原生公式绘制](step-12-native-math-rendering.md)、[持久数学服务](step-13-persistent-math-service.md)、[Markdown 正文公式](step-14-markdown-math-body.md)、[文本缓冲](step-15-document-buffer.md)、[原生光标与选区](step-16-native-caret-selection.md)、[键盘编辑](step-17-keyboard-editing.md)及 [Markdown 解析](step-20-markdown-syntax.md)。

步骤 2 没有修改窗口文件。步骤 3 只在内容区添加独立画布，并接入窗口生命周期，不修改 TitleBar / NonClient 的三个金刚键实现，也不修改最低 Windows 版本。

已有解决方案继续位于 `MDEditor/MDEditor.slnx`，包含应用、Core、Typesetting、Native、MathWorker 和测试六个项目。应用、Native 与 MathWorker 使用 x86 / x64 / ARM64，Core、Typesetting 和测试显式映射为 AnyCPU。

## 构建与测试

在仓库根目录执行：

```powershell
dotnet restore MDEditor/MDEditor.slnx -p:Platform=x64
dotnet build MDEditor/MDEditor.slnx --no-restore -p:Platform=x64 -c Debug
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Debug
```

独立核心测试只还原 Core、Typesetting 和测试依赖，不需要部署应用、初始化 WinUI 或拥有包身份。测试 SDK 固定为 MSTest.Sdk 4.3.3，使用 Microsoft.Testing.Platform，关闭非必要测试扩展。根目录的 `global.json` 仅选择测试运行器，不固定或更换现有 .NET SDK；.NET 10 的新命令使用 `--project` 指定测试工程，不使用旧的 VSTest 位置参数。也可直接运行测试程序：

```powershell
dotnet run --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Debug --no-build
```

测试将项目/解决方案配置复制到输出目录作为架构快照，因此从其他工作目录运行也不依赖定位源码树。配置快照不是应用或 Native 的运行依赖。

本阶段测试检查：纯模块加载、目标框架、禁止的平台依赖、项目引用方向、已解析的测试运行依赖、解决方案配置，以及应用原有平台约定。算法和编辑功能的测试在相应实现步骤补充；模块边界测试不能证明尚未实现的功能正确。

测试方案参考：[MSTest 官方入门](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-getting-started)、[SDK 配置](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-sdk)、[.NET 10 测试运行器配置](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test)。

## 运行现有应用

在 Visual Studio 中打开 `MDEditor/MDEditor.slnx`，设置 MDEditor 为启动项目，选择 x64 / Debug 和打包启动配置。不要将普通核心测试项目误设为 WinUI 启动项目。

开发包的安装目录与持久性注意事项参见步骤 1 基线记录。之后新增的程序集必须随开发包部署；不能运行步骤 1 的旧二进制来验证步骤 2。
