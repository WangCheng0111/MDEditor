# 步骤 11：TeX 数学工作进程与最小公式布局

## 范围与状态

步骤 11 新增独立 `MDEditor.MathWorker` 进程，完成分式、根号、上下标的最小 TeX 语法、递归盒模型与布局数据提取。每个成功结果给出文档 DIP 中的 `Width / Height / Depth / Baseline`，以及可交给后续原生绘制层的字体面、glyph ID、字号、baseline origin、advance、源码范围和水平 rule。

本步不绘制公式。应用中的三行面板只展示进程返回的数字和检查状态，避免用 XAML 文本伪装公式效果；真正按 glyph/rule 原生重放、嵌套盒、伸展符号属于步骤 12。持久工作进程、请求复用、缓存、崩溃恢复与协议版本协商已由步骤 13 完成；正文行内/块级公式属于步骤 14。

这里的“TeX”是受限、可测试的 TeX 数学语法与盒模型内核，不宣称已经兼容完整 LaTeX 宏系统。步骤 11 的语法闭包为：

- 普通符号、Unicode scalar、`{...}` 分组；
- `\frac{...}{...}`、`\sqrt{...}`；
- `_`、`^`，支持单 atom 或分组参数及嵌套；
- 一组明确列出的希腊字母、运算符和关系符控制序列。

未知命令、重复脚本、无 base 脚本、缺少参数及不闭合分组均返回结构化错误，不猜测修复。请求长度上限为 4096 UTF-16 code units；该内核没有文件读写、shell escape、宏展开或任意 TeX 代码执行面。

## 进程与模块边界

`MDEditor.MathWorker` 是 `net10.0-windows10.0.19041.0` 的 x86/x64/ARM64 自包含控制台进程，只引用 `MDEditor.Typesetting`。它不引用 WinUI、应用程序集或 `MDEditor.Native`，直接通过系统 `dwrite.dll` 的 `IDWriteFontCollection / IDWriteFontFace` 解析字体、Unicode scalar、glyph ID、设计 advance 和 font metrics。

应用对 Worker 的 ProjectReference 仅建立构建和打包顺序，`ReferenceOutputAssembly=false`；UI 进程不加载 `MDEditor.MathWorker.dll`。Worker apphost、deps/runtimeconfig 和托管程序集随 MSIX 部署，运行时与主应用同 RID，不要求机器全局安装 .NET，也不要求安装 LuaLaTeX、Tectonic 或 MiKTeX。步骤 11 r1 曾让 Worker 间接引用 Win2D，导致其 Windows App SDK 私有负载污染主应用包；r2 改为系统 DirectWrite COM 后，包文件组成恢复到步骤 10 的主应用基线，问题没有通过忽略崩溃来掩盖。

步骤 11 使用一次请求一个进程的 stdin/stdout JSON。步骤 13 保留该模式作为兼容入口，并新增显式 `--server` 的持久逐行协议。应用在窗口和画布出现后异步发出诊断，不阻塞首次窗口显示。

## 布局与数据契约

公式结果的坐标原点在盒顶左角，Y 向下：

- `Height`：baseline 以上高度；
- `Depth`：baseline 以下深度；
- `Baseline == Height`；
- `TotalHeight == Height + Depth`。

sequence 按共同 baseline 横排。分式将分子、分母切换到下一 script style，抽取独立 fraction rule，并以 math axis 确定主 baseline。根号抽取 radical glyph 与 vinculum rule，根号最小尺寸按被开方盒的总高调整。上下标共享 base，script/script-script 使用 70%/50% 级别，并保证上下脚之间的最小间隙。所有 glyph/rule 坐标和源码范围在结果构造时再次校验，调用者数组会冻结为不可变集合。

字母使用 Unicode 数学斜体 scalar；DirectWrite 返回的 Cambria Math glyph ID 已与此前 Win2D/DirectWrite shaping 路径交叉对照，四个样例的全部 glyph ID 相同。OpenType MATH 表本身只提供字体相关数学数据，不定义完整数学布局算法，这一边界与 [OpenType MATH 规范](https://learn.microsoft.com/en-us/typography/opentype/spec/math) 一致。步骤 12 才会读取/应用更完整的 MATH constants 与 variants，并验证原生像素输出。

## 协议

请求和响应均为 UTF-8 JSON；协议版本当前为 1。成功响应包含 `layout`，失败响应包含稳定 `errorCode`：`invalid-json`、`invalid-request`、`invalid-formula`、`unsupported-glyph` 或 `worker-failure`。请求 ID 必须原样返回，应用还会核对协议版本和进程退出码。

示意请求：

```json
{"protocolVersion":1,"requestId":"example","source":"\\frac{a+b}{c+d}","style":"display","emSize":32,"fontFamily":"Cambria Math"}
```

进程端成功与失败都输出一个完整 JSON 对象；非法公式以退出码 2 结束。正常结果不含 COM、WinUI 或可变数组，因此可以安全跨进程序列化、测试和保存。

## 自动验收

```powershell
dotnet build MDEditor/MDEditor.slnx --no-restore -p:Platform=x64 -c Debug
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Debug --no-build --no-restore --minimum-expected-tests 558
./tests/Verify-Step11MathWorker.ps1
```

步骤 11 新增 46 项注册测试，覆盖最小语法、嵌套盒、四级 style、分式/radical rule、上下标相对 baseline、源码映射、不可变结果、JSON round-trip、请求边界、非法命令/语法、确定性、Worker 隔离及应用包引用方向。此前 512 项全部保留，Debug 与 Release 均为 558/558 通过。

真实进程脚本对分式、根号、上下标、嵌套公式执行 5 次成功请求，并验证 4 个并行样例拥有不同 PID；另一次非法分式返回 `invalid-formula`。最终样例解析到的字体均为 Cambria Math，分式 6 glyph + 1 rule，根号 6 glyph + 1 rule，上下标 5 glyph，嵌套式 9 glyph + 2 rules。重复嵌套请求的布局 JSON 完全一致。

最终开发包目录为 `MDEditor/obj/step-11-deploy-r3`。包内 Worker 已独立运行同一脚本通过；385 个包文件中包含 Worker apphost/程序集，不含 Worker 私带的 `CoreMessagingXP.dll` 或 `Microsoft.ui.xaml.dll`。自动生命周期复验中窗口约 628 ms 出现，三组公式均成功返回且使用不同 PID；标准关窗后约 467 ms 内主进程退出，残留 Worker 为 0。步骤 10 的画布、缩放、重排和三个金刚键实现未修改。

## 人工验收

2026-09-19 人工窗口验收通过：三行均显示非零 `W/H/D/baseline`、glyph/rule 数量和不同 PID，顶部为“检查通过”，按钮“重新提取布局数据”可再次成功；窗口调整、缩放、滚动、最小化/恢复、最大化/还原、Snap 悬停、失焦首次点击、标题栏拖动和三个金刚键均正常。

人工关窗后发现 r2 的窗口句柄已经销毁、Worker 已退出，但 WinUI 主进程仍空闲驻留。r3 在应用层主窗口 `Closed` 收尾中显式调用 `Application.Exit()`；没有改动三个金刚键或非客户区处理。最终自动关窗复验确认主进程和 Worker 均退出，步骤 11 的退出资源检查通过。
