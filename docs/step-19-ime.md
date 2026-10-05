# 步骤 19：中文输入法组合

正文通过随原生光标移动的 WinUI `TextBox` 接收系统文本输入和输入法候选。该控件负责桌面 TSF 上下文；正文、选区、排版和已提交的文本仍由现有 Core/Typesetting/Native 链路管理。WinUI 3 桌面窗口不使用 UWP 的 `CoreTextServicesManager.GetForCurrentView()`，因为 Windows 桌面应用不支持一般的 `GetForCurrentView()` 入口。参见 [微软桌面 WinRT API 说明](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-api-desktop-app-support)和 [WinUI TextBox 的输入法组合事件](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.textcompositionchanged?view=windows-app-sdk-1.8)。

输入法组词期间，候选文字临时写入版本化正文并触发原生重排，组合范围显示下划线。中英文都只由原生画布显示；TSF 输入控件维持固定的低透明度，不因新布局尚未完成而代显临时文字。候选框跟随已提交的当前版本光标；等待新布局时保留上一次有效位置，不使用过期正文坐标。确认候选只记录一条撤销；Esc 取消会恢复原文、选区及跨段样式锚点，之前的撤销记录仍有效。普通键盘字符由同一输入控件传入；Backspace/Delete、导航键和既有 Ctrl 快捷键仍走原编辑规则。只读数学样张不启用输入控件，仍可选择和复制。

2026-09-26 修正输入桥接：真实桌面验收发现输入控件的 `Text` 会增长，但异步 `TextChanged` 不一定到来。现在由同步的 `TextChanging` 只排队（不在其回调中改动布局），并以路由字符事件兜底；UI 队列按与上次已镜像内容的增量提交普通英文，重复事件不会再次提交。组合事件使用 `StartIndex` 排除此前输入的英文；候选结束后在低优先级 UI 队列中读取最终上屏文本，再待原生布局追上后清空暂存文本。导航、删除、剪贴板命令与鼠标换位会先冲刷待处理的普通输入。已移除诊断日志及试验性的英文浮层。

源码编译：x64 Debug 应用构建通过。人工验证已确认 CHINESE 正文中连续英文可按键实时出现，中文候选可上屏；移除英文浮层后中英文均正常，无悬浮字。后续完整步骤 19 验收仍应覆盖 ENGLISH 段落、候选方向键/数字选择、Esc 取消、跨段选区、Ctrl+Z/Ctrl+Y、缩放滚动后的候选定位、长按 Backspace/Delete、公式复制粘贴和三个金刚键。

2026-10-05 组词闪烁修正：去掉输入控件随排版版本切换 1/0.01 透明度的分支；组词预览从 Composition 事件延后到 UI 队列，开始事务内不再移动/缩放 TSF 宿主；组词期间保持其字体与视口尺寸，布局尚未追上时不重新定位候选框；停掉不显示原生光标时的闪烁计时器，结束后恢复。x64 Debug/Release 构建和各 984 项回归测试通过；候选框稳定性仍需真实中文输入法人工复测，逻辑测试不代替 TSF 验收。
