<p align="center">
  <img src="https://raw.githubusercontent.com/zkkkillua/NightGate/main/assets/NightGate.Icon.svg" alt="NightGate 月牙图标" width="80" height="80">
</p>

<h1 align="center">NightGate · 收尾</h1>

<p align="center">帮助你按时结束游戏和追剧的 Windows 早睡工具。</p>

<p align="center">Windows 11 x64 · .NET 10 / WPF · Chrome 扩展 · 本地存储</p>

<p align="center">
  <a href="#开始使用">开始使用</a> ·
  <a href="https://github.com/zkkkillua/NightGate/blob/main/USER-GUIDE.zh-CN.md">使用指南</a> ·
  <a href="https://github.com/zkkkillua/NightGate/blob/main/docs/DEVELOPMENT.md">开发文档</a> ·
  <a href="https://github.com/zkkkillua/NightGate/issues">反馈问题</a>
</p>

---

一局结束后再开一局，片尾之后自动播放下一集，普通闹钟往往点掉就忘了。

收尾把结束时间提前安排好：先停止新开游戏和继续追剧，给已经开始的内容留出收尾时间，
再按计划锁屏。临时需要继续用电脑时，有明确时限的例外入口。

项目处于早期开发阶段。当前源码版本为 **0.3.17**，Chrome 扩展版本为 **0.1.5**。

## 能做什么

| 功能 | 使用方式 |
| --- | --- |
| 游戏收尾 | 扫描 Steam、Epic、Xbox 和常见安装目录，也可手动添加。每个游戏单独设置 15–90 分钟的一局时长，据此提前停止新开局。 |
| 视频防连播 | 在选定的网站允许当前视频结束，拦截下一集和新的娱乐页面。支持 Chrome 中的哔哩哔哩、爱奇艺、腾讯视频、Netflix 和 YouTube。 |
| 到点锁屏 | 到约定时间锁定 Windows；限制期内重新登录，会保留例外入口并再次锁屏。到起床时间后解除限制。 |
| 倒计时浮窗 | 显示当前时间和剩余时间。游戏仍在运行时显示更大的卡片；位置和外圈光效随机变化，不抢键盘焦点，鼠标可穿透。 |
| 渐进作息 | 四级作息台阶逐步提前结束时间，也支持自定义作息。周五、周六默认顺延一小时。 |
| 本地回顾 | 查看锁屏时间、例外原因和近期趋势。没有连续打卡清零或失败惩罚。 |

### 一个晚上的流程

以第一级默认作息为例；游戏的一局时长和自定义设置会影响实际停止开局时间。

| 阶段 | 默认时间 | 收尾会做什么 |
| --- | --- | --- |
| 自由使用 | 收尾提醒前 | 正常使用，不在 21:00 固定弹出提醒。 |
| 最后开局 | 00:05 起 | 停止新的受限娱乐，让已开始的游戏和视频收尾。 |
| 结束使用 | 00:40 | 锁定 Windows；不主动断网或修改电源计划。 |
| 早晨 | 09:00 | 解除限制，显示简短结果。 |

四级计划的锁屏时间从 00:40 逐步提前到 23:55。最近四个合格工作夜达成三个后，
才会提示进入下一台阶；未达成就保持当前台阶。

### 确实需要继续使用时

| 例外 | 时限与规则 |
| --- | --- |
| 团队救场 | 每滚动 168 小时一次，延长 20 分钟，只允许当前游戏和已配置的语音工具。 |
| 紧急情况 | 选择健康、安全或紧急工作原因后，立即完整解锁 30 分钟，不限制次数。 |
| 娱乐再用 | 先锁屏冷静 10 分钟，再提供一次不可续期的 20 分钟窗口。 |

例外和冷静期保存在本机，重启、重新登录或回拨系统时间不会刷新机会。

## 开始使用

**目前仓库提供源码，尚未发布可下载的正式安装包。** GitHub 的「Download ZIP」下载的是源码，
不能直接当作 Windows 程序安装。自行构建和生成 MSI 的步骤见
[开发文档](https://github.com/zkkkillua/NightGate/blob/main/docs/DEVELOPMENT.md)。

运行环境：

- Windows 11 x64，内部版本 22000 或更高。
- 一个 Windows 登录账户、一个 Chrome 用户资料。
- iPhone 为可选项；不使用 iPhone 也可以使用电脑端保护。

构建并安装后，从桌面、开始菜单或托盘打开「收尾」，按首次向导完成配置：

1. 选择作息，检查是否有需要停用的旧关机任务。
2. 扫描并勾选游戏，调整各自的一局时长，选择要限制的网站。
3. 加载 Chrome 扩展并授予所选网站权限，确认连接状态。
4. 如使用 iPhone，按清单手动配置睡眠计划与屏幕使用时间。

Chrome 扩展需手工加载，尚未通过 Chrome 网上应用店分发。完整操作、升级、卸载和恢复说明见
[中文使用指南](https://github.com/zkkkillua/NightGate/blob/main/USER-GUIDE.zh-CN.md)。

## 隐私与使用边界

设置与历史保存在本地 SQLite 中，无需注册账号，没有云同步。
历史事件只记录时间、事件类型和网站类别，不保存页面标题或完整浏览历史；原始事件默认保留 90 天。
清除历史不会重置当前夜间状态。

收尾的定位是「防冲动，不防蓄意拆除」：

- 管理员可以停止服务或卸载。它不使用内核驱动、反卸载机制或强制浏览器策略。
- 门禁前已经运行的游戏不会被强杀；新启动的受限游戏会先收到正常退出请求，仍未退出时才终止。
- 服务或数据库异常时安全放行。扩展单独不可用会显示网页保护降级，电脑端锁屏仍可执行。
- 托盘菜单中的「退出」会停止桌面保护，并通知 Chrome 解除网页限制。
- iPhone 设置需要手动完成，Windows 端不能远程控制手机。收尾不诊断或治疗失眠。

### 已知限制

独占全屏游戏可能遮住浮窗，可尝试无边框全屏或窗口模式。不同显示器缩放比例、游戏和视频网站
可能带来兼容问题；0.3.17 的随机移动与光效仍需要更多真实设备验证。自动化测试不能代替这些
手工验收，也不能证明提醒对每个人都有效。

## 构建与测试

准备 Windows 11 x64、**.NET SDK 10.0.301** 和 **Node.js 24**。
SDK 版本由 `global.json` 固定；首次还原需要能访问 NuGet.org。

在 PowerShell 中执行：

```powershell
git clone https://github.com/zkkkillua/NightGate.git
cd NightGate

.\scripts\Restore.ps1
.\scripts\Test.ps1 -SkipRestore
.\scripts\Build.ps1 -SkipRestore
```

这些命令构建并测试源码，不会安装服务、操作旧关机任务或锁定当前电脑。
正式安装包的运行时依赖、签名核验和打包步骤见
[构建与发布说明](https://github.com/zkkkillua/NightGate/blob/main/docs/DEVELOPMENT.md)。

### 代码结构

```text
src/
  NightGate.Core/               作息、夜间状态机和例外规则
  NightGate.Protocol/           本机通信协议
  NightGate.Service/            后台服务与 SQLite 持久化
  NightGate.Desktop/            WPF 界面、托盘、进程检测与锁屏
  NightGate.NativeHost/         Chrome 与本机服务的通信桥接
  NightGate.Chrome.Extension/   Manifest V3 网页保护扩展
tests/                         核心逻辑、桌面端、服务、扩展与发布测试
scripts/                       构建、测试、发布与验证
installer/                     MSI 与备用安装脚本
```

## 参与项目

欢迎通过 [Issues](https://github.com/zkkkillua/NightGate/issues) 报告问题或讨论改进。
兼容性反馈请附上 Windows 与程序版本、游戏或网站名称、复现步骤；
浮窗问题也请说明显示器缩放和全屏模式。提交截图、日志前，请先移除个人信息。

涉及锁屏时机、进程处理或例外规则的改动，请先开 Issue 说明预期行为。
提交代码时附上相关测试，不要提交本机配置、数据库、凭据或生成的安装包。

## 文档

- [使用指南](https://github.com/zkkkillua/NightGate/blob/main/USER-GUIDE.zh-CN.md)：首次设置、Chrome、iPhone 与故障排查。
- [开发文档](https://github.com/zkkkillua/NightGate/blob/main/docs/DEVELOPMENT.md)：构建、测试、发布与手工验收。
- [安装与维护](https://github.com/zkkkillua/NightGate/blob/main/docs/OPERATIONS.md)：安装事务、升级卸载、旧关机任务恢复。
- [设计记录](https://github.com/zkkkillua/NightGate/tree/main/docs/specs)：近期功能变更的设计与验收要求。

## 许可证

当前仓库尚未添加许可证文件。
