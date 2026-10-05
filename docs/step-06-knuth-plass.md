# 步骤 6：纯算法 Knuth–Plass 断行

本步骤实现纯 .NET 的 Box / Glue / Penalty、全段最优断行路径和代价计算，位于 `MDEditor.Typesetting/LineBreaking`。验收标准是小规模结果与独立穷举最优解一致，并正确处理强制断行、禁断及无可行解。没有接入真实字形测量或画布，不改变步骤 5 的窗口样张，不提前实现步骤 7。

## 输入契约

`LineBreakItem` 是只读值类型，由工厂创建：

- `Box(width)`：不可拆分、不可伸缩的已测量对象。
- `Glue(width, stretch, shrink)`：有限的自然宽度与伸缩容量；均非负，shrink 不超过 width，避免收缩成负宽度。
- `Penalty(width, value, flagged)`：合法断点及其代价；width 只在选择该断点时加入前一行，不断开时为零。flagged 可表示断词类断点，但本步不生成英文断词位置。
- penalty <= -10000 为强制断点，>= 10000 为禁断，包含 int.MinValue / int.MaxValue 边界。普通负惩罚允许产生负的局部/总成本，不截成零。

输入宽度是抽象逻辑单位，算法不涉及 XAML、字体或屏幕 DPI；之后接入真实塑形时再以 DIP 度量填入。item 索引不是 UTF-16 索引或字形簇边界；适配层必须保留自己的源码/字形映射，不得按本步的 item 序号直接定位原文。

`LineWidthProfile` 接受正、有限的逐行宽度；零基索引，超过最后一项后重复最后宽度。单一行宽使用简便重载。`LineBreakOptions` 只读，可配置最大伸长比例、基础行惩罚、相邻松紧跳变、连续 flagged 与倒数第二行 flagged 的附加代价，以及段末行自然长度/两端对齐策略。

所有输入集合均复制，包括外部数组包装的 ImmutableArray；返回的输入和路径不借用调用者可变数组。非法值抛参数异常，有限数累加或成本运算超出 double 范围抛 OverflowException，不伪装为排版无解。整段 box/glue 的总 width/stretch/shrink 必须能以有限 double 表示；本步不处理负宽 kern 或无限阶 glue。

## 断点、空白和段尾

Glue 只有紧邻 Box 之后才能断开；因此 Box 后插入禁断 Penalty，再接 Glue，可防止该处断行。选中的 Glue 不计入行宽和伸缩容量；断点后的普通 Glue / Penalty 丢弃到下一个 Box，但遇到强制 Penalty 必须停止，不能把强制断点一并丢弃。

强制断点清除所有可绕过它的状态。若到该断点的行无法满足宽度约束，则整段无解；强制不等于允许过宽或任意拉伸。非末行（包括内部强制行）仍按有限 glue 两端对齐；真正显式换行的自然长度政策由后续适配步骤明确处理，本步不冒充已完成该功能。

段末自动补一个虚拟强制终点，索引为 items.Length；若已有末尾强制 Penalty，则直接用它，不增加虚假的末行。段尾未选中的 Glue / Penalty 从自然宽度中移除；没有后续内容或强制控制的软断点不额外制造空末行。

默认末行 RaggedRight：能自然放下时比例为 0，不拉大可见空格；自然宽度超出行宽时仍需合法收缩。也可选择 Justified，要求末行同样具备足够的伸缩容量。空输入返回成功、零行、零代价；显式控制可表达空行，但内部空强制行也受上述有限宽度约束。

## 全段代价与动态规划

模型核对了 [Knuth–Plass 原论文的 Box / Glue / Penalty 与段落级优化定义](https://onlinelibrary.wiley.com/doi/10.1002/spe.4380111102)，以及 [Knuth 的 TeX 原始程序 try_break / demerits](https://github.com/TeX-Live/texlive-source/blob/trunk/texk/web2c/tex.web)。这里使用连续 double 比例，不宣称与 TeX 的整数近似、宏参数或紧急多轮回退逐位兼容。

令自然宽度 W、目标宽度 L：

```text
伸长时 r = (L - W) / 总 stretch
收缩时 r = (L - W) / 总 shrink
可行范围：-1 <= r <= MaximumStretchRatio
badness = min(10000, 100 * |r|^3)

基础行代价：(LinePenalty + badness)^2
普通正 penalty：加 penalty^2
普通负 penalty：减 penalty^2
强制 penalty：不加减平方项
```

另加相邻 fitness 跳变、连续 flagged 和段尾前一次 flagged 的配置项；段尾只收取 FinalFlaggedDemerits，不再重复收取 ConsecutiveFlaggedDemerits。初始化前一行 fitness 为 Decent。

四类 fitness：Tight（r < -0.5）、Decent（-0.5 <= r <= 0.5）、Loose（0.5 < r <= 1）、VeryLoose（r > 1）。相邻差超过一级时计跳变成本。

同一终点保留不同“下一行宽档位 × fitness”的最优状态；该终点的 flagged 已确定。只有未来行宽、跳变和 flagged 成本相同的状态才合并，不把每个终点粗暴压成一个最便宜状态。行宽进入重复尾段后才可合并该档位的不同累计行数；不提供指定最终行数或 looseness 目标，基础行惩罚仍参与每行成本。

候选按索引和稳定状态顺序遍历，精确成本相同时保留首次路径，不用 epsilon 删除竞争状态。路径按断点前驱回溯。搜索是有向无环图，普通负惩罚不会导致无限循环，也不能据此采用只适用于非负成本的贪心剪枝。

每个起点增量累加自己的度量，不以巨大前缀和相减求小行宽，避免例如 1e20 宽对象后的 5 单位行宽丢失。保存最后 Box 的度量以便段末去掉尾部空白。候选数 B、item 数 N、宽度档数 P 下，当前参考实现约 O(NB + B²P)，保留状态约 O(N + BP)；尚未实施长文档调度或激进性能裁剪。CancellationToken 在枚举和搜索中检查，取消不返回部分结果。

## 输出及使用

`LineBreakResult` 显式返回 Success 或 NoFeasibleBreaks；无解时路径为空、TotalDemerits=null，不以 Infinity 混入 JSON。成功包含不可变 item 副本、逐行路径、自然/实际宽度、比例、badness、fitness、break width、强制/flagged 标记、终点和分项成本。item 内容范围为 [StartItemIndex, EndItemIndex)，BreakItemIndex 指选中的 glue/penalty 或虚拟终点，NextItemIndex 指丢弃空白后的下一起点。

```csharp
using MDEditor.Typesetting.LineBreaking;

var items = new List<LineBreakItem>();
foreach (var wordWidth in new double[] { 10, 9, 8, 7, 10, 5, 8 })
{
    if (items.Count != 0) items.Add(LineBreakItem.Glue(3, 4, 2));
    items.Add(LineBreakItem.Box(wordWidth));
}
var result = KnuthPlassLineBreaker.Break(items, lineWidth: 33);
if (!result.IsSuccess)
{
    // Caller decides an explicit overflow/retry policy; the core does not silently force a path.
    return;
}
```

该固定样例的测试结果：

| 行 | 断点 item 索引 | 自然宽度 | r | 实际宽度 |
| --- | --- | --- | --- | --- |
| 1 | 5 | 33 | 0 | 33 |
| 2 | 11 | 28 | 0.625 | 33 |
| 3（段末） | 13（虚拟） | 8 | 0 | 8 |

最优代价 1384.3276977539062；逐行选择最远可行断点的路径 [5, 13] 代价为 12200，证明本样例不等同于贪心换行。该结果来自抽象已测量 box，不宣称已在真实文本上实现两端对齐。

## 2026-09-18 验收

- Typesetting 与普通核心测试 Debug 编译：0 错误、0 警告。
- 全解决方案 x64 Debug 编译：0 错误；保留原有 AppInfo.Current 的 CA1416 警告。
- Debug / Release 普通 .NET 测试各 223/223 通过，0 失败、0 跳过；之前 151 项回归保留，本步新增 72 用例（44 方法，含 DataRow）。
- 从 C:/ 仓库外直接运行 Debug 测试 DLL：223/223 通过，不依赖 WinUI 或开发包身份。
- 独立穷举器逐条枚举所有合法路径，直接累计区间度量，另行计算 ratio/fitness/代价，不调用生产搜索、候选判断或度量/成本辅助函数。没有合并或代价剪枝。
- 8 个种子 × 400 个随机小问题 = 3200；覆盖变行宽、正负/强制/禁断 penalty、flagged、末行策略、非断行 glue 与连续控制。
- 3^5 个五词宽度组合 × 8 行宽 = 1944；以上合计 5144 个问题全部与穷举最优代价一致，所选路径也属于独立合法最优路径。另验连续控制及重复行宽档等边界。
- 核对逐行自然宽度、伸缩容量、比例、实际宽度、fitness、条件宽度、标记、索引与局部成本，而不只比较总成本。普通指标允许 double 运算顺序误差，不以容差剪枝生产状态。
- 验证深复制、四种 culture 确定性、超宽无解、度量/代价溢出、极小容量、取消请求，以及 250 词有限段落完整路径。

复现命令：

```powershell
dotnet build MDEditor/MDEditor.slnx --no-restore -p:Platform=x64 -c Debug
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Debug --no-build --no-restore --minimum-expected-tests 223

dotnet build tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj --no-restore -c Release
dotnet test --project tests/MDEditor.Core.Tests/MDEditor.Core.Tests.csproj -c Release --no-build --no-restore --minimum-expected-tests 223
```

本轮 dotnet test 在正常执行权限环境运行，避免步骤 5 中受限命名管道导致的测试宿主错误。无需主程序以管理员运行。没有部署、启动、自动操作窗口或要求重复人工按钮验收；也没有删除应用数据或改动 MainWindow.TitleBar.cs / MainWindow.NonClient.cs。

步骤 6 自动验收完成。步骤 7 的真实字形/源码映射、行边界塑形校验、不拆字形簇和画布两端对齐仍未实现，等用户下一步授权。
