using System.Diagnostics;
using System.Text;
using WorkTimer.Core.Diagnostics;

namespace WorkTimer.Core.Monitoring;

/// <summary>
/// 前台窗口采集器。每调用一次 <see cref="Capture"/> 就抓一份当且快照。
/// 注意：这里<b>只读前台窗口</b>，不枚举后台进程 —— 所以后台挂着游戏或聊天软件
/// 不会影响判定，这是架构层面的保证。
/// </summary>
public sealed class ForegroundWindowWatcher
{
    private readonly int _selfPid;

    public ForegroundWindowWatcher()
    {
        _selfPid = Environment.ProcessId;
    }

    /// <summary>本程序自身的进程 Id，用于把自身窗口识别为 Inherit。</summary>
    public int SelfPid => _selfPid;

    public WindowInfo Capture()
    {
        var idle = IdleDetector.GetIdleSeconds();

        IntPtr hwnd;
        try
        {
            hwnd = NativeMethods.GetForegroundWindow();
        }
        catch (Exception ex)
        {
            Log.Warn("Monitor", "GetForegroundWindow 调用失败", ex);
            return WindowInfo.None(idle);
        }

        if (hwnd == IntPtr.Zero)
        {
            return WindowInfo.None(idle);
        }

        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            var className = ReadClassName(hwnd);
            var title = ReadTitle(hwnd);

            // UWP 应用（含系统设置、商店应用、Xbox 应用）的前台窗口属于 ApplicationFrameHost，
            // 真正的应用进程藏在子窗口里。不处理的话所有 UWP 应用都会被判成同一个进程。
            var (processName, processPath) = ResolveProcess((int)pid);
            if (string.Equals(processName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                if (TryResolveUwpHost(hwnd, out var realPid, out var realTitle))
                {
                    var (realName, realPath) = ResolveProcess(realPid);
                    if (!string.IsNullOrEmpty(realName))
                    {
                        processName = realName;
                        processPath = realPath;
                        if (!string.IsNullOrEmpty(realTitle))
                        {
                            title = realTitle;
                        }
                    }
                }
            }

            var fullscreen = IsFullscreen(hwnd);

            return new WindowInfo(hwnd, (int)pid, processName, processPath, title, className, fullscreen, idle);
        }
        catch (Exception ex)
        {
            Log.Warn("Monitor", $"采集前台窗口信息失败 hwnd=0x{hwnd.ToInt64():X}", ex);
            return WindowInfo.None(idle);
        }
    }

    private static string ReadTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        var len = NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
        if (len > 0)
        {
            return sb.ToString();
        }

        // 部分应用（尤其游戏与自绘界面）不响应 GetWindowText，退回 WM_GETTEXT。
        try
        {
            var buffer = new StringBuilder(1024);
            var ok = NativeMethods.SendMessageTimeout(
                hwnd, NativeMethods.WM_GETTEXT, (IntPtr)buffer.Capacity, buffer,
                NativeMethods.SMTO_ABORTIFHUNG, 200, out _);
            if (ok != IntPtr.Zero)
            {
                return buffer.ToString();
            }
        }
        catch
        {
            // 超时或对方未响应，忽略即可
        }

        return string.Empty;
    }

    private static string ReadClassName(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            var len = NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        try
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return false;
            }

            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfoW(monitor, ref mi))
            {
                return false;
            }

            var m = mi.rcMonitor;
            // 留 2px 容差，避免某些全屏窗口边缘差 1px 判不出来
            const int tolerance = 2;
            return rect.Left <= m.Left + tolerance
                && rect.Top <= m.Top + tolerance
                && rect.Right >= m.Right - tolerance
                && rect.Bottom >= m.Bottom - tolerance;
        }
        catch
        {
            return false;
        }
    }

    private static (string Name, string Path) ResolveProcess(int pid)
    {
        if (pid <= 0)
        {
            return (string.Empty, string.Empty);
        }

        var path = QueryProcessPath(pid);
        if (!string.IsNullOrEmpty(path))
        {
            return (Path.GetFileNameWithoutExtension(path), path);
        }

        // 提权进程读不到完整路径，退回进程名（规则本身就是按进程名匹配的，不受影响）
        try
        {
            using var p = Process.GetProcessById(pid);
            return (p.ProcessName, string.Empty);
        }
        catch (Exception ex)
        {
            Log.Debug("Monitor", $"无法解析进程 pid={pid}：{ex.GetType().Name}");
            return (string.Empty, string.Empty);
        }
    }

    private static string QueryProcessPath(int pid)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(handle, 0, sb, ref size) ? sb.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// 从 ApplicationFrameHost 的子窗口里找出真正的 UWP 应用进程。
    /// 子窗口类名固定是 Windows.UI.Core.CoreWindow，其 pid 才是应用本体。
    /// </summary>
    private static bool TryResolveUwpHost(IntPtr host, out int pid, out string title)
    {
        // 先落到局部变量（out 参数不能直接在 lambda 里用），最后再统一赋值出去
        var foundPid = 0;
        var foundTitle = string.Empty;

        try
        {
            // 委托必须在调用期间保持存活，否则 GC 会回收导致回调崩溃
            EnumWindowsProc callback = (child, _) =>
            {
                var cls = ReadClassName(child);
                if (!string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(child, out var childPid);
                if (childPid == 0)
                {
                    return true;
                }

                foundPid = (int)childPid;
                foundTitle = ReadTitle(child);
                return false; // 找到就停
            };

            NativeMethods.EnumChildWindows(host, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        catch (Exception ex)
        {
            Log.Debug("Monitor", $"UWP 子窗口解析失败：{ex.GetType().Name}");
        }

        pid = foundPid;
        title = foundTitle;
        return foundPid != 0;
    }
}
