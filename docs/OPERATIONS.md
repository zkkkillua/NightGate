# 安装、升级与恢复

日常安装步骤、首次向导、Chrome 和 iPhone 配置见[中文使用指南](https://github.com/zkkkillua/NightGate/blob/main/USER-GUIDE.zh-CN.md)。本页解释维护操作会改动什么、如何恢复，以及不能越过的安全边界。构建安装包见[开发文档](https://github.com/zkkkillua/NightGate/blob/main/docs/DEVELOPMENT.md)。

## MSI：首选安装路径

构建生成的 `outputs/NightGate-x64.msi` 要求 **x64 Windows 11 build 22000 或更高版本**。请从实际使用 NightGate 的 Windows 账户发起首次安装；SYSTEM 静默首装或 LocalService 身份会被拒绝。

安装后可从公共桌面的“收尾”、开始菜单的“收尾 NightGate”或托盘打开。首次打开显示五步向导。服务暂不可用时，界面应显示安全放行状态并提供重试，不应继续保存不可用的旧数据。

### 用户身份与权限

- 发布包的服务配置只有固定 SID 占位符，不包含构建电脑的用户身份。
- 首次安装读取 Windows Installer 的 `UserSID`，确认是普通 Windows 账户 SID 后写入服务配置。服务以 LocalService 运行，ACL 按 LocalService 和目标账户设置。
- 目标 SID 保存在 `%ProgramData%\NightGate\installer-state\install-state.json`，该机器级状态仅允许 SYSTEM 和 Administrators 访问。
- 维修、升级和卸载读取安装时记录的 SID，不改用发起本次操作的账户。

安装事务在写入目标用户的登录启动值和 Chrome 本地宿主值前，保存值是否存在、原类型及原值。安装、维修或升级失败时逐项恢复；成功卸载也恢复安装前的注册表值。安装需要 Windows Installer 回滚功能开启。实现见 [Finalize-NightGateMsi.ps1](../installer/Finalize-NightGateMsi.ps1)。

[NightGate.wxs](../installer/NightGate.wxs) 是要求显式 ProductVersion 和 ProductCode 的 WiX v4 模板。打包后的审计源会与实际 MSI 的 ProductVersion、ProductCode、UpgradeCode 逐项比对；它只标记为 `authored-only`，没有编译，不能称为与已验证 MSI 等价的产物。正式包的 `UserSID` 在目标机器安装时确定，绝不由构建机器预填。

### 升级

升级前先正常退出旧桌面端，再手工双击新版 MSI。0.3.17 的升级路径覆盖 0.1.0、0.2.0–0.2.2 和 0.3.0–0.3.16，沿用固定 UpgradeCode，并保留 `C:\ProgramData\NightGate` 中的当前状态和历史。不同版本使用不同 ProductCode，禁止降级。

仓库默认交付流程只生成并只读检查 MSI，不会替用户安装、维修或卸载。安装器结构检查通过，不等于这些升级路径已经全部完成实机验收。

### 卸载

标准入口是“Windows 设置 → 应用 → 已安装的应用 → 收尾 NightGate”。使用 MSI 安装的版本应从此入口卸载，不要混用 ZIP 卸载脚本。默认保留应用数据和历史，没有反卸载机制。

## ZIP：备用安装路径

解压 `outputs/NightGate-win-x64.zip`。找到包含 `apps/`、`installer/` 和 `.release-mode.json` 的发布目录，在该目录打开提升权限的 PowerShell，先预演再安装：

```powershell
.\installer\Install-NightGate.ps1 -SourcePath 'C:\解压目录' -WhatIf
.\installer\Install-NightGate.ps1 -SourcePath 'C:\解压目录'
```

将 `C:\解压目录` 替换为实际发布目录。脚本也可省略 `-SourcePath`，默认使用 `installer` 的上级目录。

[安装脚本](../installer/Install-NightGate.ps1) 只写入 NightGate 自有的 Program Files/ProgramData、`NightGate.LocalService`、目标用户的登录启动值和 Chrome 本地宿主键。发布阶段保留 `appsettings.sample.json` 的固定 SID 占位符，安装阶段通过当前 WTS 交互桌面会话确定规范 SID；空 SID 或 LocalService SID 会被拒绝。服务或提升权限的安装进程身份不能冒充桌面用户。

配置 ACL 明确允许 LocalService 读取；数据目录允许服务写入。服务默认只登记、不立即启动；确需立即启动时显式添加 `-StartService`，否则重启后启动。

### ZIP 回滚范围

Chrome 本地宿主的 32 位和 64 位注册表视图会分别保存原默认值及类型，再写入 NightGate 清单；后续安装步骤失败时恢复这两个值。卸载也恢复保存的原值，而非直接删除同名值。

这不等于 ZIP 安装具有完整的 MSI 事务回滚。ZIP 的登录启动值按安装记录删除，不能把 Chrome 双视图快照保证套用到所有系统改动。

早期 ZIP 安装记录没有双视图快照。卸载这些旧记录时不会修改 Chrome 本地宿主值，也不会猜测这些值归谁所有。

### ZIP 卸载

使用[卸载脚本](../installer/Uninstall-NightGate.ps1)，在提升权限的 PowerShell 中先预演：

```powershell
.\installer\Uninstall-NightGate.ps1 -WhatIf
.\installer\Uninstall-NightGate.ps1
```

脚本依据 `%ProgramData%\NightGate\install-state.json` 反转已记录的服务与注册值，并移除 NightGate 程序目录；没有有效安装记录时拒绝猜测系统项。默认保留 ProgramData、历史和安装记录。只有用户明确添加 `-RemoveApplicationData` 才删除这些数据。

## 旧自动关机任务

NightGate 不删除或改写旧任务的定义。“设置与向导 → 旧自动关机任务”只读扫描调用 `shutdown.exe` 的计划任务，逐项展示，默认全部不勾选。

只有用户明确勾选后，程序才会：

1. 将任务路径、指纹和原启用状态保存到本机数据库。
2. 按同一指纹停用任务，不扩大用户授权范围。
3. 重新读取 Windows 实际状态，确认后才显示“已停用”。

需要管理员权限时，Windows 会弹出账户控制确认；取消后可重新勾选重试。任务内容已变化、记录无法持久化或系统接口不可用时，一律不改动。操作中断后只按已保存记录安全续完，不刷新授权。

### 恢复

在同一页面选择“恢复此前停用的旧任务”。程序只恢复自己记录且内容仍匹配的任务；失败时保留记录供重试。管理员也可在 Windows“任务计划程序”中核对完整定义后手工重新启用。卸载不会搜索或恢复未授权的旧任务。

**0.3.3/0.3.8 的旧记录有限制：**这些版本只保存动作级指纹。若任务被停用后经历桌面端重启或升级，程序无法再证明触发器、运行账户及其余定义未变，因此会保持停用并拒绝自动恢复；这不是任务已经重新启用。确需恢复时，必须由用户核对完整定义后手工启用。当前版本新记录使用包含完整任务定义的组合指纹。

旧手机闹钟不由 Windows 程序操作；手工处理方式见[中文使用指南](https://github.com/zkkkillua/NightGate/blob/main/USER-GUIDE.zh-CN.md)。

## 故障处理与边界

- 服务或数据库异常时安全放行；故障不应触发断网、修改电源计划、强杀门禁前已有进程或锁死账户。
- 管理员始终可以停止服务或卸载。NightGate 防冲动，不防蓄意拆除，也不是反篡改或医疗产品。
- 无法正常卸载时，先按安装方式查看对应的安装记录：MSI 位于 `installer-state/install-state.json`，ZIP 位于 ProgramData 根下的 `install-state.json`。不要按相似名称扫描或批量删除系统项。
- 本地数据受 ACL 保护。清除历史不会重置当前夜间状态和例外令牌；维护前应先备份所需记录，不要用删除数据库来“修复”承诺状态。
- Chrome 权限、扩展更新、网页保护降级，以及 iPhone 手工同步与官方恢复路径，统一见[中文使用指南](https://github.com/zkkkillua/NightGate/blob/main/USER-GUIDE.zh-CN.md)。
