# ezgetBMCIP X99 开发与实机测试交接

更新时间：2026-09-04

## 1. 交接目标

将 ezgetBMCIP 的后续开发和 X99 实机测试迁移到 X99 Windows 工作站。接手任务必须在本仓库目录运行，不要沿用此前误绑定到“呼损查询工具V3.8-9100端口”的 Codex 任务。

建议的 X99 工作目录：

```text
C:\Dev\ezgetBMCIP
```

## 2. Git 状态

- 远端：`git@github.com:FAYOO777/ezgetBMCIP.git`
- 开发分支：`codex/firewall-compat-v1.5.2-test1`
- 基线 `main` 提交：`3dd0d688d52077ae31101a1ddd53be21f01dbe5e`（v1.5.1）
- 防火墙兼容性代码提交：`ae294d6`（`feat: add read-only firewall compatibility assessment`）
- 当前阶段是 v1.5.2-test.1 验证，不是正式发布。
- 未经用户再次授权，不得合并 `main`、不得创建标签、不得发布正式版。

如果 X99 尚未克隆仓库：

```powershell
New-Item -ItemType Directory -Path C:\Dev -Force
Set-Location C:\Dev
git clone git@github.com:FAYOO777/ezgetBMCIP.git
Set-Location C:\Dev\ezgetBMCIP
git fetch --prune origin
git switch --track origin/codex/firewall-compat-v1.5.2-test1
```

如果 X99 已有仓库：

```powershell
Set-Location C:\Dev\ezgetBMCIP
git status --short --branch
git fetch --prune origin
git switch codex/firewall-compat-v1.5.2-test1
git pull --ff-only
```

如果切换分支前 `git status` 显示本地修改，立即停止，不要 reset、checkout 或覆盖，先让用户确认这些修改的来源。

## 3. 本阶段已实现内容

- 主版/Lite 与 Legacy 共用只读 `FirewallAssessment` 兼容模型。
- 在第二次网络修改确认前检测网络类别、防火墙 Profile、当前 EXE 路径及相关程序/UDP 67 入站规则。
- 显式 Block 优先；仅端口 Allow 仍提示兼容性待确认；当前路径程序 Allow 且无冲突 Block 时不提示风险。
- 检测失败降级为 Unknown，不阻断工程师继续。
- DHCP 超时说明不再断言 BMC 没发包，并始终包含“固定 IP BMC 不会主动重新获取 DHCP”的提示。
- 主版与 Legacy 支持包增加防火墙证据与最终风险结论。
- 官网两份教程已同步防火墙规则与 EXE 路径绑定说明。
- 没有自动创建、删除、启停或修改 Windows 防火墙规则。

## 4. 提交前自动化验证结果

以下检查已在原开发机使用 .NET SDK 8.0.424 串行通过：

```powershell
.\tests\release-workflow\ReleaseContract.Tests.ps1
.\tests\network-recovery-contract\NetworkRecoveryContract.Tests.ps1
dotnet restore .\ezgetBMCIP.csproj -r win-x64
dotnet restore .\ezgetBMCIP.Legacy\ezgetBMCIP.Legacy.csproj
dotnet restore .\tests\ezgetBMCIP.SmokeTests\ezgetBMCIP.SmokeTests.csproj -r win-x64
dotnet run --project .\tests\ezgetBMCIP.SmokeTests\ezgetBMCIP.SmokeTests.csproj -c Release --no-restore
dotnet build .\ezgetBMCIP.csproj -c Release -r win-x64 --no-restore
dotnet build .\ezgetBMCIP.Legacy\ezgetBMCIP.Legacy.csproj -c Release --no-restore
```

结果：两项契约测试通过，SmokeTests 显示 `All smoke tests passed.`，主版和 Legacy 均为 0 警告、0 错误。

X99 拉取后应再次串行运行同一组命令。仓库 `global.json` 请求 SDK 8.0.422，并允许 `latestFeature`；8.0.424 已验证可用。Legacy 构建还需要 .NET Framework 4.6 Targeting/Developer Pack。

## 5. 已完成的 X99 实机结果

现有证据目录：

```text
C:\Users\Fayoo\Downloads\ezgetBMCIP-X99-After-Allow-20260904-142442\ezgetBMCIP-X99-After-Allow-20260904-142442
```

已经确认：

- X99 当前网络类别为 Public，防火墙开启。
- Windows 安全警报允许访问后，为当前 EXE 生成了 TCP/UDP、Private/Public 程序 Allow。
- 当前路径程序 Allow 时风险判定为 None，Alpine 可完成 DORA 和页面访问。
- EXE 移动到新目录后，旧路径规则不再算当前程序允许，风险判定为 Warning。
- 只有 UDP/67 端口 Allow 时判定为 Warning。
- 当前程序 Block 与 UDP/67 Allow 同时存在时，Block 优先，判定为 High；三分钟超时文案符合设计。
- 所有实际修改网卡配置的轮次均出现 `Original DHCP configuration restored and verified` 与 `Cleanup success`。

注意：名为 `05-program-allow-no-dhcp-timeout.png` 的截图实际显示成功页面，不是超时证据。

## 6. 当前未完成问题

原计划第十步“程序 Allow，但设备不发 DHCP”没有成功构造。实际日志显示 Alpine 在 15:10:17 仍执行了完整的 Discover、Offer、Request、ACK，所以软件成功分配地址并打开页面是正确行为，不是旧租约误判，也不是防火墙检测失效。

Alpine 已知配置：

```text
/etc/network/interfaces:
auto eth2
iface eth2 inet dhcp

DHCP 客户端：/sbin/udhcpc -b -R -p /var/run/udhcpc.eth2.pid -i eth2 ...
```

`udhcpc -b` 会在后台运行，因此仅关闭终端或等待不能构造“设备不发 DHCP”。

## 7. X99 接手后的下一步

先在 Alpine 上只读收集状态：

```sh
date
ip -4 addr show dev eth2
ip route
ps w | grep '[u]dhcpc'
cat /var/run/udhcpc.eth2.pid
rc-service networking status
cat /etc/network/interfaces
```

在确认管理连接不经过 `eth2` 之前，不要停止服务或修改接口。尤其不要执行：

```sh
rc-service networking stop
```

接手 Codex 应先分析上述只读输出，确认管理接口和 `eth2` 的关系，再给出仅影响 `eth2`、可恢复的“链路保持 Up 但不运行 DHCP 客户端”步骤。目标是让 Windows 端真实等待三分钟并验证低防火墙风险时的超时原因排序。

完成该场景后，必须保存：

- 第二次确认页截图。
- 三分钟超时页截图。
- 当轮支持包。
- Alpine 同期状态或抓包证据。
- 退出后的 `Original network configuration restored and verified` 与 `Cleanup success`。

## 8. Git 不包含的资料

以下内容受到 `.gitignore` 影响，拉取分支不会自动获得：

- `versions.json`
- `artifacts\`
- `publish\`
- X99 支持包、截图和实机记录

其中 `bin\`、`obj\` 可以重新生成；`versions.json`、测试包和实机证据必须从原电脑或现有 X99 目录单独保留。不要为了迁移而擅自把这些目录整体加入 Git。

已生成的 Lite 测试包（原电脑路径）：

```text
C:\Dev\ezgetBMCIP\artifacts\local-test\20260904-104553-v1.5.2-firewall-lite-test.1\ezgetBMCIP-lite-v1.5.2-test.1.zip
```

- ZIP SHA256：`68FF04C9C31D08D09073B3448C835BEBD5CE37C16C02C22C959B07DBEF2B2DE2`
- EXE SHA256：`BB33E99300BA6B7C487F6464CF144B127D32B697EF13B3A598028C1C24980CBC`

## 9. 给 X99 Codex 的首条指令

```text
请完整阅读 docs/X99-Codex-Handoff-2026-09-04.md，并核对当前仓库分支、HEAD 和工作区状态。先在 X99 上串行复跑文档中的两项契约测试、SmokeTests、主版与 Legacy Release 构建；然后读取现有 X99 实机证据及 Alpine 只读状态，从“程序 Allow，但设备不发 DHCP”场景继续。未经我明确授权，不要合并 main、不要打标签、不要发布正式版、不要自动修改 Windows 防火墙，也不要执行会中断 Alpine 管理网络的命令。
```
