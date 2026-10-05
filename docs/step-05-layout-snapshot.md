# 步骤 5：统一、不可变的布局快照

本步骤把源文范围、字形、字形簇、run、行、块、基线与最终几何位置统一到纯 .NET 数据契约中。独立检查器和实际画布消费同一个 `LayoutSnapshot`，不把诊断副本当成绘制数据。只实现固定样张的转换与重放；没有实现段落断行、Knuth–Plass、正文两端对齐、Markdown 编辑、光标命中或数学排版。

## 数据与坐标约定

依赖方向保持 `Typesetting -> Core`、`Native -> Core + Typesetting`；普通 .NET 测试不加载 Native、WinUI 或 COM。

- `Core/Text/SourceTextSnapshot.cs`：只读文本、非负版本，以及半开 UTF-16 `SourceRange [Start, End)`。这不是可编辑缓冲区，也不是 Unicode grapheme 范围。
- `Typesetting/Layout/LayoutGeometry.cs`：有限 double 的文档局部 DIP 坐标；宽高非负，边界和需保持有限。预览缩放、屏幕 DPI 和窗口偏移不写入快照。
- `GlyphRunLayout.cs`：快照局部字体槽、实际字体名称元数据、glyph ID、advance、双向级别、偏移、字号、locale、绝对基线原点，以及源文到 glyph 范围的簇映射。
- `LayoutSnapshot.cs`：`Snapshot -> Block -> Line -> Run -> Glyph/Cluster`，各层源文范围相互包含，行/块几何边界相互包含。空行使用零个 run，不制造空字形。

所有公开数据只读，构造时逐项复制输入集合。即使输入是通过 `ImmutableCollectionsMarshal` 包装的外部数组，也不复用其底层存储。子对象自身不可变；源文 string 不会被外部修改。没有原生句柄、时间戳、设备状态或资源所有权混入快照。

glyph 推进宽度不等于墨迹包围盒，尾部空格必须计入。保留小数，不逐字形取整。run advance 是其 glyph advance 之和；line advance 是独立的逻辑行宽，为以后 glue 留出空间，不强制等于 run advance 总和。各 run 的基线原点也不强制与行基线相同，以允许以后不同字号或基线偏移。

字形簇按逻辑源文顺序保存，glyph 数组保留塑形输出顺序；RTL 的 glyph 范围可以下降，不能把 source 顺序当成 glyph 顺序。簇在源文中不重叠，glyph 范围不重叠且完整覆盖所属 run，簇宽度与对应 glyph advance 一致。源文范围允许间隙和零长度锚点的数据表示，但这不代表已实现 Markdown 隐藏语法或生成内容功能。编辑边界仍待后续步骤建立。

## 独立检查与确定性

`LayoutSnapshotInspector` 提供摘要、完整 JSON 和 SHA-256 指纹。它只读取生产快照，不依赖 Windows、字体对象或绘制会话。相同契约/runtime、字体环境及输入产生相同元数据时，JSON 和指纹一致；修改源文、版本、几何、glyph、字体元数据、locale 或 bidi 会改变指纹。

JSON 目前是诊断格式，不承诺反序列化、跨版本持久化 ABI、跨机器字体标识或跨字体安装环境相同排版。字体族/字型名字只是可读元数据，不足以重建原生字体或作为全局字体身份。

## 原生绑定和实际绘制

`Native/Text/NativeSnapshotBinding.cs` 把步骤 4 的塑形结果转为共享快照，同时单独保存借用的实际 `CanvasFontFace`、所属 `ShapedText` 的生命周期引用及 run 的原生 glyph 缓存。快照与这些原生资源没有反向引用。

转换时先核对源文切片与塑形文本。run 范围由回调 `characterIndex` 和 `clusterMap.Length` 决定：Win2D 回调的 `Text` 可能是完整关联缓冲区，不能拿其字符串长度充当 run 长度。簇的 source start 已是塑形文本局部位置，只加一次文档源文起点，不能再加 run 起点。

绘制从传入快照读取 glyph、推进量、偏移和最终基线坐标，而不是退回捕获数组或捕获位置。允许使用同一活字体目录创建的几何/glyph 变体；字体槽必须对应绑定的原始 descriptor 对象，只有名字相同的外来目录也会被拒绝。这项活资源兼容检查与语义指纹不同：指纹相同不代表能够借用另一绑定的字体资源。

`ShapedText` 拥有 layout，绑定借用它和回调字体，不独立 Dispose 借用字体。释放绑定不释放调用者的塑形结果；显式释放任一字体 owner 后，包括空快照在内的重放都拒绝执行。释放顺序为绑定在前、样张 owner 在后。绑定可重复 Dispose；纯快照在绑定/owner 释放后仍可检查和计算指纹。转为 Direct2D float 时拒绝非有限值或超出 float 范围的坐标/度量。

实际画布、基线、推进终点与 source 标签读取共享快照。原始 DirectWrite layout 的绘制仅用于独立像素基准，不作为步骤 5 的正常样张绘制路径。

## 自动验收

2026-09-17 完成实现与首次开发包验证；2026-09-18 中断恢复后重新编译、测试和启动同一已验证的开发包：

- 完整 x64 / Debug 解决方案编译通过，0 错误；保留既有 `AppInfo.Current` 的 CA1416 警告。
- 151 项普通 .NET 测试全部通过，0 失败、0 跳过；此前 48 项，本步骤新增 103 个测试用例（49 个方法，含 DataRow）。仓库外直接执行测试 DLL 同样通过。
- 覆盖 UTF-16、范围/溢出、几何、层级包含、RTL、连字/组合字符、簇覆盖、错误输入、空布局、外部数组别名的完整深复制、独立摘要和 JSON，以及四种 culture 下的指纹一致性。
- 实际开发包 `MDEditor/obj/step-05-deploy-r2` 注册状态正常。恢复时未重装，没有删除应用数据；运行 EXE 路径与已验证 Native / Typesetting DLL 哈希一致。
- 八行真实字体样张共 209 个 UTF-16 单元、18 个字体槽、8 个块、8 行、18 个 run、194 个 glyph 和 190 个簇。字体槽目前按捕获 run 分配，不代表 18 种不同字体。
- 重复转换、完全重新塑形以及恢复后的独立进程启动均得到以下指纹：

```text
F6DA9EC8F2772FD38DC5DAEE5C5901C8F8F7A834849AF0DD72F848774499DBD8
```

- 八组样张各在 96 DPI × 1.0、144 DPI × 1.25、192 DPI × 0.75 下比较原始 DirectWrite 绘制与快照重放：共 24 次，差异字节数和最大通道差异全部为 0；真实宽度检查通过，缺失 glyph 为 0。
- 只修改快照坐标，以及只修改快照 glyph advance/offset，均真实改变输出，并逐字节等于独立构造的预期图像。这证明绘制消费的是传入快照，而非旧捕获数据。
- 普通文本、尾部空格、全空格、空文本的宽度检查通过。空重放、owner/binding 释放拒绝、重复释放、释放后快照仍可读、source 不匹配、已释放输入、外来字体目录和 float 溢出拒绝检查均通过。

构建/测试命令见 [architecture.md](architecture.md)。受限环境曾使 `dotnet test` 的测试宿主命名管道连接失败；在受限环境外重新运行后，151 项测试全部通过，仓库外直接执行测试 DLL 也通过。失败堆栈是 `System.UnauthorizedAccessException -> NamedPipeClientStream.TryConnect -> Microsoft.Testing.Platform.IPC.NamedPipeClient.ConnectAsync`，退出码 -532462766 对应 `0xe0434352`，与用户看到的 `MDEditor.Core.Tests.exe` 异常弹窗吻合。未发现主程序相应异常，不需要修改编辑器代码或要求主程序以管理员权限运行；不能把这次零测试的失败运行当成测试通过。

这些验收限于当前 x64 环境、固定样张、已安装字体和离屏渲染配置；不代表 x86/ARM64、全部 Unicode/字体、真实跨屏 DPI、设备丢失或长期内存压力已验收。代理对目前来自纯契约/解码器测试，不冒充真实补充平面 glyph 绘制测试。缺少 mspdbcmf.exe 的已有符号包警告与本步骤运行无关。

## Debug 报告和窗口验收

每次创建设备资源生成同一 run ID 的两份文件：`step-05-layout-时间-ID.json` 保存纯生产快照，`step-05-shaping-时间-ID.json` 保存原生宽度、像素及快照验证结果。快照指纹不包含 shaping report 的时间戳。设备重建生成新报告，验收新启动必须筛选启动之后的报告。

普通 .NET 的 LocalApplicationData 路径在此开发包中重定向到：

```text
%LOCALAPPDATA%/Packages/2d40a5e2-fc23-4e7c-8964-6b3c400c56c1_f7j9ca68mjy0c/LocalCache/Local/MDEditor/DevelopmentDiagnostics
```

恢复后的报告 run ID 为 `20260918-185028-856-ae785d21f350443585c432f69a0d3493`。此前报告保留，不覆盖、不删除。

2026-09-18 用户对下列窗口验收请求反馈“软件正常”，随后确认“已关闭”。18:54:09 检查 MDEditor 主程序和 MDEditor.Core.Tests 驻留进程均为 0，验收启动以来未发现相关 Application 1000/1026 崩溃事件。步骤 5 验收完成；用户反馈是人工验收，不冒充自动化 GUI 测试。测试程序弹窗的独立原因见上节，未混同为主程序绘制问题：

1. 八行样张可见，中文/重音/连字正常；底部显示“快照 / 字形 / 宽度 / 像素检查通过”、指纹 `F6DA9EC8`，绘制计数大于 0。
2. 调整窗口、最大化/还原、最小化恢复不黑屏，快照指纹不变。
3. 三个金刚键、Snap 悬停、失焦首次点击、标题栏拖动和关闭正常。
4. 关闭最后一个窗口后无 MDEditor 驻留进程，验收启动以来无相关 Application 1000/1026 崩溃事件。

本步骤未改动 `MainWindow.TitleBar.cs` 或 `MainWindow.NonClient.cs`。样张缩小适配仅是预览变换，不是段落重排；不同行尾仍不对齐属当前范围内正常现象。确认步骤 5 后再由用户授权步骤 6；本次没有进入后续步骤。
