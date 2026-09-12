#nullable disable
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace EzGetBmcIp
{
    internal enum DhcpRequestDisposition
    {
        Ignore,
        Ack,
        Nak
    }

    internal sealed class DhcpRequestDecision
    {
        public DhcpRequestDecision(
            DhcpRequestDisposition disposition,
            string reason,
            IPAddress ciaddr,
            IPAddress giaddr,
            IPAddress requestedIp,
            IPAddress requestedServer)
        {
            Disposition = disposition;
            Reason = reason;
            Ciaddr = ciaddr;
            Giaddr = giaddr;
            RequestedIp = requestedIp;
            RequestedServer = requestedServer;
        }

        public DhcpRequestDisposition Disposition { get; private set; }
        public string Reason { get; private set; }
        public IPAddress Ciaddr { get; private set; }
        public IPAddress Giaddr { get; private set; }
        public IPAddress RequestedIp { get; private set; }
        public IPAddress RequestedServer { get; private set; }

        public string ToDiagnosticText()
        {
            return "ciaddr=" + FormatAddress(Ciaddr) +
                " giaddr=" + FormatAddress(Giaddr) +
                " option50=" + FormatAddress(RequestedIp) +
                " option54=" + FormatAddress(RequestedServer) +
                " disposition=" + Disposition +
                " reason=" + Reason;
        }

        private static string FormatAddress(IPAddress address)
        {
            return address == null ? "absent" : address.ToString();
        }
    }

    /// <summary>
    /// Applies the direct-connect, single-BMC DHCPREQUEST policy shared by the
    /// current .NET and .NET Framework release paths.
    /// </summary>
    internal static class DhcpRequestPolicy
    {
        public static DhcpRequestDecision Classify(
            byte[] request,
            IPAddress serverIp,
            IPAddress mask,
            IPAddress fixedClientIp)
        {
            if (request == null || request.Length < 240)
            {
                return Create(DhcpRequestDisposition.Ignore, "packet is shorter than the DHCP fixed header", null, null, null, null);
            }

            if (request[236] != 99 || request[237] != 130 || request[238] != 83 || request[239] != 99)
            {
                return Create(DhcpRequestDisposition.Ignore, "DHCP magic cookie is invalid", null, null, null, null);
            }

            var ciaddr = ReadAddress(request, 12);
            var giaddr = ReadAddress(request, 24);
            byte[] option53;
            byte[] option50;
            byte[] option54;
            if (!TryReadRequestOptions(request, out option53, out option50, out option54))
            {
                return Create(DhcpRequestDisposition.Ignore, "options are malformed or duplicated", ciaddr, giaddr, null, null);
            }

            if (option53 == null || option53.Length != 1 || option53[0] != 3)
            {
                return Create(DhcpRequestDisposition.Ignore, "DHCP message type is missing or invalid", ciaddr, giaddr, null, null);
            }

            if (option50 != null && option50.Length != 4)
            {
                return Create(DhcpRequestDisposition.Ignore, "Option 50 has an invalid length", ciaddr, giaddr, null, null);
            }

            if (option54 != null && option54.Length != 4)
            {
                return Create(DhcpRequestDisposition.Ignore, "Option 54 has an invalid length", ciaddr, giaddr, null, null);
            }

            var requestedIp = option50 == null ? null : new IPAddress(option50);
            var requestedServer = option54 == null ? null : new IPAddress(option54);

            // This utility serves a directly connected BMC and does not implement
            // relay-agent forwarding. Do not send a broadcast onto a relay path.
            if (!IsUnspecified(giaddr))
            {
                return Create(DhcpRequestDisposition.Ignore, "DHCP relay requests are unsupported", ciaddr, giaddr, requestedIp, requestedServer);
            }

            // A SELECTING client that selected another DHCP server must not be
            // disturbed. This deliberately precedes local lease validation.
            if (requestedServer != null && !requestedServer.Equals(serverIp))
            {
                return Create(DhcpRequestDisposition.Ignore, "request selected another DHCP server", ciaddr, giaddr, requestedIp, requestedServer);
            }

            // A RENEWING/REBINDING request identifies the address via ciaddr.
            // An Option 50 alongside ciaddr is tolerated only when it agrees.
            if (!IsUnspecified(ciaddr))
            {
                if (requestedIp != null || requestedServer != null)
                {
                    return Create(DhcpRequestDisposition.Ignore, "ciaddr request contains SELECTING or INIT-REBOOT options", ciaddr, giaddr, requestedIp, requestedServer);
                }

                if (!IsInSubnet(ciaddr, serverIp, mask))
                {
                    return Create(DhcpRequestDisposition.Nak, "ciaddr is outside the active subnet", ciaddr, giaddr, requestedIp, requestedServer);
                }

                if (!ciaddr.Equals(fixedClientIp))
                {
                    return Create(DhcpRequestDisposition.Nak, "ciaddr is not the configured fixed BMC address", ciaddr, giaddr, requestedIp, requestedServer);
                }

                return Create(DhcpRequestDisposition.Ack, "current-subnet renewing or rebinding request", ciaddr, giaddr, requestedIp, requestedServer);
            }

            // SELECTING and INIT-REBOOT requests identify the address by Option 50.
            if (requestedIp == null)
            {
                return Create(DhcpRequestDisposition.Ignore, "request has neither ciaddr nor Option 50", ciaddr, giaddr, requestedIp, requestedServer);
            }

            if (!IsInSubnet(requestedIp, serverIp, mask))
            {
                return Create(DhcpRequestDisposition.Nak, "Option 50 is outside the active subnet", ciaddr, giaddr, requestedIp, requestedServer);
            }

            if (!requestedIp.Equals(fixedClientIp))
            {
                return Create(DhcpRequestDisposition.Nak, "Option 50 is not the configured fixed BMC address", ciaddr, giaddr, requestedIp, requestedServer);
            }

            return Create(DhcpRequestDisposition.Ack, "current-subnet selecting or INIT-REBOOT request", ciaddr, giaddr, requestedIp, requestedServer);
        }

        private static DhcpRequestDecision Create(
            DhcpRequestDisposition disposition,
            string reason,
            IPAddress ciaddr,
            IPAddress giaddr,
            IPAddress requestedIp,
            IPAddress requestedServer)
        {
            return new DhcpRequestDecision(disposition, reason, ciaddr, giaddr, requestedIp, requestedServer);
        }

        private static IPAddress ReadAddress(byte[] packet, int offset)
        {
            var bytes = new byte[4];
            Array.Copy(packet, offset, bytes, 0, bytes.Length);
            return new IPAddress(bytes);
        }

        private static bool IsUnspecified(IPAddress address)
        {
            return address == null || address.Equals(IPAddress.Any);
        }

        private static bool IsInSubnet(IPAddress address, IPAddress networkAddress, IPAddress mask)
        {
            var addressBytes = address.GetAddressBytes();
            var networkBytes = networkAddress.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            if (addressBytes.Length != 4 || networkBytes.Length != 4 || maskBytes.Length != 4)
            {
                return false;
            }

            for (var index = 0; index < 4; index++)
            {
                if ((addressBytes[index] & maskBytes[index]) != (networkBytes[index] & maskBytes[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadRequestOptions(byte[] packet, out byte[] option53, out byte[] option50, out byte[] option54)
        {
            option53 = null;
            option50 = null;
            option54 = null;
            var index = 240;
            while (index < packet.Length)
            {
                var code = packet[index++];
                if (code == 255)
                {
                    return true;
                }

                if (code == 0)
                {
                    continue;
                }

                if (index >= packet.Length)
                {
                    return false;
                }

                var length = packet[index++];
                if (index + length > packet.Length)
                {
                    return false;
                }

                var value = new byte[length];
                Array.Copy(packet, index, value, 0, length);
                index += length;

                if (code == 53)
                {
                    if (option53 != null)
                    {
                        return false;
                    }

                    option53 = value;
                }
                else if (code == 50)
                {
                    if (option50 != null)
                    {
                        return false;
                    }

                    option50 = value;
                }
                else if (code == 54)
                {
                    if (option54 != null)
                    {
                        return false;
                    }

                    option54 = value;
                }
            }

            return false;
        }
    }

    internal static class DhcpNakResponse
    {
        internal static readonly IPAddress DestinationAddress = IPAddress.Broadcast;
        private const int IpProtoIp = 0;
        private const int IpPktInfo = 19;
        private static readonly Guid WsaSendMsgGuid = new Guid("a441e712-754f-43ca-84a7-0dee44cf606d");

        public static void Send(Socket socket, byte[] packet, IPAddress sourceAddress, int interfaceIndex)
        {
            if (socket == null)
            {
                throw new ArgumentNullException("socket");
            }

            if (packet == null)
            {
                throw new ArgumentNullException("packet");
            }

            if (sourceAddress == null || sourceAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("A DHCPNAK source address must be IPv4.", "sourceAddress");
            }

            if (interfaceIndex <= 0)
            {
                socket.SendTo(packet, new IPEndPoint(DestinationAddress, 68));
                return;
            }

            // Windows routes 255.255.255.255 using the lowest-metric broadcast
            // route unless both source address and ifIndex are provided through
            // IP_PKTINFO. WSASendMsg applies those fields to this one packet.
            var sendMessage = GetWsaSendMsg(socket);
            var destination = CreateSockAddrIn(DestinationAddress, 68);
            var control = CreateIpPacketInfoControl(sourceAddress, interfaceIndex);
            var packetHandle = GCHandle.Alloc(packet, GCHandleType.Pinned);
            var destinationHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);
            var buffersPointer = IntPtr.Zero;
            var controlPointer = IntPtr.Zero;

            try
            {
                buffersPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WsaBuf)));
                Marshal.StructureToPtr(new WsaBuf
                {
                    Length = packet.Length,
                    Buffer = packetHandle.AddrOfPinnedObject()
                }, buffersPointer, false);

                controlPointer = Marshal.AllocHGlobal(control.Length);
                Marshal.Copy(control, 0, controlPointer, control.Length);

                var message = new WsaMsg
                {
                    Name = destinationHandle.AddrOfPinnedObject(),
                    NameLength = destination.Length,
                    Buffers = buffersPointer,
                    BufferCount = 1,
                    Control = new WsaBuf { Length = control.Length, Buffer = controlPointer },
                    Flags = 0
                };
                int bytesSent;
                var result = sendMessage(socket.Handle, ref message, 0, out bytesSent, IntPtr.Zero, IntPtr.Zero);
                if (result != 0)
                {
                    throw new SocketException(Marshal.GetLastWin32Error());
                }

                if (bytesSent != packet.Length)
                {
                    throw new IOException("DHCPNAK send did not write the complete packet.");
                }
            }
            finally
            {
                if (controlPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(controlPointer);
                }

                if (buffersPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffersPointer);
                }

                destinationHandle.Free();
                packetHandle.Free();
            }
        }

        public static byte[] Build(byte[] request, IPAddress serverIp)
        {
            var response = new byte[300];
            response[0] = 2;
            response[1] = request[1];
            response[2] = request[2];
            response[3] = request[3];
            Array.Copy(request, 4, response, 4, 4);
            Array.Copy(request, 28, response, 28, 16);
            response[236] = 99;
            response[237] = 130;
            response[238] = 83;
            response[239] = 99;

            using (var options = new MemoryStream())
            {
                WriteOption(options, 53, new byte[] { 6 });
                WriteOption(options, 54, serverIp.GetAddressBytes());
                options.WriteByte(255);

                var optionBytes = options.ToArray();
                Array.Copy(optionBytes, 0, response, 240, optionBytes.Length);
            }

            return response;
        }

        private static void WriteOption(Stream stream, byte code, byte[] data)
        {
            stream.WriteByte(code);
            stream.WriteByte((byte)data.Length);
            stream.Write(data, 0, data.Length);
        }

        private static WsaSendMsgDelegate GetWsaSendMsg(Socket socket)
        {
            var output = new byte[IntPtr.Size];
            socket.IOControl(IOControlCode.GetExtensionFunctionPointer, WsaSendMsgGuid.ToByteArray(), output);
            var functionPointer = IntPtr.Size == 8
                ? new IntPtr(BitConverter.ToInt64(output, 0))
                : new IntPtr(BitConverter.ToInt32(output, 0));
            if (functionPointer == IntPtr.Zero)
            {
                throw new SocketException((int)SocketError.OperationNotSupported);
            }

            return (WsaSendMsgDelegate)Marshal.GetDelegateForFunctionPointer(functionPointer, typeof(WsaSendMsgDelegate));
        }

        private static byte[] CreateSockAddrIn(IPAddress address, int port)
        {
            var result = new byte[16];
            result[0] = (byte)AddressFamily.InterNetwork;
            result[2] = (byte)(port >> 8);
            result[3] = (byte)port;
            Array.Copy(address.GetAddressBytes(), 0, result, 4, 4);
            return result;
        }

        private static byte[] CreateIpPacketInfoControl(IPAddress sourceAddress, int interfaceIndex)
        {
            var headerLength = IntPtr.Size + sizeof(int) + sizeof(int);
            var control = new byte[headerLength + 8];
            WriteNativeSize(control, 0, control.Length);
            WriteNativeInt32(control, IntPtr.Size, IpProtoIp);
            WriteNativeInt32(control, IntPtr.Size + sizeof(int), IpPktInfo);
            Array.Copy(sourceAddress.GetAddressBytes(), 0, control, headerLength, 4);
            WriteNativeInt32(control, headerLength + 4, interfaceIndex);
            return control;
        }

        private static void WriteNativeSize(byte[] buffer, int offset, int value)
        {
            if (IntPtr.Size == 8)
            {
                Array.Copy(BitConverter.GetBytes((long)value), 0, buffer, offset, sizeof(long));
            }
            else
            {
                Array.Copy(BitConverter.GetBytes(value), 0, buffer, offset, sizeof(int));
            }
        }

        private static void WriteNativeInt32(byte[] buffer, int offset, int value)
        {
            Array.Copy(BitConverter.GetBytes(value), 0, buffer, offset, sizeof(int));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WsaBuf
        {
            public int Length;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WsaMsg
        {
            public IntPtr Name;
            public int NameLength;
            public IntPtr Buffers;
            public int BufferCount;
            public WsaBuf Control;
            public int Flags;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int WsaSendMsgDelegate(
            IntPtr socketHandle,
            ref WsaMsg message,
            int flags,
            out int bytesSent,
            IntPtr overlapped,
            IntPtr completionRoutine);
    }
}
