# 步骤 9：英文断词与原生排版质量

状态：完成。2026-09-19 最终 r4 开发包的自动检查、人工窗口验收与退出检查均通过。尚未实施步骤 10。

## 实现

Typesetting 新增 Liang 最大权重模式 trie、离线 en-US 数据与独立策略。重叠模式取最高权重，奇数允许、偶数抑制；显式例外覆盖模式结果。内嵌文件有 4,938 个模式及 14 个例外，另明确覆盖上游记录的 democrat 问题，运行时共 15 个例外。源文件版权及再分发说明完整保留，内容 SHA-256 固定，运行时不访问网络。

- [数据版本及授权](../MDEditor.Typesetting/Hyphenation/Data/README.md)
- [上游模式](https://github.com/hyphenation/tex-hyphen/blob/master/hyph-utf8/tex/generic/hyph-utf8/patterns/tex/hyph-en-us.tex)
- [Liang 算法论文来源](https://tug.org/docs/liang/)

默认碎片下限为左 2 / 右 3，可配置得更严格；自动断词 penalty 默认 50，并带 flagged 标记，参与既有整段 Knuth–Plass 连续断词及末行前断词的评分。Native 会话通过 HyphenationOptions.EnglishUs 显式启用；步骤 7、8 回归保持关闭断词，不改变原断点。

词典建议必须落在整段真实字形簇与 grapheme 的共同边界。Gabriola 实际 ffi 连字保护已用原生样张验证；组合字符、代理对和 emoji 不被自动拆开。策略接受全小写或首字母大写，默认不处理全大写、camelCase、数字/标识符片段、链接、邮箱、路径、紧邻撇号或连字符的词。可传入绝对 UTF-16 排除范围，给未来解析后的代码、链接和公式排除留出明确契约。

## 条件连字符与源码

ParagraphItemMap 为合法词典点加入 conditional penalty，不修改源文本。只有选中断词边才通过 DiscretionaryLine 显示 U+2010，或策略指定的 ASCII 连字符；未选 penalty 不产生字形，末行不生成连字符。

候选行按“源码片段 + 条件连字符”完整重新塑形，真实字形宽度和有限间距容量一起参与评分。primitive 的孤立连字符宽度只是实测 item 输入，生产评分采用完整候选的上下文宽度。缓存键包含源码范围及后缀，选中行再次塑形并核对度量。

生成连字符在快照中使用行末零长度 GlyphClusterLayout.Source 锚点，并保存 GeneratedText；普通源码簇该字段为空。行、run、block 和 SourceTextSnapshot 不扩大或伪造源码字符。若字体融合原文和后缀形成跨界簇，该候选被拒绝。快照仅允许一个行末生成连字符，间距分配保留标记，连字符不参加字间扩张或标点压缩。

字体仍由独立存活绑定负责，绘制只读取生产快照。会话释放后，已选布局仍可绘制。此契约为后续选择/复制提供依据，本步未实现光标、选择、复制或编辑事务。

## 非整数原点修正

非二进制整齐的列宽暴露了 double 文档原点与 float 平移逐段相加的消去误差。GlyphPaintCoordinates 先转换共享行锚点，再加入各 run 的局部基线；绘制不使用源塑形对象的旧坐标。

独立 DirectWrite 像素参照放在实际 float 平移后的原点，原点计算不调用生产坐标 helper，也不以生产字形回放制造参照。新增任意小数原点的英语和重音/双向文字回归，三组 DPI 检查全为零字节差异。步骤 8 的整簇绘制分段及 float 局部 pen 均保留。

## 自动验收

Debug、Release 和从仓库外直接运行测试 DLL 各 425 项通过，无失败、无跳过。本步新增 82 个注册测试结果，保留此前 343 个。包括 900 组随机模式问题与独立逐模式覆盖对照，以及 flagged 路径和既有独立穷举断行 oracle 的核对。

| 样张 | 组数 | 行数 | 像素比较 |
| --- | ---: | ---: | ---: |
| 英文及条件后缀 | 17 | 66 | 198 |
| 步骤 8 中文回归 | 17 | 34 | 102 |
| 步骤 7 Legacy 回归 | 16 | 44 | 132 |
| 基础塑形/快照回归 | 8 | 8 | 24 |
| 合计 | 58 | 152 | 456 |

每行比较 96 DPI × 1、144 DPI × 1.25、192 DPI × 0.75，456 次均为零字节差异。英文 49 个非末行对齐逻辑右边界，19 行断词；OFF/ON、源码偏移 7、上下文字体、ASCII 后缀、连字、CJK/重音/RTL、排除及窄宽拒绝均通过。

独立脚本 tests/Verify-Step09Snapshot.ps1 重新检查真实字形和、全字形簇覆盖、字体索引、连续源码、只略去断行空格、生成簇行末锚点、有限间距及零像素差异。英文最大真实字形误差为 3.176064637955278e-7 DIP，实际原生绘制跨度误差为 6.135276271379553e-5 DIP，均小于 0.01 DIP。中文及 Legacy 独立快照回归也通过。

    dotnet build MDEditor/MDEditor.slnx --no-restore -p:Platform=x64 -c Debug
    dotnet build tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj --no-restore -c Debug
    dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Debug --no-build --no-restore --minimum-expected-tests 425
    ./tests/Verify-Step09Snapshot.ps1 -ReportPath <英文报告.json> -LayoutsPath <英文布局快照.json>

包身份未改变，应用数据保留。最终安装位置为 MDEditor/obj/step-09-deploy-r4；启动 2026-09-19T00:08:58.3098327+08:00，报告 Timestamp 为 2026-09-19T00:09:16.3314962+08:00，共同 runId 为 20260919-000916-331-a02ecc6eafc84207803cb49ac03d7cba。Core、Typesetting、Native、应用 DLL 与验收构建 SHA-256 全部一致。

r1 的空源码锚点墨迹度量诊断退出及 r3 的非整数换算差异已修复，不计入最终通过结果。最终启动后的资源错误及相关 Application 1000/1026 为 0。已有 CA1416 最低 Windows 版本提示和缺少 symbols-package 工具的警告保留，未改变最低系统版本。

## 人工与退出验收

用户反馈“全部正常”：英文三列、断词、OFF/ON 及混排样张，状态通过且绘制计数大于零；调整窗口、最大化/还原、最小化恢复不黑屏；三个金刚键、Snap 悬停、失焦首次点击、标题栏拖动及关闭正常。关闭后 MDEditor 和测试进程为 0。未修改三键 TitleBar / NonClient / MainWindow 逻辑。

## 范围边界

仅支持所声明的 ASCII 美式英语模式；英式及其他语言、重音英语词、Unicode 表现形式连字和超过 128 字符的词不自动断词。模式不保证未知专有名词的正确分词，后续需文档策略和例外表管理。排除范围已实现，但没有 Markdown 解析器，token 防护不等于完整代码语义排除。

源文 U+00AD 软连字符、控制字符和段落分隔符仍被拒绝；手动 discretionary 与多段落输入另行实现。有限 Refined 间距可能无合法路径，明确返回 NoFeasibleBreaks，不改字体、强拆簇或突破上限。保留 248.125 DIP 等无解专项，样张采用合法列宽；正文应急排版策略尚未实施。

窗口缩放仍为固定样张适配，不是正文重排；任意宽度、编辑缩放和 DPI 重新布局属于步骤 10。完整 Markdown、单栏编辑、TSF/输入法、TeX 数学、光学行尾悬挂和长文增量性能尚未实施。
