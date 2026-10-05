# 步骤 8：中文禁则、实测标点压缩与中西文间距

状态：2026-09-18 已完成本机纯测试、生产快照原生/像素验证、用户人工窗口验收及退出检查。步骤 9 尚未实施。

## 实现与接口

Native 的 ParagraphLayoutSession 默认使用 CjkTypographyOptions.Refined；纯 ParagraphItemMap 的默认值仍为 Legacy，以保留步骤 7 的独立算法基线。步骤 7 原生回归样张显式传入 Legacy，不混淆两种间距政策。

新增纯模块 Typography/CjkTypographyOptions.cs 与 CjkGlyphSpacingPlan.cs。源文、glyph ID、字体槽、bidi level、字形簇和 grapheme 安全边界不变；候选行先重新塑形，实测白边及间距容量参与 Knuth–Plass 路径评分，选中后使用同一规则修改真实 glyph advance / offset，再由生产 LayoutSnapshot 绘制。未选中候选仍及时释放，只缓存纯度量。

IGlyphSpacingPlan 抽出两种分配器的公共契约。AppliedGlyphSpacing 增加可选 LeadingDelta 与 Kind，保持旧调用源码兼容；Delta 仍表示整簇总推进量变化，trailing = Delta − LeadingDelta。原生引用布局使用这些源码范围独立调用 DirectWrite SetCharacterSpacing，不调用生产分配器来生成参考字形。

## 中文禁则

在 DirectWrite 平台机会之上增加中文 tailoring，两者取交集，并继续要求实际 glyph cluster 和 Unicode grapheme 都安全：

- 开括号/开引号不能出现在内部行尾；闭括号/闭引号及逗号、句号、顿号、分号、冒号、问号、叹号等不能出现在内部行首。
- 连续 ASCII 空格不能绕过开闭标点禁则；数字串只在真实相邻字符间禁断，显式空格分隔的数字词元仍可按平台机会断行。
- 两字符省略号“……”和破折号“——”内部不可拆；Basic 允许两个完整标记之间的机会，Strict 另禁止省略号/破折号起行及斜线收行。
- 紧邻的数字与百分号/温度/角度单位、货币/正负前缀与数字受保护。
- 段落原本以闭标点开头或以开标点结束的字面内容不删除；段落真正起止点保留用户输入。

这不是自行实现整套 UAX #14，也不是已完成所有地区/语言/竖排规范。当前平台断行属性为底座，Basic/Strict 为可配置的水平中文细调；NBSP、不兼容控制符等继续沿用现有保护/拒绝契约。

## 标点压缩不靠猜字体

Native 批量读取实际 CanvasFontFace.GetGlyphMetrics，DrawBounds 的 em 度量按各 run 字号转成 DIP，并结合 glyph advance / offset 计算每簇左右白边。

仅压缩 LTR、单 glyph、明确分类的标点，且必须有匹配的实测白边。没有度量、窄于半 em 的标点、无可用白边或禁用压缩时不猜测压缩量：

- 开标点只使用前侧白边，闭标点只使用后侧白边；点号可使用两侧实际白边。
- 最大减量为 min(半 em，原 advance − 半 em)，不得将推进宽度压到半 em 以下。
- 每侧保留至多 0.04 em 的原有白边安全距离；原本不足的侧承不增加，也不继续吃掉。
- 行首开标点、相邻标点、行末闭标点/点号的自然压缩先进入 NaturalWidth；其余容量才进入 Shrink。
- 前侧压缩同时修改 offset 和 advance，墨迹位置与实际逻辑宽度一致。

不同字体的标点居中、偏侧、负侧承直接体现在真实容量里，不根据字体显示名称或固定“四分之一字宽”强压。当前没有新增标点悬挂或光学页边算法；绿色导线仍代表逻辑推进边界，不是逐行墨迹包围盒。

## 中西文间距与有限容量

默认 CJK–Latin/ASCII 数字的自动间距为 0.25 em，可收缩至 0.125 em、扩张至 0.5 em。已存在显式空格、标点、emoji 或 Arabic 的边界不自动插入这类间距；Latin 组合重音按基字分类。边界必须位于完整 glyph cluster/grapheme 之外，行首/末没有额外自动 gap。

相邻 CJK 及允许的 CJK–开标点、闭标点/点号–CJK 边界提供自然为零、最多 0.24 em 的扩张容量；不会给 Latin 单词内部增加字距。内部 ASCII 空格可收缩原 advance 的 1/3，最大扩张到半 em。

这些数字是当前可检查的默认样式参数，不是 CLREQ 强制的唯一风格，也不是最终编辑器字体选择。可关闭压缩、指定固定四分之一 em 混排间距，或更改合法容量。有限容量的调整比限定在 −1..1；Refined 明确拒绝 MaximumStretchRatio > 1，不把用户选项静默截断。

容量边界判断另做了数值修正：先用 target 与 natural + maximumRatio × stretch / natural − shrink 的实际宽度边界比较，再限制除法所得比例。恰好达到可计算上限不因除法舍入略超过 1 被误拒绝；超过边界一个 double ULP 仍严格无解，没有添加容量 epsilon 或成本剪枝容差。独立穷举 oracle 分别实现同一边界语义，原随机问题继续回归。

分配优先顺序：

- 收缩：实测标点白边 → 词间空格 → 混排 gap。
- 扩张：词间空格 → 混排 gap → CJK 字间 gap。

优先级分组内部按真实容量分摊。advance / offset 转为 native float，微小余差只在已有合法机会修正；真实 glyph 推进总和及 native 原点视觉跨度继续受 0.01 DIP 契约约束，不能修改名义 Line.Advance 来伪造对齐。

## 组合字符的原生修正

首轮对照在 192 DPI × 0.75 发现组合重音差异。重新捕获独立 DirectWrite 参考 run 后确认：参考实现把字簇后间距加在最后一个有推进量的基字上，尾随零宽重音仍为零推进，并相应补偿 offset。

生产分配器已改为同一原则，余差校正也使用相同处理；多重重音的 glyph ID、ascender offset 与墨迹位置受纯测试保护。不能把总 gap 加到尾随零宽重音上，即使总行宽和浮点簇边界看起来正确。

后续定向对照确认，仅修正 mark 度量还不够：DPI 捕获参数、文本描述和字体槽本身均不是剩余差异的来源。按实际 leading/trailing 间距属性变化分段，并使用行局部 native float pen 定位后，失败样例全部恢复零差异。

生产快照已采用该规则：相同间距的相邻完整簇合为绘制 run，只在不同间距属性的簇间分段；RTL 复杂 run 保持完整。分段共享原有 FontIndex，不新增字体槽、拆开字形簇或重新塑形。每个分段的最终几何写入 LayoutSnapshot，NativeSnapshotBinding 继续读取同一生产快照，不在诊断绘制分支偷偷补偿。原始 run 数量可变化，保护契约改按原字体槽核对全部 glyph、bidi、簇源码与簇大小，而非误要求片段数等于原 run 数。

最终包所有正/负/零比例与 Strict 样例在三种离屏 DPI/缩放配置下严格零像素差异；分段合并、字体槽复用、源码覆盖和 native 原点累加另有专门纯测试。

## 无解与样张

可见主段落固定三列 240.125 / 320.375 / 420.625 DIP，包含开闭引号、括号、书名号、点号、Windows2026、office、组合重音、成对省略号/破折号。另展示 Legacy / Refined 自然长度对照，以及连字、负间距、居中标点、双向文字、补充平面/emoji、多空格样张。

原 220.125 DIP 复杂主样张在当前字体、禁则和有限容量下没有完整可行路径，保留为明确无解专项，不扩大 gap 上限或假画到右边界。无解时 Break 返回 NoFeasibleBreaks、Layout 返回 null，不更改原文或偷偷改字号。可见窄列使用有可行路径的 240.125 DIP。后续编辑器必须为极窄视口设计明确 overflow/断词政策；当前并不承诺任意段落在任意列宽都可严格对齐。

页面仍只做固定样张缩小适配，没有实施步骤 9 英文断词、步骤 10 视口重排/编辑缩放，也没有引入 Markdown 编辑或数学排版。单一水平段落、候选 O(B²) 参考路径及现有生命周期限制不变。

## 自动验证

当前 Debug / Release / 仓库外直接运行各 343 项纯测试通过（原 263 项 + 80 项）。新增覆盖禁则、跨空格标点、成对符号、Strict/Basic 差别、数字与显式空格、完整簇/源码映射、−1..1 容量、压缩白边/半 em、优先级、固定样式、RTL、错误输入、native float、多重零宽重音、绘制分段、容量边界及边界外一 ULP 拒绝。

1000 组随机标点白边/负比例验证推进宽度与白边安全距离；原 5144 个算法问题和 1200 个独立上下文度量穷举问题仍回归。随机样例属于这些测试的内部循环，不把 343 项夸大成单独注册用例数量。

```powershell
dotnet build MDEditor/MDEditor.slnx --no-restore -p:Platform=x64 -c Debug
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Debug --no-build --no-restore --minimum-expected-tests 343
dotnet build tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj --no-restore -c Release
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Release --no-build --no-restore --minimum-expected-tests 343
```

原生 QA 保留步骤 7 的 16 组 Legacy 和步骤 4/5 的八行回归，新增 Refined、实际负比收缩、不同字体回退、非零源码起点、三种多重重音比例、Strict、完整双字符不能拆、固定 gap、最大比保护、明确无解检查。每条实际生产行与独立 DirectWrite 参考在 96 DPI × 1、144 DPI × 1.25、192 DPI × 0.75 逐 byte 对照，要求零差异，不降低阈值。

DEBUG LocalApplicationData/MDEditor/DevelopmentDiagnostics 写出同 runId 的：

- step-08-cjk-{runId}.json / step-08-cjk-layouts-{runId}.json
- step-08-legacy-paragraph-{runId}.json / step-08-legacy-layouts-{runId}.json
- step-08-shaping-{runId}.json / step-08-shaping-layout-{runId}.json
- step-08-resource-error-{timestamp}.txt（仅异常）

失败行可额外保留 ReferenceRuns，便于定位原生间距行为。最终验收必须匹配新启动时间、新部署目录及同 runId 快照，不接受以前启动遗留的通过报告。

## 人工验收与本机最终结果

确认三列完整、内部行首/尾没有违禁标点、成对符号不拆、非末行逻辑边界对齐绿色导线，末行自然；Legacy / Refined 对照和底部字形正常。状态需显示“禁则 / 标点 / 混排 / 字形 / 像素检查通过”，绘制计数 > 0。

调整窗口、最大化/还原、最小化恢复不黑屏；三个金刚键、Snap 悬停、失焦首次点击、标题栏拖动回归。最后关闭所有窗口，再检查 app/test 进程退出和新启动以来的异常事件。本机人工结果见下，不将自动离屏结果冒充窗口操作结果。没有执行跨机器/字体版本、系统 DPI 切换或强制 GPU 设备丢失的测试，不作相应承诺。

2026-09-18 最终 x64 Debug 包为 obj/step-08-deploy-r9，启动 20:16:01，报告 20:16:09，runId 为 20260918-201609-904-719bfa6d2cf74f31887a254cb96afcea，非旧包或旧报告。

- Debug、Release、C:/ 仓库外直接运行均 343 项测试通过，未跳过。
- 新样张 17 组、34 行（17 条非末行）、102 次像素比较通过；步骤 7 Legacy 的 16 组/44 行/132 次比较及旧八行塑形的 24 次比较同时通过，合计 258 次严格零字节差异比较。
- 独立读取实际生产 JSON 重算：新样张 glyph 推进量最大误差 0.000000953674 DIP，float 原点视觉跨度最大误差 0.000069618225 DIP；复核实际混排/字间/词空格容量、标点半 em 下限、完整簇字体槽、源码只省略 ASCII 空格等全部通过。
- 成对标记无解保护、固定四分之一 em、最大比例拒绝、220.125 DIP 明确无解、三种真实负比收缩全部通过；重复布局指纹稳定。
- 用户对所有样张、视口调整、最小化恢复及三个金刚键/Snap/失焦首次点击/标题栏拖动反馈“全部正常”。随后实际 app/test 进程均为 0，最终启动以来相关 Application 1000/1026 事件及资源创建异常为 0。
- 新旧报告与独立 Verify-Step08Snapshot.ps1 保存于本轮验收记录目录；开发包切换保留应用数据，未强制结束用户进程。

完整解决方案构建成功；原有 CA1416 最低系统版本警告及打包缺少 mspdbcmf.exe 的符号包警告保留，未更改最低系统版本或声称生成了符号包。所有测试通过不等于任意系统/字体更新后的跨机器像素一致。

## 技术参考

- [W3C 中文排版需求 CLREQ](https://www.w3.org/TR/clreq/)：当前引用为草案，参考中文禁则、压缩方向与间距优先策略，不声称全规范实现。
- [Unicode UAX #14](https://www.unicode.org/reports/tr14/)：底层机会继续由 DirectWrite 提供。
- [Win2D CanvasGlyphMetrics](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasGlyphMetrics.htm) 与 [CanvasFontFace.GetGlyphMetrics](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_Text_CanvasFontFace_GetGlyphMetrics.htm)：实际 em 字形度量。
- [Win2D SetCharacterSpacing](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_Text_CanvasTextLayout_SetCharacterSpacing.htm)：独立原生间距对照。
