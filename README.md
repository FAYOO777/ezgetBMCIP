# ezgetBMCIP

<p align="center">
  <img src="ezgetBMCIP.svg" width="120" alt="ezgetBMCIP icon"/>
</p>

Windows WPF 桌面工具，获取BMC的IP，应该可以变得更简单。直连服务器 IPMI/BMC 管理口后自动配置临时网段、分配 DHCP 地址并打开 BMC 页面。

## 功能

- 启动时检测管理员权限，不足时自动 UAC 提权重启。
- 检测有线网卡，多块网卡时提示选择。
- 记录原始网络配置，退出时统一恢复网卡为 DHCP。
- 临时配置本机网卡为 `10.77.77.1/255.255.255.0`。
- 内嵌轻量 DHCP Server，地址池 `10.77.77.100` 到 `10.77.77.200`，网关 `10.77.77.1`。
- 通过 WMI/CIM 查询 `Win32_NetworkAdapter` 轮询链路状态。
- 收到 IPMI DHCP 请求后自动打开 `http://<IPMI IP>`。
- 点击"完成 / 退出"或关闭窗口时都会关闭 DHCP Server 并还原网卡。

## 下载

每次 Release 提供两个版本：

| 版本 | 文件名 | 大小 | 需要安装运行时？ |
|---|---|---|---|
| **Full** | `ezgetBMCIP-full.exe` | ~65 MB | 不需要 |
| **Lite** | `ezgetBMCIP-lite.exe` | ~1.3 MB | 需要 .NET Desktop Runtime 8.0 x64 |

- **Full 版**：适合现场运维，插 U 盘直接运行。
- **Lite 版**：适合已安装运行时的机器，下载快。

### Lite 版运行前
请先安装 Microsoft .NET Desktop Runtime 8.0 x64：
https://dotnet.microsoft.com/en-us/download/dotnet/8.0

> 注意：需要的是 **Desktop Runtime**，不是 ASP.NET Core Runtime 或普通 .NET Runtime。

## 编译

```powershell
dotnet build -c Release
```

## 发布

### Full 版（自包含，免运行时）
```powershell
.\scripts\publish-full.ps1
```

### Lite 版（依赖运行时，体积小）
```powershell
.\scripts\publish-lite.ps1
```

手动命令：

**Full：**
```powershell
dotnet publish -c Release -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish\full
```

**Lite：**
```powershell
dotnet publish -c Release -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=false `
  -p:PublishReadyToRun=false `
  -p:EnableCompressionInSingleFile=false `
  -o publish\lite
```

发布后输出：

```text
publish\
  ezgetBMCIP-full.exe   (~65 MB)
  ezgetBMCIP-lite.exe   (~2 MB)
  full\                  (中间产物)
  lite\                  (中间产物)
```

## 使用提示

运行程序后按界面提示插入网线即可。程序退出前会还原网卡配置，请尽量通过"完成 / 退出"按钮关闭；直接点窗口叉号也会触发还原流程。
