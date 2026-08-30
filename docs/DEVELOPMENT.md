# 开发、构建与验收

本页面向源码贡献者和发布维护者。日常使用见[中文使用指南](https://github.com/zkkkillua/NightGate/blob/main/USER-GUIDE.zh-CN.md)，安装事务和恢复边界见[安装与维护](https://github.com/zkkkillua/NightGate/blob/main/docs/OPERATIONS.md)。

## 从干净克隆构建

准备 x64 Windows 11、Git、精确版本的 **.NET SDK 10.0.301**（只有运行时不够）和 **Node.js 24**（参考版本 24.19.0）。可使用 Windows PowerShell 5.1 或 PowerShell 7。首次还原需能访问 NuGet.org；WPF 和 Windows Installer COM 相关测试必须在 Windows 上执行。

```powershell
git clone https://github.com/zkkkillua/NightGate.git
Set-Location NightGate
.\scripts\Restore.ps1
.\scripts\Test.ps1 -SkipRestore
.\scripts\Build.ps1 -SkipRestore
```

不需要安装 Codex，也不需要复制作者的 `work/` 或缓存。SDK 和 Node 可以安装到系统 PATH。

- [global.json](../global.json) 禁止自动换用其他 SDK 版本；[Common.ps1](../scripts/Common.ps1) 在 `work/.dotnet/dotnet.exe` 存在时优先使用它，否则查找 PATH。
- Node 可用 `NIGHTGATE_NODE` 指定；未指定时查找配套或系统 Node。
- `work/dotnet-home` 和 `work/.nuget/packages` 由脚本创建，不属于源码。
- 构建和测试使用 Release、x64、确定性构建，并将警告视为错误；正式发布目标为 `win-x64`。
- 默认验证不会安装服务、注册 Chrome、启动生产程序、锁屏或操作真实计划任务。

## 离线还原

默认使用 [NuGet.Config](../NuGet.Config) 中的在线源；存在 `work/nuget-feed` 不会自动启用离线模式。只有准备好全部项目所需的**完整依赖源**后，才在当前 PowerShell 中设置：

```powershell
$env:NIGHTGATE_OFFLINE_RESTORE = '1'
.\scripts\Restore.ps1
```

离线模式仅使用 `work/nuget-feed`。将变量设为 `'0'` 或清除它可恢复在线源。下文的三个 runtime pack 只是运行时包，不能单独完成全部项目的离线还原。

## 制作正式发布物

正式 MSI/ZIP 必须是真正的 .NET self-contained `win-x64` 发布物。先从官方 NuGet 来源取得以下原始签名包；三个包的版本必须都是 **10.0.9**：

- `Microsoft.NETCore.App.Runtime.win-x64.10.0.9.nupkg`
- `Microsoft.WindowsDesktop.App.Runtime.win-x64.10.0.9.nupkg`
- `Microsoft.AspNetCore.App.Runtime.win-x64.10.0.9.nupkg`

完成源码构建和测试后，在仓库根目录执行；将示例目录替换为实际下载位置：

```powershell
.\scripts\Import-OfficialRuntimePacks.ps1 -SourceDirectory 'C:\path\official-runtime-packs'
.\scripts\Publish.ps1 -SkipBuild
.\scripts\Package.ps1 -SkipPublish
.\scripts\Verify.ps1 -SkipPipeline
```

[导入脚本](../scripts/Import-OfficialRuntimePacks.ps1) 将包放入 `work/nuget-feed`。导入和正式发布都会核对 nuspec 身份并执行 `dotnet nuget verify --all`，同时记录 SHA-512；缺包、签名验证失败或身份不符就停止。可通过 `NIGHTGATE_RUNTIME_PACK_SHA512_MANIFEST` 或发布/验证脚本的 `-RuntimePackSha512ManifestPath` 额外核对受信任的 SHA-512 清单。

不会从本机 `shared` runtime 反向制作同名 Microsoft 包，也不会自动退回降级发布。`-ForcePrivateRuntimeFallback` 仅供显式诊断，其产物为 `releaseEligible=false`，默认验证拒绝，不能作为正式发布物。诊断用放行参数不改变这条发布边界。

主要输出为 `outputs/NightGate-x64.msi`、`outputs/NightGate-win-x64.zip` 及各自的 `.sha256` 文件。`artifacts/publish` 保存发布模式和运行时包证据；`outputs/installer-status.json` 记录实际 MSI 身份。

### MSI 身份与审计源

[Package.ps1](../scripts/Package.ps1) 使用 Windows Installer COM 生成 MSI，不要求安装 WiX。仓库中的 [NightGate.wxs](../installer/NightGate.wxs) 是 WiX v4 模板，要求显式提供 ProductVersion 和 ProductCode。

打包按 ProductVersion 稳定派生 ProductCode，不同版本使用固定 UpgradeCode 主升级并阻止降级；每次生成 MSI 都有新的 PackageCode。ZIP 中的 WiX 审计源会展开身份，并与实际 MSI 的 ProductVersion、ProductCode、UpgradeCode 逐项比对。该源只标记为 `authored-only`，未编译，不能称为与 MSI 等价的已验证产物。

不要提交 SDK、NuGet 包、`work/`、`artifacts/`、`outputs/`、`bin/obj` 或机器安装状态。[安装状态测试探针](../tests/Shared/InstalledStateProbe.cs) 的源码已随仓库提交，不依赖作者机器上的副本。

## 测试与证据

[Test.ps1](../scripts/Test.ps1) 执行 .NET 测试和 Node 测试，保存 TRX、TAP 及每次运行的摘要。摘要与源码指纹绑定，README、使用指南和 `docs/` 也参与指纹；改过文档后，旧成功摘要不能用来证明当前源码通过测试。

[Verify.ps1](../scripts/Verify.ps1) 默认执行还原、测试、构建、发布和打包，再验证发布物。`-SkipPipeline` 只复核已有产物，仍要求当前源码对应的完整成功测试摘要。其 MSI 检查是重新打开数据库后的只读结构检查，**不是已安装、维修、升级或卸载的实机证明**。

[MSI 生命周期脚本](../installer/Test-NightGateMsiLifecycle.ps1) 会真实修改机器，只能在**可回滚的专用 Win11 虚拟机快照**中，以管理员权限显式添加 `-RunLifecycle` 执行：

```powershell
.\installer\Test-NightGateMsiLifecycle.ps1 `
  -MsiPath .\outputs\NightGate-x64.msi `
  -PreviousMsiPath 'C:\path\NightGate-0.3.16-x64.msi' `
  -PreviousProductVersion '0.3.16' -RunLifecycle
```

旧版本可选 `0.3.15` 或 `0.3.16`（默认），脚本会核对旧 MSI 的真实版本。安装了 Windows SDK 时，可另传 `-IceValidatorPath` 和 `-IceCubePath`，使用 `msival2.exe` 与 ICE cube 做额外检查。不要在日常使用的电脑上运行生命周期脚本。

## 手工验收清单

使用专用账户和可恢复的 Win11 测试机，先备份设置。以下是待执行的验收要求，不是完成记录：

1. 从目标账户安装 MSI；测试 ZIP 路径时先运行 `-WhatIf`，确认只涉及 NightGate 自有路径、服务和该 SID 的注册位置。
2. 确认服务账户为 LocalService，重新登录后只有预期托盘代理；电源和网络设置不应改变。
3. 门禁前启动测试游戏应可善后；门禁后启动的测试实例才受阻止。语音工具或同名异路径程序不得被当成游戏主程序。
4. 已授权网站的当前视频可结束，下一项被拦截；停用扩展、失去网站权限或退出桌面端后应安全放行并显示相应状态。
5. 覆盖锁屏、重新登录、睡眠/唤醒、时间回拨；例外次数和当晚状态不得因此刷新。
6. 检查团队救场、紧急解锁、娱乐再用的次数、冷静期与到期；紧急原因仅留在本地隐私安全事件中。
7. 按[使用指南](https://github.com/zkkkillua/NightGate/blob/main/USER-GUIDE.zh-CN.md)检查 iPhone 必要功能仍可用，Chrome 配置与降级提示符合预期。
8. 卸载预演不应触及旧关机任务或非 NightGate 数据；默认卸载后保留历史。
9. 有效最后开局提醒后，游戏主进程在运行时显示大号浮窗，包含当前时间、锁屏截止及倒计时；退出游戏后恢复最后 10 分钟提示规则。
10. 新旧浮窗每 12 秒明显换位，完整留在前台显示器工作区，不抢焦点、可点击穿透。覆盖负坐标显示器、跨屏混合 DPI、竖向任务栏和时间回拨；移动不得延长倒计时。
11. 光晕和粒子每轮变化且正文清晰，透明特效边缘也不越界；隐藏时停止动画，关闭系统动画或开启高对比度时只显示静态外圈。视觉变化不得改变剩余时间、例外或锁屏。

布局计算测试和单机探针不能替代混合 DPI 与真实游戏验收。独占全屏可能遮住普通置顶浮窗；需另验窗口和无边框全屏模式。0.3.17 的移动浮窗与扩散特效尚未完成安装后的真实游戏验收；随机特效也不保证用户不会适应提示。

## 项目结构

- [NightGate.Core](../src/NightGate.Core)：夜间阶段、规则、例外和状态模型。
- [NightGate.Desktop](../src/NightGate.Desktop)：WPF 界面、托盘、进程门禁、锁屏和倒计时提示。
- [NightGate.Service](../src/NightGate.Service)：本地服务、策略协调、SQLite 持久化和命名管道通信。
- [NightGate.Protocol](../src/NightGate.Protocol)：桌面端、本地宿主与服务间的协议。
- [NightGate.NativeHost](../src/NightGate.NativeHost)：Chrome Native Messaging 桥接。
- [NightGate.Chrome.Extension](../src/NightGate.Chrome.Extension)：网站权限、当前媒体善后与后续内容拦截。
- [tests](../tests)：.NET、扩展、发布流程测试及共享探针。
- [scripts](../scripts)：还原、构建、测试、发布与验证入口。
- [installer](../installer)：MSI 审计模板、事务逻辑、ZIP 安装/卸载及实机生命周期脚本。
- [specs](specs)：版本行为与验收规格。
