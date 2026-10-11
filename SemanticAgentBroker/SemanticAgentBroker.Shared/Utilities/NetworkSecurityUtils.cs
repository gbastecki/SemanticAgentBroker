using System.Net;
using System.Net.Sockets;

namespace SemanticAgentBroker.Shared.Utilities;

/// <summary>
/// Utility methods for network security and IP range validation.
/// </summary>
public static class NetworkSecurityUtils
{
    /// <summary>
    /// Determines whether the specified IP address belongs to a private, loopback, link-local, or reserved network range.
    /// </summary>
    /// <param name="ip">The IP address to evaluate.</param>
    /// <returns><c>true</c> if the IP is private, loopback, or reserved; otherwise <c>false</c>.</returns>
    public static bool IsPrivateOrLoopbackIp(IPAddress? ip)
    {
        if (ip == null) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.None)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 0) return true;
            if (bytes[0] == 10) return true;
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true; // RFC 6598 CGNAT
            if (bytes[0] == 127) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true; // RFC 2544 Benchmark
            if (bytes[0] >= 224) return true; // Multicast / reserved
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            var bytes = ip.GetAddressBytes();
            if (bytes.All(b => b == 0)) return true; // IPv6 unspecified address ::
            if ((bytes[0] & 0xFE) == 0xFC) return true; // ULA fc00::/7
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true; // fe80::/10
        }

        return false;
    }
}
