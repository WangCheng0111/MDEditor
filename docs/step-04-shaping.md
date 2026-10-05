# 步骤 4：DirectWrite 字形塑形

本步骤取得实际字体、glyph ID、浮点推进量、偏移、基线和 UTF-16 字形簇映射，并用同一批字形绘制。只实现 Native 塑形适配与固定诊断样张；没有实现共享布局快照、Knuth–Plass、正文两端对齐、Markdown 编辑或数学排版。

## 实现路径

`DirectWriteTextShaper.Shape` 创建不换行的 `CanvasTextLayout`，通过 `ICanvasTextRenderer.DrawGlyphRun` 捕获 DirectWrite 最终输出。DirectWrite 负责字体回退、脚本分析、OpenType 塑形和双向文字；这里没有按字符估算宽度、手动拼接字体或复用 XAML TextBlock 的测量值。无换行 layout 只是塑形和基准绘制工具，不采用其段落断行策略。

捕获的 Native `ShapedText` 包含：

- 每个 run 实际解析到的字体族和字型、字号、Bidi level、基线原点及源文 UTF-16 范围。
- 原始 glyph ID、advance、advance offset 和 ascender offset。
- UTF-16 到 glyph 的 cluster map；解码为源文范围、run 内 glyph 范围和精确累计宽度。
- Natural 模式下不依赖屏幕 DPI 的 DIP 推进宽度，以及 DirectWrite layout / cluster metrics 参考值。

绘制用 `CanvasDrawingSession.DrawGlyphRun` 重放捕获的字体、glyph、偏移、推进量和基线，不再次调用文本塑形。推进宽度不等于字形墨迹包围盒：行尾空格也必须计入。所有坐标保留小数，不逐字形取整。

标准连字可通过 OpenType `StandardLigatures` 参数开启/关闭；不强制使用旧式 pair-kerning 代替字体默认的高级塑形。连字依赖字体真实特性，不能要求不支持 `liga` 的字体也产生连字。验收使用 Gabriola，两行分别显示 ON/OFF。

字形簇不是编辑用的 Unicode grapheme。一个簇可能对应多个 UTF-16 单元或多个 glyph；RTL map 也不能按递增序号解读。后续光标、删除与输入法步骤将建立自己的编辑边界，不把这里的簇简单当成“一个字符”。

## 所有权与边界

`ShapedText` 拥有 source layout；回调字体是借用对象，不单独 Dispose。借用字体只用于该对象存活期间的绘制，Dispose 后拒绝重放。对外的元数据和数组为只读副本，不能修改用于重放的原始数据。

设备资源集中拥有五个画刷与八份样张 layout；创建失败清理已创建资源，重建成功后释放旧资源。卸载/关闭沿用步骤 3 的显式释放路径，不释放 CanvasControl 的共享 device / drawing session。设备丢失异常仍交回 CanvasControl 恢复机制。

当前只接受水平、无制表符/控制字符的样张，字号需为有限正数且不超过 256 DIP；竖排、inline object 和装饰线不会被静默忽略。本文数据是 Native 适配结果，不是步骤 5 将定义的 Core / Typesetting 共享布局契约。Core 和 Typesetting 仍不加载任何 Windows 依赖。

## 自动验收

2026-09-17，Windows 11 x64 / Debug：

- 完整解决方案编译通过，0 错误；保留既有 AppInfo.Current 的 CA1416 警告。
- 48 项普通 .NET 测试通过，0 失败、0 跳过；其中新增 17 项字形簇测试（含数据行），覆盖连字、组合字符、代理对、RTL、浮点累计与错误输入。测试链接生产解码器源文件，不加载 Native / WinUI。
- 直接从仓库外运行测试 DLL 也通过。
- 最终开发包 `obj/step-04-deploy-r5` 已注册并真实启动；实际 EXE 路径及 Native DLL 哈希均已核对。
- 8 组真实字体样张：中西文混排、西文、连字 ON/OFF、组合字符、字体回退、双向文字、尾部空格。
- 每组比较 glyph advance 总和、DirectWrite 含尾部空格的 layout width、cluster width 总和，实际误差全部为 0 DIP；源文范围完整覆盖，缺失字形数全部为 0。
- 每组在 96 DPI × 1.0、144 DPI × 1.25、192 DPI × 0.75 三种配置下，比较 DirectWrite 原始绘制与捕获字形重放的 BGRA 像素。共 24 次对比，差异字节数和最大通道差异全部为 0。
- Gabriola 连字 ON 为 17 glyphs / 152.7119140625 DIP，OFF 为 25 glyphs / 156.86962890625 DIP。组合字符为 14 glyphs / 10 clusters。
- Arial 请求中的中文实际回退到 Microsoft YaHei UI；记录的不是请求字体名。

这些结果只证明此次样张、字体及渲染配置，不代表全部 Unicode 字体、真正设备丢失、跨屏 DPI 或长期内存压力已验收。代理对的当前验收来自纯解码器测试，不冒充真实补充平面字形绘制测试。

开发包最初出现的启动 fail-fast 已定位：崩溃堆栈在 `Windows.Storage.ApplicationData.Current` 的 activation factory 内，而不是 DirectWrite 塑形。移除该 UWP 存储调用后已重新部署验证。Debug 诊断现在通过普通 .NET 写入 LocalApplicationData 下的 `MDEditor/DevelopmentDiagnostics`；有包身份时该路径可能重定向到本包的 LocalCache/Local。仅保存本应用诊断，不采集桌面；写入失败记录 Debug 输出，不抑制绘制/设备异常。临时全局异常探针已移除。

构建、测试命令见 [architecture.md](architecture.md)。Debug 首次创建设备资源时自动生成 `step-04-shaping-时间-唯一ID.json`，里面保存完整 run、glyph、cluster、字体和像素结果；后续资源重建生成新文件，不能拿旧报告验证新构建。

## 人工验收

2026-09-17，用户对以下窗口/视觉项目回复“全部正常”，已记录为手动验收通过；随后确认“已关闭”。22:27:45 检查剩余 MDEditor 进程为 0，最终启动以来相关 Application 1000/1026 崩溃事件为 0。步骤 4 验收完成；不把用户反馈冒充自动化 GUI 测试：

1. 八行样张可见，中文无方框，组合重音位置正常，连字 ON/OFF 有差异。
2. 底部显示“字形 / 宽度 / 像素检查通过”，绘制计数大于零。
3. 调整窗口、最大化/还原、最小化恢复无黑屏；内容区不会覆盖标题栏。
4. 三个金刚键、Snap 悬停、失焦首次点击、标题栏拖动、关闭正常。

样张按窗口大小等比缩小适配；这是同一批矢量 glyph 的预览变换，不是文档重排。蓝线标记基线，绿线标记推进宽度终点，尾部空格会使绿线与最后墨迹之间留白。不同样张行尾不齐属正常：步骤 4 尚未实现正文两端对齐。

本环境 computer-use 技能需要的 node_repl 不可用，未自动执行窗口输入、截图或交互；该部分由用户手动验收。

## 官方接口参考

- [ICanvasTextRenderer / DirectWrite 字形回调](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_ICanvasTextRenderer.htm)
- [DrawGlyphRun 回调的数据](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_Text_ICanvasTextRenderer_DrawGlyphRun.htm)
- [原始 glyph 绘制](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasDrawingSession_DrawGlyphRun_2.htm)
- [CanvasTypography / OpenType 特性](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTypography.htm)
- [Natural 测量模式](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextMeasuringMode.htm)
