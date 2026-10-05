# 模块架构

模块只按下列方向引用，不反向引用或形成循环：

```text
MDEditor（WinUI 应用） → Core、Typesetting、Native
Native（Windows 适配） → Core、Typesetting
MathWorker（独立进程） → Typesetting
Typesetting（纯排版） → Core
Core（纯文档核心） → 无其他项目
Core.Tests（普通 .NET） → Core、Typesetting
```

| 模块 | 目标框架 | 职责 |
| --- | --- | --- |
| MDEditor | net10.0-windows10.0.19041.0 | 窗口、原生画布宿主、MVVM、输入法、剪贴板、文件与异步服务 |
| MDEditor.Core | net10.0 | 原文、编辑事务、历史、解析、投影、源码映射、文件和恢复的数据模型 |
| MDEditor.Typesetting | net10.0 | 排版算法、数学布局、快照、文本交互及 PDF 写入契约 |
| MDEditor.Native | net10.0-windows10.0.19041.0 | DirectWrite/Direct2D/Win2D 适配、字体塑形、字形及 PDF 绘制 |
| MDEditor.MathWorker | net10.0-windows10.0.19041.0 | 持久 TeX 数学子进程与系统字体解析，不引用 App、Native 或 WinUI |
| MDEditor.Core.Tests | net10.0 | 核心、纯排版、模块边界和发布配置回归，不加载 WinUI 主程序 |

Core 和 Typesetting 使用自己的逻辑坐标与源码范围，不引入 Windows/COM/XAML 类型。MVVM 在应用层；逐字形数据和光标闪烁不通过逐帧 ViewModel 通知传递。

## 数据与生命周期

- 原文快照是保存、编辑、撤销、搜索及无障碍接口的唯一文本来源；显示投影不改写原文。
- 原文/显示坐标双向映射，保留隐藏语法、组合字符、公式和图片的合法光标边界。
- 后台工作读取不可变快照；布局请求按纪元取消或丢弃过期结果，缓存有容量限制。
- 数学与高亮使用持久的本地子进程，复用结果并在编辑器退出后释放。
- 绘制按视口查询对象；PDF 录制同一生产布局，不另建一套浏览器排版。
- 本地图片附件及崩溃恢复数据可能仍被文档引用，不能当作普通临时缓存删除。

## 构建

应用、Native 和 MathWorker 使用 x64 / x86 / ARM64；纯核心、Typesetting 和测试使用 AnyCPU。VS 打开 `MDEditor/MDEditor.slnx`，只把 MDEditor 设为启动和部署项目。

在 Visual Studio Developer PowerShell、仓库根目录执行：

```powershell
MSBuild.exe MDEditor/MDEditor.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x64 /p:GenerateAppxPackageOnBuild=false
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Debug
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Release
```

核心测试使用 MSTest.Sdk 4.3.3 和 Microsoft.Testing.Platform。根目录 `global.json` 选择测试运行器，不锁定 .NET SDK。测试复制项目配置作为架构快照；它们不是应用运行依赖。

发布和目标机器验证见[打包说明](Packaging.md)；离线高亮资源更新见[starry-night](starry-night-highlighting.md)。
