# MDEditor 打包说明

## 哪个项目需要发布？

只选 `MDEditor/MDEditor.csproj`。现有单项目 MSIX 流程会携带 Core、Typesetting、Native、数学子进程及离线代码高亮资源；不要分别发布这些项目，也不需要多选所有项目。

`MDEditor.MathWorker.exe` 用于公式塑形，随包的 Node 用于 starry-night 高亮，都是应用自己启动的后台子进程。用户启动入口只有 MDEditor。**不要求使用 Windows Application Packaging Project（WAPP）**，继续使用单项目流程，以依赖校验和目标机器运行结果为准。

## 正常开发，不生成安装包

用 Visual Studio 打开 `MDEditor/MDEditor.slnx`，启动项目设为 MDEditor，选择 Debug/Release 和 x64。配置管理器中 MDEditor 的“生成”和“部署”都需勾选；依赖只需正常生成，不分别部署。VS F5 的开发部署不是发布一个新安装包。

三个无凭据的共享 `.pubxml` 随源码保存；`.pubxml.user`、证书私钥和本地 VS 状态不应提交。项目默认不生成 MSIX，只有你主动调用打包向导或显式设置 `GenerateAppxPackageOnBuild=true` 才生成安装包。

开发构建可在 VS Developer PowerShell 使用：

```powershell
MSBuild.exe MDEditor/MDEditor.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false
```

架构必须匹配：x64/win-x64、x86/win-x86、ARM64/win-arm64。不使用 AnyCPU 发布 WinUI 主程序。不允许裁剪、单文件发布或移除随包 .NET 运行时；它们会破坏子进程共享依赖。

## 你自行发布时

右键 MDEditor →“打包和发布”→“创建应用包”，按实际发行渠道选择签名与架构。保留 Release、不裁剪、非单文件、自包含 .NET。当前 `AppxBundle=Never`，每个目标架构分别验收。不要把单独的 EXE 当作可分发应用。

当前 Publisher 为 `CN=31059`，开发证书用于本机/内部测试。正式分发需要目标机器信任的签名，签名 Subject 必须与 Publisher 一致；证书和发行渠道由发布者选择。升级需递增包版本。不由测试脚本替你更换身份、证书或版本。

**.NET 自包含不等于 Windows App SDK 自包含。** 当前生成清单声明 `Microsoft.WindowsAppRuntime.2`，最低版本 2.4.0.0。离线目标机器也需要对应架构、满足版本要求的框架依赖。使用打包向导生成的 Dependencies/安装脚本交付并在干净机器验证；不能因为开发机已安装框架，就认为单个主 MSIX 在所有电脑可安装。当前应用没有联网加载远程图片或下载高亮器的路径。

字体预设还使用系统中文字体及 Cambria Math；目标机器需有可用的数学字体。本地图片相对文档目录解析，移动文档时应一起交付其相对路径资源。不要清理仍被文档/恢复草稿引用的剪贴板图片目录。

## 发布前依赖校验

使用 PowerShell 7。刚 Build、尚未 VS 部署时，可直接检查 MSBuild 文件清单（自动创建短期目录，用后删除，不生成安装包）：

```powershell
./tests/Verify-Step35Release.ps1 -Configuration Release -Architectures x64,x86,ARM64
```

所选架构需先分别 Build。对已生成的 AppX 暂存目录（或自己解包后的 MSIX 目录）可执行：

```powershell
./tools/Verify-ReleasePayload.ps1 -Path 'MDEditor/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/AppX' -Architecture x64 -ReferencePath 'MDEditor/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64'
```

命令只读，不生成/安装/注册任何包，不下载依赖。检查主程序、数学子进程的完整依赖、.NET runtimeconfig/deps、原生 PE 架构、Win2D、字体、图片、Node/WASM/许可文件、单一启动入口，并用哈希检查项目程序集是否为最新。随后从被检查目录直接启动数学和高亮子进程，在移除外部 Node/.NET 搜索路径的环境中验证公式、错误恢复、高亮、Unicode 范围和正常退出。

ARM64 的交叉构建在 x64 机器只能加 `-StaticOnly` 检查文件，不能标记为运行通过。即使 x86 子进程在 WoW64 通过，也不代替完整 x86 UI 验收。真正的 ARM64 机器与干净目标机器安装仍需发布者实测。

最后应从安装后的 Windows 入口（而不是仅 VS F5）回归：标题栏/Snap、输入法、连续编辑/删除、搜索替换、公式和代码高亮、本地图片、打开保存恢复、矢量 PDF、缩放滚动及退出后的子进程释放。

交叉构建成功不等于实机验收。正式发布前需在对应架构和干净目标机器验证安装、离线依赖、签名信任及上述功能；不要只用已安装开发依赖的电脑判断完整性。
