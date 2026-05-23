using System.IO;
using System.Net;
using System.Net.Sockets;

namespace EzGetBmcIp;

internal sealed class DhcpServer : IDisposable
{
    private static readonly IPAddress ServerIp = IPAddress.Parse("10.77.77.1");
    private static readonly IPAddress Mask = IPAddress.Parse("255.255.255.0");
    private static readonly IPAddress PoolStart = IPAddress.Parse("10.77.77.100");
    private const int DhcpServerPort = 67;
    private const int DhcpClientPort = 68;

    private readonly Dictionary<string, DhcpLease> _leases = new();
    private readonly object _sync = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private int _nextOffset;

    public event EventHandler<DhcpLease>? LeaseAssigned;

    public void Start()
    {
        if (_udp is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.EnableBroadcast = true;
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DhcpServerPort));
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Close();
        _udp?.Dispose();
        _udp = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _udp is not null)
        {
            try
            {
                var packet = await _udp.ReceiveAsync(cancellationToken);
                await HandlePacketAsync(packet.Buffer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // Keep the tiny DHCP listener alive even if a malformed packet arrives.
            }
        }
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

        var macLength = Math.Min(request[2], (byte)16);
        var mac = request.Skip(28).Take(macLength).ToArray();
        if (mac.Length == 0)
        {
            return;
        }

        var lease = GetOrCreateLease(mac);
        var responseType = messageType == 1 ? (byte)2 : (byte)5;
        var response = BuildResponse(request, mac, lease.IpAddress, responseType);
        var destination = new IPEndPoint(IPAddress.Broadcast, DhcpClientPort);
        if (_udp is not null)
        {
            await _udp.SendAsync(response, response.Length, destination);
        }

        if (responseType == 5)
        {
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

            var ipBytes = PoolStart.GetAddressBytes();
            ipBytes[3] = (byte)(100 + (_nextOffset++ % 101));
            var lease = new DhcpLease
            {
                IpAddress = new IPAddress(ipBytes),
                MacAddress = mac
            };
            _leases[key] = lease;
            return lease;
        }
    }

    private static byte[] BuildResponse(byte[] request, byte[] mac, IPAddress clientIp, byte messageType)
    {
        var response = new byte[300];
        response[0] = 2;
        response[1] = request[1];
        response[2] = request[2];
        response[3] = request[3];
        Array.Copy(request, 4, response, 4, 4);
        Array.Copy(clientIp.GetAddressBytes(), 0, response, 16, 4);
        Array.Copy(ServerIp.GetAddressBytes(), 0, response, 20, 4);
        Array.Copy(request, 28, response, 28, 16);

        response[236] = 99;
        response[237] = 130;
        response[238] = 83;
        response[239] = 99;

        using var options = new MemoryStream();
        WriteOption(options, 53, new[] { messageType });
        WriteOption(options, 54, ServerIp.GetAddressBytes());
        WriteOption(options, 51, UInt32Bytes(3600));
        WriteOption(options, 1, Mask.GetAddressBytes());
        WriteOption(options, 3, ServerIp.GetAddressBytes());
        WriteOption(options, 6, ServerIp.GetAddressBytes());
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
