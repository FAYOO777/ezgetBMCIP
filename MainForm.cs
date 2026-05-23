using System.Diagnostics;
using System.Drawing;

namespace EzGetBmcIp;

internal sealed class MainForm : Form
{
    private readonly List<StepStatus> _steps = new()
    {
        new StepStatus("1. 配置本机网卡", "准备设置 10.77.77.1 并启动内嵌 DHCP Server"),
        new StepStatus("2. 连接 IPMI 管理口", "请用网线连接服务器的 IPMI 管理口"),
        new StepStatus("3. 自动获取 IPMI 地址", "等待 IPMI 发出 DHCP 请求"),
        new StepStatus("4. 打开 BMC 页面", "自动调用默认浏览器打开管理页面"),
        new StepStatus("5. 完成后退出", "退出前自动还原网卡配置")
    };

    private readonly Panel _currentStepPanel = new();
    private readonly Label _currentStepIconLabel = new();
    private readonly Label _currentStepTitleLabel = new();
    private readonly Label _currentStepActivityLabel = new();
    private readonly Label _currentStepDescriptionLabel = new();
    private readonly Label _currentStepBadgeLabel = new();
    private readonly TableLayoutPanel _timelinePanel = new();
    private readonly Panel _adapterPanel = new();
    private readonly ComboBox _adapterComboBox = new();
    private readonly Button _startButton = new();
    private readonly Label _mainStatusLabel = new();
    private readonly Label _detailLabel = new();
    private readonly Button _exitButton = new();
    private readonly List<Label> _timelineDotLabels = new();
    private readonly List<Label> _timelineTitleLabels = new();
    private readonly List<Panel> _timelineLinePanels = new();
    private readonly System.Windows.Forms.Timer _transitionTimer = new() { Interval = 15 };
    private readonly Stopwatch _transitionWatch = new();
    private readonly CancellationTokenSource _flowCts = new();
    private readonly DhcpServer _dhcpServer = new();

    private const int TransitionDurationMs = 180;
    private static readonly Color VsBase = Color.FromArgb(30, 30, 30);
    private static readonly Color VsCard = Color.FromArgb(37, 37, 38);
    private static readonly Color VsTitle = Color.FromArgb(50, 50, 51);
    private static readonly Color VsBorder = Color.FromArgb(63, 63, 70);
    private static readonly Color VsText = Color.FromArgb(220, 220, 220);
    private static readonly Color VsMuted = Color.FromArgb(150, 150, 150);
    private static readonly Color VsBlue = Color.FromArgb(0, 122, 204);
    private static readonly Color VsGreen = Color.FromArgb(78, 201, 176);
    private static readonly Color VsExitGreen = Color.FromArgb(35, 134, 88);
    private WiredAdapter? _selectedAdapter;
    private AdapterOriginalConfig? _originalConfig;
    private int _displayedStepIndex = -1;
    private Color _transitionFromBack;
    private Color _transitionFromIcon;
    private Color _transitionFromTitle;
    private Color _transitionToBack;
    private Color _transitionToIcon;
    private Color _transitionToTitle;
    private bool _cleanupStarted;
    private bool _cleanupFinished;
    private bool _allowClose;
    private bool _isClosing;

    public MainForm()
    {
        Text = "ezgetBMCIP - BMC 管理口快速登录工具";
        ClientSize = new Size(760, 710);
        MinimumSize = new Size(740, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = VsBase;

        BuildLayout();
        _transitionTimer.Tick += (_, _) => TickStepTransition();
        Shown += async (_, _) => await RunFlowAsync();
        FormClosing += MainForm_FormClosing;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = VsBase,
            Padding = new Padding(8, 10, 8, 10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        Controls.Add(root);

        var appBar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = VsTitle,
            Padding = new Padding(18, 0, 18, 0)
        };
        root.Controls.Add(appBar, 0, 0);

        var appIcon = new Label
        {
            Text = "■",
            Width = 26,
            Dock = DockStyle.Left,
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            ForeColor = VsBlue,
            TextAlign = ContentAlignment.MiddleLeft
        };
        appBar.Controls.Add(appIcon);

        var appTitle = new Label
        {
            Text = "ezgetBMCIP — BMC 管理口快速登录工具",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold),
            ForeColor = VsText,
            TextAlign = ContentAlignment.MiddleLeft
        };
        appBar.Controls.Add(appTitle);

        var hero = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = VsCard,
            Padding = new Padding(28, 12, 28, 10)
        };
        root.Controls.Add(hero, 0, 1);

        _mainStatusLabel.Text = "BMC 管理口快速登录";
        _mainStatusLabel.Dock = DockStyle.Top;
        _mainStatusLabel.Height = 34;
        _mainStatusLabel.Font = new Font(Font.FontFamily, 19F, FontStyle.Bold);
        _mainStatusLabel.ForeColor = VsText;
        hero.Controls.Add(_mainStatusLabel);

        _detailLabel.Text = "全程自动配置，用户只需插入网线";
        _detailLabel.Dock = DockStyle.Bottom;
        _detailLabel.Height = 28;
        _detailLabel.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Regular);
        _detailLabel.ForeColor = VsMuted;
        hero.Controls.Add(_detailLabel);

        BuildAdapterSelector();
        root.Controls.Add(_adapterPanel, 0, 2);

        var progressArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = VsBase,
            Padding = new Padding(26, 0, 26, 12)
        };
        progressArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        progressArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        root.Controls.Add(progressArea, 0, 3);

        BuildCurrentStepPanel();
        progressArea.Controls.Add(_currentStepPanel, 0, 0);

        BuildTimelinePanel();
        root.Controls.Add(_timelinePanel, 0, 4);
        RefreshProgressView();

        _exitButton.Text = "完成 / 退出";
        _exitButton.Dock = DockStyle.Right;
        _exitButton.Width = 116;
        _exitButton.Height = 42;
        _exitButton.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
        _exitButton.BackColor = VsExitGreen;
        _exitButton.ForeColor = Color.White;
        _exitButton.FlatStyle = FlatStyle.Flat;
        _exitButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 95);
        _exitButton.FlatAppearance.BorderSize = 1;
        _exitButton.Enabled = true;
        _exitButton.Click += (_, _) => Close();

        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = VsCard,
            Padding = new Padding(0, 14, 26, 14)
        };
        footer.Controls.Add(_exitButton);
        root.Controls.Add(footer, 0, 5);
    }

    private void BuildAdapterSelector()
    {
        _adapterPanel.Dock = DockStyle.Fill;
        _adapterPanel.BackColor = VsBase;
        _adapterPanel.Padding = new Padding(26, 22, 26, 14);

        var box = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = VsCard,
            ColumnCount = 3,
            RowCount = 3,
            Padding = new Padding(18, 14, 18, 14)
        };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        box.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        box.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        box.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _adapterPanel.Controls.Add(box);

        var header = new Label
        {
            Text = "目标网卡",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold),
            ForeColor = VsBlue
        };
        box.Controls.Add(header, 0, 0);
        box.SetColumnSpan(header, 3);

        var label = new Label
        {
            Text = "选择网卡",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Regular),
            ForeColor = VsMuted
        };
        box.Controls.Add(label, 0, 1);

        _adapterComboBox.Dock = DockStyle.Fill;
        _adapterComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _adapterComboBox.DisplayMember = nameof(WiredAdapter.DisplayName);
        _adapterComboBox.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        _adapterComboBox.BackColor = VsTitle;
        _adapterComboBox.ForeColor = Color.White;
        _adapterComboBox.FlatStyle = FlatStyle.Flat;
        box.Controls.Add(_adapterComboBox, 1, 1);

        _startButton.Text = "开始";
        _startButton.Dock = DockStyle.Fill;
        _startButton.Margin = new Padding(12, 2, 0, 2);
        _startButton.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
        _startButton.BackColor = VsBlue;
        _startButton.ForeColor = Color.White;
        _startButton.FlatStyle = FlatStyle.Flat;
        _startButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 95);
        _startButton.FlatAppearance.BorderSize = 1;
        _startButton.Click += (_, _) => StartSelectedAdapterFlow();
        box.Controls.Add(_startButton, 2, 1);

        var hint = new Label
        {
            Text = "请选择你准备用网线连接服务器的那块网卡",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9.5F, FontStyle.Regular),
            ForeColor = VsMuted
        };
        box.Controls.Add(hint, 0, 2);
        box.SetColumnSpan(hint, 3);
    }

    private void BuildCurrentStepPanel()
    {
        _currentStepPanel.Dock = DockStyle.Fill;
        _currentStepPanel.BackColor = VsCard;
        _currentStepPanel.Padding = new Padding(32, 28, 32, 24);
        _currentStepPanel.Margin = new Padding(0, 0, 0, 0);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent
        };
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 26));
        _currentStepPanel.Controls.Add(content);

        _currentStepIconLabel.Text = "▭";
        _currentStepIconLabel.Dock = DockStyle.Fill;
        _currentStepIconLabel.TextAlign = ContentAlignment.BottomCenter;
        _currentStepIconLabel.Font = new Font(Font.FontFamily, 42F, FontStyle.Bold);
        content.Controls.Add(_currentStepIconLabel, 0, 0);

        _currentStepTitleLabel.Dock = DockStyle.Fill;
        _currentStepTitleLabel.TextAlign = ContentAlignment.MiddleCenter;
        _currentStepTitleLabel.Font = new Font(Font.FontFamily, 17F, FontStyle.Bold);
        _currentStepTitleLabel.ForeColor = VsText;
        content.Controls.Add(_currentStepTitleLabel, 0, 1);

        _currentStepActivityLabel.Dock = DockStyle.Fill;
        _currentStepActivityLabel.TextAlign = ContentAlignment.TopCenter;
        _currentStepActivityLabel.Font = new Font(Font.FontFamily, 10F, FontStyle.Regular);
        _currentStepActivityLabel.ForeColor = VsMuted;
        _currentStepActivityLabel.Padding = new Padding(120, 0, 120, 0);
        content.Controls.Add(_currentStepActivityLabel, 0, 2);

        _currentStepBadgeLabel.Dock = DockStyle.None;
        _currentStepBadgeLabel.Anchor = AnchorStyles.Top;
        _currentStepBadgeLabel.Width = 92;
        _currentStepBadgeLabel.Height = 26;
        _currentStepBadgeLabel.TextAlign = ContentAlignment.MiddleCenter;
        _currentStepBadgeLabel.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        _currentStepBadgeLabel.ForeColor = Color.White;
        _currentStepBadgeLabel.BackColor = Color.FromArgb(14, 82, 130);
        content.Controls.Add(_currentStepBadgeLabel, 0, 3);

        _currentStepDescriptionLabel.Dock = DockStyle.Fill;
        _currentStepDescriptionLabel.TextAlign = ContentAlignment.TopCenter;
        _currentStepDescriptionLabel.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Regular);
        _currentStepDescriptionLabel.ForeColor = Color.FromArgb(120, 120, 120);
        _currentStepDescriptionLabel.Padding = new Padding(40, 2, 40, 0);
        content.Controls.Add(_currentStepDescriptionLabel, 0, 4);
    }

    private void BuildTimelinePanel()
    {
        _timelinePanel.Dock = DockStyle.Fill;
        _timelinePanel.BackColor = VsCard;
        _timelinePanel.ColumnCount = _steps.Count * 2 - 1;
        _timelinePanel.RowCount = 2;
        _timelinePanel.Padding = new Padding(72, 8, 72, 8);

        for (var i = 0; i < _timelinePanel.ColumnCount; i++)
        {
            _timelinePanel.ColumnStyles.Add(i % 2 == 0
                ? new ColumnStyle(SizeType.Absolute, 44)
                : new ColumnStyle(SizeType.Percent, 100));
        }
        _timelinePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        _timelinePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        for (var i = 0; i < _steps.Count; i++)
        {
            var dot = new Label
            {
                Dock = DockStyle.Fill,
                Text = "○",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Font.FontFamily, 24F, FontStyle.Bold),
                ForeColor = Color.FromArgb(110, 110, 116)
            };
            _timelinePanel.Controls.Add(dot, i * 2, 0);
            _timelineDotLabels.Add(dot);

            var title = new Label
            {
                Dock = DockStyle.Fill,
                Text = ShortStepTitle(i),
                TextAlign = ContentAlignment.TopCenter,
                Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(130, 130, 136)
            };
            _timelinePanel.Controls.Add(title, i * 2, 1);
            _timelineTitleLabels.Add(title);

            if (i < _steps.Count - 1)
            {
                var lineHost = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = VsCard,
                    Padding = new Padding(0, 17, 0, 17)
                };
                var line = new Panel
                {
                    Dock = DockStyle.Fill,
                    Height = 2,
                    BackColor = Color.FromArgb(78, 78, 84)
                };
                lineHost.Controls.Add(line);
                _timelinePanel.Controls.Add(lineHost, i * 2 + 1, 0);
                _timelineLinePanels.Add(line);
            }
        }
    }

    private int GetCurrentStepIndex()
    {
        var running = _steps.FindIndex(step => step.State == StepState.Running);
        if (running >= 0)
        {
            return running;
        }

        var failed = _steps.FindIndex(step => step.State == StepState.Failed);
        if (failed >= 0)
        {
            return failed;
        }

        var waiting = _steps.FindIndex(step => step.State == StepState.Waiting);
        if (waiting >= 0)
        {
            return waiting;
        }

        return _steps.Count - 1;
    }

    private void RefreshProgressView()
    {
        if (_timelineDotLabels.Count != _steps.Count)
        {
            return;
        }

        var currentIndex = GetCurrentStepIndex();
        var currentStep = _steps[currentIndex];
        var currentPalette = GetStepPalette(currentStep.State, currentIndex);

        _currentStepIconLabel.Text = currentStep.State switch
        {
            StepState.Done => "✓",
            StepState.Failed => "!",
            StepState.Running => "▭",
            _ => "▭"
        };
        _currentStepTitleLabel.Text = currentStep.Title;
        _currentStepDescriptionLabel.Text = currentStep.Description;
        _currentStepActivityLabel.Text = GetActivityText(currentIndex, currentStep.State);
        _currentStepBadgeLabel.Text = currentStep.State switch
        {
            StepState.Done => "✓ 已完成",
            StepState.Failed => "! 失败",
            StepState.Running => "处理中",
            _ => "等待中"
        };
        _currentStepBadgeLabel.BackColor = currentStep.State switch
        {
            StepState.Done => Color.FromArgb(35, 134, 88),
            StepState.Failed => Color.FromArgb(170, 70, 70),
            StepState.Running => Color.FromArgb(14, 82, 130),
            _ => Color.FromArgb(72, 72, 78)
        };

        if (_displayedStepIndex < 0)
        {
            _displayedStepIndex = currentIndex;
            ApplyCurrentStepColors(currentPalette.ActiveBack, currentPalette.Accent, currentPalette.ActiveText);
        }
        else if (_displayedStepIndex != currentIndex)
        {
            StartStepTransition(currentIndex, currentPalette);
        }
        else if (!_transitionTimer.Enabled)
        {
            ApplyCurrentStepColors(currentPalette.ActiveBack, currentPalette.Accent, currentPalette.ActiveText);
        }

        for (var i = 0; i < _steps.Count; i++)
        {
            var state = _steps[i].State;
            var isCurrent = i == currentIndex;
            var palette = GetStepPalette(state, i);
            _timelineDotLabels[i].Text = state switch
            {
                StepState.Done => "✓",
                StepState.Failed => "!",
                StepState.Running => "●",
                _ => "○"
            };
            _timelineDotLabels[i].ForeColor = state switch
            {
                StepState.Done => VsGreen,
                StepState.Failed => Color.FromArgb(204, 52, 52),
                StepState.Running => VsBlue,
                _ => Color.FromArgb(110, 110, 116)
            };
            _timelineDotLabels[i].Font = new Font(Font.FontFamily, isCurrent ? 31F : state == StepState.Done ? 24F : 25F, FontStyle.Bold);
            _timelineTitleLabels[i].ForeColor = state == StepState.Done
                ? VsGreen
                : isCurrent ? VsText : Color.FromArgb(115, 115, 120);
            _timelineTitleLabels[i].Font = new Font(Font.FontFamily, isCurrent ? 9.5F : 9F, isCurrent ? FontStyle.Bold : FontStyle.Regular);
        }

        for (var i = 0; i < _timelineLinePanels.Count; i++)
        {
            _timelineLinePanels[i].BackColor = _steps[i].State == StepState.Done
                ? VsGreen
                : i < currentIndex ? VsBlue : Color.FromArgb(78, 78, 84);
        }
    }

    private void StartStepTransition(int currentIndex, StepPalette targetPalette)
    {
        _transitionTimer.Stop();
        _displayedStepIndex = currentIndex;
        _transitionFromBack = _currentStepPanel.BackColor;
        _transitionFromIcon = _currentStepIconLabel.ForeColor;
        _transitionFromTitle = _currentStepTitleLabel.ForeColor;
        _transitionToBack = targetPalette.ActiveBack;
        _transitionToIcon = targetPalette.Accent;
        _transitionToTitle = targetPalette.ActiveText;
        _transitionWatch.Restart();
        _transitionTimer.Start();
    }

    private void TickStepTransition()
    {
        var progress = Math.Min(1.0, _transitionWatch.Elapsed.TotalMilliseconds / TransitionDurationMs);
        var eased = 1 - Math.Pow(1 - progress, 3);
        ApplyCurrentStepColors(
            LerpColor(_transitionFromBack, _transitionToBack, eased),
            LerpColor(_transitionFromIcon, _transitionToIcon, eased),
            LerpColor(_transitionFromTitle, _transitionToTitle, eased));

        if (progress >= 1)
        {
            _transitionTimer.Stop();
            _transitionWatch.Reset();
        }
    }

    private void ApplyCurrentStepColors(Color backColor, Color iconColor, Color titleColor)
    {
        _currentStepPanel.BackColor = VsCard;
        foreach (Control child in _currentStepPanel.Controls)
        {
            child.BackColor = Color.Transparent;
        }

        _currentStepIconLabel.ForeColor = iconColor;
        _currentStepTitleLabel.ForeColor = titleColor;
    }

    private static StepPalette GetStepPalette(StepState state, int index)
    {
        return state switch
        {
            StepState.Done => new StepPalette(
                VsCard,
                VsCard,
                VsCard,
                VsGreen,
                VsText),
            StepState.Running => new StepPalette(
                VsCard,
                VsCard,
                VsCard,
                VsBlue,
                VsText),
            StepState.Failed => new StepPalette(
                VsCard,
                VsCard,
                VsCard,
                Color.FromArgb(204, 52, 52),
                VsText),
            _ => new StepPalette(
                VsCard,
                VsCard,
                VsCard,
                Color.FromArgb(110, 110, 116),
                VsText)
        };
    }

    private static string ShortStepTitle(int index)
    {
        return index switch
        {
            0 => "配置网卡",
            1 => "连接网线",
            2 => "获取 IP",
            3 => "打开页面",
            _ => "清理退出"
        };
    }

    private static string GetActivityText(int index, StepState state)
    {
        if (state == StepState.Done)
        {
            return index switch
            {
                0 => "本机网卡和 DHCP 服务已准备好",
                1 => "已检测到网线连接，链路已 UP",
                2 => "已获取 IPMI 设备地址",
                3 => "BMC 管理页面已打开",
                _ => "清理完成，可以安全退出"
            };
        }

        if (state == StepState.Failed)
        {
            return "遇到问题，请按提示检查后重试";
        }

        return index switch
        {
            0 => "正在将网卡设置为静态 IP 10.77.77.1，请稍候...",
            1 => "正在等待你插入连接 IPMI 管理口的网线...",
            2 => "正在等待 IPMI 设备通过 DHCP 获取地址...",
            3 => "正在打开默认浏览器访问 BMC 管理页面...",
            _ => "正在关闭 DHCP 服务并恢复网卡配置..."
        };
    }

    private static Color FadeColor(Color color, double amount)
    {
        var r = (int)(color.R + (255 - color.R) * amount);
        var g = (int)(color.G + (255 - color.G) * amount);
        var b = (int)(color.B + (255 - color.B) * amount);
        return Color.FromArgb(r, g, b);
    }

    private static Color LerpColor(Color from, Color to, double amount)
    {
        var r = (int)(from.R + (to.R - from.R) * amount);
        var g = (int)(from.G + (to.G - from.G) * amount);
        var b = (int)(from.B + (to.B - from.B) * amount);
        return Color.FromArgb(r, g, b);
    }

    private async Task RunFlowAsync()
    {
        try
        {
            SetBusy("正在检测网卡...", "请选择要连接 IPMI 管理口的目标网卡。");
            var adapters = NetworkConfigManager.GetWiredAdapters();
            if (adapters.Count == 0)
            {
                throw new InvalidOperationException("未检测到可用网卡，请确认网卡驱动已安装。");
            }

            _adapterComboBox.Items.Clear();
            _adapterComboBox.Items.AddRange(adapters.Cast<object>().ToArray());
            _adapterComboBox.SelectedIndex = 0;
            SetIdle("请选择目标网卡", "请选择你准备用网线连接服务器的那块网卡。");
            _startButton.Enabled = true;
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                SetIdle("正在退出...", "正在还原网卡配置。");
            }
        }
        catch (Exception ex)
        {
            if (_isClosing || IsDisposed)
            {
                return;
            }

            MarkCurrentFailure(ex.Message);
            SetIdle("❌ 操作失败", ex.Message);
            _exitButton.Enabled = true;
        }
    }

    private async void StartSelectedAdapterFlow()
    {
        if (_adapterComboBox.SelectedItem is not WiredAdapter adapter)
        {
            return;
        }

        _selectedAdapter = adapter;
        _adapterComboBox.Enabled = false;
        _startButton.Enabled = false;

        try
        {
            await ConfigureLocalAdapterAsync(_flowCts.Token);
            await WaitForLinkAsync(_flowCts.Token);
            var lease = await WaitForDhcpLeaseAsync(_flowCts.Token);
            OpenBrowser(lease.IpAddress.ToString());

            SetStep(3, StepState.Done, "✅ 已自动打开 BMC 管理页面");
            SetStep(4, StepState.Done, "可以登录 BMC 后点击“完成 / 退出”还原网卡。");
            SetIdle("✅ 已自动打开 BMC 管理页面", "BMC 地址：http://" + lease.IpAddress);
            _exitButton.Enabled = true;
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                SetIdle("正在退出...", "正在还原网卡配置。");
            }
        }
        catch (Exception ex)
        {
            if (_isClosing || IsDisposed)
            {
                return;
            }

            MarkCurrentFailure(ex.Message);
            SetIdle("❌ 操作失败", ex.Message);
            _adapterComboBox.Enabled = true;
            _startButton.Enabled = true;
            _exitButton.Enabled = true;
        }
    }

    private async Task ConfigureLocalAdapterAsync(CancellationToken cancellationToken)
    {
        SetStep(0, StepState.Running, "正在记录原始配置：" + _selectedAdapter!.Name);
        SetBusy("正在配置本机网卡...", "先记录原始配置，再将网卡切换到 10.77.77.1。");
        _originalConfig = NetworkConfigManager.CaptureOriginalConfig(_selectedAdapter);

        if (!_originalConfig.DhcpEnabled)
        {
            SetStep(0, StepState.Running, "检测到当前是静态 IP，先还原为 DHCP 以清理残留配置。");
            await NetworkConfigManager.ForceDhcpBestEffortAsync(_selectedAdapter, cancellationToken);
            await Task.Delay(1200, cancellationToken);
            _originalConfig = AdapterOriginalConfig.CreateDhcp();
        }

        await NetworkConfigManager.SetStaticForToolAsync(_selectedAdapter, cancellationToken);
        _dhcpServer.Start();
        await CompleteStepAsync(0, "✅ 本机网卡配置完成：10.77.77.1 / 255.255.255.0", cancellationToken);
    }

    private async Task WaitForLinkAsync(CancellationToken cancellationToken)
    {
        SetStep(1, StepState.Running, "请用网线连接服务器的 IPMI 管理口，正在等待 Link UP。");
        SetBusy("请插入网线", "等待网卡链路变为 Link UP，界面会持续自动刷新。");

        while (!cancellationToken.IsCancellationRequested)
        {
            if (await NetworkConfigManager.IsLinkUpAsync(_selectedAdapter!, cancellationToken))
            {
                await CompleteStepAsync(1, "✅ 网线已连接，链路已 UP", cancellationToken);
                return;
            }

            await Task.Delay(1500, cancellationToken);
        }
    }

    private async Task<DhcpLease> WaitForDhcpLeaseAsync(CancellationToken cancellationToken)
    {
        SetStep(2, StepState.Running, "链路已 UP，正在等待 IPMI 通过 DHCP 获取地址，最多等待 3 分钟。");
        SetBusy("正在等待 IPMI 获取 IP...", "如果 3 分钟内没有响应，请检查网线是否插在 IPMI 管理口。");

        var tcs = new TaskCompletionSource<DhcpLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, DhcpLease lease) => tcs.TrySetResult(lease);

        _dhcpServer.LeaseAssigned += Handler;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var registration = linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token));

            var lease = await tcs.Task;
            await CompleteStepAsync(2, "✅ 已检测到 IPMI 设备，IP：" + lease.IpAddress, cancellationToken);
            return lease;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("3 分钟内未收到 IPMI 的 DHCP 请求，请检查网线是否插在 IPMI 管理口。");
        }
        finally
        {
            _dhcpServer.LeaseAssigned -= Handler;
        }
    }

    private static void OpenBrowser(string ipAddress)
    {
        Process.Start(new ProcessStartInfo("http://" + ipAddress)
        {
            UseShellExecute = true
        });
    }

    private async Task CompleteStepAsync(int index, string description, CancellationToken cancellationToken)
    {
        SetStep(index, StepState.Done, description);
        await Task.Delay(1300, cancellationToken);
    }

    private void SetStep(int index, StepState state, string description)
    {
        _steps[index].State = state;
        _steps[index].Description = description;
        RefreshProgressView();
    }

    private void SetBusy(string main, string detail)
    {
        _mainStatusLabel.Text = "BMC 管理口快速登录";
        _detailLabel.Text = detail;
    }

    private void SetIdle(string main, string detail)
    {
        _mainStatusLabel.Text = "BMC 管理口快速登录";
        _detailLabel.Text = detail;
    }

    private void MarkCurrentFailure(string message)
    {
        var index = _steps.FindIndex(step => step.State == StepState.Running);
        if (index < 0)
        {
            index = _steps.FindIndex(step => step.State == StepState.Waiting);
        }

        if (index >= 0)
        {
            SetStep(index, StepState.Failed, "❌ " + message);
        }
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_cleanupStarted)
        {
            return;
        }

        _cleanupStarted = true;
        _isClosing = true;
        _exitButton.Enabled = false;
        _flowCts.Cancel();
        await CleanupAsync();
        _allowClose = true;
        Close();
    }

    private async Task CleanupAsync()
    {
        if (_cleanupFinished)
        {
            return;
        }

        try
        {
            if (!IsDisposed)
            {
                SetStep(4, StepState.Running, "正在关闭 DHCP Server 并还原网卡配置...");
                SetBusy("正在清理并退出...", "请稍候，正在把网卡恢复为启动前的配置。");
            }

            _dhcpServer.Stop();

            if (_selectedAdapter is not null)
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
                await NetworkConfigManager.ForceDhcpBestEffortAsync(_selectedAdapter, cleanupCts.Token);
            }

            if (!IsDisposed)
            {
                SetStep(4, StepState.Done, "✅ 网卡配置已还原，DHCP Server 已关闭");
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                SetStep(4, StepState.Failed, "❌ 自动还原时遇到问题：" + ex.Message);
            }

            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "ezgetBMCIP.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " cleanup failed: " + ex + Environment.NewLine);
            }
            catch
            {
                // Exiting must remain best-effort even when the app directory is read-only.
            }
        }

        _cleanupFinished = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _transitionTimer.Dispose();
            _flowCts.Dispose();
            _dhcpServer.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record StepPalette(
        Color ActiveBack,
        Color NearBack,
        Color FarBack,
        Color Accent,
        Color ActiveText);
}
