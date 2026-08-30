using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace EzGetBmcIp
{
    [XmlRoot("NetworkRecovery")]
    public sealed class NetworkRecoverySnapshot
    {
        public int SchemaVersion { get; set; } = 2;
        public string SessionId { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public string AdapterName { get; set; } = string.Empty;
        public string AdapterDescription { get; set; } = string.Empty;
        public string AdapterId { get; set; } = string.Empty;
        public string AdapterMacAddress { get; set; } = string.Empty;
        public string ToolServerIp { get; set; } = string.Empty;
        public string ToolLeaseIp { get; set; } = string.Empty;
        public bool DhcpEnabled { get; set; }
        public bool DnsServersFromDhcp { get; set; }

        [XmlArrayItem("Address")]
        public List<RecoveryAddress> StaticAddresses { get; set; } = new List<RecoveryAddress>();

        [XmlArrayItem("Gateway")]
        public List<string> Gateways { get; set; } = new List<string>();

        [XmlArrayItem("Metric")]
        public List<int> GatewayMetrics { get; set; } = new List<int>();

        [XmlArrayItem("Server")]
        public List<string> DnsServers { get; set; } = new List<string>();

        public WiredAdapter ToAdapter()
        {
            return new WiredAdapter(AdapterName, AdapterDescription, AdapterId, AdapterMacAddress);
        }

        public AdapterOriginalConfig ToOriginalConfig()
        {
            var config = new AdapterOriginalConfig
            {
                DhcpEnabled = DhcpEnabled,
                ActiveDhcpEnabled = DhcpEnabled,
                PersistentDhcpEnabled = DhcpEnabled,
                DnsServersFromDhcp = DnsServersFromDhcp
            };

            foreach (var item in StaticAddresses)
            {
                config.StaticAddresses.Add(new AdapterIpv4Address(
                    IPAddress.Parse(item.Address),
                    IPAddress.Parse(item.Mask),
                    ParseEnum(item.PrefixOrigin, PrefixOrigin.Other),
                    ParseEnum(item.SuffixOrigin, SuffixOrigin.Other),
                    ParseEnum(item.AddressState, DuplicateAddressDetectionState.Invalid)));
            }

            foreach (var gateway in Gateways)
            {
                config.Gateways.Add(IPAddress.Parse(gateway));
            }

            config.GatewayMetrics.AddRange(GatewayMetrics);

            foreach (var dns in DnsServers)
            {
                config.DnsServers.Add(IPAddress.Parse(dns));
            }

            return config;
        }

        public AdapterOriginalConfig ToOriginalConfig(WiredAdapter currentAdapter)
        {
            var config = ToOriginalConfig();
            if (SchemaVersion == 1)
            {
                config.StaticAddresses.RemoveAll(item =>
                {
                    var remove = NetworkConfigManager.IsCurrentlyConfirmedAutomaticApipa(
                        currentAdapter, item.Address);
                    if (remove)
                    {
                        NetworkConfigManager.Logger?.Invoke(
                            "Legacy snapshot automatic APIPA excluded after live origin confirmation: " +
                            item.Address);
                    }
                    return remove;
                });
            }
            return config;
        }

        private static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            return Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;
        }

        public SubnetConfig ToSubnetConfig()
        {
            var bytes = IPAddress.Parse(ToolServerIp).GetAddressBytes();
            if (bytes.Length != 4)
            {
                throw new InvalidDataException("Recovery snapshot contains an invalid tool IPv4 address.");
            }

            return new SubnetConfig
            {
                Octet1 = bytes[0],
                Octet2 = bytes[1],
                Octet3 = bytes[2],
                Octet4 = bytes[3]
            };
        }

        public bool MatchesAdapter(WiredAdapter adapter)
        {
            var snapshotId = NormalizeId(AdapterId);
            var currentId = NormalizeId(adapter.Id);
            var idMatches = snapshotId.Length > 0
                && snapshotId.Equals(currentId, StringComparison.OrdinalIgnoreCase);
            var snapshotMac = NormalizeMac(AdapterMacAddress);
            var currentMac = NormalizeMac(adapter.MacAddress);
            var macMatches = snapshotMac.Length > 0
                && snapshotMac.Equals(currentMac, StringComparison.OrdinalIgnoreCase);
            return idMatches || macMatches;
        }

        private static string NormalizeId(string value)
        {
            return (value ?? string.Empty).Trim().Trim('{', '}');
        }

        private static string NormalizeMac(string value)
        {
            return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        }
    }

    public sealed class RecoveryAddress
    {
        [XmlAttribute]
        public string Address { get; set; } = string.Empty;

        [XmlAttribute]
        public string Mask { get; set; } = string.Empty;

        [XmlAttribute]
        public string PrefixOrigin { get; set; } = string.Empty;

        [XmlAttribute]
        public string SuffixOrigin { get; set; } = string.Empty;

        [XmlAttribute]
        public string AddressState { get; set; } = string.Empty;
    }

    public static class NetworkRecoveryStore
    {
        public const string WatchdogArgument = "--recovery-watchdog";
        private const string RecoveryMutexName = @"Global\ezgetBMCIP-NetworkRecovery";

        public static string RecoveryFilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ezgetBMCIP",
                    "recovery",
                    "active.xml");
            }
        }

        public static NetworkRecoverySnapshot Save(
            WiredAdapter adapter,
            AdapterOriginalConfig originalConfig,
            SubnetConfig subnetConfig)
        {
            var snapshot = new NetworkRecoverySnapshot
            {
                SessionId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow,
                AdapterName = adapter.Name,
                AdapterDescription = adapter.Description,
                AdapterId = adapter.Id,
                AdapterMacAddress = adapter.MacAddress,
                ToolServerIp = subnetConfig.ServerIp,
                ToolLeaseIp = subnetConfig.PoolStart,
                DhcpEnabled = originalConfig.DhcpEnabled,
                DnsServersFromDhcp = originalConfig.DnsServersFromDhcp,
                StaticAddresses = originalConfig.StaticAddresses
                    .Select(item => new RecoveryAddress
                    {
                        Address = item.Address.ToString(),
                        Mask = item.Mask.ToString(),
                        PrefixOrigin = item.PrefixOrigin.ToString(),
                        SuffixOrigin = item.SuffixOrigin.ToString(),
                        AddressState = item.AddressState.ToString()
                    })
                    .ToList(),
                Gateways = originalConfig.Gateways.Select(ip => ip.ToString()).ToList(),
                GatewayMetrics = originalConfig.GatewayMetrics.ToList(),
                DnsServers = originalConfig.DnsServers.Select(ip => ip.ToString()).ToList()
            };

            WriteAtomically(snapshot);
            return snapshot;
        }

        public static bool TryLoad(out NetworkRecoverySnapshot snapshot, out string error)
        {
            snapshot = new NetworkRecoverySnapshot();
            error = string.Empty;
            if (!File.Exists(RecoveryFilePath))
            {
                return false;
            }

            try
            {
                var serializer = new XmlSerializer(typeof(NetworkRecoverySnapshot));
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
                using (var stream = new FileStream(RecoveryFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = XmlReader.Create(stream, settings))
                {
                    snapshot = serializer.Deserialize(reader) as NetworkRecoverySnapshot
                        ?? throw new InvalidDataException("Recovery snapshot is empty.");
                }

                Validate(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                snapshot = new NetworkRecoverySnapshot();
                return false;
            }
        }

        public static void DeleteIfSessionMatches(string sessionId)
        {
            NetworkRecoverySnapshot snapshot;
            string error;
            if (!TryLoad(out snapshot, out error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    throw new InvalidDataException("Unable to verify the recovery snapshot before deletion: " + error);
                return;
            }

            if (!snapshot.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                return;

            File.Delete(RecoveryFilePath);
            if (File.Exists(RecoveryFilePath))
                throw new IOException("The recovery snapshot could not be deleted.");
        }

        public static void StartWatchdog(NetworkRecoverySnapshot snapshot, Action<string> logger)
        {
            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                throw new InvalidOperationException("Unable to locate the current executable for recovery watchdog.");
            }

            var arguments = WatchdogArgument + " " + Process.GetCurrentProcess().Id + " " + snapshot.SessionId;
            var process = Process.Start(new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            });

            if (process == null)
            {
                throw new InvalidOperationException("Unable to start the network recovery watchdog.");
            }

            logger("Recovery watchdog started: pid=" + process.Id + ", session=" + snapshot.SessionId);
        }

        public static bool TryParseWatchdogArguments(string[] args, out int ownerProcessId, out string sessionId)
        {
            ownerProcessId = 0;
            sessionId = string.Empty;
            return args.Length == 3
                && args[0].Equals(WatchdogArgument, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[1], out ownerProcessId)
                && ownerProcessId > 0
                && Guid.TryParseExact(args[2], "N", out _)
                && (sessionId = args[2]).Length > 0;
        }

        public static Task ExecuteWithRecoveryLockAsync(Func<Task> action, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                using var mutex = new Mutex(false, RecoveryMutexName);
                var ownsMutex = false;
                try
                {
                    int signaled;
                    try
                    {
                        signaled = WaitHandle.WaitAny(new WaitHandle[] { mutex, cancellationToken.WaitHandle });
                    }
                    catch (AbandonedMutexException ex)
                    {
                        signaled = ex.MutexIndex;
                        ownsMutex = signaled == 0;
                    }

                    if (signaled != 0)
                        throw new OperationCanceledException(cancellationToken);

                    ownsMutex = true;
                    action().GetAwaiter().GetResult();
                }
                finally
                {
                    if (ownsMutex)
                        mutex.ReleaseMutex();
                }
            }, cancellationToken);
        }

        public static async Task<int> RunWatchdogAsync(
            int ownerProcessId,
            string sessionId,
            Action<string> logger)
        {
            try
            {
                try
                {
                    using (var owner = Process.GetProcessById(ownerProcessId))
                    {
                        await Task.Run(() => owner.WaitForExit());
                    }
                }
                catch (ArgumentException)
                {
                }

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70)))
                {
                    await ExecuteWithRecoveryLockAsync(async () =>
                    {
                        if (!TryLoad(out var snapshot, out var error))
                        {
                            if (!string.IsNullOrWhiteSpace(error))
                                logger("Recovery watchdog could not read snapshot: " + error);
                            return;
                        }

                        if (!snapshot.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                        {
                            logger("Recovery watchdog ignored a newer recovery session.");
                            return;
                        }

                        logger("Owner process exited with an active recovery snapshot; restoring adapter.");
                        var adapter = NetworkConfigManager.ResolveCurrentAdapter(snapshot.ToAdapter());
                        await NetworkConfigManager.RestoreOriginalConfigAsync(
                            adapter,
                            snapshot.ToOriginalConfig(adapter),
                            snapshot.ToSubnetConfig(),
                            cts.Token);

                        DeleteIfSessionMatches(sessionId);
                        logger("Recovery watchdog restored the original adapter configuration.");
                    }, cts.Token);
                }
                return 0;
            }
            catch (Exception ex)
            {
                logger("Recovery watchdog failed: " + ex);
                return 1;
            }
        }

        private static void WriteAtomically(NetworkRecoverySnapshot snapshot)
        {
            var directory = Path.GetDirectoryName(RecoveryFilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Recovery directory is unavailable.");
            }

            EnsureSecureRecoveryDirectory(directory);
            var tempPath = RecoveryFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var serializer = new XmlSerializer(typeof(NetworkRecoverySnapshot));
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    serializer.Serialize(stream, snapshot);
                    stream.Flush(true);
                }

                if (File.Exists(RecoveryFilePath))
                {
                    try
                    {
                        File.Replace(tempPath, RecoveryFilePath, null, true);
                    }
                    catch
                    {
                        File.Delete(RecoveryFilePath);
                        File.Move(tempPath, RecoveryFilePath);
                    }
                }
                else
                {
                    File.Move(tempPath, RecoveryFilePath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static void Validate(NetworkRecoverySnapshot snapshot)
        {
            if (snapshot == null
                || (snapshot.SchemaVersion != 1 && snapshot.SchemaVersion != 2)
                || !Guid.TryParseExact(snapshot.SessionId, "N", out _)
                || string.IsNullOrWhiteSpace(snapshot.AdapterName)
                || (string.IsNullOrWhiteSpace(snapshot.AdapterId)
                    && string.IsNullOrWhiteSpace(snapshot.AdapterMacAddress)))
            {
                throw new InvalidDataException("Recovery snapshot is incomplete or unsupported.");
            }

            snapshot.ToOriginalConfig();
            snapshot.ToSubnetConfig();
        }

        private static void EnsureSecureRecoveryDirectory(string recoveryDirectory)
        {
            var appDirectory = Directory.GetParent(recoveryDirectory)?.FullName
                ?? throw new InvalidOperationException("Recovery application directory is unavailable.");
            Directory.CreateDirectory(appDirectory);
            ApplyAdministratorOnlyAcl(appDirectory);
            Directory.CreateDirectory(recoveryDirectory);
            ApplyAdministratorOnlyAcl(recoveryDirectory);
        }

        private static void ApplyAdministratorOnlyAcl(string directory)
        {
            RunIcacls(directory, "/setowner *S-1-5-32-544");
            RunIcacls(directory,
                "/inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F");
        }

        private static void RunIcacls(string directory, string operation)
        {
            using var process = Process.Start(new ProcessStartInfo(
                "icacls.exe",
                "\"" + directory + "\" " + operation)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Unable to start icacls for recovery storage.");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Unable to secure recovery storage. " + (stderr + Environment.NewLine + stdout).Trim());
            }
        }
    }
}
