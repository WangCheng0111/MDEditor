# 无障碍文本与选区接口

原生编辑区公开 UI Automation Document，名称“Markdown 编辑器”、ID `MarkdownDocument`，实现 Text、Text2 和 Value 模式。

系统可以取得完整文本、选区、光标、可见范围和位置，执行查找、范围移动、设置选区、滚入视口及文本修改。坐标指向原始 Markdown；Unicode 字符移动不会切断组合重音、emoji、ZWJ 或 CRLF。

- 折叠只影响视觉，接口保留 Markdown 标记、TeX 和换行。
- 外部文本修改走正常编辑事务，可撤销/重做，并更新保存和恢复状态。
- 输入法组合期间拒绝外部改写或重新定位选区。
- 隐藏 TSF 桥接框转发到真正的文档，不公开临时输入缓冲。
- 文本、选区和焦点变化分别通知；光标闪烁不产生重复文本事件。
- 过期布局不报告旧坐标；普通编辑后范围重定位，打开新文档后旧范围失效。

可自行开启 Windows 讲述人检查正文与方向键、选区朗读。当前提供源码文本语义，尚不提供 TeX 数学自然语言朗读、表格 Grid 子树或图片子元素，也不声称达到完整无障碍认证。

## WinUI 桥接

GetBoundingRectangles 返回窗口内物理像素，由 WinUI 原生桥接补屏幕原点；RangeFromPoint 使用桥接已转换的窗口内物理像素。仅使用 XamlRoot.RasterizationScale，不能重复叠加 Canvas DPI 或 HWND 原点。

不支持的属性返回 E_NOT_SUPPORTED；空范围矩形为空。RangeFromPoint 返回最近的有效位置，不放宽普通鼠标对表格的点击边界。
