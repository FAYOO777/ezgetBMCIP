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

运行后先阅读并勾选首道风险告知 → 选择网卡和私有网段 → 点击“开始”并等待只读预检查 → 核对并同意网络修改告知 → 按页面提示连接 IPMI/BMC 管理口 → 工具建立直连、获取候选地址并验证访问 → 完成管理操作后恢复网卡并退出。

- 两道告知均默认不同意；关闭、按 Esc 或点击“取消 / 不同意”都不会继续，也不会修改网卡。
- 点击“开始”后的预检查只读取网卡原配置并检查防火墙；取消网络修改告知时不会创建恢复快照、修改网卡或启动 DHCP。
- 网络修改告知会显示所选网卡、临时本机 IP、预期候选地址和退出时的恢复方式；只有现有证据需要留意时才显示防火墙提示。
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
- 🌐 获得候选地址后，在约 5 秒内检测 Ping、TCP 443 和 TCP 80；任一有响应即确认地址可达，管理端口可用时优先使用 HTTPS 请求系统打开浏览器
- 🧹 关闭时停止 DHCP Server、恢复并验证原始网卡配置
- 🪜 “建立直连 → 获取地址 → 验证访问”三阶段状态提示，严格区分候选地址、访问验证和浏览器启动请求
- 🌗 自动跟随 Windows 系统亮/暗主题

## 工作流程

| 阶段 | 说明 |
|---|---|
| 准备 | 阅读两次风险告知，选择目标网卡和私有网段；未明确同意时不会修改网卡 |
| 建立直连 | 等待所选网卡 Link UP，记录原配置后写入临时 IPv4 并启动 DHCP |
| 获取地址 | 在所选有线网卡上等待 BMC 的 DHCP 请求，同时保留预期 `.100` 地址的探测机会 |
| 验证访问 | 对候选地址检测 Ping、TCP 443 和 TCP 80；系统接受浏览器启动请求不代表网页已经加载成功 |
| 恢复退出 | 停止 DHCP Server，恢复并验证本机网卡已记录的 IPv4、网关和 DNS 配置 |

## 自定义网段规则

默认网段为 `10.77.77.1/24`，BMC 固定分配为 `.100`。如果需要避让本机已有网段，只能改为私有 IPv4 网段：

- `10.x.x.x`
- `172.16.x.x` 到 `172.31.x.x`
- `192.168.x.x`

不要使用 `102.x.x.x` 这类公网地址段；在部分 Windows、浏览器或代理环境下，请求可能被按公网流量处理，导致页面打不开或出现 HTTP 502。

## 下载

每次 Release 提供多个下载包：[📥 下载页](https://ezgetbmcip.pages.dev/) / [GitHub Releases](https://github.com/FAYOO777/ezgetBMCIP/releases)。

`dl.fayoo.fun` 仅承载下载文件和版本索引，不是网页入口。

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
