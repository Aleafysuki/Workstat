using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using WorkTimer.App.Services;
using WorkTimer.App.ViewModel;
using WorkTimer.Core.Classification;
using WorkTimer.Core.Data;
using WorkTimer.Core.Diagnostics;
using WorkTimer.Core.Timing;

namespace WorkTimer.App.Views;

/// <summary>
/// 主窗口。顶部常驻"计时器 + 实时判定"，下面四个页签：概览 / 记录 / 诊断 / 设置。
///
/// 两个体验要点：
///  1. 计时显示用 200ms 的界面定时器做<b>插值</b>，所以秒数每秒平滑跳动，
///     不需要靠提高采样率来让数字"看起来在动"；
///  2. 所有采样线程来的事件都经 Dispatcher 投递，界面本身不做任何耗时计算。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _uiTimer;
    private readonly ObservableCollection<ProjectRow> _projectRows = new();
    private readonly ObservableCollection<DayRow> _dayRows = new();
    private readonly ObservableCollection<SegmentRow> _segmentRows = new();
    private readonly ObservableCollection<KvRow> _diagnosticRows = new();

    private TimerSnapshot _snapshot = TimerSnapshot.Empty;
    private DateTimeOffset _snapshotAt;
    private bool _subscribed;
    private bool _suppressProjectChange;
    private bool _suppressRangeChange;

    /// <summary>真正退出程序时置 true，绕过"关闭即最小化到托盘"。</summary>
    public bool AllowClose { get; set; }

    public MainWindow(AppHost host)
    {
        _host = host;

        InitializeComponent();

        ProjectGrid.ItemsSource = _projectRows;
        DayGrid.ItemsSource = _dayRows;
        SegmentGrid.ItemsSource = _segmentRows;
        DiagnosticsGrid.ItemsSource = _diagnosticRows;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _uiTimer.Tick += OnUiTimerTick;

        Loaded += OnLoaded;
        Closing += OnClosing;

        BuildSettingsNav();
    }

    // ================================================================ 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _host.SnapshotUpdated += OnSnapshotUpdated;
            _host.Notice += OnNotice;
            _host.ProjectsChanged += OnProjectsChanged;
            Log.EntryAdded += OnLogEntry;
            _subscribed = true;
        }

        _uiTimer.Start();

        if (_host.DataDirectoryFallbackReason is { Length: > 0 } reason)
        {
            SetStatus("数据目录已回退：" + reason);
        }

        _host.EnsureDefaultProject();
        ReloadProjects();
        BuildRangeCombo();
        ApplyInitialViewAnchor();
        RefreshStats();
        RefreshSegments();
        RefreshDiagnostics();
        RefreshLogs();
        ApplySnapshot(_host.Timer.Snapshot);

        ShowSettingsGroup(0);

        // 等窗口显示出来再问，否则对话框会挡在还不可见的窗口前面
        Dispatcher.BeginInvoke(new Action(OfferResume), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>启动时如果发现上次退出保存的会话，问用户是否继续。</summary>
    private void OfferResume()
    {
        var pending = _host.PendingResumeSession;
        if (pending is null)
        {
            return;
        }

        var elapsed = DateTimeOffset.Now - (pending.LastHeartbeatAt ?? pending.StartedAt);
        var elapsedText = elapsed.TotalDays >= 1
            ? $"{(int)elapsed.TotalDays} 天"
            : elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours} 小时"
                : $"{(int)elapsed.TotalMinutes} 分钟";

        var answer = MessageBox.Show(this,
            $"发现上次退出时未结束的会话：\n\n" +
            $"项目：{pending.ProjectName}\n" +
            $"开始于：{pending.StartedAt:yyyy-MM-dd HH:mm}\n" +
            $"已计工时：{TimeFormat.Hms(pending.NetSeconds)}\n" +
            $"距上次记录：{elapsedText}\n\n" +
            "要继续这条会话接着计时吗？\n（选择「否」会把它结束并归档）",
            "继续上次会话？", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            _host.Timer.RestorePausedSession(
                pending.Id,
                pending.ProjectId,
                pending.ProjectName,
                pending.StartedAt,
                new SessionProgress(
                    pending.NetSeconds,
                    0,
                    pending.SuspendedSeconds,
                    pending.UserPausedSeconds,
                    pending.IdleSeconds));
        }
        else
        {
            _host.Repository.CloseCrashedSession(pending, DateTimeOffset.Now);
        }

        _host.ClearPendingResumeSession();
        ApplySnapshot(_host.Timer.Snapshot);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (AllowClose)
        {
            return;
        }

        if (_host.Config.General.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            SetStatus("已最小化到托盘（右键托盘图标可退出）");
        }
    }

    /// <summary>托盘退出时调用，确保关窗不被"关闭到托盘"拦下。</summary>
    public void CloseForReal()
    {
        AllowClose = true;
        _uiTimer.Stop();
        Close();
    }

    // ================================================================ 采样 → 界面

    private void OnSnapshotUpdated(TimerSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _snapshot = snapshot;
            _snapshotAt = DateTimeOffset.Now;
            ApplySnapshot(snapshot);
        }), DispatcherPriority.Background);
    }

    /// <summary>200ms 一次，把"上次采样到现在"的时间补上去，让秒数平滑跳动。</summary>
    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        if (_snapshotAt == default)
        {
            return;
        }

        var elapsed = (DateTimeOffset.Now - _snapshotAt).TotalSeconds;
        if (elapsed < 0)
        {
            elapsed = 0;
        }

        var counting = _snapshot.State is TimerState.Working or TimerState.PendingEntertainment;
        var extra = counting ? elapsed : 0;

        // 只在文本真的变了才赋值 —— TextBlock.Text 赋值会触发布局失效，
        // 每 200ms 无脑重设会让界面持续重排，是"看起来卡"的一个常见来源。
        SetText(TimerText, TimeFormat.Hms(_snapshot.NetSeconds + extra));

        var pending = _snapshot.PendingSeconds +
                      (_snapshot.State == TimerState.PendingEntertainment ? extra : 0);
        SetText(SubTimerText,
            $"待判定 {TimeFormat.Hms(pending)} · " +
            $"挂起 {TimeFormat.Hms(_snapshot.SuspendedSeconds)} · " +
            $"暂停 {TimeFormat.Hms(_snapshot.UserPausedSeconds)} · " +
            $"空闲 {TimeFormat.Hms(_snapshot.IdleSeconds)}");

        if (_snapshot.RunThresholdSeconds > 1)
        {
            var run = _snapshot.RunSeconds + extra;
            var target = Math.Clamp(run / _snapshot.RunThresholdSeconds * 1000, 0, 1000);
            if (Math.Abs(RunProgress.Value - target) > 1)
            {
                RunProgress.Value = target;
            }

            SetText(RunText,
                $"娱乐连续：{TimeFormat.Hms(run)} / {TimeFormat.Hms(_snapshot.RunThresholdSeconds)}" +
                $"（阈值策略：{ThresholdPolicy.DescribeMode(_snapshot.ThresholdMode)}）");
        }
    }

    /// <summary>文本没变就不赋值，避免无谓的布局失效与重绘。</summary>
    private static void SetText(System.Windows.Controls.TextBlock target, string value)
    {
        if (!string.Equals(target.Text, value, StringComparison.Ordinal))
        {
            target.Text = value;
        }
    }

    private void ApplySnapshot(TimerSnapshot snapshot)
    {
        StateDot.Fill = AppIcons.WpfBrush(snapshot.State);
        SetText(StateText, snapshot.State.ToDisplay());

        TimerText.Text = TimeFormat.Hms(snapshot.NetSeconds);

        // 「没有会话」才是"当前没在计时"的权威信号，所以开始按钮以它为判断依据。
        // 原来只看 State 是否 Stopped：一旦状态机卡在中间状态（出过这种故障），
        // 开始 / 暂停 / 停止三个按钮会一起变灰，用户连"重新开始"都点不了。
        StartButton.IsEnabled = !snapshot.HasSession || snapshot.State == TimerState.PausedByUser;
        StartButton.Content = snapshot.State == TimerState.PausedByUser ? "继续计时" : "开始计时";
        PauseButton.IsEnabled = snapshot.HasSession && snapshot.State != TimerState.PausedByUser;
        PauseButton.Content = snapshot.State == TimerState.PausedByUser ? "继续" : "暂停";
        StopButton.IsEnabled = snapshot.HasSession;

        if (snapshot.State == TimerState.PendingEntertainment && snapshot.RunThresholdSeconds <= 1)
        {
            RunText.Text = "娱乐连续：待判定";
            RunProgress.Value = 0;
        }
        else if (snapshot.RunSeconds <= 0.5)
        {
            RunText.Text = "娱乐连续：当前无娱乐窗口在前台";
            RunProgress.Value = 0;
        }

        // 同步项目下拉框的选中项（托盘/悬浮窗切换项目后主窗口要跟上）
        _suppressProjectChange = true;
        try
        {
            foreach (var item in ProjectCombo.Items)
            {
                if (item is ProjectRecord record && record.Id == snapshot.ProjectId)
                {
                    if (!ReferenceEquals(ProjectCombo.SelectedItem, item))
                    {
                        ProjectCombo.SelectedItem = item;
                    }
                    break;
                }
            }
        }
        finally
        {
            _suppressProjectChange = false;
        }

        if (snapshot.State is TimerState.Working or TimerState.PendingEntertainment)
        {
            SetStatus($"{snapshot.ProjectName} · {TimeFormat.Hms(snapshot.NetSeconds)} · {snapshot.State.ToDisplay()} · 采样 {Diag.TickCount} 次");
        }
    }

    private void OnNotice(string area, string message)
    {
        Log.Info("Notice", message);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            SetStatus(message);
            if (_host.Config.Notification.NotifyOnSuspend)
            {
                _host.Tray?.ShowBalloon("工作计时", message);
            }
        }), DispatcherPriority.Background);
    }

    private void OnProjectsChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ReloadProjects();
            RefreshStats();
        }), DispatcherPriority.Background);
    }

    private void OnLogEntry(LogEntry entry)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            LogList.Items.Add(entry.ToString().Replace(Environment.NewLine, " | "));
            while (LogList.Items.Count > 200)
            {
                LogList.Items.RemoveAt(0);
            }

            LogList.ScrollIntoView(LogList.Items[^1]);
            RefreshWindowFrameInfo(entry);
        }), DispatcherPriority.Background);
    }

    /// <summary>日志里会带上前台窗口与判定结果，出问题时肉眼就能对上"当时判了什么"。</summary>
    private void RefreshWindowFrameInfo(LogEntry entry)
    {
        if (entry.Area != "Timer" && entry.Area != "Classify")
        {
            return;
        }

        var window = _host.LastWindow;
        ForegroundText.Text = window.HasForeground
            ? $"前台：{window.ProcessName} — {Trim(window.Title, 110)}"
            : "前台：（当前没有前台窗口，继承上一分类）";

        var snap = _host.Timer.Snapshot;
        VerdictText.Text = $"判定：{snap.CurrentCategoryDisplay} · {snap.DisplayCurrent}　←　{snap.CurrentReason}";
    }

    private static string Trim(string text, int max)
        => string.IsNullOrEmpty(text) ? "(无标题)" : text.Length <= max ? text : text[..max] + "…";

    private void SetStatus(string text) => StatusText.Text = text;

    // ================================================================ 操作

    private void OnStartOrResume(object sender, RoutedEventArgs e)
    {
        var snapshot = _host.Timer.Snapshot;
        if (snapshot.State == TimerState.PausedByUser)
        {
            _host.Timer.Resume();
            return;
        }

        if (ProjectCombo.SelectedItem is not ProjectRecord project)
        {
            MessageBox.Show(this, "请先选择一个项目。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _host.Timer.Start(project.Id, project.Name);
        _host.Timer.SetSessionDisplay(project.Name, DateTimeOffset.Now);
    }

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        if (!_host.Timer.Snapshot.HasSession)
        {
            return;
        }

        if (_host.Timer.Snapshot.State == TimerState.PausedByUser)
        {
            _host.Timer.Resume();
        }
        else
        {
            _host.Timer.Pause();
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        var snapshot = _host.Timer.Snapshot;
        if (!snapshot.HasSession)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"停止并归档本次会话？\n\n项目：{snapshot.ProjectName}\n有效计时：{TimeFormat.Hms(snapshot.NetSeconds)}",
            "停止并保存", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _host.Timer.Stop(StopReasons.UserStop);
        RefreshStats();
        RefreshSegments();
    }

    private void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProjectChange || ProjectCombo.SelectedItem is not ProjectRecord project)
        {
            return;
        }

        var snapshot = _host.Timer.Snapshot;
        if (!snapshot.HasSession || snapshot.ProjectId == project.Id)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"切换项目会结束当前会话（{snapshot.ProjectName}）并开始新的计时。\n继续吗？",
            "切换项目", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            ReloadProjects();
            return;
        }

        _host.Timer.SwitchProject(project.Id, project.Name);
        _host.Timer.SetSessionDisplay(project.Name, DateTimeOffset.Now);
    }

    private void OnRenameProject(object sender, RoutedEventArgs e)
    {
        if (ProjectCombo.SelectedItem is not ProjectRecord project)
        {
            return;
        }

        var newName = InputDialog.Show(this, "重命名项目", "新的项目名称：", project.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == project.Name)
        {
            return;
        }

        if (_host.Repository.ProjectNameExists(newName.Trim(), excludeId: project.Id))
        {
            MessageBox.Show(this, $"已经有一个叫「{newName.Trim()}」的项目了，换个名字吧。",
                "重命名项目", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_host.Repository.RenameProject(project.Id, newName.Trim()))
        {
            return;
        }

        if (_host.Timer.Snapshot.ProjectId == project.Id && _host.Timer.Snapshot.HasSession)
        {
            _host.Timer.SetSessionDisplay(newName.Trim(), _host.Timer.Snapshot.SessionStartedAt ?? DateTimeOffset.Now);
        }

        SetStatus($"项目已重命名为「{newName.Trim()}」");
        OnProjectsChanged();
    }

    /// <summary>
    /// 新建项目。
    ///
    /// 默认名沿用「项目 + 八位日期」，和首次运行生成的默认项目保持一致的说法。
    /// 重名会先拦下来给一句人话 —— projects 表上有「未删除项目名唯一」的条件索引，
    /// 不拦的话用户看到的是 SQLite 的 UNIQUE constraint 报错。
    /// </summary>
    private void OnCreateProject(object sender, RoutedEventArgs e)
    {
        var suggested = $"项目{DateTimeOffset.Now:yyyyMMdd}";
        var input = InputDialog.Show(this, "新建项目", "项目名称：", suggested);
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        var name = input.Trim();
        if (_host.Repository.ProjectNameExists(name))
        {
            MessageBox.Show(this, $"已经有一个叫「{name}」的项目了，换个名字吧。",
                "新建项目", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ProjectRecord created;
        try
        {
            created = _host.Repository.CreateProject(name);
        }
        catch (Exception ex)
        {
            Log.Error("App", $"新建项目失败：{name}", ex);
            MessageBox.Show(this, $"新建项目失败：{ex.Message}\n\n详细信息已写入日志。",
                "新建项目", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OnProjectsChanged();

        // 直接选中新项目 —— 「新建」的意图就是要用它，省得再去下拉框里翻一遍。
        // 若此刻正在计时，OnProjectSelectionChanged 会照常问一句是否切项目。
        var index = ProjectCombo.Items.OfType<ProjectRecord>().ToList().FindIndex(p => p.Id == created.Id);
        if (index >= 0)
        {
            ProjectCombo.SelectedIndex = index;
        }

        SetStatus($"已新建项目「{created.Name}」");
    }

    private void ReloadProjects()
    {
        _suppressProjectChange = true;
        try
        {
            var projects = _host.GetProjects();
            var currentId = _host.Timer.Snapshot.ProjectId;

            ProjectCombo.Items.Clear();
            foreach (var project in projects)
            {
                ProjectCombo.Items.Add(project);
            }

            var index = projects.FindIndex(p => p.Id == currentId);
            ProjectCombo.SelectedIndex = index >= 0 ? index : (projects.Count > 0 ? 0 : -1);
        }
        finally
        {
            _suppressProjectChange = false;
        }
    }

    /// <summary>一键纠错：按当前前台窗口生成用户规则，立即生效。</summary>
    private void OnMarkAsWork(object sender, RoutedEventArgs e) => CorrectClassification(Category.Work);

    private void OnMarkAsEntertainment(object sender, RoutedEventArgs e) => CorrectClassification(Category.Video);

    private void CorrectClassification(Category category)
    {
        var window = _host.LastWindow;
        if (!window.HasForeground)
        {
            MessageBox.Show(this, "当前没有前台窗口，无法生成规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var snapshot = _host.Timer.Snapshot;
        var rule = new Rule
        {
            Id = "u-" + Guid.NewGuid().ToString("N")[..6],
            Enabled = true,
            Category = CategoryExtensions.ToKey(category),
            MatchMode = "process",
            ProcessPattern = window.ProcessName,
            Note = $"{window.ProcessName}（{DateTimeOffset.Now:MM-dd HH:mm} 纠错）",
        };

        // 浏览器要连站点一起锁定，否则同进程的其他页面会被一起改掉
        if (RuleEngine.IsBrowser(window.ProcessName) && !string.IsNullOrEmpty(snapshot.CurrentSitePlatform))
        {
            rule.MatchMode = "all";
            rule.TitlePattern = snapshot.CurrentSitePlatform;
            rule.Note = $"{snapshot.CurrentSitePlatform}（{DateTimeOffset.Now:MM-dd HH:mm} 纠错）";
        }

        _host.RuleStore.Upsert(rule);
        _host.ReloadRules();

        SetStatus($"已生成用户规则：{rule.Note} → {CategoryExtensions.ToDisplay(category)}");
    }

    // ================================================================ 统计

    private void BuildRangeCombo()
    {
        _suppressRangeChange = true;
        try
        {
            RangeCombo.Items.Clear();
            RangeCombo.Items.Add("今天");
            RangeCombo.Items.Add("本周");
            RangeCombo.Items.Add("本月");
            RangeCombo.Items.Add("最近 7 天");
            RangeCombo.Items.Add("最近 30 天");
            RangeCombo.Items.Add("自定义");
            RangeCombo.Items.Add("全部时间");
            RangeCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressRangeChange = false;
        }
    }

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRangeChange)
        {
            return;
        }

        // 原来是按 SelectedIndex == 5 判断自定义。改成认文本，
        // 这样以后往 RangeCombo 里插新选项不会因为下标移位而悄悄改坏行为。
        // var isCustom = RangeCombo.SelectedIndex == 5;
        var isCustom = string.Equals(RangeCombo.SelectedItem as string, "自定义", StringComparison.Ordinal);
        RangeFrom.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        RangeTo.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

        if (isCustom)
        {
            if (RangeFrom.SelectedDate is null)
            {
                RangeFrom.SelectedDate = DateTime.Today.AddDays(-6);
            }
            if (RangeTo.SelectedDate is null)
            {
                RangeTo.SelectedDate = DateTime.Today;
            }
        }

        RefreshStats();
    }

    private (DateTimeOffset From, DateTimeOffset To) ResolveRange()
    {
        var offset = DateTimeOffset.Now.Offset;
        var today = DateTime.Today;
        var end = new DateTimeOffset(today.AddDays(1).AddSeconds(-1), offset);
        var selected = RangeCombo.SelectedItem as string ?? "今天";

        // ---- 旧实现（按下标取范围）保留备查，已被下面按文本匹配的版本取代 ----
        // 换掉它的原因：加「全部时间」这种新选项时，下标会整体错位，很容易改漏一处。
        // return RangeCombo.SelectedIndex switch
        // {
        //     1 => (new DateTimeOffset(today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), offset), end),
        //     2 => (new DateTimeOffset(new DateTime(today.Year, today.Month, 1), offset), end),
        //     3 => (new DateTimeOffset(today.AddDays(-6), offset), end),
        //     4 => (new DateTimeOffset(today.AddDays(-29), offset), end),
        //     5 => (new DateTimeOffset(RangeFrom.SelectedDate ?? today, offset),
        //           new DateTimeOffset((RangeTo.SelectedDate ?? today).AddDays(1).AddSeconds(-1), offset)),
        //     _ => (new DateTimeOffset(today, offset), end),
        // };

        return selected switch
        {
            "本周" => (new DateTimeOffset(today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), offset), end),
            "本月" => (new DateTimeOffset(new DateTime(today.Year, today.Month, 1), offset), end),
            "最近 7 天" => (new DateTimeOffset(today.AddDays(-6), offset), end),
            "最近 30 天" => (new DateTimeOffset(today.AddDays(-29), offset), end),
            "自定义" => (new DateTimeOffset(RangeFrom.SelectedDate ?? today, offset),
                         new DateTimeOffset((RangeTo.SelectedDate ?? today).AddDays(1).AddSeconds(-1), offset)),

            // 下界取一个足够早的固定日期：历史数据肯定会落在这之后，不用去查最早日期
            "全部时间" => (new DateTimeOffset(new DateTime(2000, 1, 1), offset),
                           new DateTimeOffset(today.AddDays(1), offset)),
            _ => (new DateTimeOffset(today, offset), end),
        };
    }

    private void OnRefreshStats(object sender, RoutedEventArgs e) => RefreshStats();

    private void RefreshStats()
    {
        try
        {
            var (from, to) = ResolveRange();

            var totals = _host.Repository.GetRangeTotals(from, to);
            var projects = _host.Repository.GetProjectTotals(from, to);
            var days = _host.Repository.GetDailyTotals(from, to);
            var sessionCount = _host.Repository.GetSessionCount(from, to);

            MetricNet.Text = TimeFormat.Short(totals.NetSeconds);
            MetricSuspended.Text = TimeFormat.Short(totals.SuspendedSeconds);

            var totalForRatio = totals.NetSeconds + totals.SuspendedSeconds;
            MetricRatio.Text = totalForRatio <= 0 ? "0%" : $"{totals.SuspendedSeconds * 100.0 / totalForRatio:F0}%";
            MetricSessions.Text = sessionCount.ToString();

            _projectRows.Clear();
            var sum = Math.Max(1, projects.Sum(p => p.NetSeconds));
            foreach (var item in projects)
            {
                _projectRows.Add(new ProjectRow
                {
                    ProjectName = item.ProjectName,
                    NetText = TimeFormat.Hms(item.NetSeconds),
                    RatioText = $"{item.NetSeconds * 100.0 / sum:F0}%",
                    SuspendedText = TimeFormat.Short(item.SuspendedSeconds),
                });
            }

            _dayRows.Clear();
            foreach (var day in days)
            {
                _dayRows.Add(new DayRow
                {
                    Day = day.Day,
                    NetText = TimeFormat.Hms(day.NetSeconds),
                    SuspendedText = TimeFormat.Short(day.SuspendedSeconds),
                });
            }

            DataDirText.Text =
                $"数据目录：{_host.Location.Root}（{( _host.Location.IsPortable ? "便携模式：与 exe 同目录" : "已回退")}）　|　" +
                $"日志：{Log.LogDirectory}";

            UpdateOverviewHint();
        }
        catch (Exception ex)
        {
            Log.Error("App", "刷新统计失败", ex);
            SetStatus("刷新统计失败：" + ex.Message);
        }
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        try
        {
            var (from, to) = ResolveRange();
            var segments = _host.Repository.GetSegments(from, to, null, 200000);

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = $"工时明细-{from:yyyyMMdd}-{to:yyyyMMdd}.csv",
                InitialDirectory = _host.Location.ExportDirectory,
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var builder = new System.Text.StringBuilder();
            builder.AppendLine("开始时间,结束时间,时长(秒),应用或站点,类别,是否计入");
            foreach (var segment in segments)
            {
                builder.AppendLine(string.Join(',',
                    Escape(segment.StartedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                    Escape(segment.EndedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                    segment.Seconds.ToString(),
                    Escape(segment.DisplayName),
                    Escape(CategoryExtensions.ToDisplay(segment.Category)),
                    segment.Counted ? "1" : "0"));
            }

            // 带 BOM，否则 Excel 打开中文会乱码
            File.WriteAllText(dialog.FileName, builder.ToString(), new System.Text.UTF8Encoding(true));
            SetStatus($"已导出 {segments.Count} 条到 {dialog.FileName}");
            Log.Info("App", $"导出 CSV：{dialog.FileName}（{segments.Count} 条）");
        }
        catch (Exception ex)
        {
            Log.Error("App", "导出 CSV 失败", ex);
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出 CSV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Escape(string value)
        => value.Contains(',') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    // ================================================================ 视图锚定与空态提示
    //
    // 这一段存在的唯一理由：让「打开程序看不到历史记录」这个错觉不再发生。
    // 数据一直是持久化在 data\worktimer.db 里的，问题只出在视图默认落在「今天」，
    // 于是在还没开始记录的某一天打开，到处都是 0，看着就像数据丢了。

    private static string DayKey(DateTime day) => day.ToString("yyyy-MM-dd");

    /// <summary>
    /// 启动时把视图锚到真正有数据的地方。
    /// 只在「今天完全没有数据」时才动手 —— 正常工作日打开，看到的仍然是今天。
    /// </summary>
    private void ApplyInitialViewAnchor()
    {
        var today = DayKey(DateTime.Today);

        try
        {
            // 概览：今天没有会话但历史有 → 切到「全部时间」，并在提示条里说明原因
            if (!_host.Repository.HasSessionsOnDay(today)
                && _host.Repository.GetLatestSessionDay() is { Length: > 0 } latestSession
                && latestSession != today)
            {
                SelectRangeByText("全部时间");
                Log.Info("App", $"今天（{today}）没有会话记录，概览已锚定到「全部时间」（最近一次 {latestSession}）");
            }

            // 明细：今天没有窗口段 → 直接翻到最近有明细的那一天
            if (!_host.Repository.HasSegmentsOnDay(today)
                && _host.Repository.GetLatestSegmentDay() is { Length: > 0 } latestSegment
                && latestSegment != today
                && DateTime.TryParseExact(latestSegment, "yyyy-MM-dd",
                       CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                RecordDate.SelectedDate = day;
                Log.Info("App", $"今天（{today}）没有明细，记录页已锚定到 {latestSegment}");
            }
        }
        catch (Exception ex)
        {
            // 锚定只是体验优化，失败就老老实实停在今天，不能让启动流程挂掉
            Log.Warn("App", "视图锚定失败，按默认的「今天」显示", ex);
        }
    }

    /// <summary>按文本选中范围。用文本而不是下标，避免选项顺序变化带来的隐性耦合。</summary>
    private void SelectRangeByText(string text)
    {
        var index = RangeCombo.Items.IndexOf(text);
        if (index < 0)
        {
            return;
        }

        _suppressRangeChange = true;
        try
        {
            RangeCombo.SelectedIndex = index;
        }
        finally
        {
            _suppressRangeChange = false;
        }
    }

    /// <summary>
    /// 概览空态提示。两种情况都会显示：
    ///  1. 今天还没开始记录 —— 说清「历史数据都在」，并给出跳到历史范围的入口；
    ///  2. 当前选中的范围里没有记录 —— 说明是范围问题，不是数据问题。
    /// </summary>
    private void UpdateOverviewHint()
    {
        try
        {
            var today = DayKey(DateTime.Today);
            var latestSession = _host.Repository.GetLatestSessionDay();
            var todayHasData = _host.Repository.HasSessionsOnDay(today);

            // 今天有记录，或者压根没有任何历史数据（首次使用）—— 都不需要提示
            if (todayHasData || string.IsNullOrEmpty(latestSession) || latestSession == today)
            {
                OverviewHintBar.Visibility = Visibility.Collapsed;
                return;
            }

            var range = RangeCombo.SelectedItem as string ?? "当前范围";

            // 关键是要说准：当前范围是不是真的把最近那次记录排除在外了。
            // 锚定之后范围通常是「全部时间」，这时候再说"当前范围看不到"就是自相矛盾。
            var (from, to) = ResolveRange();
            var inRange =
                string.CompareOrdinal(latestSession, from.ToString("yyyy-MM-dd")) >= 0 &&
                string.CompareOrdinal(latestSession, to.ToString("yyyy-MM-dd")) <= 0;

            OverviewHintText.Text =
                $"今天（{today}）还没有计时记录；最近一次记录在 {latestSession}。" +
                (inRange
                    ? "历史数据都在本机数据库里，下面显示的就是它们。"
                    : $"当前范围「{range}」不包含这一天，所以下面是空的 —— 数据没有丢，换个范围就能看到。");

            OverviewHintBar.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Warn("App", "更新概览提示条失败", ex);
            OverviewHintBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>记录页空态提示：当前日期没有明细时，指出最近有明细的是哪一天。</summary>
    private void UpdateRecordHint(int rowCount, DateTime selectedDay)
    {
        try
        {
            if (rowCount > 0)
            {
                RecordHintBar.Visibility = Visibility.Collapsed;
                return;
            }

            var latest = _host.Repository.GetLatestSegmentDay();
            var selectedKey = DayKey(selectedDay);

            RecordHintText.Text = !string.IsNullOrEmpty(latest) && latest != selectedKey
                ? $"{selectedKey} 没有窗口段明细。最近一次有明细的日期是 {latest}。"
                : $"{selectedKey} 没有窗口段明细。";

            // 「跳到那天」只在真的存在别的日期时才有意义
            RecordJumpButton.Visibility = !string.IsNullOrEmpty(latest) && latest != selectedKey
                ? Visibility.Visible
                : Visibility.Collapsed;

            RecordHintBar.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Warn("App", "更新记录页提示条失败", ex);
            RecordHintBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnJumpRecent7(object sender, RoutedEventArgs e)
    {
        SelectRangeByText("最近 7 天");
        RefreshStats();
    }

    private void OnJumpAllTime(object sender, RoutedEventArgs e)
    {
        SelectRangeByText("全部时间");
        RefreshStats();
    }

    /// <summary>记录页跳到最近有明细的那一天。</summary>
    private void OnJumpToLatestSegmentDay(object sender, RoutedEventArgs e)
    {
        var latest = _host.Repository.GetLatestSegmentDay();
        if (string.IsNullOrEmpty(latest)
            || !DateTime.TryParseExact(latest, "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            SetStatus("还没有任何窗口段明细。");
            return;
        }

        // SelectedDate 变了会触发 OnRecordDateChanged → RefreshSegments，不用手动再刷
        RecordDate.SelectedDate = day;
        SetStatus($"已跳到 {latest}。");
    }

    // ================================================================ 记录

    private void OnRecordDateChanged(object sender, SelectionChangedEventArgs e) => RefreshSegments();

    private void OnRecordToday(object sender, RoutedEventArgs e) => RecordDate.SelectedDate = DateTime.Today;

    private void OnReloadSegments(object sender, RoutedEventArgs e) => RefreshSegments();

    private void RefreshSegments()
    {
        try
        {
            var day = RecordDate.SelectedDate ?? DateTime.Today;
            var from = new DateTimeOffset(day, DateTimeOffset.Now.Offset);
            var to = from.AddDays(1).AddSeconds(-1);

            var segments = _host.Repository.GetSegments(from, to);

            _segmentRows.Clear();
            foreach (var segment in segments)
            {
                _segmentRows.Add(new SegmentRow
                {
                    Range = TimeFormat.Range(segment.StartedAt, segment.EndedAt),
                    Duration = TimeFormat.Hms(segment.Seconds),
                    AppName = segment.DisplayName,
                    Category = CategoryExtensions.ToDisplay(segment.Category),
                    Counted = segment.Counted ? "✓ 计入" : "✗ 未计入",
                });
            }

            UpdateRecordHint(segments.Count, day);
            SetStatus(segments.Count > 0
                ? $"已载入 {segments.Count} 条窗口段（{day:yyyy-MM-dd}）"
                : $"{day:yyyy-MM-dd} 没有窗口段明细。");
        }
        catch (Exception ex)
        {
            Log.Error("App", "刷新明细失败", ex);
        }
    }

    // ================================================================ 诊断

    private void OnRefreshDiagnostics(object sender, RoutedEventArgs e)
    {
        RefreshDiagnostics();
        RefreshLogs();
    }

    private void OnForceSample(object sender, RoutedEventArgs e)
    {
        _host.TickOnce();
        RefreshDiagnostics();
        RefreshLogs();
    }

    private void RefreshDiagnostics()
    {
        try
        {
            _diagnosticRows.Clear();
            foreach (var (key, value) in _host.BuildDiagnostics())
            {
                _diagnosticRows.Add(new KvRow { Key = key, Value = value });
            }
        }
        catch (Exception ex)
        {
            Log.Error("App", "刷新诊断失败", ex);
        }
    }

    private void RefreshLogs()
    {
        LogList.Items.Clear();
        foreach (var entry in Log.Recent(200))
        {
            LogList.Items.Add(entry.ToString().Replace(Environment.NewLine, " | "));
        }

        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e) => OpenPath(Log.LogDirectory);

    private void OnOpenDataDir(object sender, RoutedEventArgs e) => OpenPath(_host.Location.Root);

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"# 工作计时诊断信息 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
            foreach (var (key, value) in _host.BuildDiagnostics())
            {
                builder.AppendLine($"{key}\t{value}");
            }

            builder.AppendLine();
            builder.AppendLine("# 最近日志");
            foreach (var entry in Log.Recent(200))
            {
                builder.AppendLine(entry.ToString());
            }

            Clipboard.SetText(builder.ToString());
            SetStatus("诊断信息已复制到剪贴板");
        }
        catch (Exception ex)
        {
            Log.Error("App", "复制诊断信息失败", ex);
        }
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("App", $"打开目录失败 {path}", ex);
        }
    }

    // ================================================================ 设置

    private void BuildSettingsNav()
    {
        foreach (var group in SettingsPageBuilder.Groups)
        {
            SettingsNav.Items.Add(group.Title);
        }

        SettingsNav.SelectedIndex = 0;
    }

    private void OnSettingsGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNav.SelectedIndex < 0)
        {
            return;
        }

        ShowSettingsGroup(SettingsNav.SelectedIndex);
    }

    private void ShowSettingsGroup(int index)
    {
        if (index < 0 || index >= SettingsPageBuilder.Groups.Count)
        {
            return;
        }

        var group = SettingsPageBuilder.Groups[index];
        SettingsGroupTitle.Text = group.Title;
        SettingsGroupHint.Text = group.Hint;

        SettingsContent.Children.Clear();
        SettingsContent.Children.Add(SettingsPageBuilder.Build(group, _host, OnSettingsDirty, RefreshStats));
        SettingsSaveHint.Text = string.Empty;
    }

    private void OnSettingsDirty() => SettingsSaveHint.Text = "有未保存的改动，点「保存设置」生效";

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            _host.SaveConfig();
            _host.ReloadRules();
            _host.ReconfigureTicking();
            SettingsSaveHint.Text = $"已保存 {DateTimeOffset.Now:HH:mm:ss}";
            SetStatus("设置已保存并生效");
            RefreshDiagnostics();
        }
        catch (Exception ex)
        {
            Log.Error("Config", "保存设置失败", ex);
            MessageBox.Show(this, "保存失败：" + ex.Message, "保存设置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenConfigFile(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _host.ConfigStore.Path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Config", "打开配置文件失败", ex);
        }
    }

    // ================================================================ 销毁

    protected override void OnClosed(EventArgs e)
    {
        if (_subscribed)
        {
            _host.SnapshotUpdated -= OnSnapshotUpdated;
            _host.Notice -= OnNotice;
            _host.ProjectsChanged -= OnProjectsChanged;
            Log.EntryAdded -= OnLogEntry;
            _subscribed = false;
        }

        _uiTimer.Stop();
        base.OnClosed(e);
    }
}
