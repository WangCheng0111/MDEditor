# 步骤 10：正文宽度重排、编辑缩放与 DPI

## 范围与状态

已接入只读多段落正文视口，取代步骤 9 的固定多列样张缩小适配。窗口宽度及编辑缩放重新求解 Knuth–Plass；高度变化、滚动、同设备 DPI 变化复用布局。保留英文断词、生成后缀锚点、中文禁则、有限标点/混排间距、连字、重音与双向文字。

本步仍不是 Markdown 编辑器：样文分段及样式是显式只读 fixture，没有解析、输入、光标、选择、TSF、公式或文件操作。没有修改 MainWindow.TitleBar / NonClient / 三键代码。

步骤 10 最终 r8 的自动检查、缩放/窗口拖动优化和用户人工验收均已通过；本页中 r3/476 项统计保留为当时验收截面，后续 r8 测试总数为 512。步骤 11 已另见独立文档。

## 坐标契约

| 空间 | 单位 | 含义 |
| --- | --- | --- |
| 文档 | DIP | 不可变快照、字体大小、段落/行位置、滚动偏移 |
| 视口 | DIP | CanvasControl 实际宽高、四周 24 DIP 留白、编辑缩放 |
| 光栅 | 像素 | 视口 DIP × CanvasControl.Dpi / 96 |

编辑缩放为 50%～300%，命令每次 10 个百分点；Direct2D 使用同一个 canonical Single zoom。正文宽度 = Single((视口宽度 − 48) / zoom)，不把正文宽度四舍五入到整数像素。Doc→View 为 (24 + x×zoom, 24 + (y−scroll)×zoom)。字体在文档空间只设置一次；绘制施加统一横纵缩放，DPI 不乘入字号或断行宽度。

零尺寸/留白后无空间时停止重排并撤销旧请求。恢复可见后再次请求。纵向 ScrollBar 固定占据 16 DIP，避免滚动条出现与否造成宽度反馈振荡。ScrollBar 数值是文档 DIP；滚轮每格移动 48 视口 DIP。重排保留顶部文本源码锚点，最后重新 clamp 到新文档范围。

DPI 独立资源依据 [Win2D 官方 DPI/DIP 说明](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/dpi-and-dips)。Debug 工具栏的“光栅密度测试 ×1 / ×1.5 / ×2”只设置 CanvasControl.DpiScale，不改变 Windows 显示设置；它不是编辑缩放，也不等同于人工验证跨显示器迁移。

## 交互与 MVVM

EditorViewportViewModel 使用 CommunityToolkit.Mvvm ObservableObject、ObservableProperty 部分属性和 RelayCommand；只公开编辑级缩放状态，不把字形/行变成 ObservableCollection 或逐字通知。

- 工具栏 − / ＋ / 100%。
- Ctrl+滚轮、Ctrl+加减号、Ctrl+0。
- 普通滚轮与右侧滚动条滚动。
- Debug 光栅密度切换应改变 DPI，不改变同逻辑宽度的布局编号、指纹、断行和字号。

Toolbar、滚动条及 XAML 事件属于视图；排版、坐标及快照规则不依赖 ViewModel。

## 请求、缓存与所有权

窗口宽度请求按 16 ms 帧节拍取最新值，不因每次拖动重启并无限延后定时器；编辑缩放立即请求，没有 140 ms debounce。Task.Run 只使用 CanvasDevice，不触碰 CanvasControl 或其他 XAML 对象。ReflowEngine 按源文/样式/设备 epoch 持有段落会话，SemaphoreSlim 串行化缓存访问。每段在 epoch 建立时只做一次完整 DirectWrite 塑形，并保留 glyph-cluster advance、bidi level、实测标点墨迹边距和 advance 前缀和；Knuth–Plass 候选用同一套 CJK 有限间距规则计算纯量 NaturalWidth/Stretch/Shrink，不再为 O(B²) 候选反复创建 CanvasTextLayout、binding 和 spacing plan。最终选中的行仍独立塑形并与候选度量逐项核对，任何上下文差异都会拒绝提交，而不是产生一次宽度跳变。

LayoutRequestGate 单调 revision；宽度/缩放、隐藏视口、新设备和释放都会撤销旧请求。取消中间请求，最新结果才能提交；过期完成结果自行释放。缩放比例与新断行快照在 UI 线程同帧提交：等待期间保留旧快照与旧显示 zoom，不先把新 zoom 套到旧布局。状态区分别显示“显示 / 请求”百分比；失败不把旧布局伪称为新宽度已对齐。

CompletedLayoutCache 在同一设备/源码/样式 epoch 内拥有最多 32 份已完成布局（LRU）。已知正文宽度直接切换布局与显示 zoom，无需重新断行或塑形；首次宽度也只执行纯标量断行和选中行塑形，响应不再依赖预热全部缩放档位。资源不跨设备复用，淘汰/换设备/关闭会释放；命中只借用，不由视图释放。26 个 50%～300% 的 10% 缩放档位可装入缓存，任意窗口宽度则受 LRU 容量限制。

大快照 JSON 序列化及文件写入移到后台；单调诊断 sequence 加串行写入防止旧日志覆盖新日志，不读取 XAML 或活跃原生字体。请求→提交耗时单独记录，避免把“排版耗时”误当成实际交互响应。不能保证首次遇到的任意宽度/任意长段落为零计算时间。

LayoutSnapshotComposer 组合段落最终快照，拼接并 remap 字体槽，不按显示名称合并字体身份；源码范围、字形簇和生成后缀保持原样。组合 NativeSnapshotBinding 借用各段落独立所有者，先释放组合绑定再释放段落。快照不持有 COM，资源释放后仍可检查与序列化。绘制按可见行裁剪，不缩小整页来装进高度。

DpiChanged + 同设备复用字体/画刷/快照；NewDevice 撤销旧请求、关闭旧引擎并重建资源。异步排版发生 device-lost 异常时使用 IsDeviceLost / RaiseDeviceLost 交给 CanvasControl 恢复，遵循 [官方 device-lost 说明](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/handling-device-lost)。不通过禁用用户 GPU 来做破坏性的测试。

## 有限间距与无解

Refined ratio 必须 −1～1；没有为了任何宽度看起来“对齐”而强拆簇、违禁断行、拉伸字形或放大间距容量。NoFeasibleBreaks 在该段位置明确显示提示，状态列出段数；源文保留，扩大窗口或降低缩放后自动重试并恢复。1 DIP 诊断要求全部段落明确无解、没有伪造字形行。

这是本步的显式无解诊断策略，不是最终面向用户的应急阅读排版方案；后续若要加入有限的 emergency pass，需要单独定义排版政策和测试。

## 高倍率原生精度

300% / 高 DPI 专项暴露两类此前较小缩放下未出现的差异，已按独立 DirectWrite run 对照修正：

1. 行原点与文档基线的 double 加减可能把行内 Single 基线移到舍入中点另一侧。Y 先恢复行内 Single，再加共享 Single 行锚点。
2. DirectWrite 对每个 styled run 从零独立逐字形累加 Single 宽度，再将该宽度加到全行 Single 游标。既不是对 double run advance 一次舍入，也不是把所有 run 字形串成一个连续 Single 游标。

GlyphPaintCoordinates.AdvancePen 和基线中点测试固定这一契约。Legacy 及步骤 8/9 的独立像素回归仍必须通过。没有放宽“零字节像素差异”的验收阈值。

## 可复现验收

    dotnet build MDEditor/MDEditor.slnx --no-restore -p:Platform=x64 -c Debug
    dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Debug --no-build --no-restore --minimum-expected-tests 476
    dotnet build MDEditor/MDEditor.slnx --no-restore -p:Platform=x64 -c Release
    dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Release --no-build --no-restore --minimum-expected-tests 476
    ./tests/Verify-Step10Snapshot.ps1 -ReportPath <reflow.json> -LayoutsPath <reflow-layouts.json> -LiveReportPath <live.json> -LiveLayoutPath <live-layout.json>

新增 51 项注册测试：7 个缩放坐标、5 个 DPI、隐藏/非法视口与滚动、最新请求/释放、5000 请求逆序、字体槽拼接、7 个缩放→KP 集成、基线舍入与局部游标，另有 7 个完整帧缓存的借用/淘汰/换设备/重复释放/非法所有权/26 档位测试。此前 425 项测试保留。Debug/Release 各 476 项通过，无失败、无跳过；仓库外最终运行和原生最终统计见下文。

原生重排检查使用 260.125 / 412.625 / 680.375 / 997.125 DIP，在 96 DPI ×0.5、144 DPI ×1.5、192 DPI ×3 与独立 DrawTextLayout 比较；组合绘制另在 3 组 DPI/zoom、首尾滚动位置比较原段落目录。要求宽度改变真实断行、往返指纹恢复、无解显式、取消有效、释放后快照可读，以及同逻辑宽度 DPI/高度无关。旧 456 次像素回归保留。

Verify-Step10Snapshot.ps1 独立解析生产快照，核对字体索引、每个真实字形和、字形簇完整覆盖、grapheme 边界、只省略空格、生成后缀行末锚点、实际 staged Single 绘制跨度。实时报告额外核对 viewWidth/zoom 导出的正文宽度、明确无解段数和物理行尾误差 <0.01 pixel。

诊断位于开发包 LocalCache/Local/MDEditor/DevelopmentDiagnostics；回归报告带共同 runId，live.json/live-layout.json 是最近提交或 DPI 复用的状态，不是文件持久化协议。日志仅 Debug 写入。已有 CA1416 最低 Windows 版本和 symbols package 工具缺失警告保留。

## 自动验收记录

Debug、Release 与仓库外直接运行各 476 项测试通过，无失败、无跳过。

| 专项 | 组数 | 行数 | 严格零差异比较 |
| --- | ---: | ---: | ---: |
| 步骤 10 动态正文 | 4 | 74 | 222（独立 DirectWrite） |
| 步骤 10 组合快照 | 4 | 首尾滚动视口 | 24（原段落目录） |
| 英文断词回归 | 17 | 66 | 198 |
| 中文细调回归 | 17 | 34 | 102 |
| Legacy 回归 | 16 | 44 | 132 |
| 基础塑形回归 | 8 | 8 | 24 |

独立 Verify-Step10Snapshot 对上述 74 行与实时正文 12 行共 86 行通过，生成连字符 5 个，最大真实字形和误差 9.5367431640625e-7 DIP，实际 native paint span 最大误差 0.000244140625 DIP。实时初始正文宽度 1150.4000244140625 DIP、100%、120 DPI、6 段均可排版；行尾物理误差最大约 0.00030518 pixel，小于 0.01 pixel。当前正文最终首次请求 428.9952 ms（开发诊断先行填充部分缓存），不代表任意长文或未缓存长段落性能保证。

260.125 DIP 组有 1 个明确 NoFeasibleBreaks 段，其他三组无无解段。扩宽→回到 680.375 指纹一致；1 DIP 全部 6 段明确无解、没有伪造字形行。Verify-Step09Snapshot 对最终包的英文 66 行亦通过，19 行断词，198 次像素比较零差异。

最终自动验收包安装位置 MDEditor/obj/step-10-deploy-r3，启动 2026-09-19T00:39:57.9765816+08:00；报告 Timestamp 2026-09-19T00:40:32.8038656+08:00，共同 runId 为 20260919-004032-804-62c5b34ffa5d480d847f4a4e7e48c54a。最终启动后资源/重排错误及相关 Application 1000/1026 为 0。最终包初始实时指纹 C8187ACEF44B15D4D261193A0E283BFA7C98ECA561D4CA990D965915464419FD（step-10-live-r3.json）。

Core / Typesetting / Native / App 四个 DLL 与构建 SHA-256 一致：

    Core        EAAF8C8962D142B185BE0816DA9A3D55CB9BD8F008DAC329B43B2AE008C966D8
    Typesetting 04FA3476620344BE9E2088E953454ED15BD20FEFA05D807663BBB4910B715A41
    Native      DDA6C185F7B1363E70A03C2F15FF31665493553346E8552A6B6E417D7DE56073
    App         9E7766425BA4401B229D0512F1155FD063EDB29DDC221C948BD0844D125A5B13

r1/r2 的非零像素对照不计入最终通过结果，定位及定向修复过程保留；没有放宽阈值。

## 人工验收与缩放优化（已通过）

用户对 r3 反馈“功能正常”，包含 resize/zoom/scroll/光栅密度及窗口三键等交互，但指出缩放“先同步变大/变小，再瞬间对齐”，要求消除延迟。r3 的最后一次布局 24.8181 ms（70%、120 DPI、12 次提交、3 次 DPI 复用），主因还包括 140 ms 请求合并及提前展示新 zoom。旧版本两段式视觉不作为最终缩放通过。

已取消编辑缩放等待、同帧提交 zoom 与断行、移出 UI 线程诊断 I/O，并增加有界已完成帧缓存。随后针对用户报告的“只有访问过的缩放才快”完成冷路径改造：固定验收正文从约 2.4 秒、约 603 次候选子串塑形，降为应用 epoch 首帧约 0.23 秒；首帧完成后，打乱访问 50%～300% 全部 26 档的新宽度约为 0.03～0.07 秒。性能探针同时保留选中行真实塑形核对，不能用缓存命中掩盖错误。长文增量、实际显示器 DPI 迁移和真实 GPU device-lost 仍需后续按独立指标验收。
