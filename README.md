# MDEditor

WinUI 3 + CommunityToolkit.Mvvm 的原生 Markdown 编辑器项目，按逐步实现、逐步验收的方式推进。

当前运行版保留正文编辑、原生排版和正文公式；历史步骤文档中的开发诊断面板、专项测试按钮、像素画廊及本地诊断日志已从应用移除。`tests/MDEditor.Core.Tests` 和公式服务验证脚本仍作为回归测试保留。

当前已建立运行基线、分层模块、原生画布、DirectWrite 塑形、不可变布局快照、Knuth–Plass、中西文细调、英文断词，以及步骤 10 的实时视口重排/缩放。冷缩放候选已改为整段一次塑形后的纯标量度量，新宽度不再依赖已完成帧缓存。步骤 11 新增独立 TeX 数学工作进程；步骤 12 读取 Cambria Math 的 OpenType MATH constants、vertical variants 与 glyph assembly，并将真实 glyph ID、坐标和 rule 交给 Win2D 原生绘制。根号 continuation 与字形 outline 合并成单一几何，避免叠画、断口、接头台阶和粗细突变。步骤 13 将 Worker 升级为协议协商后的持久服务：并发调用复用同一进程和字体资源，128 项有界 LRU 缓存复用不可变布局，结构化公式错误不终止会话，请求取消/超时撤销流，进程崩溃后无副作用请求自动恢复。步骤 14 将 `$...$` / `$$...$$` 接入只读 Markdown 正文验收样张，行内数学盒与 DirectWrite 文字共同断行并在原生画布上绘制。步骤 15 在纯 Core 中建立保留 Unicode 与原始换行的 piece-table 文本缓冲、原子编辑事务和版本化行索引。步骤 16 在原生画布上接入源码映射的点击光标、跨行鼠标选区、公式原子盒子与缩放/重排后的选区重投影。步骤 17 将键盘输入、删除、换行、光标导航与选区替换接入可编辑的正文样张，并用版本化快照触发原生重排。步骤 18 接入分组撤销、重做和剪贴板，并提前接入最小可编辑数学链路：公式连同文字可粘贴到正文并原生渲染；单独的陈列公式粘在句中会自动独占一行。步骤 19 接入 WinUI 桌面的输入法组合事件、候选位置、临时组词及单次撤销。步骤 20 在纯 Core 中建立 CommonMark/约定 GFM 子集的解析配置、原文范围节点与语法测试集；文件持久化与完整 LaTeX 语法仍属后续步骤。

步骤 21 建立独立的 Markdown 编辑投影：语法标记可折叠/展开，显示位置与原文位置双向映射，并规定隐藏标记旁的光标、删除和选区规则。步骤 22 已将段落、H1–H6、加粗、强调、删除线、行内代码和链接接入原生画布实时编辑；格式命令只替换目标源码，支持撤销/重做。步骤 23 接入引用、无序/有序/任务列表的原生标记绘制和结构命令，包括续项、空项退出、缩进/退格、嵌套、任务勾选与同级有序项顺延。步骤 24 接入围栏/缩进代码块、语言标签及有界的原生词法着色；代码保持自然等宽行宽，不参与正文两端对齐。步骤 25 支持四种数学公式分隔符的实时源码展开、原生渲染与错误定位。步骤 26 接入可编辑 GFM 表格、对齐和行列命令。步骤 27 接入本地图片、脚注及公式编号和交叉引用；远程图片保持禁用。

步骤 28 对隔离普通段落使用增量 Markdown 解析，并保守处理围栏、列表、表格与引用定义的跨段失效；原生文字会话按未改段落复用字形塑形、候选测量和有界断行结果。长文视口绘制与调度随后在步骤 29 处理。

步骤 29 为长文档的原生绘制对象建立视口区间索引，按估算内存约束已完成布局缓存，并让长文新请求取消过期后台构建；源码坐标的滚动锚点与数学/代码区间查询也针对长文做了修正。全篇布局仍在后台完成，尚非只布局可见段落的虚拟文档。

步骤 30 接入“均衡 / 书刊”字体预设；步骤 31 接入本地 Markdown 文件打开/保存、修改标记、外部修改冲突检查与崩溃恢复检查点。远程图片仍不联网加载。

步骤 32 接入查找、替换和同区源码模式；步骤 33 公开原生编辑区的无障碍文本/选区接口；步骤 34 接入共享生产布局的矢量 PDF 导出。按 Ctrl+Shift+E 选择保存位置，当前排版的字形、公式曲线、表格、代码配色及本地图片会写入 PDF，不改动 Markdown 文件、修改标记或撤销历史。使用方法和分页约定见 [PDF 导出](docs/step-34-vector-pdf.md)。

Markdown 主题已按用户提供的 GitHub CSS 改为原生浅色/深色样式，默认跟随系统；说明见 [GitHub 风格主题](docs/github-markdown-theme.md)。当前界面已移除标题栏下方的整排工具栏、内容区卡片外框及画布实色底，正文直接绘制在透明编辑区。标题栏、编辑区和状态栏共用窗口原有的亚克力背景及同一层半透明主题遮罩，不再有独立的画布底色；右上角窗口键、底部状态栏与现有键盘快捷键保留。字体和主题切换逻辑作为界面设置接口保留，不再创建工具栏控件。

步骤 35 补齐发布配置保护、离线依赖和架构校验及整体回归；不自动生成安装包。继续从 MDEditor 单项目发布即可，WAPP 不是必需。方法见 [打包说明](docs/Packaging.md)，证据及目标机验收边界见 [发布回归](docs/step-35-release-regression.md)。

- 解决方案：`MDEditor/MDEditor.slnx`
- 模块边界与构建/测试命令：[architecture.md](docs/architecture.md)
- 核心测试：`tests/MDEditor.Core.Tests`
- 原生画布与验收：[step-03-canvas.md](docs/step-03-canvas.md)
- 字形塑形与验收：[step-04-shaping.md](docs/step-04-shaping.md)
- 统一布局快照与验收：[step-05-layout-snapshot.md](docs/step-05-layout-snapshot.md)
- 纯算法 Knuth–Plass 与验收：[step-06-knuth-plass.md](docs/step-06-knuth-plass.md)
- 真实字形与原生两端对齐：[step-07-justified-paragraphs.md](docs/step-07-justified-paragraphs.md)
- 中文禁则、标点与混排细调：[step-08-cjk-typography.md](docs/step-08-cjk-typography.md)
- 英文断词、条件连字符与验收：[step-09-english-hyphenation.md](docs/step-09-english-hyphenation.md)
- 正文重排、缩放、滚动与 DPI：[step-10-viewport-reflow.md](docs/step-10-viewport-reflow.md)
- TeX 数学工作进程与最小布局：[step-11-tex-math-worker.md](docs/step-11-tex-math-worker.md)
- OpenType MATH 与原生公式绘制：[step-12-native-math-rendering.md](docs/step-12-native-math-rendering.md)
- 持久数学服务、缓存与恢复：[step-13-persistent-math-service.md](docs/step-13-persistent-math-service.md)
- Markdown 正文中的数学公式：[step-14-markdown-math-body.md](docs/step-14-markdown-math-body.md)
- 文本缓冲与编辑事务：[step-15-document-buffer.md](docs/step-15-document-buffer.md)
- 原生光标、鼠标命中与选区：[step-16-native-caret-selection.md](docs/step-16-native-caret-selection.md)
- 键盘编辑与正文重排：[step-17-keyboard-editing.md](docs/step-17-keyboard-editing.md)
- 撤销、重做与剪贴板：[step-18-undo-clipboard.md](docs/step-18-undo-clipboard.md)
- 中文输入法组合：[step-19-ime.md](docs/step-19-ime.md)
- Markdown 解析配置与语法测试：[step-20-markdown-syntax.md](docs/step-20-markdown-syntax.md)
- 编辑投影与双向源码映射：[step-21-edit-projection.md](docs/step-21-edit-projection.md)
- 实时 Markdown 富文本编辑：[step-22-live-markdown.md](docs/step-22-live-markdown.md)
- 引用、列表与任务项结构编辑：[step-23-structural-markdown.md](docs/step-23-structural-markdown.md)
- 代码块与基础语法高亮：[step-24-code-blocks.md](docs/step-24-code-blocks.md)
- 实时数学公式编辑：[step-25-markdown-math-editing.md](docs/step-25-markdown-math-editing.md)
- 可编辑 GFM 表格：[step-26-markdown-tables.md](docs/step-26-markdown-tables.md)
- 本地图片、脚注和交叉引用：[step-27-images-footnotes-cross-references.md](docs/step-27-images-footnotes-cross-references.md)
- 增量解析与局部排版复用：[step-28-incremental-parse-layout.md](docs/step-28-incremental-parse-layout.md)
- 长文档视口绘制、缓存与调度：[step-29-long-document.md](docs/step-29-long-document.md)
- 字体预设与高级排版调校：[step-30-font-presets.md](docs/step-30-font-presets.md)
- 文件打开、保存与崩溃恢复：[step-31-files-recovery.md](docs/step-31-files-recovery.md)
- 无障碍文本与选区：[step33-accessibility.md](docs/step33-accessibility.md)
- 共享布局矢量 PDF 导出：[step-34-vector-pdf.md](docs/step-34-vector-pdf.md)
