using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WorkTimer.App.Services;
using WorkTimer.App.Views;
using WorkTimer.Core.Diagnostics;

namespace WorkTimer.App;

/// <summary>
/// 应用宿主。用 Application 而不是"启动窗口"来管理生命周期（ShutdownMode = OnExplicitShutdown），
/// 这样程序可以在只显示托盘 / 只显示悬浮窗的状态下活着。
/// </summary>
internal sealed class WorkTimerApp : Application
{
    private readonly EventWaitHandle _activateEvent;

    private AppHost? _host;
    private MainWindow? _main;
    private WidgetWindow? _widget;
    private TrayController? _tray;
    private Thread? _listener;
    private volatile bool _exiting;

    public WorkTimerApp(EventWaitHandle activateEvent)
    {
        _activateEvent = activateEvent;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 主题必须在任何窗口创建之前挂上，否则 StaticResource 解析不到
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/WorkTimer;component/Theme.xaml", UriKind.Relative),
        });

        try
        {
            _host = new AppHost();
            _host.Initialize();
        }
        catch (Exception ex)
        {
            Fatal("初始化失败，程序无法启动。", ex);
            Shutdown();
            return;
        }

        InstallExceptionHandlers(_host);

        _host.ShowMainWindowRequested += ShowMainWindow;

        _main = new MainWindow(_host);

        _widget = new WidgetWindow(_host);
        _host.Widget = _widget;

        if (_host.Config.FloatingWidget.Enabled)
        {
            _widget.Attach();
            _widget.Show();
            Log.Info("Widget", "悬浮窗已显示");
        }

        _tray = new TrayController(
            _host,
            ShowMainWindow,
            ToggleWidget,
            () => _widget?.IsVisible ?? false,
            RequestExit);
        _host.Tray = _tray;

        if (!_host.Config.General.StartMinimizedToTray)
        {
            ShowMainWindow();
        }
        else
        {
            Log.Info("App", "已启动并最小化到托盘");
        }

        _host.StartTicking();

        StartActivationListener();
    }

    // ================================================================ 窗口

    private void ShowMainWindow()
    {
        if (_main is null || _exiting)
        {
            return;
        }

        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
        _main.Topmost = true;
        _main.Topmost = false;
        _main.Focus();
    }

    private void ToggleWidget()
    {
        if (_widget is null || _host is null)
        {
            return;
        }

        if (_widget.IsVisible)
        {
            _widget.Hide();
            _host.Config.FloatingWidget.Enabled = false;
            Log.Info("Widget", "悬浮窗已隐藏");
        }
        else
        {
            _widget.Show();
            _host.Config.FloatingWidget.Enabled = true;
            Log.Info("Widget", "悬浮窗已显示");
        }

        _host.SaveConfig();
    }

    /// <summary>第二个实例启动时会发信号过来，把主窗口拉到前台。</summary>
    private void StartActivationListener()
    {
        _listener = new Thread(() =>
        {
            while (!_exiting)
            {
                try
                {
                    if (!_activateEvent.WaitOne(500))
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("App", $"激活监听退出：{ex.Message}");
                    return;
                }

                if (_exiting)
                {
                    return;
                }

                Log.Info("App", "检测到重复启动，正在唤醒主窗口");
                Dispatcher.BeginInvoke(new Action(ShowMainWindow), DispatcherPriority.Normal);
            }
        })
        {
            IsBackground = true,
            Name = "WorkTimer.ActivationListener",
        };

        _listener.Start();
    }

    // ================================================================ 退出

    private void RequestExit()
    {
        var snapshot = _host?.Timer.Snapshot;

        var message = snapshot is { HasSession: true }
            ? $"退出后当前计时会暂停保存，下次启动可以继续。\r\n\r\n项目：{snapshot.ProjectName}\r\n已计：{TimeFormat.Hms(snapshot.NetSeconds)}\r\n\r\n确定退出吗？"
            : "确定退出工作计时吗？";

        if (MessageBox.Show(message, "退出", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        ExitApplication();
    }

    public void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Log.Info("App", "开始退出流程");

        try
        {
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("App", "释放托盘失败", ex);
        }

        try
        {
            _widget?.Hide();
        }
        catch (Exception ex)
        {
            Log.Warn("App", "隐藏悬浮窗失败", ex);
        }

        // 退出时把会话保存为暂停状态，下次启动可继续
        _host?.Shutdown();

        try
        {
            _main?.CloseForReal();
        }
        catch (Exception ex)
        {
            Log.Warn("App", "关闭主窗口失败", ex);
        }

        Shutdown();
    }

    // ================================================================ 异常兜底

    /// <summary>
    /// 三层兜底。目标很明确：任何未处理异常都必须落到日志文件里，
    /// 而不是只弹一个"程序已停止工作"让人无从下手。
    /// </summary>
    private void InstallExceptionHandlers(AppHost host)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("App", "UI 线程未处理异常", args.Exception);
            MessageBox.Show(
                $"界面发生未处理异常：\r\n\r\n{args.Exception.Message}\r\n\r\n详细信息已写入日志：\r\n{Log.CurrentFilePath}",
                "工作计时 - 异常", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                Log.Error("App", $"后台线程未处理异常（IsTerminating={args.IsTerminating}）", exception);
            }
            else
            {
                Log.Error("App", $"后台线程未处理异常：{args.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("App", "未观察的任务异常", args.Exception);
            args.SetObserved();
        };
    }

    private static void Fatal(string title, Exception ex)
    {
        var logPath = Log.CurrentFilePath ?? "(未能创建日志文件)";

        try
        {
            Log.Error("App", title, ex);
        }
        catch
        {
            // 日志都写不了就只能弹窗了
        }

        MessageBox.Show(
            $"{title}\r\n\r\n{ex.GetType().Name}: {ex.Message}\r\n\r\n日志位置：\r\n{logPath}",
            "工作计时", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
