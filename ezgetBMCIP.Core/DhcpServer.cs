using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EzGetBmcIp;

public sealed class DhcpServer : IDisposable
{
    private readonly IPAddress _serverIp;
    private readonly IPAddress _mask;
    private readonly IPAddress _poolStart;
    private readonly IPAddress _broadcastIp;
    private readonly int _expectedInterfaceIndex;
    private const int DhcpServerPort = 67;
    private const int DhcpClientPort = 68;

    private readonly Dictionary<string, DhcpLease> _leases = new();
    private readonly object _sync = new();
    private DhcpLease? _lastAssignedLease;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public DhcpServer(SubnetConfig config, WiredAdapter adapter)
    {
        _serverIp = IPAddress.Parse(config.ServerIp);
        _mask = IPAddress.Parse(config.Mask);
        _poolStart = IPAddress.Parse(config.PoolStart);
        _broadcastIp = CalculateBroadcastAddress(_serverIp, _mask);
        _expectedInterfaceIndex = ResolveInterfaceIndex(adapter);
    }

    public event EventHandler<DhcpLease>? LeaseAssigned;
    public event EventHandler<string>? ErrorEncountered;
    public Action<string>? Logger { get; set; }
    public DhcpLease? LastAssignedLease
    {
        get
        {
            lock (_sync)
                return _lastAssignedLease;
        }
    }

    public void Start()
    {
        if (_udp is not null)
        {
            return;
        }

        try
        {
            _cts = new CancellationTokenSource();
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.EnableBroadcast = true;
            _udp.Client.ExclusiveAddressUse = true;
            _udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DhcpServerPort));
            Logger?.Invoke("DHCP server bound to " + _udp.Client.LocalEndPoint +
                "; accepting interface index " + _expectedInterfaceIndex +
                "; reply broadcast " + _broadcastIp);
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
        }
        catch
        {
            ReleaseSocket();
            throw;
        }
    }

    public void Stop()
    {
        ReleaseSocket();
    }

    private void ReleaseSocket()
    {
        _cts?.Cancel();
        _udp?.Close();
        _udp?.Dispose();
        _udp = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private void ReceiveLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _udp is not null)
        {
            try
            {
                var udp = _udp;
                if (udp is null)
                    break;
                var buffer = new byte[1500];
                EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                var flags = SocketFlags.None;
                var received = udp.Client.ReceiveMessageFrom(
                    buffer, 0, buffer.Length, ref flags, ref remoteEndpoint, out var packetInformation);
                if (!IsPacketFromExpectedInterface(_expectedInterfaceIndex, packetInformation.Interface))
                {
                    Logger?.Invoke("DHCP packet ignored from interface index " +
                        packetInformation.Interface + "; expected " + _expectedInterfaceIndex);
                    continue;
                }
                HandlePacketAsync(buffer.Take(received).ToArray(), cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException se) when (se.SocketErrorCode is SocketError.TimedOut
                or SocketError.ConnectionReset or SocketError.MessageSize
                or SocketError.ConnectionRefused or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable or SocketError.NetworkReset)
            {
                // transient — keep listening
            }
            catch (SocketException se)
            {
                ErrorEncountered?.Invoke(this, "DHCP Socket 错误：" + se.Message);
                break;
            }
            catch (Exception ex)
            {
                ErrorEncountered?.Invoke(this, "DHCP 未知错误：" + ex.Message);
                break;
            }
        }
    }

    internal static bool IsPacketFromExpectedInterface(int expectedInterfaceIndex, int packetInterfaceIndex)
    {
        return expectedInterfaceIndex <= 0 || packetInterfaceIndex == expectedInterfaceIndex;
    }

    private async Task HandlePacketAsync(byte[] request, CancellationToken cancellationToken)
    {
        if (request.Length < 240 || request[0] != 1)
        {
            return;
        }

        var messageType = GetOption(request, 53)?.FirstOrDefault();
        if (messageType is not (1 or 3))
        {
            return;
        }

        var mac = request.Skip(28).Take(6).ToArray();
        if (mac.Length == 0 || mac.All(b => b == 0))
        {
            return;
        }

        var lease = GetOrCreateLease(mac);

        if (messageType == 3)
        {
            var serverOption = GetOption(request, 54);
            if (serverOption is { Length: 4 })
            {
                var requestedServer = new IPAddress(serverOption);
                if (!requestedServer.Equals(_serverIp))
                {
                    Logger?.Invoke("DHCP: REQUEST ignored because it selected another server: " + requestedServer);
                    return;
                }
            }

            var opt50 = GetOption(request, 50);
            if (opt50 is { Length: 4 })
            {
                var requestedIp = new IPAddress(opt50);
                lock (_sync)
                {
                    var requestedLease = _leases.Values.FirstOrDefault(l => l.IpAddress.Equals(requestedIp));
                    if (requestedLease != null && requestedLease != lease)
                    {
                        var key = Convert.ToHexString(mac);
                        _leases[key] = requestedLease;
                        lease = requestedLease;
                    }
                }
            }
        }

        var responseType = messageType == 1 ? (byte)2 : (byte)5;
        var response = BuildResponse(request, mac, lease.IpAddress, responseType, _serverIp, _mask);
        var destination = new IPEndPoint(_broadcastIp, DhcpClientPort);
        if (_udp is not null)
        {
            await _udp.SendAsync(response, response.Length, destination);
        }

        if (responseType == 5)
        {
            lock (_sync)
                _lastAssignedLease = lease;
            LeaseAssigned?.Invoke(this, lease);
        }
    }

    private DhcpLease GetOrCreateLease(byte[] mac)
    {
        var key = Convert.ToHexString(mac);
        lock (_sync)
        {
            if (_leases.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (_leases.Count > 0)
                Debug.WriteLine($"DHCP: second MAC {Convert.ToHexString(mac)} ignored, reusing fixed IP.");

            var lease = new DhcpLease
            {
                IpAddress = _poolStart,
                MacAddress = mac
            };
            _leases[key] = lease;
            return lease;
        }
    }

    private static int ResolveInterfaceIndex(WiredAdapter adapter)
    {
        var normalizedMac = NormalizeMac(adapter.MacAddress);
        var networkInterface = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
            NormalizeId(item.Id) == NormalizeId(adapter.Id)
            || NormalizeMac(item.GetPhysicalAddress().ToString()) == normalizedMac);
        var index = networkInterface?.GetIPProperties().GetIPv4Properties()?.Index;
        return index ?? throw new InvalidOperationException("无法确定所选网卡的 IPv4 接口索引。");
    }

    private static string NormalizeId(string value) => value.Trim().Trim('{', '}').ToUpperInvariant();
    private static string NormalizeMac(string value) =>
        new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static IPAddress CalculateBroadcastAddress(IPAddress address, IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var broadcast = new byte[addressBytes.Length];
        for (var i = 0; i < broadcast.Length; i++)
            broadcast[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
        return new IPAddress(broadcast);
    }

    private static byte[] BuildResponse(
        byte[] request,
        byte[] mac,
        IPAddress clientIp,
        byte messageType,
        IPAddress serverIp,
        IPAddress mask)
    {
        var response = new byte[300];
        response[0] = 2;
        response[1] = request[1];
        response[2] = request[2];
        response[3] = request[3];
        Array.Copy(request, 4, response, 4, 4);
        Array.Copy(clientIp.GetAddressBytes(), 0, response, 16, 4);
        Array.Copy(serverIp.GetAddressBytes(), 0, response, 20, 4);
        Array.Copy(request, 28, response, 28, 16);

        response[236] = 99;
        response[237] = 130;
        response[238] = 83;
        response[239] = 99;

        using var options = new MemoryStream();
        WriteOption(options, 53, new[] { messageType });
        WriteOption(options, 54, serverIp.GetAddressBytes());
        WriteOption(options, 51, UInt32Bytes(3600));
        WriteOption(options, 1, mask.GetAddressBytes());
        WriteOption(options, 3, serverIp.GetAddressBytes());
        WriteOption(options, 6, serverIp.GetAddressBytes());
        WriteOption(options, 58, UInt32Bytes(1800));
        WriteOption(options, 59, UInt32Bytes(3150));
        options.WriteByte(255);

        var optionBytes = options.ToArray();
        Array.Copy(optionBytes, 0, response, 240, optionBytes.Length);
        return response;
    }

    private static byte[] UInt32Bytes(uint value)
    {
        return new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        };
    }

    private static void WriteOption(Stream stream, byte code, byte[] data)
    {
        stream.WriteByte(code);
        stream.WriteByte((byte)data.Length);
        stream.Write(data, 0, data.Length);
    }

    private static byte[]? GetOption(byte[] packet, byte code)
    {
        var index = 240;
        while (index < packet.Length)
        {
            var optionCode = packet[index++];
            if (optionCode == 255)
            {
                return null;
            }

            if (optionCode == 0)
            {
                continue;
            }

            if (index >= packet.Length)
            {
                return null;
            }

            var length = packet[index++];
            if (index + length > packet.Length)
            {
                return null;
            }

            if (optionCode == code)
            {
                return packet.Skip(index).Take(length).ToArray();
            }

            index += length;
        }

        return null;
    }
}
