# 步骤 33：无障碍文本、选区与位置

## 已实现

- 原生编辑区公开 UI Automation `Document` 元素，名称为“Markdown 编辑器”，ID 为 `MarkdownDocument`。
- 实现 `Text`、`Text2`、`Value` 模式：完整文本、当前选区、光标范围、可见范围、坐标命中、选区矩形、文本查找、范围移动、设置选区与滚入视口。
- UTF-16 坐标始终指向原文。中文、组合重音、emoji、ZWJ 和 CRLF 不在字符移动/设置选区时被切开。视觉行与原文段落分别导航。
- 作为源码编辑器，文本接口保留 Markdown 标记、公式 TeX 与换行；折叠只改变显示，不改变可访问的文本。尚未提供数学语义朗读、表格 Grid/单元格子树或图片子元素。独立只读诊断公式页不属于可编辑文档源。
- 系统改写正文走现有编辑事务，能撤销/重做，并参与文件修改标记及崩溃恢复；输入法组合期间拒绝外部改写/重定位选区。
- 隐藏 TSF 输入框的无障碍接口转发到真正的文档，不公开临时输入缓冲区。
- 文本、选区、焦点变化分别发事件；光标单纯闪烁不发重复的文本/选区事件。没有创建无障碍 peer 时不额外构建导航或几何缓存。
- 位置使用现有交互布局、滚动和缩放；旧版本排版未提交时不报告旧坐标。范围对象跨普通编辑重新定位，打开另一份文档后旧范围失效。

## WinUI 桥接约定

`GetBoundingRectangles` 在 WinUI 托管层返回窗口内物理像素，由原生桥接补上窗口屏幕原点；`RangeFromPoint` 接收原生桥接已经转换的窗口内物理像素。只使用 `XamlRoot.RasterizationScale`，不能再使用 Canvas DPI 或再次增加 HWND 屏幕原点。

不支持的文字属性抛出 `E_NOT_SUPPORTED (0x80070032)`，WinUI 桥接转换为 UIA 的保留 NotSupported 值。空范围的矩形为空数组；`RangeFromPoint` 总是返回最近的有效范围。这不会放宽正常鼠标点击表格的边界。

实现依据：[Microsoft Text/TextRange 约定](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementingtextandtextrange)、[WinUI 原生桥接源码](https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/win/shared/UIAPatternProviderWrapper.cpp)。

## 验证

- x64 Debug、Release 主程序构建通过；不生成 MSIX。
- Debug、Release 各 964 项测试通过（新增 26 项）：原文/公式保留、Unicode 导航、范围查找/编辑重定位、视觉行、可见范围、折叠语法矩形、缩放/滚动/DPI 等。
- 真实运行窗口的系统无障碍树公开完整正文；隐藏 TSF 框没有单独的空白编辑区。Shift+Right 选中的“是”字与系统读出的选区一致。
- 人工讲述人朗读和常规交互仍需最终验收，不把构建或纯逻辑测试当作讲述人语音验收。

## 人工验收

1. 用最新源码 F5，或使用已刷新的现有开发入口。若有恢复草稿，请保留需要的内容。
2. 自行开启 Windows 讲述人，点击可编辑正文，检查能读取文字，方向键及 Shift+方向键移动/选择时朗读跟随。
3. 切换搜索框和正文，检查不把搜索文字或输入法临时字串当作文档正文；测试中文输入候选。
4. 测试跨行/跨段选区、公式源码展开/折叠、缩放、窗口重排及滚动后的位置。
5. 回归输入、撤销/重做、公式、图片、搜索与三个窗口键。

当前仍按源码文本提供可访问内容，而非把 TeX 转成数学自然语言。更丰富的对象/格式语义属于后续独立扩展，不改变本步的编辑和渲染功能。
