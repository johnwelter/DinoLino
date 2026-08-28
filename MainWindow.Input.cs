using DinoLino.DataTypes;
using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace DinoLino
{
    /// Routes raw keyboard and mouse input to workspace actions and the active work
    /// mode.
    public partial class MainWindow
    {
        // ---- Pan state ----

        private bool _isPanning = false;
        private Point _panStartMouse; // Mouse position in workspace coordinates when the drag started.
        private double _panStartImageTx, _panStartImageTy;

        // ---- Outline brush state ----

        // True between the first and last frame of a brush stroke.
        private bool _outlineBrushActive;

        // ---- Keyboard input ----

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // Specimen navigation: plain Up/Down only.
            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (e.Key == Key.Up)
                {
                    SpecimenCount_Up(UI_SpecimenUp, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Down)
                {
                    SpecimenCount_Down(UI_SpecimenDown, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
            }

            if ((e.Key == Key.Left || e.Key == Key.Right)
                && Keyboard.Modifiers == ModifierKeys.None
                && UI_WorkCanvas.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                return;
            }

            // Space presses and holds the left mouse button, so the workspace can be
            // clicked and dragged without a mouse.
            if (e.Key == Key.Space
                && Keyboard.Modifiers == ModifierKeys.None
                && UI_WorkCanvas.IsKeyboardFocusWithin)
            {
                if (!e.IsRepeat) PressKeyboardClick();
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                ClearAllOperations();
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                ClearAllOperations();
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                Menu_OpenImage(this, new RoutedEventArgs());
                e.Handled = true;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.H)
            {
                Menu_SeeHistory(this, new RoutedEventArgs());
                e.Handled = true;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E)
            {
                Menu_ExportHistory(this, new RoutedEventArgs());
                e.Handled = true;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                Menu_Screenshot(this, new RoutedEventArgs());
                e.Handled = true;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                Menu_Undo(this, new RoutedEventArgs());
                e.Handled = true;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
            {
                Menu_Redo(this, new RoutedEventArgs());
                e.Handled = true;
            }

            // Finish an in-progress spline when Enter is pressed.
            if (e.Key == Key.Enter && CurrentWorkMode is CurvatureMode cm && cm.CanFinalizeSpline)
            {
                // Remove the preview element queued by the last click before committing the final spline.
                RemovePendingElements();

                foreach (UIElement element in cm.FinalizeSpline())
                    AddElementToWorkSpace(element);

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (_scaleMode)
                {
                    // Ends the capture, removes the dashed line and cue dot, and
                    // restores the workspace cursor.
                    CancelScaleCapture();
                    e.Handled = true;
                    return;
                }

                // Cancel the active probe interaction without changing the underlying specimen data.
                if (CurrentWorkMode is CurvatureMode probeCm && probeCm.FindTurningAngleMode)
                {
                    probeCm.FindTurningAngleMode = false;
                    e.Handled = true;
                    return;
                }

                CurrentWorkMode?.CancelCurrentOperation();
                e.Handled = true;
                return;
            }
        }

        // ---- Keyboard cursor movement ----

        // How far one arrow keypress moves the pointer, in screen pixels.
        private const double CursorNudgeStep = 1;
        private const double CursorNudgeStepCoarse = 10;

        // Holding an arrow accelerates up to this multiple of the step, so the far
        // side of the screen is reachable without hundreds of presses.
        private const double CursorNudgeMaxAcceleration = 14;

        private Key _heldArrow = Key.None;
        private int _arrowRepeatCount;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        // The delegate is held in a field because the hook keeps an unmanaged
        // pointer to it, which the garbage collector cannot see.
        private LowLevelKeyboardProc _keyboardHookProc;
        private IntPtr _keyboardHook = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);

        /// Installs a system-wide keyboard hook, which is what lets the arrow keys
        /// reach the pointer even while a dialog owns the keyboard. Safe to call
        /// repeatedly: Windows drops the hook if the UI thread ever stalls too long,
        /// so it is reinstalled whenever the window is activated.
        private void InstallKeyboardHook()
        {
            if (_keyboardHook != IntPtr.Zero) return;

            _keyboardHookProc = KeyboardHookCallback;
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc, GetModuleHandle(null), 0);
        }

        private void RemoveKeyboardHook()
        {
            if (_keyboardHook == IntPtr.Zero) return;

            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            Key key = KeyInterop.KeyFromVirtualKey(Marshal.ReadInt32(lParam));
            bool isArrow = key == Key.Left || key == Key.Right || key == Key.Up || key == Key.Down;

            // The hook sees every key on the machine, so anything that is not an
            // arrow, and everything typed into other programs, passes straight on.
            if (!isArrow || !IsThisApplicationInForeground())
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            int message = wParam.ToInt32();
            bool isKeyUp = message == WM_KEYUP || message == WM_SYSKEYUP;
            bool coarse = IsKeyDown(VK_SHIFT);

            // Ctrl + arrow moves the pointer one pixel and Shift + arrow moves it in
            // large steps.
            bool movesCursor = !IsKeyDown(VK_MENU)
                && (IsKeyDown(VK_CONTROL) || (coarse && !(Keyboard.FocusedElement is TextBox)));

            if (!movesCursor)
            {
                // Letting go of the modifier before the arrow still ends the movement.
                if (isKeyUp && key == _heldArrow) ClearHeldArrow();
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            if (isKeyUp)
            {
                if (key != _heldArrow) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

                ClearHeldArrow();
                return (IntPtr)1;
            }

            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
            {
                NudgeCursor(key, coarse);
                return (IntPtr)1;   // Swallowed, so no control receives the arrow key.
            }

            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private void ClearHeldArrow()
        {
            _heldArrow = Key.None;
            _arrowRepeatCount = 0;
        }

        /// Moves the pointer in raw screen coordinates. Windows clips the result to
        /// the desktop, so every monitor is reachable and no bounds check is needed.
        private void NudgeCursor(Key key, bool coarse)
        {
            if (key == _heldArrow) _arrowRepeatCount++;
            else
            {
                _heldArrow = key;
                _arrowRepeatCount = 0;
            }

            double step = coarse ? CursorNudgeStepCoarse : CursorNudgeStep;
            step *= Math.Min(1 + _arrowRepeatCount / 4.0, CursorNudgeMaxAcceleration);

            int dx = key == Key.Left ? -1 : key == Key.Right ? 1 : 0;
            int dy = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;

            if (!GetCursorPos(out POINT p)) return;

            SetCursorPos(
                p.X + (int)Math.Round(dx * step),
                p.Y + (int)Math.Round(dy * step));
        }

        private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        private static bool IsThisApplicationInForeground()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            GetWindowThreadProcessId(foreground, out uint processId);
            return processId == GetCurrentProcessId();
        }

        // ---- Keyboard clicking ----

        // True while the spacebar is holding the left mouse button down.
        private bool _keyboardClickHeld;

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        private void MainWindow_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space) return;

            // Released whatever the focus or modifiers are now: whatever pressed the
            // button has to be able to let go of it.
            if (ReleaseKeyboardClick()) e.Handled = true;
        }

        /// Presses the real left button at the pointer's current position, so the
        /// click reaches every mode exactly as a mouse click would.
        private void PressKeyboardClick()
        {
            if (_keyboardClickHeld) return;

            _keyboardClickHeld = true;
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        }

        private bool ReleaseKeyboardClick()
        {
            if (!_keyboardClickHeld) return false;

            _keyboardClickHeld = false;
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            return true;
        }

        // ---- Workspace clicks ----

        private void WorkSpace_Click(object sender, MouseButtonEventArgs e)
        {
            // Ctrl + left drag starts image panning instead of a drawing action.
            if (e.ChangedButton == MouseButton.Left &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                _isPanning = true;
                _panStartMouse = e.GetPosition(UI_WorkSpace);

                var tt = UI_WorkImage.GetTranslateTransform();
                _panStartImageTx = tt.X;
                _panStartImageTy = tt.Y;

                (sender as UIElement)?.CaptureMouse();  // Keep receiving drag events even if the pointer leaves the canvas.
                Mouse.OverrideCursor = Cursors.SizeAll;  // Show pan cursor feedback.
                e.Handled = true;
                return;
            }

            if (_scaleMode)
            {
                HandleScaleClick(new Vector2(Mouse.GetPosition(UI_WorkCanvas)));
                return;
            }

            if (AlignCaptureClick(new Vector2(Mouse.GetPosition(UI_WorkCanvas)))) return;

            Vector2 mousePos = new Vector2(Mouse.GetPosition(UI_WorkCanvas));

            if (CurrentWorkMode is OutlineMode)
                SyncOutlineImageTransform();

            // Begin a freehand stroke in outline mode.
            if (CurrentWorkMode is OutlineMode handOm && handOm.HandDrawMode &&
                e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
            {
                if (!handOm.SeePreviousOperations && handOm.IsStartingNewOperation)
                    ClearWorkspaceVisualsOnly();

                handOm.HandDraw.BeginStroke(mousePos);
                (sender as UIElement)?.CaptureMouse();  // Keep receiving movement while the stroke is active.
                e.Handled = true;
                return;
            }

            // Skip clearing the workspace for probe interactions or other non-destructive actions.
            bool probing = CurrentWorkMode.IsProbeInteraction
                || (CurrentWorkMode is CurvatureMode probeGuardCm && probeGuardCm.FindTurningAngleMode)
                || (CurrentWorkMode is OutlineMode probeGuardOm
                    && (probeGuardOm.EraseOutlineMode || probeGuardOm.SmoothOutlineMode
                        || probeGuardOm.OutlineMetadataMode));

            if (!CurrentWorkMode.SeePreviousOperations
                && CurrentWorkMode.IsStartingNewOperation
                && !probing)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[workspace] clearing visuals on click (seePrev={CurrentWorkMode.SeePreviousOperations}, " +
                    $"starting={CurrentWorkMode.IsStartingNewOperation}, probe={probing}, mode={CurrentWorkMode.GetType().Name})");

                // Preserve the cursor overlay while removing old workspace drawings.
                ClearWorkspaceVisualsOnly();
            }

            RemovePendingElements();

            foreach (UIElement element in CurrentWorkMode.ProcessClick(mousePos))
                AddElementToWorkSpace(element);
        }

        // ---- Workspace drag ----

        private void WorkSpace_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                Point now = e.GetPosition(UI_WorkSpace);
                var it = UI_WorkImage.GetTranslateTransform();
                it.X = _panStartImageTx + (now.X - _panStartMouse.X);
                it.Y = _panStartImageTy + (now.Y - _panStartMouse.Y);

                // Keep the border and overlay layers aligned with the image while panning.
                UI_WorkBorder.CopyTransforms(UI_WorkImage);
                return;
            }

            if (_scaleMode)
            {
                Vector2 p = new Vector2(Mouse.GetPosition(UI_WorkCanvas));

                // While the second click is pending, the dashed line follows the cursor.
                if (_scaleClicks == 1 && _scaleLine != null)
                {
                    _scaleLine.X2 = p.X;
                    _scaleLine.Y2 = p.Y;
                }

                // Keep the yellow cue dot glued to the cursor so the user can see
                // that scale capture is active.
                MoveScaleCue(p);
                UI_DotCursor.SetPosition(p.X - 5, p.Y - 5);
                return;
            }

            if (AlignCaptureMove(new Vector2(Mouse.GetPosition(UI_WorkCanvas)))) return;

            Vector2 mousePos = new Vector2(Mouse.GetPosition(UI_WorkCanvas));
            Vector2 centeredCursorPos = CurrentWorkMode.ProcessMouseMovement(mousePos) - new Vector2(5, 5);
            UI_DotCursor.SetPosition(centeredCursorPos.X, centeredCursorPos.Y);

            if (CurrentWorkMode is OutlineMode om)
                RouteOutlineBrush(om, mousePos, e.LeftButton == MouseButtonState.Pressed);
            else
                _outlineBrushActive = false;
        }

        /// Applies whichever outline brush is armed. Erase, push, and local smooth
        /// respond to a held left button or to Alt; hand-draw requires the button,
        /// since its strokes have explicit begin and end points.
        private void RouteOutlineBrush(OutlineMode om, Vector2 mousePos, bool buttonHeld)
        {
            bool brushing = buttonHeld
                || (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

            // OutlineMode's canvas-to-image transform is refreshed on click. A stroke
            // driven by Alt alone has no click, so refresh it as the stroke begins.
            if (brushing && !_outlineBrushActive)
                SyncOutlineImageTransform();
            _outlineBrushActive = brushing;

            if (!brushing) return;

            if (om.EraseOutlineMode)
                om.Erase.ProcessDrag(mousePos);
            else if (om.PushOutlineMode)
                om.Push.ProcessDrag(mousePos);
            else if (om.SmoothOutlineMode && om.Smooth.IsLocalScope)
                om.Smooth.ProcessLocalDrag(mousePos);
            else if (buttonHeld && om.HandDrawMode)
                om.HandDraw.ProcessDrag(mousePos);
        }

        private void WorkSpace_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning && e.ChangedButton == MouseButton.Left)
            {
                _isPanning = false;
                (sender as UIElement)?.ReleaseMouseCapture();
                Mouse.OverrideCursor = null;
                e.Handled = true;
                return;
            }

            // Pause the freehand stroke; the stroke remains resumable until the mode ends it.
            if (e.ChangedButton == MouseButton.Left && CurrentWorkMode is OutlineMode om && om.HandDrawMode)
            {
                (sender as UIElement)?.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void WorkSpace_LostCapture(object sender, MouseEventArgs e)
        {
            _isPanning = false;
            _outlineBrushActive = false;
            Mouse.OverrideCursor = null;
        }

        // ---- Workspace zoom ----

        private void WorkSpace_ScrollZoom(object sender, MouseWheelEventArgs e)
        {
            UpdateWorkSpaceZoom(e.Delta, e.GetPosition(UI_WorkImage));
        }

        // ---- Horizontal scrolling ----

        // WPF raises no event for WM_MOUSEHWHEEL, the message a two-finger
        // sideways trackpad gesture (or a tilt wheel) sends, so it is caught on
        // the window handle instead.
        private const int WM_MOUSEHWHEEL = 0x020E;

        // One notch moves this many device-independent pixels sideways.
        private const double HorizontalScrollPixelsPerNotch = 48;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // The window has a handle only from here on, which is what the hook
            // needs, so this cannot move into the constructor.
            AttachHorizontalWheel(this);

            InstallKeyboardHook();
            Activated += (s, args) => InstallKeyboardHook();
        }

        protected override void OnClosed(EventArgs e)
        {
            RemoveKeyboardHook();
            base.OnClosed(e);
        }

        /// Gives one window sideways scrolling: two-finger trackpad gestures and
        /// tilt wheels, plus Shift+wheel for mice that have neither. The messages
        /// go to the window under the cursor, so every window that wants the
        /// gesture hooks itself; secondary windows call this on their own.
        public static void AttachHorizontalWheel(Window window)
        {
            if (window == null) return;

            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.AddHook(HorizontalWheelHook);
            else
                window.SourceInitialized += (s, e) =>
                {
                    if (PresentationSource.FromVisual(window) is HwndSource late)
                        late.AddHook(HorizontalWheelHook);
                };

            // Tunnels from the window, so it is seen before any child's own wheel
            // handler; a gesture over anything that cannot scroll sideways falls
            // through to that untouched.
            window.PreviewMouseWheel += ShiftWheelScroll;
        }

        private static IntPtr HorizontalWheelHook(
            IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_MOUSEHWHEEL) return IntPtr.Zero;

            // The delta is the signed high word of wParam; positive means rightward.
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);

            if (ScrollHorizontallyUnderCursor(delta / 120.0 * HorizontalScrollPixelsPerNotch))
                handled = true;

            return IntPtr.Zero;
        }

        /// Shift+wheel scrolls sideways too, which covers mice with no tilt wheel.
        private static void ShiftWheelScroll(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift) return;

            // Wheel-up scrolls left, matching the usual convention.
            if (ScrollHorizontallyUnderCursor(-e.Delta / 120.0 * HorizontalScrollPixelsPerNotch))
                e.Handled = true;
        }

        // Applies the offset to the nearest horizontally scrollable ancestor of
        // whatever the pointer is over. False when nothing can scroll, which
        // leaves the gesture to whoever else wants it.
        private static bool ScrollHorizontallyUnderCursor(double offset)
        {
            var target = FindHorizontalScrollViewer(Mouse.DirectlyOver as DependencyObject);
            if (target == null) return false;

            target.ScrollToHorizontalOffset(target.HorizontalOffset + offset);
            return true;
        }

        private static ScrollViewer FindHorizontalScrollViewer(DependencyObject start)
        {
            var node = start;

            while (node != null)
            {
                // A viewer with nothing to scroll sideways is skipped rather than
                // swallowing the gesture, so an outer one can still take it.
                if (node is ScrollViewer viewer && viewer.ScrollableWidth > 0)
                    return viewer;

                // Mouse.DirectlyOver can land on a content element (a Run inside a
                // TextBlock, say), which has no visual parent.
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return null;
        }
    }
}