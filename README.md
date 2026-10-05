# MDEditor

基于 WinUI 3、CommunityToolkit.Mvvm、DirectWrite 和 Win2D 的原生 Markdown 编辑器。正文、代码和数学公式使用原生绘制，不依赖浏览器或 WebView。

## 功能

- CommonMark 与约定的 GFM 扩展：标题、强调、删除线、链接、引用、列表、任务项、代码块、表格及分割线。
- 实时编辑与源码展开/折叠；Unicode 光标和选区、撤销/重做、剪贴板及中文输入法。
- Knuth–Plass 两端对齐、中文禁则和标点处理、英文断词；调整窗口和缩放时重新排版。
- 行内及独立 TeX 公式、原生数学字形、脚注、公式编号和交叉引用。
- 离线 starry-night 代码高亮、本地图片与剪贴板图片粘贴。
- 本地 Markdown 打开/保存、未保存修改提示、外部修改检查和崩溃恢复。
- 浮动搜索/替换、同区域源码模式、无障碍文本/选区接口，以及共享布局的矢量 PDF 导出。
- GitHub 风格浅色/深色主题，默认跟随系统；右上角主题按钮可切换浅深色。

远程图片不联网加载；数学支持有明确的语法边界，并非完整 LaTeX。长文按视口绘制，但布局仍在后台处理整篇文档。标题下方不设工具栏，常用功能通过快捷键操作。

## 开发与测试

使用 Windows、.NET 10 SDK、支持 WinUI 的 Visual Studio 和 PowerShell 7。打开 `MDEditor/MDEditor.slnx`，将 MDEditor 设为启动项目，选择 x64 Debug 或 Release，并勾选主项目的“生成”和“部署”。

在 Visual Studio Developer PowerShell、仓库根目录执行：

```powershell
MSBuild.exe MDEditor/MDEditor.csproj /restore /t:Build /p:Configuration=Release /p:Platform=x64 /p:GenerateAppxPackageOnBuild=false
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Debug
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Release
```

正常构建不生成安装包。随源码保留的 Node、WASM、高亮包及许可文件是离线运行所需资源，不是临时缓存；不要删除。发布只需选择 MDEditor 主项目，详见[打包说明](docs/Packaging.md)。

## 常用快捷键

| 操作 | 快捷键 |
| --- | --- |
| 新建 / 打开 / 保存 / 另存为 | Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S |
| 撤销 / 重做 | Ctrl+Z / Ctrl+Y 或 Ctrl+Shift+Z |
| 复制 / 剪切 / 粘贴 / 全选 | Ctrl+C / Ctrl+X / Ctrl+V / Ctrl+A |
| 加粗 / 强调 / 链接 / 删除线 | Ctrl+B / Ctrl+I / Ctrl+K / Ctrl+Shift+X |
| 查找 / 替换 | Ctrl+F / Ctrl+H |
| 下一项 / 上一项 | F3 / Shift+F3 |
| 源码 / 实时预览 | Ctrl+/ |
| 编辑缩放 / 恢复 100% | Ctrl+滚轮或 Ctrl+加减号 / Ctrl+0 |
| 导出矢量 PDF | Ctrl+Shift+E |

## 文档

- [模块架构与构建](docs/architecture.md)
- [打包与发行检查](docs/Packaging.md)
- [GitHub 风格主题](docs/github-markdown-theme.md)
- [离线代码高亮与依赖更新](docs/starry-night-highlighting.md)
- [搜索、替换与源码模式](docs/search-source-mode.md)
- [剪贴板图片与附件保存](docs/clipboard-images.md)
- [Markdown 分割线](docs/markdown-thematic-breaks.md)
- [矢量 PDF 导出](docs/pdf-export.md)
- [无障碍接口](docs/accessibility.md)
