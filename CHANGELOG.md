## ezgetBMCIP 更新日志

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
