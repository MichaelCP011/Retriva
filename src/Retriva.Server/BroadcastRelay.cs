using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Retriva.Shared.Broadcast;
using Retriva.Shared.Protocol;

namespace Retriva.Server;

/// <summary>Siapa yang sedang memegang siaran, dan dengan pengaturan apa.</summary>
public sealed record BroadcastHolder(
    string DeviceId, string DisplayName, string SourceLabel,
    DateTimeOffset Since)
{
    /// <summary>
    /// Alamat pengirim aliran, null sampai datagram pertama tiba.
    ///
    /// Sengaja tidak diisi dari alamat sambungan TLS. Lihat alasannya di
    /// BroadcastRelay.ExecuteAsync — singkatnya, UDP dan TCP dari mesin yang
    /// sama tidak wajib keluar lewat alamat yang sama.
    /// </summary>
    public IPAddress? Source { get; init; }
}

/// <summary>
/// Menerima aliran siaran dari panel dan memancarkannya ke lab.
///
/// MENGAPA TIDAK PANEL SAJA YANG MEMANCARKAN MULTICAST.
///
/// Alasannya fisika jaringan, bukan kerapian. Panel dosen tersambung lewat
/// Wi-Fi. Multicast di Wi-Fi dikirim pada laju dasar terendah, tanpa ACK dan
/// tanpa pengiriman ulang, dan access point menyiarkan ulang SELURUH aliran itu
/// ke udara — termasuk kembali ke laptop yang mengirimnya. Laptop dosen berebut
/// airtime dengan siarannya sendiri, dan empat puluh mahasiswa menerima gambar
/// yang tersendat.
///
/// Dengan relay, laptop dosen hanya mengirim satu aliran unicast ke Superadmin.
/// Superadmin duduk di kabel gigabit dan memancarkan multicast dari sana, di
/// tempat multicast memang bekerja dengan baik.
///
/// EFEK SAMPING YANG DISENGAJA: karena semuanya lewat satu titik, di sinilah
/// aturan "satu sumber siaran pada satu waktu" dapat ditegakkan sungguhan.
/// Sebelumnya dua panel dapat memancarkan ke grup yang sama secara bersamaan,
/// dan yang terlihat mahasiswa adalah dua gambar yang saling menimpa frame demi
/// frame — kacau tanpa satu pun galat di mana pun.
/// </summary>
public sealed class BroadcastRelay(ILogger<BroadcastRelay> log) : BackgroundService
{
    private readonly Lock _gate = new();
    private BroadcastHolder? _holder;
    private long _bytesRelayed;
    private long _framesRelayed;
    private long _rejected;

    public event Action? Changed;

    public BroadcastHolder? Holder
    {
        get { lock (_gate) return _holder; }
    }

    /// <summary>
    /// Mengambil alih siaran, atau menolak bila sedang dipegang orang lain.
    ///
    /// <paramref name="takeover"/> memaksa perebutan. Asisten kadang memang
    /// perlu memotong siaran dosen yang lupa menghentikannya — tetapi itu harus
    /// disengaja, bukan terjadi diam-diam karena dua orang menekan tombol
    /// dalam menit yang sama.
    /// </summary>
    public (bool Granted, string? HeldBy) TryClaim(
        string deviceId, string displayName, string sourceLabel, bool takeover)
    {
        lock (_gate)
        {
            if (_holder is { } current
                && current.DeviceId != deviceId
                && !takeover)
            {
                return (false, current.DisplayName);
            }

            _holder = new BroadcastHolder(
                deviceId, displayName, sourceLabel, DateTimeOffset.UtcNow);
            _bytesRelayed = 0;
            _framesRelayed = 0;
            _rejected = 0;
        }

        log.LogInformation("Siaran dipegang {Name} · sumber {Label} · menunggu frame pertama",
            displayName, sourceLabel);

        Changed?.Invoke();
        return (true, null);
    }

    public void Release(string deviceId)
    {
        lock (_gate)
        {
            // Hanya pemegangnya yang boleh melepas. Tanpa syarat ini, panel
            // mana pun dapat menghentikan siaran orang lain dengan mengirim
            // satu pesan — termasuk panel yang tombolnya tidak sengaja tertekan.
            if (_holder?.DeviceId != deviceId) return;
            _holder = null;
        }

        log.LogInformation("Siaran dilepas oleh {Device}", deviceId);
        Changed?.Invoke();
    }

    public (long Frames, double Mbps) Stats
    {
        get
        {
            lock (_gate)
            {
                if (_holder is not { } h) return (0, 0);

                var seconds = Math.Max(1, (DateTimeOffset.UtcNow - h.Since).TotalSeconds);
                return (_framesRelayed, _bytesRelayed * 8 / seconds / 1_000_000);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var inbound = new UdpClient(AddressFamily.InterNetwork);
        inbound.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        /*
            BUFFER PENERIMA DIBESARKAN. INI PENYEBAB LAYAR HITAM.

            Buffer UDP bawaan Windows sekitar 64 KB — kira-kira 48 datagram
            berukuran 1400 byte. Satu frame 1920px berisi 140 potongan yang
            dikirim panel secepat mungkin, beruntun tanpa jeda.

            Akibatnya kernel menerima 48 potongan pertama, lalu MEMBUANG sisanya
            tanpa memberi tahu siapa pun, sampai relay selesai memproses dan
            buffernya lega kembali. Setiap frame kehilangan potongan ke-48 sampai
            ke-139, dan tidak satu pun frame pernah lengkap.

            Jejaknya terbaca persis di log klien:

                48/140 potongan · kurang=[48,49,50,51,52,53,54,55]
                51/140 potongan · kurang=[48,49,50,51,52,53,54,55]

            Potongan yang hilang selalu BERURUTAN dan selalu mulai di sekitar
            indeks yang sama. Kehilangan jaringan tidak pernah berpola begitu;
            buffer yang meluap selalu.

            Dan inilah sebabnya seluruh penelusuran tersesat berjam-jam: dari
            sisi relay semuanya tampak sehat — 900 datagram, 8,8 Mbps, tanpa satu
            pun galat. Yang tidak terlihat adalah 60% datagram yang dibuang
            kernel SEBELUM relay sempat membacanya.
        */
        inbound.Client.ReceiveBufferSize = 8 << 20;

        inbound.Client.Bind(new IPEndPoint(IPAddress.Any, PanelProtocol.RelayPort));

        // Dicetak apa adanya, bukan diandaikan terpasang. Windows dapat
        // memangkas nilai yang diminta, dan buffer yang lebih kecil daripada
        // yang dikira mengembalikan bug ini dalam bentuk yang lebih halus.
        log.LogInformation("Buffer penerima relay: {Bytes:N0} byte (diminta {Wanted:N0})",
            inbound.Client.ReceiveBufferSize, 8 << 20);

        /*
            DIPANCARKAN KE SETIAP ANTARMUKA, BUKAN KE SATU YANG DITEBAK.

            Ini keputusan yang diambil setelah tiga percobaan menebak antarmuka
            mana yang benar, dan ketiganya gagal dengan cara yang berbeda:

              - antarmuka bawaan sistem : tabel rute memilih adaptor virtual
              - antarmuka tujuan panel  : 192.168.56.1, jaringan VirtualBox yang
                                          tidak dihuni satu unit pun
              - antarmuka milik agent   : 127.0.0.1 saat menguji di satu mesin,
                                          dan multicast tidak dapat keluar lewat
                                          loopback sama sekali

            Setiap kegagalan itu terlihat SUKSES dari sisi relay: puluhan ribu
            datagram diteruskan, belasan Mbps, tanpa satu pun galat — sementara
            mahasiswa melihat layar hitam. Itu bentuk kegagalan yang paling mahal
            untuk ditelusuri, dan penyebabnya selalu sama: satu tebakan tentang
            topologi jaringan yang tidak dapat dibuktikan dari dalam program.

            Jadi tebakannya dihapus. Setiap antarmuka yang aktif dan mendukung
            multicast mendapat soketnya sendiri, diikat ke alamat antarmuka itu —
            satu-satunya cara yang dapat diandalkan untuk memaksa jalur keluar di
            Windows. Datagram dikirim ke semuanya.

            Biayanya: di lab biasanya ada satu atau dua antarmuka, jadi hampir
            tidak ada. Di mesin pengembangan dengan VirtualBox dan VPN mungkin
            empat, artinya empat salinan 10 Mbps yang tidak pernah keluar dari
            mesin itu. Jauh lebih murah daripada satu jam menelusuri layar hitam.
        */
        /*
            DAFTAR PEMANCAR DISUSUN ULANG SAAT JARINGAN BERUBAH.

            Versi sebelumnya memanggil OpenSenders sekali di sini, saat layanan
            mulai, lalu memakai daftar itu selamanya. Di mesin yang jaringannya
            sudah siap sebelum Retriva dijalankan, itu benar. Di mesin lab yang
            dinyalakan pagi hari, itu salah — dan salahnya sunyi.

            Windows memberi kartu jaringan alamat APIPA 169.254.x selama DHCP
            belum menjawab. Koordinator yang mulai pada detik itu membuka soket
            pemancar terikat ke alamat sementara tersebut. Beberapa detik
            kemudian DHCP menjawab, kartunya berpindah ke alamat lab yang
            sesungguhnya, dan soket relay tetap terikat ke alamat yang sudah
            tidak ada.

            Yang terjadi kemudian persis seperti yang terlihat di lab:

                Siaran dipancarkan lewat 2 antarmuka: 169.254.172.150, 192.168.56.1
                Relay gagal memancarkan (238000x): The requested address is not
                valid in its context.

            Jaringan lab tidak ada di daftar sama sekali, dan tidak akan pernah
            masuk. Panel melaporkan siaran berjalan pada 29 Mbps; tidak satu
            datagram pun meninggalkan mesin.

            NetworkAddressChanged menutup celah itu. Ia menyala pada setiap sewa
            DHCP, kabel yang dicolok, dan Wi-Fi yang tersambung — yaitu setiap
            keadaan yang membuat daftar lama menjadi bohong.
        */
        var senders = OpenSenders();
        var rebuild = 0;

        void Rebuild(string why)
        {
            // Ditandai lewat penghitung, bukan langsung disusun ulang di sini.
            // Peristiwa jaringan tiba di utas lain, dan menukar daftar soket
            // sementara gelung pengiriman sedang menyusurinya akan merusak
            // pengiriman yang sedang berjalan.
            Interlocked.Increment(ref rebuild);
            log.LogInformation("Jaringan berubah ({Why}) — daftar pemancar akan disusun ulang", why);
        }

        NetworkChange.NetworkAddressChanged += (_, _) => Rebuild("alamat");
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Rebuild("ketersediaan");

        void Announce()
        {
            if (senders.Count == 0)
            {
                log.LogError("Tidak ada antarmuka jaringan yang dapat memancarkan multicast. "
                           + "Siaran tidak akan sampai ke unit mana pun.");
            }
            else
            {
                log.LogInformation("Siaran dipancarkan lewat {Count} antarmuka: {List}",
                    senders.Count, string.Join(", ", senders.Select(x => x.Address)));
            }
        }

        Announce();

        var group = new IPEndPoint(
            IPAddress.Parse(FramePacket.MulticastGroup), FramePacket.DefaultPort);

        log.LogInformation("Relay siaran mendengarkan di :{In}, memancarkan ke {Group}:{Out}",
            PanelProtocol.RelayPort, FramePacket.MulticastGroup, FramePacket.DefaultPort);

        var sendFailures = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult packet;

            try
            {
                packet = await inbound.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                log.LogWarning("Relay gagal menerima: {Message}", ex.Message);
                continue;
            }

            BroadcastHolder? holder;
            lock (_gate) holder = _holder;

            /*
                ALAMAT SUMBER DIPELAJARI DARI DATAGRAM PERTAMA, BUKAN DIPAKSA.

                Alamat yang dicatat saat klaim berasal dari sambungan TLS. UDP
                yang dikirim panel yang sama belum tentu keluar dari alamat itu:
                mesin dengan adaptor virtual — VirtualBox, VPN, WSL — memilih
                alamat sumber menurut tabel rute, dan tabel rute untuk UDP tidak
                harus sepakat dengan sambungan TCP yang sudah terbentuk.

                Memaksa keduanya sama berarti siaran gagal total di mesin yang
                justru paling umum dipakai untuk mengembangkannya, dengan gejala
                layar hitam tanpa satu pun galat di sisi mana pun.

                Jadi: datagram pertama setelah klaim menetapkan alamatnya, dan
                sejak itu terkunci. Jendela tempat penyusup dapat menyisipkan
                diri hanya selebar milidetik antara klaim dan frame pertama —
                dan ia harus memenangkan balapan melawan panel yang sudah mulai
                mengirim. Setelah terkunci, perlindungannya utuh: datagram dari
                alamat lain diabaikan sepanjang siaran berjalan.
            */
            if (holder is { Source: null })
            {
                lock (_gate)
                {
                    if (_holder is { Source: null } pending)
                    {
                        _holder = pending with { Source = packet.RemoteEndPoint.Address };
                        holder = _holder;
                    }
                }

                log.LogInformation("Sumber siaran dikunci ke {Address}", packet.RemoteEndPoint.Address);
            }

            if (holder?.Source is null || !holder.Source.Equals(packet.RemoteEndPoint.Address))
            {
                var count = Interlocked.Increment(ref _rejected);

                /*
                    Penolakan PERTAMA dicatat seketika, lengkap dengan kedua
                    alamatnya.

                    Versi pertama hanya mencatat tiap datagram ke-300, dan
                    dengan tiga puluh datagram per detik itu berarti sepuluh
                    detik hening sebelum petunjuk pertama muncul — sementara
                    yang terlihat di layar mahasiswa sudah berupa layar hitam
                    sejak detik pertama.

                    Alamat yang diharapkan dan yang datang dicetak berdampingan
                    karena keduanya memang bisa berbeda secara sah: mesin dengan
                    adaptor virtual memilih alamat sumber UDP menurut tabel rute,
                    dan itu belum tentu alamat yang dipakai sambungan TLS-nya.
                */
                if (count == 1 || count % 300 == 0)
                {
                    log.LogWarning(
                        "Datagram siaran DIABAIKAN ({Count}x)\n"
                      + "  diharapkan dari : {Expected}\n"
                      + "  datang dari     : {Actual}",
                        count,
                        holder is null ? "(tidak ada pemegang siaran)"
                            : holder.Source?.ToString() ?? "(belum ada frame pertama)",
                        packet.RemoteEndPoint.Address);
                }
                continue;
            }

            try
            {
                /*
                    Pengiriman SINKRON, bukan await per soket.

                    SendTo pada soket UDP hampir tidak lebih dari penyalinan ke
                    buffer kernel. Menunggunya lewat SendAsync menambah
                    pembungkusan tugas dan perpindahan konteks pada laju dua ribu
                    datagram per detik — dan selama relay sibuk menunggu, buffer
                    penerimanya terus terisi oleh potongan berikutnya sampai
                    meluap.

                    Bersama buffer yang dibesarkan, inilah yang membuat relay
                    mampu menguras masukannya lebih cepat daripada kedatangannya.
                */
                // Disusun ulang di titik yang aman: sebelum menyusuri daftar,
                // bukan dari utas peristiwa jaringan.
                if (Interlocked.Exchange(ref rebuild, 0) > 0)
                {
                    foreach (var old in senders) old.Dispose();
                    senders = OpenSenders();
                    sendFailures = 0;
                    Announce();
                }

                foreach (var sender in senders)
                    sender.Client.Client.SendTo(packet.Buffer, SocketFlags.None, group);

                long relayed;

                lock (_gate)
                {
                    _bytesRelayed += packet.Buffer.Length;
                    relayed = ++_framesRelayed;
                }

                /*
                    Datagram PERTAMA yang berhasil diteruskan dicatat.

                    Inilah satu-satunya baris yang membedakan "relay tidak
                    pernah menerima apa-apa" dari "relay meneruskan tetapi
                    penonton tidak menerimanya". Keduanya terlihat sama persis
                    di layar mahasiswa — hitam — dan menuntut perbaikan di
                    tempat yang berbeda: yang pertama di sisi panel, yang kedua
                    di pemilihan antarmuka multicast.
                */
                if (relayed == 1)
                {
                    log.LogInformation(
                        "Frame siaran pertama diteruskan dari {From} ke {Group}:{Port} "
                      + "lewat {Count} antarmuka",
                        packet.RemoteEndPoint.Address,
                        FramePacket.MulticastGroup, FramePacket.DefaultPort, senders.Count);
                }
                else if (relayed % 900 == 0)
                {
                    var (_, mbps) = Stats;
                    log.LogInformation("Relay siaran: {Count} datagram · {Mbps:F1} Mbps",
                        relayed, mbps);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                /*
                    Kegagalan pancar dicatat berkala, bukan per datagram.

                    Seribu datagram per detik yang semuanya gagal menghasilkan
                    seribu baris identik per detik, dan yang terjadi berikutnya
                    adalah log yang tidak dapat dibaca sama sekali — kebalikan
                    dari gunanya. Baris pertama tetap muncul seketika.
                */
                if (++sendFailures is 1 || sendFailures % 2000 == 0)
                {
                    log.LogWarning(
                        "Relay gagal memancarkan ({Count}x): {Message}", sendFailures, ex.Message);
                }

                /*
                    Alamat yang tidak lagi sah MEMICU penyusunan ulang.

                    NetworkAddressChanged menangkap sebagian besar perubahan,
                    tetapi tidak semuanya sampai dengan andal — dan relay yang
                    menembak alamat mati dua ratus ribu kali tanpa pernah
                    mencoba memperbaiki dirinya adalah perilaku yang tidak dapat
                    dibela. Galat ini menyebutkan sebabnya dengan tepat, jadi
                    dipakai sebagai pemicu kedua.
                */
                if (ex.SocketErrorCode is SocketError.AddressNotAvailable
                                       or SocketError.NetworkDown
                                       or SocketError.NetworkUnreachable)
                {
                    Rebuild($"pengiriman ditolak: {ex.SocketErrorCode}");
                }
            }
        }

        foreach (var sender in senders) sender.Dispose();
    }

    /// <summary>Satu soket pemancar beserta alamat antarmukanya.</summary>
    private sealed record Sender(IPAddress Address, UdpClient Client) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    /// <summary>
    /// Membuka satu soket per antarmuka yang dapat memancarkan multicast.
    ///
    /// Soket DIIKAT ke alamat antarmukanya, bukan diberi opsi MulticastInterface.
    /// Keduanya seharusnya setara, tetapi pengikatan bekerja lebih dapat
    /// diandalkan di Windows — terutama pada adaptor virtual, yang justru selalu
    /// hadir di mesin tempat masalah ini paling sering muncul.
    ///
    /// Loopback dilewati: multicast tidak dapat keluar lewatnya, dan mencobanya
    /// menghasilkan "unreachable network" seribu kali per detik. Penonton di
    /// mesin yang sama tetap menerima siaran lewat MulticastLoopback pada soket
    /// antarmuka nyata.
    /// </summary>
    private List<Sender> OpenSenders()
    {
        var result = new List<Sender>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;

                /*
                    APIPA (169.254.x) DILEWATI.

                    Windows memberikannya justru KETIKA jaringan gagal: DHCP
                    tidak menjawab, jadi kartunya mengarang alamat sendiri.
                    Memancar lewat alamat itu tidak pernah sampai ke siapa pun,
                    dan lebih buruk lagi — ia membuat daftar pemancar terlihat
                    berisi, sehingga "Tidak ada antarmuka yang dapat memancarkan"
                    tidak pernah tercetak pada mesin yang justru sedang mengalami
                    persis keadaan itu.

                    Alamatnya akan berganti sendiri begitu DHCP menjawab, dan
                    NetworkAddressChanged menyusun ulang daftarnya saat itu.
                */
                if (addr.Address.GetAddressBytes() is [169, 254, ..])
                {
                    log.LogInformation(
                        "Antarmuka {Address} dilewati: alamat APIPA, DHCP belum menjawab",
                        addr.Address);
                    continue;
                }

                try
                {
                    var client = new UdpClient(new IPEndPoint(addr.Address, 0));

                    client.Client.SetSocketOption(SocketOptionLevel.IP,
                        SocketOptionName.MulticastTimeToLive, FramePacket.MulticastTtl);

                    // Wajib: penonton di mesin yang sama dengan koordinator —
                    // termasuk seluruh pengujian di satu mesin — hanya menerima
                    // siaran bila loopback multicast dinyalakan.
                    client.Client.SetSocketOption(SocketOptionLevel.IP,
                        SocketOptionName.MulticastLoopback, true);

                    // Buffer kirim ikut dibesarkan: dua soket yang mengirim
                    // bergantian pada laju dua ribu datagram per detik dapat
                    // memenuhi buffer bawaan, dan SendTo yang menemukan buffer
                    // penuh membuang datagramnya tanpa galat.
                    client.Client.SendBufferSize = 4 << 20;

                    result.Add(new Sender(addr.Address, client));
                }
                catch (SocketException ex)
                {
                    log.LogWarning("Antarmuka {Address} tidak dapat dipakai memancarkan: {Message}",
                        addr.Address, ex.Message);
                }
            }
        }

        return result;
    }
}
