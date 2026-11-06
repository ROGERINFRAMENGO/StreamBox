using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace StreamBox.Network;

public static class LanInfo
{
    /// <summary>Endereços IPv4 privados ativos, ranqueados por probabilidade de ser a rede usada pela TV.</summary>
    public static IReadOnlyList<string> GetLocalAddresses()
    {
        var results = new List<(string Address, int Score)>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var properties = nic.GetIPProperties();
                if (!properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                    continue;

                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    var ip = address.Address.ToString();
                    if (IPAddress.IsLoopback(address.Address)) continue;
                    if (ip.StartsWith("169.254", StringComparison.Ordinal)) continue;

                    var score = nic.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 30,
                        NetworkInterfaceType.Wireless80211 => 25,
                        _ => 10,
                    };

                    if (ip.StartsWith("192.168.", StringComparison.Ordinal)) score += 5;
                    else if (ip.StartsWith("10.", StringComparison.Ordinal)) score += 4;
                    else if (IsPrivate172(ip)) score += 4;

                    results.Add((ip, score));
                }
            }
        }
        catch
        {
            // Sem informações de rede: cai no fallback abaixo.
        }

        var ordered = results
            .OrderByDescending(r => r.Score)
            .Select(r => r.Address)
            .Distinct()
            .ToList();

        if (ordered.Count == 0)
        {
            var fallback = TryGetPrimaryAddress();
            if (fallback is not null) ordered.Add(fallback);
        }

        return ordered;
    }

    public static string? GetPreferredAddress() => GetLocalAddresses().FirstOrDefault();

    private static bool IsPrivate172(string ip) =>
        ip.StartsWith("172.", StringComparison.Ordinal) && int.TryParse(ip.Split('.')[1], out var second) && second is >= 16 and <= 31;

    private static string? TryGetPrimaryAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }
}
