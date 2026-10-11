using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Retriva.Server.Power;

/// <summary>
/// Menyalakan PC lewat magic packet.
///
/// Ini satu-satunya perintah yang tidak bisa lewat agent, karena saat unit
/// mati tidak ada agent yang berjalan. Yang mendengarkan justru kartu jaringan
/// itu sendiri, memakai alamat MAC yang direkam saat unit pertama mendaftar.
///
/// Syarat di sisi PC mahasiswa, keduanya tidak bisa diatur dari jarak jauh:
///   1. Wake-on-LAN diaktifkan di BIOS
///   2. "Allow this device to wake the computer" dicentang di properti adaptor
/// </summary>
public static class WakeOnLan
{
    // Port 9 (discard) adalah kelaziman untuk WoL; sebagian perangkat
    // juga mendengarkan port 7. Dikirim ke keduanya agar aman.
    private static readonly int[] Ports = [9, 7];

    public static int SendMagicPackets(
        IEnumerable<string> macAddresses, string subnetPrefix, ILogger log)
    {
        var broadcast = BroadcastAddressFor(subnetPrefix);
        var sent = 0;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.EnableBroadcast = true;

        foreach (var mac in macAddresses)
        {
            var packet = BuildPacket(mac);
            if (packet is null)
            {
                log.LogWarning("MAC tidak dapat dibaca, dilewati: {Mac}", mac);
                continue;
            }

            foreach (var port in Ports)
            {
                try
                {
                    // Broadcast subnet lebih dapat diandalkan daripada 255.255.255.255,
                    // yang sering disaring perangkat jaringan.
                    socket.SendTo(packet, new IPEndPoint(broadcast, port));
                    socket.SendTo(packet, new IPEndPoint(IPAddress.Broadcast, port));
                }
                catch (SocketException ex)
                {
                    log.LogWarning("Magic packet ke {Mac}:{Port} gagal: {Message}", mac, port, ex.Message);
                }
            }
            sent++;
        }

        log.LogInformation("Magic packet dikirim untuk {Count} unit lewat {Broadcast}", sent, broadcast);
        return sent;
    }

    /// <summary>6 byte 0xFF diikuti alamat MAC yang diulang 16 kali — total 102 byte.</summary>
    private static byte[]? BuildPacket(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return null;

        var address = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out address[i]))
                return null;
        }

        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var repeat = 1; repeat <= 16; repeat++)
            Buffer.BlockCopy(address, 0, packet, repeat * 6, 6);

        return packet;
    }

    /// <summary>"192.168.1." menjadi 192.168.1.255</summary>
    private static IPAddress BroadcastAddressFor(string subnetPrefix)
    {
        var candidate = subnetPrefix.TrimEnd('.') + ".255";
        return IPAddress.TryParse(candidate, out var address) ? address : IPAddress.Broadcast;
    }
}
