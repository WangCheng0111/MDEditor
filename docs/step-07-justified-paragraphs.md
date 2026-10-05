# 步骤 7：真实字形参与最优断行与原生两端对齐

本步骤将 DirectWrite 的真实塑形度量接入步骤 6 的 Knuth–Plass，再通过步骤 5 的不可变布局快照绘制多行段落。不是 WebView、整行图片拉伸，也不是给 Line.Advance 写入目标宽度就宣布对齐。

## 已实现的数据流

```text
DirectWrite 整段塑形 / 平台换行属性
  → 保留 glyph cluster 与 Unicode grapheme 都安全的边界
  → ParagraphItemMap：实际 Box / Glue / Penalty + UTF-16 源码映射
  → 每条候选行重新塑形，提供真实 NaturalWidth / Stretch / Shrink
  → Knuth–Plass 计算段落全局最优路径
  → 选中的行重新塑形、核对候选度量
  → GlyphSpacingPlan 分配间距并量化到 native float
  → 多行 LayoutSnapshot + 独立 NativeSnapshotBinding
  → Direct2D DrawGlyphRun 重放
```

段内字偶距、连字或双向上下文可能因行边界而变化，因此不能直接把整段 glyph 切片、事后才测量选中行。本实现让真实候选行度量参与路径评分。纯算法仍不依赖 Windows，通过可选 lineMeasurer 回调接收只读 LineMeasurement；回调返回 null 时该边不可用，错误/取消继续抛出，不伪装成无解。

LineMeasureRequest 的 EndItemIndex 不含选中的 glue/penalty；NaturalWidth 由回调完整提供，包括条件 BreakWidth。度量必须是同一次 Break 中确定的、仅与请求对应行有关，不依赖可变字体状态、路径历史或 UI。回调按起止边测量而非每个 fitness 状态重复调用。默认无回调时步骤 6 的所有行为保留。

## 源码和字形保护

ParagraphItemMap 使用半开 UTF-16 SourceRange，支持文档内非零起点。整个段落的度量簇必须连续覆盖原文，并且不拆 grapheme。DirectWrite 平台换行属性来自 CanvasTextLayout.ClusterMetrics.Properties 的 CanWrapLineAfter；只有同时不会穿过任何实际 glyph cluster 的边界才成为候选。连字、代理对、组合字符和 emoji ZWJ 不按字符数估宽。

连续 ASCII 空格合为一个 glue，保留最后的合法换行属性和完整源码范围。选中的断行空格及段末空格不进入可见行，不从 SourceTextSnapshot 删除；Block.Source 仍覆盖整个段落。诊断逐段检查可见行之间、省略的前后源码仅为空格。前导空格仍按首行实际测量保留；仅空白/空原文产生一条零字形的空视觉行。NBSP 等 Unicode 内容不作为 ASCII 空格丢弃。

本步是单一水平段落适配：拒绝控制符、tab、LF/CR、U+2028/U+2029、软连字符。显式换行与 Markdown 分块、断词等由后续步骤确定策略，不擅自降级或按任意字符切断。

## 间距政策及原生坐标

GlyphSpacingPlan 仅提供：

- 内部 ASCII 空格：实际 advance 的 1/2 为 stretch，1/3 为 shrink。
- 相邻 CJK 整簇之间：自然宽度为零的附加 gap，stretch 为字号的 0.08；仅用于基础验收，不能收缩。

这些是可检查的基础政策，不宣称已完成步骤 8 的中文标点挤压、禁则、悬挂与中西文细调，或步骤 9 的英文断词。中文判断覆盖常见汉字、补充平面、假名、韩文；当前 Arabic 用词间空格调整，没有实现 kashida/脚本专用增补字形。

空格簇多 glyph 时按原推进量比例分配，避免产生负推进量；CJK 附加间距落在整簇末尾，不拆簇或改变 glyph ID、offset、source、font slot、bidi level。最终修改后的推进量按 float 往返保存，剩余舍入误差只在已有合法机会校正。实际 glyph advance 总和必须满足 0.01 DIP 契约。

多字体/双向 run 按原视觉左边界排序定位，LTR 起点向右移动累计间距，RTL 起点还加本 run 的总调整量，保持其右侧起笔和正确视觉顺序。最终 Line.Advance 来自字形推进量之和，而非直接使用 TargetWidth。默认段末自然长度；超宽末行仍需合法收缩，Justified 政策则末行同样对齐。

真实字形的 ink 可能有侧承/伸出，不能把墨迹包围盒误当逻辑推进宽度。绿色导线标逻辑页边，蓝色基准线终点依据同一生产快照。诊断同时核对 glyph 总推进量和经 float 原点转换后的视觉 run 整体跨度，不靠 Line.Bounds 的固定列宽自证。

## 资源与可检查结果

ParagraphLayoutSession 缓存 SourceRange → 纯 LineMeasurement，不缓存所有候选的 COM 布局。未选中候选 Shape/临时 binding 在测量后立即释放；选中行重新塑形，度量与缓存核对一致后才转快照。

ParagraphLayout 独立拥有选中 Shapes 和字体 binding，释放 session 后仍可绘制。释放 layout 时先释放借用字体的 binding，再释放 Shapes；重复释放安全，释放后的 Draw 被拒绝，portable snapshot 仍可阅读和校验。字体目录沿用同一组 FontFaceDescriptor 对象，不通过显示字体名重新绑定原生字体。调用方仍必须保持 CanvasDevice/resource creator 的有效生命周期，并在合适绘制线程使用这些原生对象。

本实现是正确性优先的参考路径，真实候选数量最坏为 O(B²)，每条候选需要塑形，纯度量缓存同样可能达到 O(B²)。字号、列宽和原点应使用实际 UI 合理范围（Layout 列宽限制 0 < width <= 100000 DIP）；长文档后台调度、增量失效、字体变化、缓存上限与性能优化尚未验收，不把当前固定样张测试等同于完整编辑器性能。

## 原生验收样张与诊断

内容区为固定三列 220.125 / 320.375 / 420.625 DIP：

- 同一英文段落，在三种宽度下使用不同全段最优断行。
- 同一中文段落，由 Arial 请求触发真实字体回退。
- 底部连字 ON/OFF、重音组合/混排、Arabic/Hebrew 双向文字、补充平面/emoji、连续空格。

隐藏的离屏原生用例另外检查四种负间距和源文起点 7：Latin、bidi、多空格、CJK 混排。页面只是随视口统一缩小适配，并没有提前实现步骤 10 的窗口宽度重排/编辑缩放；三列生产逻辑宽度不会随预览适配变化。

DEBUG 开发包每次资源创建写出同 runId 的文件，使用普通 .NET LocalApplicationData 路径，不激活 Windows.Storage.ApplicationData：

- step-07-paragraph-{runId}.json：路径、行宽误差、源码边界、原生像素对照和生命周期检查。
- step-07-paragraph-layouts-{runId}.json：实际绘制/对照使用的全部段落快照数组（含离屏 QA）。
- step-07-shaping-{runId}.json / step-07-shaping-layout-{runId}.json：保留步骤 4/5 的八行/字体绑定回归。
- step-07-resource-error-{timestamp}.txt：资源创建异常。

原生像素对照独立使用 DirectWrite SetCharacterSpacing，按生产间距源码范围设置 trailing spacing，交由 CanvasTextLayout 排版与绘制，再与生产快照 DrawGlyphRun 比较，不调用纯间距分配器生成 reference glyph。配置为 96 DPI × 1、144 DPI × 1.25、192 DPI × 0.75；同尺寸离屏图逐 byte 比较，当前严格要求零差异。原生文字处于相同 grayscale 抗锯齿和 NoPixelSnap 条件。系统 DPI/字体更新后的跨机器逐像素不变不是此测试的承诺。

## 测试与人工验收

```powershell
dotnet build MDEditor/MDEditor.slnx --no-restore -p:Platform=x64 -c Debug
dotnet build tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj --no-restore -c Debug
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Debug --no-build --no-restore --minimum-expected-tests 263
dotnet build tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj --no-restore -c Release
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Release --no-build --no-restore --minimum-expected-tests 263
```

263 项纯测试包括原 223 项，以及新增 40 个用例：真实上下文候选输入、可选拒绝/取消、条件 break width、非法度量、非零起点与源码空白映射、深复制、grapheme/连字/emoji保护、CJK基础 glue、连续空格、float 推进量、合法收缩、多 glyph 空格、RTL/混合视觉 run、空视觉行、无机会不伪造对齐。新增 1200 个随机上下文度量问题与独立无合并、无剪枝的穷举路径核对最优成本和路径；原 5144 个纯算法问题仍回归。

人工验收只接受用户实际反馈：三列完整，非末行对齐、末行自然，中文无方框/组合字符正常/连字可区分；底部检查通过、绘制计数 > 0。调整窗口、最大化还原、最小化恢复不黑屏；三个金刚键、Snap 悬停、失焦首次点击和标题栏拖动回归；关闭后检查实际进程退出。没有桌面自动化结果时不把这些项标成自动通过。

## 2026-09-18 本机最终验收结果

最终 x64 Debug 开发包安装在 obj/step-07-deploy-r3，真实启动报告为 19:33:34，非旧包报告。Debug、Release、从 C:/ 直接运行均 263 项通过。16 组、44 行（28 条非末行）、132 个段落像素对照全部通过，另保留 24 个旧塑形像素回归。独立读取生产 JSON 重算：实际 glyph 推进量最大误差 0.000000190735 DIP，float 原点视觉跨度最大误差 0.000006103516 DIP。

用户对三列/连字/重音/混排/双向文字、视口调整不黑屏及三个金刚键/Snap/失焦首次点击/标题栏拖动回复“全部正常”。另确认“已关闭”后，应用与测试进程剩余 0；最终启动以来未发现相关 Application 1000/1026 异常事件。开发包切换保留应用数据，没有强制结束进程。完整原生报告与独立快照验证脚本已保存到本轮验收记录目录。

## 官方 API 参考

- [Win2D CanvasTextLayout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
- [Win2D SetCharacterSpacing](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_Text_CanvasTextLayout_SetCharacterSpacing.htm)
- [DirectWrite IDWriteTextLayout1::SetCharacterSpacing](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_1/nf-dwrite_1-idwritetextlayout1-setcharacterspacing)

断行模型的原论文/TeX 源码与连续比例算法边界仍参见 [步骤 6](step-06-knuth-plass.md)。本步不加入新依赖、不修改三个金刚键及 NonClient 代码、不进入步骤 8 或数学/Markdown 编辑阶段。
