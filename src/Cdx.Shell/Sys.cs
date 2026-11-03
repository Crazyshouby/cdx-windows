using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using NAudio.CoreAudioApi;

namespace Cdx;

// Ce que le centre rapide lit et règle : le son (Core Audio, via NAudio), le réseau, l'alimentation.
static class SysInfo
{
    public static object Center()
    {
        int volume = -1; bool muted = false; string device = "Aucun périphérique audio";
        try
        {
            using var en = new MMDeviceEnumerator();
            using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            volume = (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            muted = d.AudioEndpointVolume.Mute;
            device = d.FriendlyName;
        }
        catch (Exception ex) { Host.Log("son : " + ex.Message); }
        return new { type = "center", volume, muted, device, net = Network() };
    }

    public static void SetVolume(int percent)
    {
        using var en = new MMDeviceEnumerator();
        using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
    }

    public static void SetMute(bool on)
    {
        using var en = new MMDeviceEnumerator();
        using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        d.AudioEndpointVolume.Mute = on;
    }

    // La connexion « principale » : une interface active avec passerelle, hors adaptateurs virtuels (Hyper-V, VPN, Tailscale…), la plus rapide d'abord.
    public static object Network()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                            && !System.Text.RegularExpressions.Regex.IsMatch(n.Description, "virtual|vEthernet|hyper-v|tailscale|vpn|vmware|bluetooth", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                            && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
                .OrderByDescending(n => n.Speed).FirstOrDefault();
            if (nic == null) return new { up = false };
            string ip = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "";
            return new { up = true, type = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet", speed = nic.Speed / 1_000_000, ip };
        }
        catch (Exception ex) { Host.Log("réseau : " + ex.Message); return new { up = false }; }
    }

    // Veille, redémarrage, arrêt : la confirmation est demandée par la page avant d'arriver ici.
    public static void Power(string? action)
    {
        Host.Log("alimentation : " + action);
        switch (action)
        {
            case "sleep": Application.SetSuspendState(PowerState.Suspend, false, false); break;
            case "restart": Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false }); break;
            case "shutdown": Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false }); break;
        }
    }
}
