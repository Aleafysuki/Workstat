using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WorkTimer.Core.Diagnostics;
using WorkTimer.Core.Geometry;
using WorkTimer.Core.Timing;
using FormsScreen = System.Windows.Forms.Screen;
using FormsCursor = System.Windows.Forms.Cursor;
using DrawingPoint = System.Drawing.Point;
using WpfTimer = System.Windows.Threading.DispatcherTimer;

namespace WorkTimer.App.Views;

/// <summary>
/// 悬浮窗（加速球）。
///
/// 贴边逻辑的几何计算全部在 <see cref="SnapGeometry"/> 里（纯函数 + 单元测试），
/// 这边只负责拿真实的窗口坐标与屏幕工作区去调用它。
/// 上一版错在"展开只改尺寸、不动坐标"，贴右边时右半截就跑到屏幕外了。
/// </summary>
public partial class WidgetWindow : Window
{
    private const int SnapThresholdDips = 26;
    private const double ExpandedWidth = 246;
    private const double ExpandedHeight = 66;
    private const double CollapsedWidthTimeOnly = 118;
    private const double CollapsedWidthWithProject = 176;
    private const double CollapsedHeight = 34;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private readonly AppHost _host;
    private readonly WpfTimer _collapseTimer;
    private readonly WpfTimer _uiTimer;

    private SnapEdge _edge = SnapEdge.None;
    private bool _collapsed;

    private bool _dragging;
    private System.Drawing.Point _dragStartCursor;
    private double _dragStartLeft;
    private double _dragStartTop;
    private double _dpiScaleX = 1;
    private double _dpiScaleY = 1;

    public WidgetWindow(AppHost host)
    {
        _host = host;

        InitializeComponent();

        _collapseTimer = new WpfTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_edge != SnapEdge.None && !IsMouseOver)
            {
                ApplyState(collapsed: true);
            }
        };

        // 时间做插值，秒数每秒平滑跳动（和主窗口一致）
        _uiTimer = new WpfTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTimer.Tick += (_, _) => UpdateTimeText();

        Root.MouseLeftButtonDown += OnDragStart;
        Root.MouseMove += OnDragMove;
        Root.MouseLeftButtonUp += OnDragEnd;
        Root.MouseEnter += OnRootMouseEnter;
        Root.MouseLeave += OnRootMouseLeave;
        Root.MouseRightButtonUp += (_, _) => ShowContextMenu();

        // 探针：确认窗口到底有没有收到鼠标消息（排查"点不动"这类问题时非常有用）
        Root.PreviewMouseDown += (_, e) =>
            Log.Debug("Widget", $"收到鼠标按下 pos={e.GetPosition(this)} src={e.OriginalSource?.GetType().Name}");

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += (_, _) => _uiTimer.Stop();

        ApplyConfig();
    }

    // ================================================================ 初始化

    /// <summary>
    /// 只加 WS_EX_TOOLWINDOW（不进 Alt+Tab、不显示在任务栏）。
    ///
    /// 注意：这里<b>不能</b>加 WS_EX_NOACTIVATE。实测在 WPF 的 AllowsTransparency 透明窗口上，
    /// 一旦带上这个标志，窗口就完全收不到鼠标输入 —— 按钮点不动、右键菜单也不弹。
    /// 现在改成在处理 WM_MOUSEACTIVATE 时返回 MA_NOACTIVATE，效果一样（不抢前台焦点）
    /// 但输入正常。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

            HwndSource.FromHwnd(handle)?.AddHook(WndProcHook);
        }
        catch (Exception ex)
        {
            Log.Debug("Widget", $"设置悬浮窗扩展样式失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 拦截 WM_MOUSEACTIVATE 并返回 MA_NOACTIVATE：点击悬浮窗不会激活它，
    /// 也就不会把前台焦点从用户正在用的软件上抢走（否则点一下就等于切了一次窗口）。
    /// </summary>
    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreOrPlaceDefault();
        UpdateUi(_host.Timer.Snapshot);
        _uiTimer.Start();
    }

    /// <summary>设置页改动后调用，让外观立刻跟上。</summary>
    public void ApplyConfig()
    {
        var config = _host.Config.FloatingWidget;

        Topmost = config.AlwaysOnTop;
        Opacity = config.Opacity;
        ProjectTextSmall.Visibility = config.CollapsedContent == "projectAndTime"
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (IsLoaded)
        {
            ApplyState(_collapsed);
        }
    }

    public void ResetPosition()
    {
        var area = CurrentWorkingArea();
        Left = area.Right - ExpandedWidth - 24;
        Top = area.Top + 120;
        _edge = SnapEdge.None;
        ApplyState(collapsed: false);
        PersistPosition();
    }

    private void RestoreOrPlaceDefault()
    {
        var config = _host.Config.FloatingWidget;
        var area = CurrentWorkingArea();

        var width = _collapsed ? CollapsedWidth() : ExpandedWidth;
        var height = _collapsed ? CollapsedHeight : ExpandedHeight;

        var x = config.PositionX ?? (area.Right - width - 64);
        var y = config.PositionY ?? (area.Top + 120);

        // 显示器数量 / 分辨率变了可能把窗口丢到屏幕外，先钳回工作区再做别的
        var clamped = SnapGeometry.ClampInto(ToRectD(area), new RectD(x, y, width, height));

        Width = clamped.Width;
        Height = clamped.Height;
        Left = clamped.Left;
        Top = clamped.Top;

        if (!SnapGeometry.IsVisibleOnAny(AllWorkingAreas(), new RectD(x, y, width, height)))
        {
            Log.Warn("Widget", "保存的悬浮窗位置在屏幕外，已回到默认位置");
            Left = area.Right - width - 64;
            Top = area.Top + 120;
            _edge = SnapEdge.None;
        }
        else
        {
            // 恢复时如果本来就贴着某条边，保持吸附（但不自动收起，避免用户找不到窗口）
            _edge = DetectEdge();
        }
    }

    // ================================================================ 尺寸与定位

    private double CollapsedWidth()
        => _host.Config.FloatingWidget.CollapsedContent == "projectAndTime"
            ? CollapsedWidthWithProject
            : CollapsedWidthTimeOnly;

    /// <summary>
    /// 切换展开 / 收起。几何计算交给 <see cref="SnapGeometry.Resolve"/>：
    /// 贴哪条边就固定那条边，另一侧生长，最后统一钳制进工作区。
    /// </summary>
    private void ApplyState(bool collapsed)
    {
        var targetWidth = collapsed ? CollapsedWidth() : ExpandedWidth;
        var targetHeight = collapsed ? CollapsedHeight : ExpandedHeight;

        var resolved = SnapGeometry.Resolve(
            new RectD(Left, Top, Width, Height),
            _edge,
            targetWidth,
            targetHeight,
            ToRectD(CurrentWorkingArea()));

        Width = resolved.Width;
        Height = resolved.Height;
        Left = resolved.Left;
        Top = resolved.Top;

        _collapsed = collapsed;
        ExpandedRoot.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedRoot.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;

        UpdateTimeText();
    }

    /// <summary>
    /// 手动实现拖动，而不是用 <c>DragMove()</c>。
    ///
    /// 之前用 DragMove 有个坑：它挂在最外层容器上，会把「开始 / 停止」按钮的点击一起吞掉 ——
    /// 表现就是点按钮完全没反应，还会顺手把窗口收起。这里改成自己跟鼠标，
    /// 并且明确跳过落在按钮上的按下事件。
    /// </summary>
    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiScaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        _dpiScaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        _dragStartCursor = FormsCursor.Position;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragging = true;

        Root.CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var now = FormsCursor.Position;
        Left = _dragStartLeft + (now.X - _dragStartCursor.X) / _dpiScaleX;
        Top = _dragStartTop + (now.Y - _dragStartCursor.Y) / _dpiScaleY;
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Root.ReleaseMouseCapture();

        // 位移很小就算点击，不做吸附也不写配置，避免"点一下就被收起"
        var moved = Math.Abs(Left - _dragStartLeft) > 3 || Math.Abs(Top - _dragStartTop) > 3;
        if (!moved)
        {
            return;
        }

        _collapseTimer.Stop();
        TrySnap();
        PersistPosition();
    }

    /// <summary>事件源是否落在按钮里 —— 是的话就让按钮自己处理，不要当成拖动。</summary>
    private static bool IsInsideButton(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>拖完之后判断离哪条边最近，够近就吸附并收起。</summary>
    private void TrySnap()
    {
        var config = _host.Config.FloatingWidget;
        var area = CurrentWorkingArea();

        if (!config.SnapToEdge || config.SnapSide == "none")
        {
            _edge = SnapEdge.None;
            ApplyState(collapsed: false);
            return;
        }

        var edge = DetectEdge();

        if (edge == SnapEdge.None)
        {
            _edge = SnapEdge.None;
            ApplyState(collapsed: false);
            return;
        }

        _edge = edge;
        ApplyState(collapsed: true);
    }

    private SnapEdge DetectEdge()
        => SnapGeometry.Detect(
            new RectD(Left, Top, Width, Height),
            ToRectD(CurrentWorkingArea()),
            _host.Config.FloatingWidget.SnapSide,
            SnapThresholdDips);

    private static RectD ToRectD(Rect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);

    private void OnRootMouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_collapsed)
        {
            ApplyState(collapsed: false);
        }
    }

    private void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        if (_edge == SnapEdge.None)
        {
            return;
        }

        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    // ================================================================ 屏幕与持久化

    /// <summary>当前窗口所在显示器的工作区（已换算成 WPF 的 DIP 单位）。</summary>
    private Rect CurrentWorkingArea()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

            var devicePoint = new DrawingPoint((int)(Left * scaleX), (int)(Top * scaleY));
            var screen = FormsScreen.FromPoint(devicePoint);
            var wa = screen.WorkingArea;

            return new Rect(wa.Left / scaleX, wa.Top / scaleY, wa.Width / scaleX, wa.Height / scaleY);
        }
        catch (Exception ex)
        {
            Log.Debug("Widget", $"读取工作区失败，退回主屏：{ex.Message}");
            return new Rect(
                SystemParameters.WorkArea.Left,
                SystemParameters.WorkArea.Top,
                SystemParameters.WorkArea.Width,
                SystemParameters.WorkArea.Height);
        }
    }

    /// <summary>所有显示器的工作区（DIP 单位），用于判断窗口是否还停留在可见范围内。</summary>
    private List<RectD> AllWorkingAreas()
    {
        var list = new List<RectD>();

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

            foreach (var screen in FormsScreen.AllScreens)
            {
                var wa = screen.WorkingArea;
                list.Add(new RectD(wa.Left / scaleX, wa.Top / scaleY, wa.Width / scaleX, wa.Height / scaleY));
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Widget", $"枚举显示器失败：{ex.Message}");
        }

        if (list.Count == 0)
        {
            list.Add(new RectD(
                SystemParameters.WorkArea.Left,
                SystemParameters.WorkArea.Top,
                SystemParameters.WorkArea.Width,
                SystemParameters.WorkArea.Height));
        }

        return list;
    }

    private void PersistPosition()
    {
        var config = _host.Config.FloatingWidget;
        var x = (int)Math.Round(Left);
        var y = (int)Math.Round(Top);

        if (config.PositionX == x && config.PositionY == y)
        {
            return;
        }

        config.PositionX = x;
        config.PositionY = y;
        _host.SaveConfig();
    }

    // ================================================================ 显示刷新

    public void Attach() => _host.SnapshotUpdated += OnSnapshotUpdated;

    private void OnSnapshotUpdated(TimerSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(new Action(() => UpdateUi(snapshot)), DispatcherPriority.Background);
    }

    private TimerSnapshot _snapshot = TimerSnapshot.Empty;
    private DateTimeOffset _snapshotAt;
    private string _lastTooltipTime = string.Empty;

    private void UpdateUi(TimerSnapshot snapshot)
    {
        _snapshot = snapshot;
        _snapshotAt = DateTimeOffset.Now;

        var color = AppIcons.WpfBrush(snapshot.State);
        Dot.Fill = color;
        DotSmall.Fill = color;
        StateBar.Background = color;

        ProjectText.Text = string.IsNullOrEmpty(snapshot.ProjectName) ? "未选择项目" : snapshot.ProjectName;
        ProjectTextSmall.Text = ProjectText.Text;

        ToggleButton.Content = snapshot.State switch
        {
            TimerState.Stopped => "开始",
            TimerState.PausedByUser => "继续",
            _ => "暂停",
        };
        StopButton.IsEnabled = snapshot.HasSession;

        UpdateTimeText();
    }

    /// <summary>200ms 一次插值，让秒数平滑跳动，而不是一秒跳一大格。</summary>
    private void UpdateTimeText()
    {
        var elapsed = _snapshotAt == default ? 0 : (DateTimeOffset.Now - _snapshotAt).TotalSeconds;
        var counting = _snapshot.State is TimerState.Working or TimerState.PendingEntertainment;
        var text = TimeFormat.Hms(_snapshot.NetSeconds + (counting ? Math.Max(0, elapsed) : 0));

        // 只在文本真的变了才赋值：TextBlock.Text 赋值会触发布局失效，
        // 每 200ms 无脑重设会让这个始终置顶的小窗口持续重排，拖累整个桌面的流畅度。
        SetText(TimeText, text);
        SetText(TimeTextSmall, text);

        // 提示文字每秒才需要更新一次，没必要跟着 200ms 一起算
        if (text == _lastTooltipTime)
        {
            return;
        }

        _lastTooltipTime = text;

        var tooltip = $"{ProjectText.Text} · {text}（{_snapshot.State.ToDisplay()}）";
        if (_snapshot.PendingSeconds > 0 || _snapshot.State == TimerState.PendingEntertainment)
        {
            tooltip += $"\n待判定 {TimeFormat.Hms(_snapshot.PendingSeconds)} / 阈值 {TimeFormat.Hms(_snapshot.RunThresholdSeconds)}";
        }

        ToolTip = tooltip;
    }

    private static void SetText(System.Windows.Controls.TextBlock target, string value)
    {
        if (!string.Equals(target.Text, value, StringComparison.Ordinal))
        {
            target.Text = value;
        }
    }

    // ================================================================ 操作

    private void OnToggleTimer(object sender, RoutedEventArgs e)
    {
        var snapshot = _host.Timer.Snapshot;

        if (snapshot.State == TimerState.Stopped)
        {
            var project = _host.EnsureDefaultProject();
            _host.Timer.Start(project.Id, project.Name);
        }
        else if (snapshot.State == TimerState.PausedByUser)
        {
            _host.Timer.Resume();
        }
        else
        {
            _host.Timer.Pause();
        }
    }

    private void OnStopTimer(object sender, RoutedEventArgs e)
    {
        var snapshot = _host.Timer.Snapshot;
        if (!snapshot.HasSession)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"停止并归档本次会话？\n\n项目：{snapshot.ProjectName}\n有效计时：{TimeFormat.Hms(snapshot.NetSeconds)}",
            "停止并保存", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            _host.Timer.Stop(StopReasons.UserStop);
        }
    }

    private void ShowContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        menu.Items.Add(MenuItem("打开主窗口", () => _host.RaiseShowMainWindow()));
        menu.Items.Add(MenuItem("隐藏悬浮窗", () =>
        {
            Hide();
            _host.Config.FloatingWidget.Enabled = false;
            _host.SaveConfig();
        }));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MenuItem("恢复展开", () =>
        {
            _edge = SnapEdge.None;
            ApplyState(collapsed: false);
            PersistPosition();
        }));
        menu.Items.Add(MenuItem("回到默认位置", ResetPosition));

        menu.IsOpen = true;
    }

    private static System.Windows.Controls.MenuItem MenuItem(string header, Action action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
