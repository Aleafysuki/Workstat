using System.Drawing;
using WorkTimer.Core.Diagnostics;
using WorkTimer.Core.Timing;
using WinForms = System.Windows.Forms;

namespace WorkTimer.App.Services;

/// <summary>
/// 托盘图标与右键菜单。
/// WPF 没有托盘图标，所以这里用 WinForms 的 NotifyIcon —— 它只是创建隐藏窗口挂到
/// 当前线程的消息泵上，和 WPF 的 Dispatcher 共用同一条消息循环，不冲突。
///
/// 图标颜色跟着计时状态走（绿=计时中 / 橙=待判定 / 深橙=娱乐挂起 / 灰=暂停 / 浅灰=未开始），
/// 不打开主窗口也能一眼看出有没有被挂起。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly AppHost _host;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _toggleItem;
    private readonly WinForms.ToolStripMenuItem _stopItem;
    private readonly WinForms.ToolStripMenuItem _projectMenuItem;
    private readonly WinForms.ToolStripMenuItem _widgetItem;

    private readonly Action _showMainWindow;
    private readonly Action _toggleWidget;
    private readonly Func<bool> _isWidgetVisible;
    private readonly Action _requestExit;

    private TimerState _lastState = TimerState.Stopped;

    public TrayController(
        AppHost host,
        Action showMainWindow,
        Action toggleWidget,
        Func<bool> isWidgetVisible,
        Action requestExit)
    {
        _host = host;
        _showMainWindow = showMainWindow;
        _toggleWidget = toggleWidget;
        _isWidgetVisible = isWidgetVisible;
        _requestExit = requestExit;

        _toggleItem = new WinForms.ToolStripMenuItem("开始计时", null, (_, _) => ToggleTimer());
        _stopItem = new WinForms.ToolStripMenuItem("停止并保存", null, (_, _) => StopTimer());
        _projectMenuItem = new WinForms.ToolStripMenuItem("项目切换");
        _widgetItem = new WinForms.ToolStripMenuItem("显示 / 隐藏悬浮窗", null, (_, _) => _toggleWidget());

        _menu = new WinForms.ContextMenuStrip();
        _menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            _toggleItem,
            _stopItem,
            new WinForms.ToolStripSeparator(),
            _projectMenuItem,
            new WinForms.ToolStripMenuItem("重命名当前项目…", null, (_, _) => RenameCurrentProject()),
            new WinForms.ToolStripMenuItem("本次会话统计", null, (_, _) => ShowSessionStats()),
            new WinForms.ToolStripSeparator(),
            _widgetItem,
            new WinForms.ToolStripMenuItem("打开主窗口", null, (_, _) => _showMainWindow()),
            new WinForms.ToolStripSeparator(),
            new WinForms.ToolStripMenuItem("退出", null, (_, _) => _requestExit()),
        });

        // 每次弹出前重建动态内容，保证项目列表与勾选状态是最新的
        _menu.Opening += (_, _) => RebuildDynamicItems();

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = AppIcons.For(TimerState.Stopped),
            Text = "工作计时",
            ContextMenuStrip = _menu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => _showMainWindow();

        host.SnapshotUpdated += OnSnapshotUpdated;
    }

    private void OnSnapshotUpdated(TimerSnapshot snapshot)
    {
        // 事件在采样线程触发，这里只做最简单的属性赋值
        try
        {
            if (snapshot.State != _lastState)
            {
                _lastState = snapshot.State;
                _notifyIcon.Icon = AppIcons.For(snapshot.State);
                Log.Debug("Tray", $"托盘图标切换到 {snapshot.State.ToDisplay()}");
            }

            var project = string.IsNullOrEmpty(snapshot.ProjectName) ? "未选择项目" : snapshot.ProjectName;
            var tooltip = $"{project} · {TimeFormat.Hms(snapshot.NetSeconds)}（{snapshot.State.ToDisplay()}）";

            // NotifyIcon.Text 上限 63 个字符，超了会抛异常
            _notifyIcon.Text = tooltip.Length > 62 ? tooltip[..62] : tooltip;
        }
        catch (Exception ex)
        {
            Log.Debug("Tray", $"更新托盘提示失败：{ex.Message}");
        }
    }

    private void RebuildDynamicItems()
    {
        var snapshot = _host.Timer.Snapshot;

        _toggleItem.Text = snapshot.State switch
        {
            TimerState.Stopped => "开始计时",
            TimerState.PausedByUser => "继续计时",
            _ => "暂停计时",
        };

        _stopItem.Enabled = snapshot.HasSession;
        _widgetItem.Checked = _isWidgetVisible();

        _projectMenuItem.DropDownItems.Clear();

        var projects = _host.GetProjects();
        if (projects.Count == 0)
        {
            _projectMenuItem.DropDownItems.Add(new WinForms.ToolStripMenuItem("（还没有项目）") { Enabled = false });
            return;
        }

        foreach (var project in projects)
        {
            var item = new WinForms.ToolStripMenuItem(project.Name)
            {
                Checked = snapshot.ProjectId == project.Id,
            };
            item.Click += (_, _) => SwitchProject(project.Id, project.Name);
            _projectMenuItem.DropDownItems.Add(item);
        }
    }

    private void ToggleTimer()
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

    private void StopTimer()
    {
        var snapshot = _host.Timer.Snapshot;
        if (!snapshot.HasSession)
        {
            return;
        }

        var answer = WinForms.MessageBox.Show(
            $"确定要停止本次会话吗？\r\n\r\n项目：{snapshot.ProjectName}\r\n有效计时：{TimeFormat.Hms(snapshot.NetSeconds)}",
            "停止并保存", WinForms.MessageBoxButtons.YesNo, WinForms.MessageBoxIcon.Question);

        if (answer == WinForms.DialogResult.Yes)
        {
            _host.Timer.Stop(StopReasons.UserStop);
        }
    }

    private void SwitchProject(long projectId, string projectName)
    {
        if (_host.Timer.Snapshot.ProjectId == projectId)
        {
            return;
        }

        if (_host.Timer.Snapshot.HasSession)
        {
            var answer = WinForms.MessageBox.Show(
                $"切换项目会结束当前会话（{_host.Timer.Snapshot.ProjectName}）并开始一个新的计时。\r\n继续吗？",
                "切换项目", WinForms.MessageBoxButtons.YesNo, WinForms.MessageBoxIcon.Question);

            if (answer != WinForms.DialogResult.Yes)
            {
                return;
            }
        }

        _host.Timer.SwitchProject(projectId, projectName);
        _host.NotifyProjectsChanged();
    }

    private void RenameCurrentProject()
    {
        var snapshot = _host.Timer.Snapshot;
        var projects = _host.GetProjects();
        if (projects.Count == 0)
        {
            return;
        }

        var target = projects.FirstOrDefault(p => p.Id == snapshot.ProjectId) ?? projects[0];
        var newName = Views.InputDialog.Show(null!, "重命名项目", "新的项目名称：", target.Name);

        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == target.Name)
        {
            return;
        }

        if (_host.Repository.RenameProject(target.Id, newName.Trim()))
        {
            if (snapshot.ProjectId == target.Id && snapshot.HasSession)
            {
                _host.Timer.SetSessionDisplay(newName.Trim(), snapshot.SessionStartedAt ?? DateTimeOffset.Now);
            }

            _host.NotifyProjectsChanged();
        }
    }

    private void ShowSessionStats()
    {
        var s = _host.Timer.Snapshot;

        WinForms.MessageBox.Show(
            $"项目：{s.ProjectName}\r\n" +
            $"开始于：{s.SessionStartedAt:yyyy-MM-dd HH:mm:ss}\r\n" +
            $"状态：{s.State.ToDisplay()}\r\n\r\n" +
            $"有效计时：{TimeFormat.Hms(s.NetSeconds)}\r\n" +
            $"待判定：{TimeFormat.Hms(s.PendingSeconds)}\r\n" +
            $"娱乐挂起：{TimeFormat.Hms(s.SuspendedSeconds)}\r\n" +
            $"手动暂停：{TimeFormat.Hms(s.UserPausedSeconds)}\r\n" +
            $"空闲：{TimeFormat.Hms(s.IdleSeconds)}\r\n\r\n" +
            $"当前前台：{s.DisplayCurrent}\r\n" +
            $"判定：{s.CurrentCategoryDisplay}（{s.CurrentReason}）",
            "本次会话统计", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            Log.Debug("Tray", $"气泡提示失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _host.SnapshotUpdated -= OnSnapshotUpdated;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
