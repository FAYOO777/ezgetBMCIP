# IPMI/BMC 直连助手

<p align="center">
  <img src="ezgetBMCIP.svg" width="120" alt="logo"/>
</p>

ezgetBMCIP

用于笔记本直连服务器 IPMI/BMC 管理口时，临时给本机网卡配置地址，启动内置 DHCP 服务，等待 BMC 获取地址后打开管理页面。

主线版本面向 Windows 10/11；Legacy 版本面向 Windows 7 SP1 / Windows 8 / Windows 8.1。

<p align="center">
  <img src="ezgetBMCIP-ScreenShot.png" width="720" alt="截图"/>
</p>

## 使用提示

运行后先阅读并勾选首道风险告知 → 选择网卡和私有网段 → 点击“开始”后核对并同意网络修改告知 → 连接 IPMI/BMC 管理口 → 等待页面打开 → 操作完成后点击“完成 / 退出”。

- 两道告知均默认不同意；关闭、按 Esc 或点击“取消 / 不同意”都不会继续，也不会修改网卡。
- 点击“开始”后的告知会显示所选网卡、临时本机 IP、BMC 临时 DHCP 地址，以及退出时本机实际恢复的 DHCP 或静态 IPv4、网关和 DNS 配置。
- 程序退出前只会**恢复所选本机网卡**。原来是 DHCP 时恢复为自动获取并重新申请租约，不保证获得原 IP、网关、自动 DNS 或立即恢复联网；原来是静态配置时恢复已读取的 IPv4、网关和 DNS。
- 本工具不会主动还原 BMC 的网络设置。BMC 若接受本工具 DHCP，会获得临时地址；退出后请按现场网络方案重新接回 BMC。
- 程序异常退出时会由恢复守护进程尝试处理本机网卡；断电或守护失败时，需在目标网卡重新连接后再次启动本工具恢复。
- 需要**管理员权限**，启动时自动 UAC 提权。
- 确保网线插在服务器的 **IPMI 专用管理口**，不是普通网口。

## 功能

- 🖥️ 检测有线网卡，多网卡时可选
- 📝 退出时恢复所选本机网卡已记录的 IPv4 与 DNS 配置（不恢复 BMC 网络设置）
- 🔧 临时配置本机为 `10.77.77.1/255.255.255.0`
- 📡 内嵌轻量 DHCP Server（仅处理所选有线网卡上的 DHCP 数据包，直连场景固定分配 `10.77.77.100`）
- 🧭 可自定义私有网段：仅允许 `10.x.x.x`、`172.16-31.x.x`、`192.168.x.x`，避免公网地址被代理或路由策略干扰
- 🔗 WMI/CIM 轮询网卡链路状态
- 🌐 分配候选地址后确认 HTTPS/HTTP 管理端口可达，再调用浏览器打开 BMC 页面
- 🧹 关闭时停止 DHCP Server、恢复并验证原始网卡配置
- 🪜 5 步可视化进度指示，失败时明确提示
- 🌗 自动跟随 Windows 系统亮/暗主题

## 工作流程

| 步骤 | 说明 |
|---|---|
| 0. 使用前确认 | 阅读风险告知并主动勾选同意，未同意不会进入网卡选择 |
| 0.5 网络修改确认 | 核对本机临时 IP、BMC 临时 DHCP 地址与仅恢复本机网卡的边界后才能开始 |
| 1. 配置本机网卡 | 设为 10.77.77.1、临时清空 DNS → 启动 DHCP |
| 2. 连接网线 | 等待网线插入 IPMI 管理口，检测 Link UP |
| 3. 获取候选地址 | DHCP 仅在所选有线网卡上监听直连设备请求 |
| 4. 打开 BMC | 确认 HTTPS/HTTP 管理端口可达后再打开浏览器 |
| 5. 清理退出 | 关闭 DHCP Server，恢复并验证本机网卡已记录的 IPv4/DNS 配置 |

## 自定义网段规则

默认网段为 `10.77.77.1/24`，BMC 固定分配为 `.100`。如果需要避让本机已有网段，只能改为私有 IPv4 网段：

- `10.x.x.x`
- `172.16.x.x` 到 `172.31.x.x`
- `192.168.x.x`

不要使用 `102.x.x.x` 这类公网地址段；在部分 Windows、浏览器或代理环境下，请求可能被按公网流量处理，导致页面打不开或出现 HTTP 502。

## 下载

每次 Release 提供多个下载包：[📥 下载页](https://dl.fayoo.fun) / [GitHub Releases](https://github.com/FAYOO777/ezgetBMCIP/releases)

| 版本 | 说明 |
|---|---|
| **Full** `ezgetBMCIP-full.zip` | 面向 Windows 10/11，包含 .NET 运行时，解压后运行，文件较大 |
| **Lite** `ezgetBMCIP-lite.zip` | 体积小，下载快，解压后运行，需要系统装有 .NET Desktop Runtime 8.0 |
| **Legacy** `ezgetBMCIP-legacy-net46.zip` | 面向 Windows 7 SP1 / Windows 8 / Windows 8.1，压缩包内含 .NET Framework 4.6 离线安装包，解压后运行文件夹内的 exe |

## 技术栈

- .NET 8 WPF
- [WPF-UI 4.x](https://github.com/lepoco/wpfui)
- WMI / CIM（网卡检测、链路状态）
- 内嵌轻量 DHCP Server

## 编译 & 发布

```powershell
# 编译
dotnet build -c Release

# 发布 Full 版（自包含）
.\scripts\publish-full.ps1

# 发布 Lite 版（框架依赖）
.\scripts\publish-lite.ps1
```
