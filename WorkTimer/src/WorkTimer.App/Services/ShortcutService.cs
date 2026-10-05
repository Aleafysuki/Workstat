using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WorkTimer.Core.Diagnostics;

namespace WorkTimer.App.Services;

/// <summary>
/// 桌面快捷方式创建。
///
/// 为什么用 IShellLink 而不是 "调 PowerShell / VBScript 生成 .lnk"：
///  - 不需要外部进程，不依赖脚本执行策略（有些机器的 ExecutionPolicy 是 Restricted）；
///  - 不会为了建一个快捷方式往磁盘上写临时脚本；
///  - 目标路径里含中文或空格时也不会踩到脚本编码的坑。
///
/// 「起始位置」显式设成 exe 所在目录：便携模式下数据目录是相对可执行文件解析的，
/// 把起始位置写对，以后无论从哪儿启动行为都一致。
/// </summary>
public static class ShortcutService
{
    /// <summary>桌面快捷方式的默认显示名（同时也是 .lnk 的文件名）。</summary>
    public const string DefaultDisplayName = "工作计时";

    /// <summary>桌面快捷方式应该落地的完整路径。</summary>
    public static string DesktopShortcutPath(string displayName = DefaultDisplayName)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            displayName + ".lnk");

    /// <summary>桌面快捷方式是否已经存在。</summary>
    public static bool Exists(string displayName = DefaultDisplayName)
        => File.Exists(DesktopShortcutPath(displayName));

    /// <summary>
    /// 在桌面创建（或覆盖）指向当前程序的快捷方式，返回 .lnk 的完整路径。
    /// 失败会抛出异常，由调用方决定怎么告诉用户。
    /// </summary>
    public static string CreateDesktopShortcut(string displayName = DefaultDisplayName)
    {
        // 单文件发布下 ProcessPath 就是那个 exe 本身的路径
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            throw new InvalidOperationException("拿不到当前可执行文件的路径，无法创建快捷方式。");
        }

        var workingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
        var linkPath = DesktopShortcutPath(displayName);
        var existed = File.Exists(linkPath);

        CreateLink(linkPath, exePath, workingDirectory, displayName);

        Log.Info("App", $"{(existed ? "已覆盖" : "已创建")}桌面快捷方式：{linkPath} → {exePath}");
        return linkPath;
    }

    private static void CreateLink(string linkPath, string targetPath, string workingDirectory, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);

            // 第二个参数 true = 记住这次保存，写入 .lnk 文件
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    // ---------------------------------------------------------------- COM 声明

    /// <summary>ShellLink 的 CoClass。用 new 实例化时由运行时执行 CoCreateInstance。</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>IShellLink 要通过 IPersistFile 才能真正写盘。</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
