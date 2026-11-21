using System.Net;
using System.Net.Sockets;

namespace UniBackup.Core.Models;

public class ServerIdentity
{
    private static ServerIdentity? _current;
    public static ServerIdentity Current => _current ??= Detect();

    public string Hostname { get; init; } = Environment.MachineName;
    public string IpAddress { get; init; } = "127.0.0.1";
    public string OsVersion { get; init; } = Environment.OSVersion.ToString();

    public string DisplayLabel => $"{Hostname} ({IpAddress})";

    public string DefaultS3Prefix => $"backups/{SanitizeForPath(IpAddress)}/";
    public string DefaultGDriveFolder => $"/Backups/{SanitizeForPath(IpAddress)}";

    public static ServerIdentity Detect()
    {
        var hostname = Environment.MachineName;
        var ip = "127.0.0.1";

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            // Connect to a public DNS IP (doesn't actually send packets) to find local outbound IP
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                ip = endPoint.Address.ToString();
            }
        }
        catch
        {
            try
            {
                var entry = Dns.GetHostEntry(Dns.GetHostName());
                var ipv4 = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 != null)
                {
                    ip = ipv4.ToString();
                }
            }
            catch
            {
                ip = "127.0.0.1";
            }
        }

        return new ServerIdentity
        {
            Hostname = hostname,
            IpAddress = ip,
            OsVersion = Environment.OSVersion.ToString()
        };
    }

    private static string SanitizeForPath(string input) =>
        input.Replace(":", "_").Replace("/", "_").Replace("\\", "_");
}
