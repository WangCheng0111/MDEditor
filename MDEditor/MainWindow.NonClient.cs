using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace MDEditor
{
    // VSCode/Chromium 式做法：直接接管窗口过程处理金刚键的非客户区消息。
    // 金刚键区域的 WM_NCHITTEST 由 WASDK 的 SetRegionRects 区域机制产生
    // （HTMINBUTTON/HTMAXBUTTON/HTCLOSE），系统据此提供 Snap 悬停菜单等
    // 原生行为；按钮的悬停/按下/释放由 WM_NCMOUSE*/WM_NCLBUTTON* 直接
    // 驱动，失焦首击经 WM_MOUSEACTIVATE 返回 MA_ACTIVATE 保证"激活 +
    // 执行"一次完成。
    public sealed partial class MainWindow : Window
    {
        private const int HtMinButton = 8;
        private const int HtMaxButton = 9;
        private const int HtClose = 20;

        private const uint WmMouseActivate = 0x0021;
        private const uint WmNcLButtonDown = 0x00A1;
        private const uint WmNcLButtonUp = 0x00A2;
        private const uint WmNcMouseMove = 0x00A0;
        private const uint WmNcMouseLeave = 0x02A2;

        private const nint MaActivate = 1;

        private SubclassProc? _captionSubclassProc;

        private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

        [DllImport("comctl32.dll")]
        private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

        private const int DwmwaExtendedFrameBounds = 9;

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        private DispatcherQueueTimer? _hoverCheckTimer;

        private void InitializeWindowSubclass()
        {
            _captionSubclassProc = CaptionSubclassProc;
            var hWnd = WindowNative.GetWindowHandle(this);
            SetWindowSubclass(hWnd, _captionSubclassProc, 1, 0);

            _hoverCheckTimer = DispatcherQueue.CreateTimer();
            _hoverCheckTimer.Interval = TimeSpan.FromMilliseconds(100);
            _hoverCheckTimer.IsRepeating = true;
            _hoverCheckTimer.Tick += HoverCheckTimer_Tick;
        }

        // WM_NCMOUSELEAVE 依赖系统的非客户区跟踪，在 ExtendsContentIntoTitleBar
        // 加自定义命中测试下不可靠：光标从金刚键直接移到 Snap 面板/其他窗口时
        // 收不到任何消息，悬停色会卡死。这里用低频定时器轮询光标位置兜底：
        // 光标不在悬停按钮矩形内即过渡回 normal。
        private void HoverCheckTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (_hoveredButton == null)
            {
                _hoverCheckTimer?.Stop();
                return;
            }

            if (IsCursorOverCaptionButton(_hoveredButton))
            {
                return;
            }

            ApplyButtonVisual(_hoveredButton, StateNormal, AnimationDuration);
            _hoveredButton = null;
            _hoverCheckTimer?.Stop();
        }

        private bool IsCursorOverCaptionButton(Button button)
        {
            var point = GetCursorWindowPoint();
            if (point is null)
            {
                // 取不到光标位置时保守处理，避免误清悬停
                return true;
            }

            return ContainsPoint(GetCaptionButtonRect(button), point.Value);
        }

        private Point? GetCursorWindowPoint()
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            if (!TryGetVisibleWindowRect(hWnd, out var windowRect) || !GetCursorPos(out var cursor))
            {
                return null;
            }

            return new Point(cursor.X - windowRect.Left, cursor.Y - windowRect.Top);
        }

        private static bool TryGetVisibleWindowRect(nint hWnd, out RECT rect)
        {
            // DWM 合成下 GetWindowRect 可能包含阴影边距，优先取不含阴影的
            // 可见框架矩形（DWMWA_EXTENDED_FRAME_BOUNDS），失败时回退 GetWindowRect。
            if (DwmGetWindowAttribute(hWnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<RECT>()) == 0)
            {
                return true;
            }

            return GetWindowRect(hWnd, out rect);
        }

        private RectInt32 GetCaptionButtonRect(Button button)
        {
            if (button == MinimizeButton) return _minimizeRect;
            if (button == MaximizeButton) return _maximizeRect;
            return _closeRect;
        }

        private nint CaptionSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
        {
            switch (msg)
            {
                case WmMouseActivate:
                    {
                        // 命中金刚键时强制"激活但不吞消息"，保证失焦首击的
                        // WM_NCLBUTTONDOWN/UP 正常派发。命中时直接返回，
                        // 不让 WASDK 参与该查询消息（其内部可能为吞击做准备）。
                        var hit = (int)(long)lParam & 0xFFFF;
                        if (hit is HtMinButton or HtMaxButton or HtClose)
                        {
                            return MaActivate;
                        }
                    }
                    break;

                case WmNcLButtonDown:
                    if (HandleCaptionButtonDown((int)wParam))
                    {
                        return 0;
                    }
                    break;

                case WmNcLButtonUp:
                    if (HandleCaptionButtonUp((int)wParam))
                    {
                        return 0;
                    }
                    break;

                case WmNcMouseMove:
                    if (HandleCaptionButtonMove((int)wParam))
                    {
                        return 0;
                    }
                    break;

                case WmNcMouseLeave:
                    HandleCaptionButtonLeave();
                    break;
            }

            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        // 金刚键区域的 hit-test 交给 WASDK 的 InputNonClientPointerSource
        // 区域机制（SetRegionRects 的 Minimize/Maximize/Close），wParam
        // 中携带的 hit-test 结果由系统调用窗口过程获得，无需自行换算坐标。

        private Button? GetButtonFromHitTest(int hitTest)
        {
            return hitTest switch
            {
                HtMinButton => MinimizeButton,
                HtMaxButton => MaximizeButton,
                HtClose => CloseButton,
                _ => null
            };
        }

        private bool HandleCaptionButtonDown(int hitTest)
        {
            var button = GetButtonFromHitTest(hitTest);
            if (button is null)
            {
                return false;
            }

            SetPressedButton(button);
            return true;
        }

        private bool HandleCaptionButtonUp(int hitTest)
        {
            var button = GetButtonFromHitTest(hitTest);
            if (button is null)
            {
                // 释放时指针已离开按钮区域（按住拖出释放）：清理按压状态，
                // 悬停恢复交给后续 WM_NCMOUSEMOVE 的悬停路径。
                _pressedButton = null;
                return false;
            }

            _pressedButton = null;

            if (button == MaximizeButton)
            {
                // 最大化/还原点击完成后背景瞬切回 normal
                ApplyButtonVisual(MaximizeButton, StateNormal, TimeSpan.Zero);
            }
            // 其余按钮松开后保持按下视觉，直到指针移动（与系统标题栏一致）

            ExecuteCaptionButton(button);
            return true;
        }

        private bool HandleCaptionButtonMove(int hitTest)
        {
            if (_pressedButton != null)
            {
                if ((GetAsyncKeyState(VkLeftButton) & 0x8000) == 0)
                {
                    // 物理按键已松开但按压状态残留（释放消息路由到其他区域的边界）：
                    // 清理并回退到悬停路径，避免按钮一悬停就误切按下色。
                    _pressedButton = null;
                }
                else
                {
                    // 按住拖动：按位置瞬切按下/正常
                    var inside = GetButtonFromHitTest(hitTest) == _pressedButton;
                    ApplyButtonVisual(_pressedButton, inside ? StatePressed : StateNormal, TimeSpan.Zero);
                    return true;
                }
            }

            var button = GetButtonFromHitTest(hitTest);
            if (_hoveredButton != button)
            {
                if (_hoveredButton != null)
                {
                    ApplyButtonVisual(_hoveredButton, StateNormal, AnimationDuration);
                }
                _hoveredButton = button;
                if (_hoveredButton != null)
                {
                    ApplyButtonVisual(_hoveredButton, StatePointerOver, AnimationDuration);
                    _hoverCheckTimer?.Start();
                }
            }

            return button != null;
        }

        private void HandleCaptionButtonLeave()
        {
            if (_hoveredButton == null)
            {
                return;
            }

            ApplyButtonVisual(_hoveredButton, StateNormal, AnimationDuration);
            _hoveredButton = null;
            _hoverCheckTimer?.Stop();
        }

        private void ExecuteCaptionButton(Button button)
        {
            if (button == MinimizeButton)
            {
                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Minimize();
                }
            }
            else if (button == MaximizeButton)
            {
                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    var hWnd = WindowNative.GetWindowHandle(this);
                    if (IsZoomed(hWnd))
                    {
                        presenter.Restore();
                    }
                    else
                    {
                        presenter.Maximize();
                    }
                }

                UpdateMaximizeButtonIcon();
            }
            else if (button == CloseButton)
            {
                Close();
            }
        }
    }
}
