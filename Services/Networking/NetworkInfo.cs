using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Talky.Models;

namespace Talky.Services.Networking;

public static class NetworkInfo
{
    public static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>LAN IPv4 addresses of this PC, best candidates (adapters with a gateway) first.</summary>
    public static IReadOnlyList<string> GetLocalIPv4Addresses()
    {
        var results = new List<(string Address, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                bool hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                {
                    var ip = unicast.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int rank = hasGateway ? 0 : 1;
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                    {
                        rank -= 1;
                    }

                    results.Add((ip.ToString(), rank));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return results.OrderBy(r => r.Rank).Select(r => r.Address).Distinct().ToArray();
    }

    /// <summary>
    /// Parses "host", "host:port", "[v6]:port" or a bare IPv6 literal.
    /// </summary>
    public static bool TryParseAddress(string? input, out string host, out int port)
    {
        host = string.Empty;
        port = NetworkDefaults.Port;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();

        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !TryParsePort(rest[1..], out port)))
            {
                return false;
            }

            return IPAddress.TryParse(host, out _);
        }

        if (IPAddress.TryParse(text, out var literal) && literal.AddressFamily == AddressFamily.InterNetworkV6)
        {
            host = text;
            return true;
        }

        int colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!TryParsePort(text[(colon + 1)..], out port))
            {
                return false;
            }

            text = text[..colon];
        }

        if (text.Length == 0 || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!IPAddress.TryParse(text, out _) && Uri.CheckHostName(text) != UriHostNameType.Dns)
        {
            return false;
        }

        host = text;
        return true;
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, out port) && port is > 0 and <= 65535;
}
