# 步骤 3：原生画布

本步骤建立 WinUI 画布宿主和 Win2D / Direct2D 绘制路径，只显示测试图形，不实现文字塑形、编辑、Markdown 或排版快照。

## 实现

- 应用 `Controls/EditorCanvas.cs` 动态创建 `CanvasControl`，占用窗口内容区，不进入标题栏。
- Native 的 `EditorCanvasRenderer` 创建四个设备相关画刷，绘制纸面、矩形、圆形与基准线。
- `CreateResources` 中重建画刷；设备丢失异常保留给 CanvasControl 的恢复机制，不吞掉异常。
- `SizeChanged` 请求重绘；场景使用当前视口 DIP 尺寸，零尺寸不绘制，窄/短窗口不产生负尺寸。
- CanvasControl 管理实际像素表面和 DPI；当前全部为矢量图形，没有需要单独缓存的 DPI 位图。
- `Unloaded` 解除 Draw/CreateResources/SizeChanged 绑定，移除原生画布并释放画刷；再次 Loaded 创建新表面。
- 窗口激活/恢复时请求重绘；Closed 显式 Dispose，防止仅依赖 Unloaded 导致释放遗漏。
- 不释放 CanvasControl 所有的共享 CanvasDevice，不缓存或释放回调传入的 CanvasDrawingSession。
- 底部显示尺寸、DPI、资源代数与绘制次数，便于人工验收。没有后台绘制定时器。

Win2D 固定为 1.4.0。Native 固定引用 WinUI 组件 2.3.6，与应用 WindowsAppSDK 2.4.0 实际解析的组件版本一致。由于已接入本机 DLL，Native 由步骤 2 的 AnyCPU 占位配置变为 x86/x64/ARM64，与应用同步；Core、Typesetting 和核心测试保持 AnyCPU。

资源所有权与视口几何的两个无平台 API 的生产源文件，直接链接到普通 .NET 测试工程中测试，不增加 Native 或 WinUI 运行依赖。这些测试不能代替真实设备绘制与窗口交互检查。

## 验收

2026-09-17 已完成本步骤验收：x64 Debug 全部模块编译通过；31 项自动测试成功、0 失败、0 跳过；新开发包部署和启动通过。用户对下列实际绘制、缩放、恢复及标题栏回归项目回复“全部正常”。这些交互结果来自用户手动测试，不是自动化 GUI 测试。

构建和独立测试命令见 architecture.md。真实运行需检查：

1. 内容区出现蓝色矩形、绿色圆形、五条基准线和白色纸面；底部绘制计数大于零。
2. 连续拖动窗口大小、最大化/还原后图形随内容区更新，没有黑屏、残影或异常退出。
3. 最小化后从任务栏恢复，图形仍正确显示。
4. 三个金刚键、最大化键 Snap 悬停菜单、失焦首次点击及标题栏拖动正常。
5. 最后关闭窗口；关闭路径应移除画布并释放资源。

设备重建代码已接入，不通过禁用系统 GPU 的方式测试。跨显示器 DPI、真实设备丢失和长期内存压力另行验收，未执行不能标记通过。

参考：[Win2D 快速入门](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/quick-start)、[设备丢失恢复](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/handling-device-lost)、[避免引用循环](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/avoiding-memory-leaks)。
