using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EzGetBmcIp
{
    public sealed class DhcpServer : IDisposable
    {
        private readonly IPAddress _serverIp;
        private readonly IPAddress _mask;
        private readonly IPAddress _poolStart;
        private const int DhcpServerPort = 67;
        private const int DhcpClientPort = 68;

        private readonly Dictionary<string, DhcpLease> _leases = new Dictionary<string, DhcpLease>();
        private readonly object _sync = new object();
        private UdpClient _udp;
        private CancellationTokenSource _cts;

        public DhcpServer(SubnetConfig config)
        {
            _serverIp = IPAddress.Parse(config.ServerIp);
            _mask = IPAddress.Parse(config.Mask);
            _poolStart = IPAddress.Parse(config.PoolStart);
        }

        public event EventHandler<DhcpLease> LeaseAssigned;
        public event EventHandler<string> ErrorEncountered;
        public Action<string> Logger { get; set; }

        public void Start()
        {
            if (_udp != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.EnableBroadcast = true;
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            Logger?.Invoke("DHCP server starting on " + _serverIp + ":" + DhcpServerPort);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DhcpServerPort));
            Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            Logger?.Invoke("DHCP server stopping");
            _cts?.Cancel();
            _udp?.Close();
            _udp?.Dispose();
            _udp = null;
            _cts?.Dispose();
            _cts = null;
            Logger?.Invoke("DHCP server stopped");
        }

        public void Dispose() => Stop();

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _udp != null)
            {
                try
                {
                    var receiveTask = _udp.ReceiveAsync();
                    var completedTask = await Task.WhenAny(receiveTask, Task.Delay(-1, cancellationToken));
                    if (completedTask != receiveTask)
                    {
                        break;
                    }
                    var packet = await receiveTask;
                    await HandlePacketAsync(packet.Buffer, cancellationToken);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException se)
                {
                    if (se.SocketErrorCode == SocketError.TimedOut
                        || se.SocketErrorCode == SocketError.ConnectionReset
                        || se.SocketErrorCode == SocketError.MessageSize
                        || se.SocketErrorCode == SocketError.ConnectionRefused
                        || se.SocketErrorCode == SocketError.HostUnreachable
                        || se.SocketErrorCode == SocketError.NetworkUnreachable
                        || se.SocketErrorCode == SocketError.NetworkReset)
                    {
                        Logger?.Invoke("DHCP transient socket warning: " + se.SocketErrorCode + " - " + se.Message);
                        continue;
                    }
                    var message = "DHCP Socket \u9519\u8bef\uff1a" + se.Message;
                    Logger?.Invoke(message);
                    ErrorEncountered?.Invoke(this, message);
                    break;
                }
                catch (Exception ex)
                {
                    var message = "DHCP \u672a\u77e5\u9519\u8bef\uff1a" + ex.Message;
                    Logger?.Invoke(message);
                    ErrorEncountered?.Invoke(this, message);
                    break;
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
            if (messageType != 1 && messageType != 3)
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
                var opt50 = GetOption(request, 50);
                if (opt50 != null && opt50.Length == 4)
                {
                    var requestedIp = new IPAddress(opt50);
                    Logger?.Invoke("DHCP: Requested IP (Option 50) = " + requestedIp);
                    lock (_sync)
                    {
                        var requestedLease = _leases.Values.FirstOrDefault(l => l.IpAddress.Equals(requestedIp));
                        if (requestedLease != null && requestedLease != lease)
                        {
                            var key = MacBytesToKey(mac);
                            _leases[key] = requestedLease;
                            lease = requestedLease;
                        }
                    }
                }
            }

            var responseType = messageType == 1 ? (byte)2 : (byte)5;
            if (messageType == 1)
                Logger?.Invoke("DHCP: DISCOVER from " + MacBytesToString(mac) + " -> OFFER " + lease.IpAddress);
            else
                Logger?.Invoke("DHCP: REQUEST from " + MacBytesToString(mac) + " -> ACK " + lease.IpAddress);

            var response = BuildResponse(request, mac, lease.IpAddress, responseType, _serverIp);
            var destination = new IPEndPoint(IPAddress.Broadcast, DhcpClientPort);
            if (_udp != null)
            {
                await _udp.SendAsync(response, response.Length, destination);
            }

            if (responseType == 5)
            {
                Logger?.Invoke("DHCP: lease assigned " + lease.IpAddress + " to " + MacBytesToString(mac));
                LeaseAssigned?.Invoke(this, lease);
            }
        }

        private static string MacBytesToString(byte[] mac)
        {
            return BitConverter.ToString(mac);
        }

        private DhcpLease GetOrCreateLease(byte[] mac)
        {
            var key = MacBytesToKey(mac);
            lock (_sync)
            {
                if (_leases.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                if (_leases.Count > 0)
                    Debug.WriteLine("DHCP: second MAC " + MacBytesToKey(mac) + " ignored, reusing fixed IP.");

                var lease = new DhcpLease
                {
                    IpAddress = _poolStart,
                    MacAddress = mac
                };
                _leases[key] = lease;
                return lease;
            }
        }

        private static string MacBytesToKey(byte[] mac)
        {
            return BitConverter.ToString(mac).Replace("-", "");
        }

        private static byte[] BuildResponse(byte[] request, byte[] mac, IPAddress clientIp, byte messageType, IPAddress serverIp)
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

            using (var options = new MemoryStream())
            {
                WriteOption(options, 53, new[] { messageType });
                WriteOption(options, 54, serverIp.GetAddressBytes());
                WriteOption(options, 51, UInt32Bytes(3600));
                WriteOption(options, 1, IPAddress.Parse("255.255.255.0").GetAddressBytes());
                WriteOption(options, 3, serverIp.GetAddressBytes());
                WriteOption(options, 6, serverIp.GetAddressBytes());
                WriteOption(options, 58, UInt32Bytes(1800));
                WriteOption(options, 59, UInt32Bytes(3150));
                options.WriteByte(255);

                var optionBytes = options.ToArray();
                Array.Copy(optionBytes, 0, response, 240, optionBytes.Length);
            }
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

        private static byte[] GetOption(byte[] packet, byte code)
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
}
