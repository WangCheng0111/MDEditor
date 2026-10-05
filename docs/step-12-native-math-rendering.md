# 步骤 12：OpenType MATH 与原生公式绘制

## 范围与结果

步骤 12 将步骤 11 的可移植数学盒真正交给原生绘制栈。Worker 读取 Cambria Math 的 OpenType `MATH` 表，排版引擎消费字体给出的数学常量、竖向 ready variant 和 glyph assembly；UI 进程按返回的 glyph ID、advance、offset、baseline origin 与 rule 几何直接调用 Win2D。公式帧不使用 XAML `TextBlock`、HTML、WebView2，也不会在绘制时再次由源码整形。

步骤 12 验收时仍使用一次请求一个 Worker 进程；步骤 13 已在不改变布局/绘制契约的前提下补齐持久 IPC、公式缓存、崩溃恢复与协议协商。`$...$` / `$$...$$` 尚未接入 Markdown 正文（步骤 14），受限 TeX 语法也不冒充完整 LaTeX 宏系统。

## OpenType MATH

`OpenTypeMathTable` 是不依赖 Windows 的大端序、边界检查解析器，读取：

- script / script-script 比例、math axis、上下标、分式和根号相关 constants；
- coverage format 1 / 2；
- 竖向 variant glyph 与 advance measurement；
- glyph assembly part、connector、extender 标志与 `MinConnectorOverlap`。

系统 Worker 通过 `IDWriteFontFace::TryGetFontTable` 借用字体表，并始终调用 `ReleaseFontTable`。竖向目标先选择足够大的 ready variant；没有足够 variant 时按 MATH 规定的从下到上 part 顺序重复 extender，连接重叠不超过两端 connector 且不小于字体给定的最小值。排版结果用 `regular / verticalVariant / verticalAssemblyPart` 明确记录来源。

分式、根号、上下标与 display operator 不再使用固定经验比例：字号缩放、axis、shift、gap、rule thickness、extra ascender 和最小 display operator 高度均来自字体。竖向字形还携带顶部 continuation connector 的真实纵坐标和右端坐标，由顶部 part 的 `TopSideBearing`、`LeftSideBearing`、`RightSideBearing` 与 advance 计算，而不是把 advance box 的左上角误当成墨迹接头。实现依据 [OpenType MATH 规范](https://learn.microsoft.com/en-us/typography/opentype/spec/math)；DirectWrite 字形度量使用 [DWRITE_GLYPH_METRICS](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ns-dwrite-dwrite_glyph_metrics)。

## 原生绘制和资源生命周期

`NativeMathFormula` 为每个实际字体面保留一个 DirectWrite/Win2D 所有者，但绘制数据完全来自 `MathLayoutResult`：

- glyph 使用 `CanvasDrawingSession.DrawGlyphRun`；
- fraction rule 使用 `FillRectangle`；radical continuation 使用顶部字形的真实 outline 构造合并几何；
- glyph ID、字号、advance、advance offset、ascender offset 和 baseline origin 不重算；
- 根号顶部 part 不再与一条独立矩形叠画。渲染器按字形角色生成一个合并几何：ready variant 保留完整 outline，并把 `RadicalRuleThickness` continuation 从字形已经稳定为标准厚度的水平段内做 union，避免误把斜笔画与横线的肩部重叠当成横线厚度；assembly 则在 connector 内部取极窄切片，裁掉右侧原短横后按其真实厚度延伸。两者都只做一次 `FillGeometry`，因此不会出现双重抗锯齿、断口、细横突然变粗或接头台阶；
- double 到 float 的每次边界转换都拒绝 NaN、Infinity 与溢出。

公式 `CanvasControl` 与正文主画布一样延迟到 `Loaded` 后创建；卸载/设备重建时解除事件、移出视觉树并释放持有的字体布局。可见帧区分设备丢失与普通绘制异常，前者交还 Canvas 设备恢复，后者写入开发诊断而不让整个窗口无信息消失。三个金刚键与非客户区代码没有改动。

## 像素验证

每个样例同时走两条路径：生产路径重放预建 `CanvasGlyph[]`，独立参考路径从纯布局重新构造 glyph。两者分别绘制到 `CanvasRenderTarget`，在 96、144、192 DPI 和三个非整数原点逐字节比较；同时检查非空 ink、fraction rule 连续像素行，并从 radical continuation 内部做 8 邻域连通检查，要求同一墨迹连通分量不只越过横线左边界，还必须向下到达根号 stem，防止仅横向越界却与肩部断开的假阳性。接头列与 continuation-only 列比较墨迹上下边界；ready variant 在斜笔画重叠区之外的稳定水平段取样，assembly 在 connector 内取样，再与 continuation 列比较灰度覆盖厚度。设备像素相位只允许小于一个逻辑像素的抗锯齿差异。

`RadicalExtraAscender` 按规范作为整个 radical 墨迹上方的留白。排版器把 assembly advance box 向上补偿 `TopConnectorY`，让字体顶部 part 自带短横的墨迹上沿与 continuation rule 精确重合；如果补偿后可见高度不足，会重新选择更高 variant 或重建 assembly。Cambria Math 在 32 DIP 下的顶部 part（glyph 4616）有 5.984375 DIP 内部上边距，旧实现因此把外加横线画高；该偏移现已完整抵消。横向接头则由 glyph outline 的交集、裁剪与 union 生成，不再依赖“回退半个 rule thickness”的经验重叠。

当前三个固定样例覆盖：

1. 嵌套分式、上下标与根号 ready variant；
2. 十二层分式撑开的根号 glyph assembly；
3. display `sum` variant 与上下限、分式。

最终结果为 3 个公式 × 3 个 DPI = 9 组像素检查，`DifferentBytes = 0`，31,649 个 ink pixels，2 个 ready variants、19 个 assembly parts，所有 rule 连续。

## 自动验收

```powershell
dotnet build MDEditor/MDEditor.slnx --no-restore -p:Platform=x64 -c Debug
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Debug --no-restore --minimum-expected-tests 567
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Release --no-restore --minimum-expected-tests 567
./tests/Verify-Step12MathWorker.ps1
```

Debug 与 Release 均为 567/567。新增测试使用合成二进制 MATH 表覆盖 constants、coverage 1/2、variant、assembly、截断/越界/零 advance 拒绝；另验证字体常量实际改变 script 与 fraction rule、角色跨 JSON 无损保留、connector 锚点边界，以及 radical rule 按字体 connector 而非 assembly box 边缘定位。

真实 Worker 验收执行 4 次成功请求和一次非法请求：3 个并行样例使用不同 PID，重复 assembly 结果完全确定，非法根号返回 `invalid-formula`。真实 Cambria Math 样例合计得到 2 个 ready variants、19 个 assembly parts 与 16 条 rules；Worker 退出后无驻留进程。

开发包内自动验收确认窗口可响应、9 组 Win2D 像素检查全部通过、Worker 全部退出。最终人工验收需确认三栏公式外观、根号 assembly 连接、分式线、上下标与求和符号，并复验调整窗口、缩放、最小化/恢复、最大化/还原、Snap、标题栏拖动和三个金刚键。
