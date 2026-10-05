# 步骤 13：持久数学服务、缓存与恢复

## 范围与结果

步骤 13 将步骤 11/12 的“一次公式启动一个进程”升级为应用生命周期内的持久 `MDEditor.MathWorker` 服务。应用仍只接收纯 JSON 布局，Worker 仍只引用 `MDEditor.Typesetting` 和系统 DirectWrite；WinUI 进程不会加载 Worker 程序集，原生绘制也仍由 `MDEditor.Native` 完成。

本步完成协议协商、同进程请求复用、字体资源复用、有界公式缓存、结构化错误隔离、取消/超时安全、进程异常后的自动恢复和窗口关闭后的进程回收。`$...$` / `$$...$$` 接入 Markdown 正文属于步骤 14；当前受限 TeX 语法也没有扩展成完整 LaTeX 宏系统。

## 会话与协议

Worker 新增 `--server` 模式，stdin/stdout 使用每行一个 UTF-8 JSON 对象的 framing。第一行必须是 `hello`：客户端发送 transport 版本以及可接受的布局协议版本区间，Worker 返回 `ready`、选中的协议版本、PID、随机 session ID 和缓存容量。版本区间没有交集时返回稳定的 `protocol-mismatch` 并以退出码 3 结束。

握手完成后，每个布局请求和响应各占一行。响应除原有 `requestId / success / layout / error` 外，还包含 session ID、会话内单调 sequence 和 `cacheHit`。客户端同时核对请求 ID、协议、session ID 与 sequence，任何流错位、截断或非法 JSON 都会撤销整个会话。未带 `--server` 的旧模式仍读取 stdin 到 EOF、输出一个响应并退出，因此步骤 11/12 的脚本和退出码保持兼容。

## 并发、取消与恢复

`MathWorkerClient` 是线程安全的持久客户端。多个调用可以并发提交，但在 IPC 边界按顺序执行，因为一个 Worker 内的 DirectWrite 字体面和缓存属于同一会话；UI 线程从不等待同步排版。每次握手上限 5 秒，每次公式上限 10 秒。

如果调用在写入后取消，迟到响应仍可能留在管道中。客户端不会尝试猜测或跳过它，而是销毁整个流和 Worker；下一个请求创建新 session，因此取消结果绝不可能被误配给后续公式。stdout 提前关闭、进程崩溃或传输失败时，无外部副作用的布局请求会在全新进程中自动重试一次。公式语法错误则作为 `MathWorkerRequestException` 返回，不重启 Worker；同一会话必须能继续处理下一项请求。

窗口关闭时，画布先取消在途诊断，再关闭 Worker stdin；500 ms 内未正常退出才终止进程树。应用退出后不允许残留 `MDEditor.MathWorker`。

## 字体与公式缓存

一个 Worker 会按字体族复用 `SystemDirectWriteMathGlyphMetricsProvider`，因此系统字体集合、font face 和 OpenType MATH 表不再为每条公式重复打开。公式缓存键由源码、初始 math style、em size 的精确位模式和字体族组成。

缓存使用访问顺序 LRU，最多 128 项并受 2 MiB 估算成本上限约束。命中结果仍会构造新的不可变 `MathLayoutResult`，只替换当前 request ID；glyph/rule 内容不变，旧请求的身份不会泄漏到新响应。Worker 重启后缓存为空，不把字体相关布局跨进程或跨字体环境持久化到磁盘。

## 自动验收

```powershell
dotnet build MDEditor/MDEditor.slnx --no-restore -p:Platform=x64 -c Debug
dotnet test MDEditor/MDEditor.slnx --no-build --no-restore -p:Platform=x64 -c Debug
./tests/Verify-Step11MathWorker.ps1
./tests/Verify-Step12MathWorker.ps1
./tests/Verify-Step13MathService.ps1
```

核心回归现为 578 项。新增协议测试覆盖握手默认值、message type、transport 版本、协议范围、客户端身份、握手/响应 JSON round-trip 和旧响应兼容。真实进程脚本验证：三个排版请求在同一 PID/session 中得到 sequence 1/2/3；重复公式命中缓存；非法根号返回 `invalid-formula` 后同一 Worker 继续工作；强制结束后替换进程获得新 PID、新 session、空缓存和从 1 开始的 sequence；协议 2-only 客户端被明确拒绝。步骤 11/12 的一次性进程脚本同时继续通过。

开发窗口会执行同样的服务级检查，再对三个布局运行步骤 12 的九组 Win2D 多 DPI 像素验证。状态栏只有在“协议 / 持久进程 / 缓存 / 错误隔离 / 崩溃恢复 / MATH 像素”全部通过后才显示成功；报告写入 Debug 包的 `DevelopmentDiagnostics/step-13-math-service.json`。
