## ezgetBMCIP 更新日志

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
