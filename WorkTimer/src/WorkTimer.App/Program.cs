using System.Threading;
using WorkTimer.App.Services;

namespace WorkTimer.App;

internal static class Program
{
    private const string MutexName = @"Local\WorkTimer.SingleInstance.Mutex";
    private const string ActivateEventName = @"Local\WorkTimer.SingleInstance.Activate";

    [STAThread]
    private static void Main(string[] args)
    {
        if (!TryRunCommandLine(args))
        {
            return;
        }

        // 单实例：第二个实例直接退出，并通过命名事件把已有实例的主窗口拉到前台
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        using var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

        if (!isFirstInstance)
        {
            try
            {
                activateEvent.Set();
            }
            catch
            {
                // 已有实例没在监听就静默退出
            }

            return;
        }

        var app = new WorkTimerApp(activateEvent);
        app.Run();
    }

    /// <summary>
    /// 命令行开关，不启动界面，做完一件事就退出。
    ///
    /// 加它的动机很实在：桌面快捷方式这种"程序外"的场景，靠界面点按钮不方便，
    /// 而 PowerShell 调用 WScript.Shell 建 .lnk 在有些环境（受限执行策略 / 沙箱）会失败。
    /// 顺带的好处是 ShortcutService 里的 COM 互操作能被真正跑一遍，而不是只编译得过。
    /// </summary>
    /// <returns>true 表示继续正常启动；false 表示"这条命令已经处理完了，直接退出"。</returns>
    private static bool TryRunCommandLine(string[] args)
    {
        if (args.Length == 0)
        {
            return true;
        }

        var command = args[0].TrimStart('-', '/').ToLowerInvariant();

        switch (command)
        {
            case "create-desktop-shortcut":
            case "shortcut":
                try
                {
                    var linkPath = ShortcutService.CreateDesktopShortcut();
                    Console.WriteLine($"已创建桌面快捷方式：{linkPath}");
                    Console.WriteLine($"指向：{Environment.ProcessPath}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"创建快捷方式失败：{ex.GetType().Name}: {ex.Message}");
                    Environment.ExitCode = 1;
                }

                return false;

            case "help":
            case "?":
                Console.WriteLine(
                    """
                    工作计时

                      不加参数                    正常启动程序
                      --create-desktop-shortcut   在桌面创建快捷方式后退出
                      --help                      显示这段说明
                    """);
                return false;

            default:
                // 认不出来的参数不拦着启动，只提示一句 ——
                // 免得以后新旧版本混用时，多一个开关反而导致程序起不来
                Console.Error.WriteLine($"未知参数：{args[0]}（可用 --help 查看支持的开关）");
                return true;
        }
    }
}
