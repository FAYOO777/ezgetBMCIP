using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;
using EzGetBmcIp;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--render-ui")
            {
                RenderUiSnapshot(args[1]);
                Console.WriteLine(args[1]);
                return 0;
            }

            RecoverySnapshotRoundTrips();
            RecoverySnapshotMatchesAdapterIdentity();
            DhcpServerUsesWildcardSocketAndInterfaceFilter();
            DhcpLeaseAssignedBeforeWaitIsCached();
            DhcpRequestServerSelectionIsRespected();
            await NativeCommandOutputUsesSystemOemEncodingAsync();
            MixedNativeCommandEncodingsAreDetected();
            await EndpointProbeFindsListeningPortAsync();
            await EndpointProbeTimesOutAsync();
            Console.WriteLine("All smoke tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void DhcpLeaseAssignedBeforeWaitIsCached()
    {
        using var server = new DhcpServer(new SubnetConfig(), 0);
        var lease = new DhcpLease
        {
            IpAddress = IPAddress.Parse("10.77.77.100"),
            MacAddress = new byte[] { 0xB0, 0x7B, 0x25, 0x47, 0xF5, 0xF5 }
        };

        server.NotifyLeaseAssigned(lease);

        Assert(ReferenceEquals(server.LastAssignedLease, lease),
            "A DHCP ACK received before the UI starts waiting must remain available.");
    }

    private static void MixedNativeCommandEncodingsAreDetected()
    {
        var text = "该计算机上没有配置域名服务器(DNS)。";
        var decoded = ProcessOutputDecoder.Decode(
            Encoding.UTF8.GetBytes(text),
            DiagnosticReporter.GetNativeConsoleEncoding());
        Assert(decoded == text, "UTF-8 native command output was decoded as the OEM code page.");
    }

    private static async Task NativeCommandOutputUsesSystemOemEncodingAsync()
    {
        Assert(
            DiagnosticReporter.GetNativeConsoleEncoding().CodePage ==
            CultureInfo.CurrentCulture.TextInfo.OEMCodePage,
            "Native command output must use the current Windows OEM code page.");

        var output = await DiagnosticReporter.RunProcessAsync(
            "cmd.exe",
            "/d /c \"echo 中文编码测试\"");
        Assert(output.Contains("中文编码测试", StringComparison.Ordinal),
            "Native Chinese command output was decoded incorrectly.");
    }

    private static void DhcpServerUsesWildcardSocketAndInterfaceFilter()
    {
        var config = new SubnetConfig
        {
            Octet1 = 127,
            Octet2 = 0,
            Octet3 = 0,
            Octet4 = 1
        };

        using var server = new DhcpServer(config, 0);
        server.Start();
        Assert(server.BoundEndpoint is not null, "DHCP server did not bind a socket.");
        Assert(server.BoundEndpoint!.Address.Equals(IPAddress.Any),
            "DHCP server must use a wildcard socket so pre-address DHCP broadcasts are receivable.");
        Assert(server.ReplyBroadcastAddress.Equals(IPAddress.Parse("127.0.0.255")),
            "DHCP replies must use the configured subnet broadcast address.");
        Assert(DhcpServer.IsPacketFromExpectedInterface(14, 14),
            "DHCP packets from the selected interface must be accepted.");
        Assert(!DhcpServer.IsPacketFromExpectedInterface(14, 12),
            "DHCP packets from a different interface must be rejected.");
        Assert(DhcpServer.IsPacketFromExpectedInterface(0, 12),
            "An unspecified interface index must preserve test compatibility.");
    }

    private static void DhcpRequestServerSelectionIsRespected()
    {
        var request = BuildDhcpRequestWithServerIdentifier(IPAddress.Parse("192.168.1.1"));
        Assert(!DhcpServer.IsRequestForServer(
                request,
                IPAddress.Parse("10.77.77.1"),
                out var requestedServer),
            "A DHCPREQUEST selecting another server must be ignored.");
        Assert(requestedServer?.ToString() == "192.168.1.1",
            "The selected DHCP server was not parsed correctly.");

        var matchingRequest = BuildDhcpRequestWithServerIdentifier(IPAddress.Parse("10.77.77.1"));
        Assert(DhcpServer.IsRequestForServer(
                matchingRequest,
                IPAddress.Parse("10.77.77.1"),
                out _),
            "A DHCPREQUEST selecting this server must be accepted.");
    }

    private static byte[] BuildDhcpRequestWithServerIdentifier(IPAddress serverIdentifier)
    {
        var packet = new byte[250];
        packet[0] = 1;
        packet[236] = 99;
        packet[237] = 130;
        packet[238] = 83;
        packet[239] = 99;
        packet[240] = 53;
        packet[241] = 1;
        packet[242] = 3;
        packet[243] = 54;
        packet[244] = 4;
        Array.Copy(serverIdentifier.GetAddressBytes(), 0, packet, 245, 4);
        packet[249] = 255;
        return packet;
    }

    private static void RecoverySnapshotRoundTrips()
    {
        var snapshot = new NetworkRecoverySnapshot
        {
            SessionId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            AdapterName = "Ethernet",
            AdapterDescription = "Test adapter",
            AdapterId = "11111111-2222-3333-4444-555555555555",
            AdapterMacAddress = "001122334455",
            ToolServerIp = "10.77.77.1",
            ToolLeaseIp = "10.77.77.100",
            DhcpEnabled = false,
            DnsServersFromDhcp = false,
            StaticAddresses = new List<RecoveryAddress>
            {
                new RecoveryAddress { Address = "192.168.50.20", Mask = "255.255.255.0" },
                new RecoveryAddress { Address = "192.168.50.21", Mask = "255.255.255.0" }
            },
            Gateways = new List<string> { "192.168.50.1" },
            GatewayMetrics = new List<int> { 25 },
            DnsServers = new List<string> { "1.1.1.1", "8.8.8.8" }
        };

        var serializer = new XmlSerializer(typeof(NetworkRecoverySnapshot));
        using var stream = new MemoryStream();
        serializer.Serialize(stream, snapshot);
        stream.Position = 0;
        var restoredSnapshot = (NetworkRecoverySnapshot)serializer.Deserialize(stream)!;
        var restoredConfig = restoredSnapshot.ToOriginalConfig();
        var restoredSubnet = restoredSnapshot.ToSubnetConfig();

        Assert(!restoredConfig.DhcpEnabled, "Static mode was not preserved.");
        Assert(!restoredConfig.DnsServersFromDhcp, "Manual DNS mode was not preserved.");
        Assert(restoredConfig.StaticAddresses.Count == 2, "Static addresses were not preserved.");
        Assert(restoredConfig.Gateways.Single().ToString() == "192.168.50.1", "Gateway was not preserved.");
        Assert(restoredConfig.GatewayMetrics.Single() == 25, "Gateway metric was not preserved.");
        Assert(restoredConfig.DnsServers.Count == 2, "DNS servers were not preserved.");
        Assert(restoredSubnet.ServerIp == "10.77.77.1", "Tool subnet was not preserved.");
    }

    private static void RecoverySnapshotMatchesAdapterIdentity()
    {
        var snapshot = new NetworkRecoverySnapshot
        {
            AdapterName = "Ethernet",
            AdapterId = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
            AdapterMacAddress = "00-11-22-33-44-55"
        };

        Assert(snapshot.MatchesAdapter(new WiredAdapter(
            "Renamed", "Adapter", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "001122334455")),
            "Adapter GUID normalization failed.");
        Assert(snapshot.MatchesAdapter(new WiredAdapter(
            "Ethernet 2", "Adapter", "different", "00:11:22:33:44:55")),
            "Adapter MAC fallback failed.");
        Assert(!snapshot.MatchesAdapter(new WiredAdapter(
            "Ethernet 3", "Adapter", "different", "AABBCCDDEEFF")),
            "Unrelated adapter was incorrectly matched.");
    }

    private static async Task EndpointProbeFindsListeningPortAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();
        try
        {
            var result = await BmcEndpointProbe.WaitForEndpointAsync(
                IPAddress.Loopback,
                TimeSpan.FromSeconds(2),
                CancellationToken.None,
                candidates: new[] { new BmcEndpointCandidate { Scheme = "https", Port = port } });
            using var accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert(result is not null, "Listening endpoint was not detected.");
            Assert(result!.Scheme == "https" && result.Port == port, "Detected endpoint was incorrect.");
            Assert(result.Url == "https://127.0.0.1:" + port, "Detected URL was incorrect.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task EndpointProbeTimesOutAsync()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var unusedPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        var result = await BmcEndpointProbe.WaitForEndpointAsync(
            IPAddress.Loopback,
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None,
            candidates: new[] { new BmcEndpointCandidate { Scheme = "http", Port = unusedPort } });
        Assert(result is null, "Closed endpoint should have timed out.");
    }

    private static void RenderUiSnapshot(string outputPath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.None, updateAccent: true);
                var window = new MainWindow
                {
                    Width = 700,
                    Height = 900
                };
                var vm = (MainViewModel)window.DataContext;
                vm.AppPhase = AppPhase.FlowRunning;
                vm.DiscoveredIp = "10.77.77.100";
                vm.EndpointStatusText = "IP 已分配，但 45 秒内管理页面尚未响应。可以重新检测，或手动尝试 HTTPS / HTTP。";
                vm.AdapterCardLine1 = "测试网卡 - 直连 BMC 管理口";
                vm.CurrentStepIndex = 3;
                vm.BadgeState = StepState.Pending;
                vm.BadgeText = "等待页面";
                vm.ActivityText = "DHCP 地址分配已完成，管理页面仍在启动或使用了其他端口。";

                window.Show();
                window.UpdateLayout();
                var endpointButtons = FindVisualChildren<System.Windows.Controls.Button>(window)
                    .Where(button => button.Content is string text &&
                        (text == "复制地址" || text == "打开 HTTPS" || text == "打开 HTTP" || text == "重新检测"))
                    .ToList();
                Assert(endpointButtons.Count == 4, "Endpoint action buttons were not all rendered.");
                var buttonBounds = endpointButtons
                    .Select(button =>
                    {
                        var point = button.TransformToAncestor(window).Transform(new Point(0, 0));
                        return new Rect(point.X, point.Y, button.ActualWidth, button.ActualHeight);
                    })
                    .ToList();
                Assert(buttonBounds.All(rect => rect.Left >= 0 && rect.Right <= window.ActualWidth),
                    "Endpoint action buttons overflow the window.");
                for (var i = 0; i < buttonBounds.Count; i++)
                {
                    for (var j = i + 1; j < buttonBounds.Count; j++)
                        Assert(!buttonBounds[i].IntersectsWith(buttonBounds[j]), "Endpoint action buttons overlap.");
                }

                var dpi = VisualTreeHelper.GetDpi(window);
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
                    96 * dpi.DpiScaleX,
                    96 * dpi.DpiScaleY,
                    PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(outputPath))
                    encoder.Save(stream);

                typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, true);
                window.Close();
                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("UI snapshot render timed out.");
        if (failure is not null)
            throw new InvalidOperationException("UI snapshot render failed.", failure);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
