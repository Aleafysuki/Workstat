# WorkTimer · 工时统计工具

> Windows 托盘驻留的**工时 / 时间去向统计工具**。前台窗口采样 + 规则分类，自动把你的电脑使用时间切成「工作 / 娱乐 / 视频 / 会议 / 空闲」等类别，并提供计时、概览、记录与诊断界面。
>
> **WorkTimer** — a Windows tray-based work-hour / time-tracking tool. It samples the foreground window, classifies activity by rules, and shows where your time goes.

---

## 特性（Features）

- **托盘驻留**：最小化到系统托盘，右键菜单控制，贴边自动收起的悬浮窗（Floating Widget）。
- **前台采样**：2 秒级前台窗口采样（UWP / 全屏判定）。
- **防抖与碎片合并**：5 秒状态级防抖 + 窗口段碎片合并，避免短切换产生大量噪声。
- **规则分类**：内置 106 条分类规则 + 68 个站点表（B 站 / YouTube 等），支持 `Inherit` 继承与用户规则优先。
- **状态机引擎**：娱乐运行、追溯作废、待判定 / 实时双模式、5 种阈值策略、空闲、会议抑制、连续离开自动结束会话。
- **隐私三级**：窗口段整合、站点只记平台名（不记完整标题）。
- **数据模型**：SQLite 本地存储（schema v1），崩溃恢复按心跳补记。
- **诊断页**：分级分类日志，便于排查采样 / 分类问题。
- **单元测试**：46 个单元测试（xUnit），MVP 阶段全绿。

---

## 技术栈（Tech Stack）

| 项 | 说明 |
|---|---|
| 语言 | C#（LangVersion `latest`，Nullable enable，ImplicitUsings enable） |
| UI | WPF（主界面 / 悬浮窗）+ WinForms `NotifyIcon`（托盘，WPF 无原生托盘） |
| 框架 | `.NET 8` / `net8.0-windows`（Windows Desktop） |
| 存储 | SQLite（`Microsoft.Data.Sqlite`） |
| 测试 | xUnit（`WorkTimer.Tests`） |
| 结构 | `WorkTimer.Core`（领域 / 采样 / 分类 / 计时 / 数据）+ `WorkTimer.App`（界面 / 托盘 / 悬浮窗） |

---

## 目录结构（Layout）

```
Workstat/
├── WorkTimer/                 # Visual Studio 解决方案根
│   ├── WorkTimer.sln          # 解决方案（仅含 src 下三个项目）
│   ├── Directory.Build.props  # 全局编译选项
│   └── src/
│       ├── WorkTimer.Core/    # 领域层：采样 / 分类 / 计时 / 数据 / 配置 / 诊断
│       ├── WorkTimer.App/     # 界面层：主窗口 / 托盘 / 悬浮窗 / 设置
│       └── WorkTimer.Tests/   # xUnit 单元测试
└── docs/                      # 开发设计文档（含 v0.1 归档）
```

> 注：`_baseline/` 为早期技术验证用的临时工程，不属于发布内容，已从仓库中排除。

---

## 构建与运行（Build & Run）

需要 **Windows** 与 **.NET 8 SDK（含 Windows Desktop 负载）**。

```bash
cd WorkTimer
dotnet restore
dotnet build -c Release
dotnet run --project src/WorkTimer.App -c Release
```

运行后会常驻系统托盘。默认数据为「便携模式」（`dataDirMode: portable`），配置与数据库自动生成在程序所在目录的 `data/` 下；首次运行可直接编辑 `data/config.json`、`rules.user.json`、`sites.json` 后重启生效（设置页 UI 为后续迭代项）。

运行测试：

```bash
dotnet test
```

---

## 许可证（License）

[MIT](./LICENSE) © 2026 Aleafysuki
