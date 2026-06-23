# 本地发布规则

## 命令

```powershell
.\publish.ps1                    # Lite 版（框架依赖，单文件，net8.0-windows，Win10/11）
.\scripts\publish-legacy.ps1    # Legacy 版（框架依赖，多文件，net46，Win7/8/8.1）
```

执行后生成 `README.txt`（最近 5 条 commit），可手动补充本次改动摘要。

## 结构

```
publish\
├── ezgetBMCIP-legacy-net46\     # Legacy 版输出，内含 .NET Framework 4.6 离线安装包
│   ├── ezgetBMCIP-legacy.exe
│   ├── ezgetBMCIP-legacy.exe.config
│   ├── ezgetBMCIP.Core.Legacy.dll
│   ├── System.ValueTuple.dll
│   ├── NDP46-KB3045557-x86-x64-AllOS-ENU.exe
│   ├── 使用教程.txt
│   └── README.txt
├── ezgetBMCIP-legacy-net46.zip  # Legacy 版发布压缩包
├── 2026-5-29-14-30\             # Lite 版历史发布
│   ├── ezgetBMCIP-lite.exe
│   └── README.txt
└── ...
```

## 规则

- `publish/` 在 `.gitignore`，不会被 push
- Lite 版每次发布生成新文件夹，历史保留便于版本对比
- Legacy 版固定输出到 `publish\ezgetBMCIP-legacy-net46\`，每次覆盖；**部署时复制整个文件夹**（不是单个 exe）
- 正式 Release 应上传 `ezgetBMCIP-full.exe`、`ezgetBMCIP-lite.exe`、`ezgetBMCIP-legacy-net46.zip`
- 文件夹名格式（Lite）：`年-月-日-时-分`（`Get-Date -Format 'yyyy-M-d-H-m'`）
- `README.txt` 记录本次改动摘要

## 自定义网段规则

- 默认网段：本机 `10.77.77.1/24`，BMC 固定分配 `10.77.77.100`
- 自定义网段只允许 RFC1918 私有 IPv4：
  - `10.x.x.x`
  - `172.16.x.x` 到 `172.31.x.x`
  - `192.168.x.x`
- 不允许 `102.x.x.x` 等公网地址段。实测 Win11 上 `102.33.44.100` 会出现 HTTP 502，而 `10.44.72.100` 正常；日志显示 DHCP 分配正确，问题来自公网段被代理/路由策略干扰。
- 主线和 Legacy 都应在启动流程前拦截非私有网段，不能先修改网卡再报错。

## 当前发布候选基线

- RC 时间：2026-06-23 14:14
- Win10/11 主线 Lite：`publish\2026-6-23-14-14\ezgetBMCIP-lite.exe`
- Legacy：`publish\ezgetBMCIP-legacy-net46\`
- Win10/11 主线 Lite 已完成标准回归：默认网段、自定义私有网段、公网段拦截、退出恢复 DHCP 均通过。
- Legacy 已在前序 Win7 SP1 和 Win8.1 实机测试中多次通过完整链路，本轮按默认成功放行，不再重复阻塞测试。

## Legacy 版运行时要求

- 目标机器必须安装 **.NET Framework 4.6**（或更高 4.x）
- Legacy 压缩包内置 `NDP46-KB3045557-x86-x64-AllOS-ENU.exe`，离线环境可先运行该安装包
- Legacy 压缩包内置 `使用教程.txt`，提示用户先安装 .NET Framework 4.6，再运行 `ezgetBMCIP-legacy.exe`
- 支持操作系统：Windows 7 SP1 / Windows 8 / Windows 8.1 / Windows 10 / Windows 11
- Legacy 版不是 self-contained / single-file 发布，部署前请先确认目标机器已安装 .NET Framework
- .NET Framework 4.6 下载：<https://www.microsoft.com/zh-cn/download/details.aspx?id=48137>

## 构建命令

```powershell
# Legacy 版
dotnet build -c Release .\ezgetBMCIP.Legacy\ezgetBMCIP.Legacy.csproj

# 或使用发布脚本
.\scripts\publish-legacy.ps1
```

## Legacy 测试检查清单

在真实机器或 VM 上验证以下各项：

| # | 检查项 | Win7 SP1 | Win8 | Win8.1 | Win10/11 |
|---|--------|----------|------|--------|----------|
| 1 | 应用能正常启动（UAC 提权） | ✅ 实机通过 | 默认兼容 | ✅ 实机通过 | ☐ |
| 2 | 网卡列表正确显示 | ✅ 实机通过 | 默认兼容 | ✅ 实机通过 | ☐ |
| 3 | 可修改私有网段 IP 配置，公网段会被拦截 | ✅ 实机通过 | 默认兼容 | ✅ 实机通过 | ☐ |
| 4 | 点击"开始"后网卡切换到静态 IP | ✅ 实机通过 | 默认兼容 | ✅ 实机通过 | ☐ |
| 5 | DHCP 服务正常启动并分配地址 | ✅ 实机通过 | 默认兼容 | ✅ 实机通过 | ☐ |
| 6 | 获取到 BMC 地址并打开浏览器 | ✅ 实机通过 | 默认兼容 | ✅ 实机通过 | ☐ |
| 7 | 退出后网卡恢复为 DHCP | ✅ 实机通过 | 默认兼容 | ✅ 实机通过 | ☐ |
| 8 | 退出后清理 DHCP lease/option 缓存 | ✅ 实机通过 | 默认兼容 | ✅ 实机通过 | ☐ |
| 9 | 接回交换机后重新获取正常 DHCP 地址 | ✅ 实机通过 | 默认兼容 | ✅ 实机通过 | ☐ |
| 10 | 清理失败时错误信息保持可见 | ✅ 实机通过 | 默认兼容 | ✅ 实机通过 | ☐ |
| 11 | 多次关闭窗口不绕过清理流程 | ✅ 实机通过 | 默认兼容 | ✅ 实机通过 | ☐ |

**当前基线：** 2026-06-23 RC 已在 Win10/11 主线 Lite 完成回归；Legacy 已在 Windows 7 SP1 实体机和 Windows 8.1 实体机通过完整链路验证。Windows 8 因占用率低，不作为阻塞测试项；按同一 Legacy 技术路径默认兼容。

**Win7 关键修复：** 退出恢复不能只检查 IPv4 属性是否为 DHCP。Windows 7 会保留并回写 DHCP lease/option 缓存，必须清理并验证注册表中的 `DhcpIPAddress`、`DhcpServer`、`DhcpDefaultGateway`、`DhcpNameServer`、`DhcpInterfaceOptions` 等字段。

## Legacy 运行时诊断

### 日志位置

Legacy 版运行时日志写入 `%TEMP%\ezgetBMCIP.log`。

例如：
- Windows 7/8/8.1: `C:\Users\<用户名>\AppData\Local\Temp\ezgetBMCIP.log`
- Windows 10/11: `C:\Users\<用户名>\AppData\Local\Temp\ezgetBMCIP.log`

### 日志内容

日志格式：`yyyy-MM-dd HH:mm:ss <message>`

已记录的诊断事件：

| 事件 | 日志内容示例 |
|------|-------------|
| 应用启动 | `Legacy App started` |
| 网卡初始化 | `[Legacy] Initialize: 2 adapter(s), selected: 以太网` |
| 网卡初始化失败 | `[Legacy] Initialize failed: <error>` |
| 流程开始 | `[Legacy] Flow started, adapter: 以太网, subnet: 10.77.77.1 / 24` |
| 网卡配置 | `[Legacy] Config: dhcpEnabled=True, addr=10.77.77.1 / 24` |
| 流程成功 | `[Legacy] Flow success, BMC IP: 10.77.77.100` |
| 流程取消 | `[Legacy] Flow cancelled` |
| 流程失败 | `[Legacy] Flow failed: <error>` |
| 清理开始 | `[Legacy] Cleanup started` |
| 清理成功 | `[Legacy] Cleanup success` |
| 清理失败 | `[Legacy] Cleanup failed: <error>` |
| DHCP 恢复路径 | `DHCP restore: Registry path: SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{...}` |
| DHCP lease 清理 | `DHCP restore: Registry DHCP lease reset: OK` |
| DHCP 恢复验证 | `DHCP restore verify 1: dhcpEnabled=True, toolIpStillPresent=False, toolLeaseStillPresent=False, registryLeaseStillPresent=False` |

注：仅 `MainViewModel` 的诊断日志带有 `[Legacy]` 前缀；`App.xaml.cs` 的启动日志无此前缀。

### 收集日志

测试失败时，请收集：
1. `%TEMP%\ezgetBMCIP.log` 文件
2. 操作系统版本（`winver`）
3. 已安装的 .NET Framework 版本
4. 网卡名称和类型
5. 操作步骤和观察到的现象

## Win10/11 现代版运行时诊断

### 日志位置

现代版运行时日志写入 `%LOCALAPPDATA%\ezgetBMCIP\ezgetBMCIP.log`。

例如：
- `C:\Users\<用户名>\AppData\Local\ezgetBMCIP\ezgetBMCIP.log`

### 日志内容

日志格式：`yyyy-MM-dd HH:mm:ss <message>`

已记录的诊断事件：

| 阶段 | 日志示例 |
|------|---------|
| 启动 | `=== ezgetBMCIP startup ===`, `Version: v1.2.0`, `OS: ...`, `Running as administrator` |
| 网卡枚举 | `Adapter enumeration started/done: N adapter(s) found`, `Adapter: 以太网 \| ...` |
| 流程开始 | `Flow started: adapter=... subnet=... pool=...` |
| 原始配置 | `Original config: dhcp=True/False addrs=N gw=N dns=N` |
| 强制 DHCP | `Original was static, forcing DHCP first` |
| 静态 IP | `Static IP set: 10.77.77.1 / 24` |
| DHCP 服务 | `[DHCP] DHCP server starting/stopping/stopped` |
| DHCP 数据包 | `[DHCP] DHCP: DISCOVER from AA-BB-... -> OFFER 10.77.77.100` |
| DHCP 分配 | `[DHCP] DHCP: REQUEST ... -> ACK`, `[DHCP] DHCP: lease assigned 10.77.77.100 to ...` |
| 链路检测 | `Link wait started`, `Link detected`, `Link wait: 60s warning` |
| DHCP 等待 | `DHCP lease wait started`, `DHCP lease acquired: IP=... MAC=...` |
| BMC 发现 | `Flow: BMC IP discovered 10.77.77.100` |
| 浏览器 | `Browser opened for http://...` |
| 流程取消 | `Flow cancelled` |
| 流程失败 | `Flow failed: <error>` |
| 清理 | `Cleanup started/success`, `Cleanup: DHCP server disposing/disposed`, `Cleanup: restoring DHCP for ...`, `Cleanup: DHCP restore done` |
| 清理失败 | `Cleanup failed: <error>` |
| Core 层操作 | `[Core] <PowerShell/netsh output>` |

### 收集日志

测试失败时，请收集：
1. `%LOCALAPPDATA%\ezgetBMCIP\ezgetBMCIP.log` 文件
2. 操作系统版本（`winver`）
3. .NET 运行时版本
4. 网卡名称和类型
5. 操作步骤和观察到的现象

## Legacy VM 测试记录模板

以下模板可直接复制用于每次测试记录。

```markdown
## Legacy 测试记录

### 环境信息
- 测试日期：
- 操作系统：Windows ___ (SP ___)
- 系统架构：x64 / x86
- .NET Framework 版本：___
- 虚拟机/物理机：___
- 测试者：

### 网卡信息
- 网卡名称：
- 网卡类型（板载/USB/PCIe）：
- MAC 地址：

### 测试结果

| 步骤 | 预期结果 | 实际结果 | 通过 |
|------|---------|---------|------|
| 启动应用 | UAC 提权成功，应用正常启动 | | ☐ |
| 网卡列表 | 检测到可用有线网卡 | | ☐ |
| 配置网段 | 私有 IP 输入正常，公网 IP 点击开始会被拦截 | | ☐ |
| 开始流程 | 网卡切换到静态 IP，DHCP 服务启动 | | ☐ |
| 连接网线 | 检测到网线连接 | | ☐ |
| 等待 BMC | BMC 通过 DHCP 获取 .100 地址 | | ☐ |
| BMC IP 显示 | IP 地址显示正确 | | ☐ |
| 打开 BMC | 浏览器打开 BMC 管理页面 | | ☐ |
| 退出恢复 DHCP | 网卡恢复为 DHCP | | ☐ |
| 清理 DHCP lease/option 缓存 | `registryLeaseStillPresent=False` | | ☐ |
| 接回交换机 | 获取正常局域网 DHCP 地址 | | ☐ |
| 清理失败保持可见 | 失败时错误信息保持显示 | | ☐ |
| 多次关闭不绕过清理 | 重复关闭不会中断清理流程 | | ☐ |

### 日志摘录
```
（从 %TEMP%\ezgetBMCIP.log 粘贴相关行）
```

### 备注
