# ezgetBMCIP

<p align="center">
  <img src="ezgetBMCIP.svg" width="120" alt="logo"/>
</p>

获取BMC的IP，应该可以变得更简单。直连服务器 IPMI/BMC 管理口，自动配网段、起 DHCP、开 BMC 页面，全程无需手动操作。

基于 [WPF-UI](https://github.com/lepoco/wpfui) 4.x，Win11 Mica 云母材质，自动跟随系统亮/暗主题。

<p align="center">
  <img src="ezgetBMCIP-ScreenShot.png" width="720" alt="截图"/>
</p>

## 使用提示

运行后选网卡 → 插网线 → 等自动打开 BMC 页面 → 登录操作 → 点"完成 / 退出"。

- 程序退出前**自动还原网卡配置**，请尽量通过按钮关闭；直接关窗口也会触发还原。
- 需要**管理员权限**，启动时自动 UAC 提权。
- 确保网线插在服务器的 **IPMI 专用管理口**，不是普通网口。

## 功能

- 🖥️ 检测有线网卡，多网卡时可选
- 📝 记录原始网络配置，退出时自动还原为 DHCP
- 🔧 临时配置本机为 `10.77.77.1/255.255.255.0`
- 📡 内嵌 DHCP Server（地址池 `10.77.77.100` ~ `10.77.77.200`）
- 🔗 WMI/CIM 轮询网卡链路状态
- 🌐 获取 IPMI IP 后自动调用浏览器打开 BMC 页面
- 🧹 关闭时自动停 DHCP、还原网卡
- 🪜 5 步可视化进度指示，失败时明确提示
- 🌗 自动跟随 Windows 系统亮/暗主题

## 工作流程

| 步骤 | 说明 |
|---|---|
| 1. 配置本机网卡 | 记录原始配置 → 设为 10.77.77.1 → 启动 DHCP |
| 2. 连接网线 | 等待网线插入 IPMI 管理口，检测 Link UP |
| 3. 获取 IPMI IP | DHCP 监听 IPMI 请求，自动获取地址 |
| 4. 打开 BMC | 浏览器自动打开 `http://<IPMI IP>` |
| 5. 清理退出 | 关闭 DHCP Server，恢复网卡原始配置 |

## 下载

每次 Release 提供两个版本：[📥 下载页](https://dl.fayoo.fun) / [GitHub Releases](https://github.com/FAYOO777/ezgetBMCIP/releases)

| 版本 | 说明 |
|---|---|
| **Full** `ezgetBMCIP-full.exe` | 自包含，免运行时，适合 U 盘现场运维 |
| **Lite** `ezgetBMCIP-lite.exe` | 体积小，需安装 [.NET Desktop Runtime 8.0](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) |

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
