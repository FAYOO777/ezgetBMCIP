## ezgetBMCIP 更新日志

### v1.5.0 (2026-08-26)
- 双重确认网络修改风险，退出时只恢复本机所选网卡，不主动回退 BMC 网络设置。
- 统一主线与 Legacy 的使用教程、诊断信息和支持包说明，增加 `Alt+L` 支持包入口。
- 强化 DHCP/静态配置恢复、异常退出守护和恢复验证，降低临时配置遗留风险。
- 收紧发布 tag、构建验证、ZIP 内容检查和 R2 索引更新流程，失败时停止并保留历史记录。

### v1.3.5 (2026-07-20)
- 缓存最近一次 DHCP ACK 的租约，避免 BMC 在链路检测期间已获取地址时，界面因错过事件而误等待 3 分钟。
- DHCP 使用通配地址接收广播，并按数据包实际到达的 IPv4 接口索引过滤，只处理所选有线网卡的数据。
- 原生命令输出自动识别 UTF-8 与系统 OEM 编码，避免不同命令采用不同代码页时仍出现乱码。
- 修复诊断报告中 `ipconfig`、`route`、`netstat` 等中文系统输出乱码，并为日志和诊断 TXT 写入 UTF-8 BOM。
- 忽略明确选择了其他 DHCP Server 的 DHCPREQUEST，避免在误接局域网时干扰现有客户端。
- 恢复所选网卡使用工具前的 DHCP/静态 IPv4、网关与 DNS 配置，并要求连续两次实时验证通过。
- 增加持久化恢复快照、异常退出守护和下次启动恢复，降低强制结束或断电后遗留临时 IP 的风险。
- DHCP 分配地址后先检测 HTTPS/HTTP 管理端口，再选择协议打开浏览器；页面未就绪时保留手动打开和重新检测入口。
- 增加 DHCP 网卡绑定、Server Identifier 筛选、恢复快照、端口探测及界面布局烟雾测试。

### v1.3.4 (2026-06-28)
- Full、Lite 和 Legacy 下载改为 ZIP 压缩包，降低浏览器直接下载 EXE 时的拦截概率。
- 下载页接入 Cloudflare Pages Functions 与 KV 下载统计。

### v1.3.3 (2026-06-23)
- 将 `.NET Framework 4.6` 官方下载链接改为 Microsoft Download Center 离线安装包页面。

### v1.3.2 (2026-06-23)
- Legacy 压缩包新增 `使用教程.txt`，提示旧系统用户先安装包内 `.NET Framework 4.6` 离线安装包，再运行 Legacy 程序。

### v1.3.1 (2026-06-23)
- Legacy 发布压缩包内置 `.NET Framework 4.6` 离线安装包，减少 Windows 7/8/8.1 用户额外下载步骤。

### v1.3.0 (2026-06-23)
- 🧓 新增 Legacy 版：面向 Windows 7 SP1 / Windows 8 / Windows 8.1，基于 .NET Framework 4.6，部署时复制整个 `ezgetBMCIP-legacy-net46` 文件夹。
- 🛠️ Legacy 适配旧系统网络栈：使用 WMI / netsh / 注册表路径替代 Win10+ PowerShell cmdlet。
- 🧹 强化退出恢复 DHCP：关闭内置 DHCP Server 后恢复网卡 DHCP，Legacy 会清理 Win7/8 系列 DHCP lease/option 注册表缓存，避免接回交换机后仍保留工具分配的 IP。
- 📝 完整诊断日志：主线日志写入 `%LOCALAPPDATA%\ezgetBMCIP\ezgetBMCIP.log`，Legacy 日志写入 `%TEMP%\ezgetBMCIP.log`，应用内提供日志入口。
- 🛡️ 自定义网段限制为私有 IPv4：`10.x.x.x`、`172.16-31.x.x`、`192.168.x.x`，避免公网地址被代理/路由策略干扰。
- ✅ 已验证：Win10/11 主线 Lite 回归通过；Legacy 已在 Windows 7 SP1 和 Windows 8.1 实体机通过完整链路验证；Windows 8 按同技术路径默认兼容。

### v1.2.0 (2026-05-27)
- 🆕 自定义网段：4 输入框手动修改 IP，第 4 段自动保护 BMC 固定地址 .100
- 🆕 启动页两步化：准备 → 选网卡，Prep 卡片居中悬浮
- 🆕 IP 亮化展示：大字显示 BMC 地址 + 一键复制（2s 反馈）+ 打开 BMC
- 🆕 60s 链路超时警告：等待网线超时主动提示
- 🔧 DHCP 地址池瘦身：100~200 → 固定 .100（1:1 直连场景）
- 🐛 MAC 归一化修复：DISCOVER/REQUEST hlen 不一致导致显示 IP 比实际多 1
- 🐛 DHCP Option 50 校验：REQUEST 阶段防御性匹配客户端期望 IP
- 📝 全局文案去重、措辞统一、Hero 文字专注自身阶段
- 🎨 Step 0 内容居中 + 退出按钮降级

### v1.1.2 (2026-05-24)
- 下载页新增更新说明展示，支持 hero-notes / version-notes
- CI 自动从 git tag 注解提取完整更新日志写入 Release 和 versions.json
- README 补充界面、暗色模式说明

### v1.1.1 (2026-05-24)
- 新增自动跟随 Windows 系统亮/暗主题
- 移除 Hero 卡片多余 Logo 和呼吸动画
- 按钮改用 WPF-UI Appearance 属性

### v1.1.0 (2026-05-24)
- 迁移到 WPF-UI 4.x，全面换肤
- FluentWindow + Win11 Mica 云母材质
- 内置最小化/最大化/关闭，支持贴靠布局
- 步骤指示器改用纯 XAML DataTrigger，删除 8 个 Converter

### v1.0.7 (2026-05-24)
- 下载页资源描述优化、favicon、版本号从 CI tag 注入

### v1.0.6 (2026-05-24)
- 修复 Lite 构建、版本号读取、Full/Lite 分离
- 下载页新增最新版/历史版本双标签

### v1.0.4 (2026-05-24)
- 首个正式发布：CI/CD、R2 上传、下载页、双版本
- WinForms → WPF，.NET 8 LTS，有线网卡过滤
