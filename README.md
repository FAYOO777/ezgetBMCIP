# ezgetBMCIP

Windows WinForms 桌面工具，用于笔记本直连服务器 IPMI/BMC 管理口后自动配置临时网段、分配 DHCP 地址并打开 BMC 页面。

## 功能

- 启动时检测管理员权限，不足时自动 UAC 提权重启。
- 检测有线网卡，多块网卡时提示选择。
- 记录原始网络配置，退出时自动恢复。
- 临时配置本机网卡为 `10.77.77.1/255.255.255.0`。
- 内嵌轻量 DHCP Server，地址池 `10.77.77.100` 到 `10.77.77.200`，网关 `10.77.77.1`。
- 通过 WMI/CIM 查询 `Win32_NetworkAdapter` 轮询链路状态。
- 收到 IPMI DHCP 请求后自动打开 `http://<IPMI IP>`。
- 点击“完成 / 退出”或关闭窗口时都会关闭 DHCP Server 并还原网卡。

## 编译

```powershell
dotnet build -c Release
```

## 发布单文件免安装 exe

```powershell
dotnet publish -c Release -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

发布后的 exe 位于：

```text
bin\Release\net10.0-windows\win-x64\publish\ezgetBMCIP.exe
```

## 使用提示

运行程序后按界面提示插入网线即可。程序退出前会还原网卡配置，请尽量通过“完成 / 退出”按钮关闭；直接点窗口叉号也会触发还原流程。
