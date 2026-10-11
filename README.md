# Retriva

Sistem manajemen Laboratorium Information Retrieval — 42 unit komputer
(1 PC Asisten, 1 PC Dosen, 40 PC Mahasiswa).

## Prasyarat

- Windows 11 (x64)
- **.NET 10 SDK** (bukan hanya runtime) — <https://dotnet.microsoft.com/download/dotnet/10.0>
- Node.js 20+ (untuk UI React, dibutuhkan mulai M1)
- VS Code + ekstensi C# Dev Kit, atau Visual Studio versi yang mendukung .NET 10

Cek instalasi dengan `dotnet --list-sdks`. Kalau hasilnya kosong, yang terpasang
baru runtime-nya saja dan SDK belum ada.

> .NET 10 dipilih karena LTS sampai November 2028. .NET 8 dan 9 berakhir
> masa dukungannya 10 November 2026.

## Struktur solusi

| Project | Target | Peran |
|---|---|---|
| `Retriva.Shared` | `net10.0` | Kontrak pesan dan konfigurasi. Dipakai semua project. |
| `Retriva.Admin` | `net10.0-windows` | Aplikasi admin. WPF meng-host WebView2 + Kestrel/SignalR. |
| `Retriva.Agent` | `net10.0-windows` | Windows Service di PC mahasiswa, berjalan sebagai SYSTEM. |
| `Retriva.Client` | `net10.0-windows` | UI mahasiswa, jalan di sesi pengguna. |
| `Retriva.FakeAgent` | `net10.0` | Agent tiruan untuk uji skala 40 unit di satu mesin. |

`Retriva.Shared` sengaja platform-netral supaya `FakeAgent` ringan dan bisa
dijalankan puluhan instance sekaligus.

## Build

```powershell
dotnet restore
dotnet build
```

Menjalankan konsol:

```powershell
dotnet run --project src/Retriva.Admin
```

Menjalankan simulator unit mahasiswa:

```powershell
# 40 unit sekaligus dalam SATU proses
dotnet run --project src/Retriva.FakeAgent -- --count 40

# satu unit saja
dotnet run --project src/Retriva.FakeAgent -- --unit PC-07

# rentang tertentu
dotnet run --project src/Retriva.FakeAgent -- --from 21 --to 40

# mode chaos: memutus unit acak tiap 12 detik
dotnet run --project src/Retriva.FakeAgent -- --count 40 --chaos
```

Jangan menjalankan 40 proses `dotnet run` terpisah — itu memakan sekitar
2 GB RAM tanpa menguji apa pun yang berbeda dari satu proses berisi 40 koneksi.

## Memasang agent sebagai Windows Service (pengembangan)

```powershell
sc.exe create RetrivaAgent binPath= "C:\path\RetrivaAgent.exe" start= auto
sc.exe start RetrivaAgent
```

Hapus dengan `sc.exe delete RetrivaAgent`.

## Arsitektur singkat

**Arah koneksi dibalik: agent yang menelepon konsol**, bukan sebaliknya. Akibatnya
hanya PC admin yang butuh aturan firewall masuk, dan agent tetap menemukan konsol
meski konfigurasi jaringannya menyimpang.

**Control plane dan data plane terpisah.** Perintah lewat SignalR over TLS.
Byte video siaran layar mengalir langsung lewat UDP dari unit sumber ke klien,
tidak pernah direlay lewat primary — kalau direlay, bandwidth berlipat dan
latensi bertambah tanpa alasan.

**Primary vs otoritas adalah dua hal berbeda.** PC Asisten primary karena ia
memegang basis data dan paling sering menyala. PC Dosen tetap berprioritas
lebih tinggi atas token siaran, supaya presentasi tidak terganggu.

**Identitas Windows dan identitas Retriva terpisah.** PC autologon ke satu akun
lokal bersama; NIM dimasukkan di aplikasi. Windows hanya alat untuk sampai ke
desktop, bukan sumber identitas akademik.

## Batasan Session 0 (penting untuk M3–M5)

Windows mengisolasi layanan sistem di Session 0, terpisah dari desktop pengguna.
Konsekuensinya, `RetrivaAgent` yang berjalan sebagai Windows Service **tidak
dapat**:

- menangkap layar pengguna (pratinjau dan siaran layar),
- menyuntikkan input mouse maupun keyboard,
- menampilkan jendela apa pun (termasuk layar terkunci).

Semua fitur itu harus dijalankan oleh proses yang berada di sesi pengguna.
Rancangannya: `RetrivaAgent` (service, SYSTEM) tetap memegang koneksi,
kontrol daya, dan siklus hidup; sementara `RetrivaClient` yang berjalan di
sesi pengguna melakukan capture, injeksi input, dan menampilkan overlay.
Keduanya berkomunikasi lewat named pipe lokal.

Selama pengembangan, menjalankan agent dengan `dotnet run` membuatnya berada
di sesi pengguna sehingga semuanya bekerja. Perbedaannya baru muncul setelah
dipasang sebagai service.

## Penamaan unit

**Koordinator yang menamai unit, bukan agent.** Pemetaan oktet ke nama unit
tersimpan di `console.json` dan disunting dari layar Konfigurasi:

```
43 -> PC-01
 2 -> PC-02
 3 -> PC-03
...
```

Daftar, bukan rumus. Versi sebelumnya menyimpan oktet awal dan jumlah PC lalu
menghitung nomornya secara berurutan — model yang mensyaratkan barisan tak
terputus. Lab sungguhan memutusnya: alamat `.1` lazim dipakai gerbang jaringan,
jadi PC-01 pindah ke oktet lain sementara PC-02 tetap di `.2`, dan tidak ada
oktet awal mana pun yang menghasilkan kedua nomor itu sekaligus.

Tabel hanya berlaku pada pendaftaran **pertama**. Unit yang sudah memegang token
perangkat mempertahankan namanya walau alamatnya berubah, jadi satu sewa DHCP
yang berbeda tidak menukar nama dua PC. Unit yang sudah terdaftar diganti nama
dari layar Konfigurasi, dan penggantiannya ikut memperbarui seluruh riwayatnya.

Sifat yang tetap dipertahankan: **satu installer identik dipakai untuk 40 PC
tanpa konfigurasi per mesin.** Yang ditanam installer hanya alamat koordinator,
kode pendaftaran, dan sidik jari sertifikat — ketiganya sama untuk seluruh lab.

MAC address direkam saat pendaftaran dan dipakai untuk Wake-on-LAN. MAC yang
berubah **dicatat, tidak menolak** pendaftaran: ia tidak membuktikan apa pun
(dikirim agent sebagai teks biasa, dapat diubah dari properti adapter) sementara
ia memang berubah pada mesin yang tidak salah apa-apa — kartu jaringan diganti,
laptop dipasang ke dok, atau unit berpindah dari Wi-Fi ke kabel.

## Menambah fitur baru

1. Tambahkan turunan `AgentCommand` di `Shared/Protocol/Commands.cs`
   beserta `[JsonDerivedType]`-nya.
2. Tangani di agent (`Retriva.Agent`).
3. Picu dari UI React.

Tidak ada tempat lain yang perlu disentuh. Kalau menambah field dan lupa
menanganinya di salah satu sisi, compiler yang memberi tahu.

## Membangun installer

```powershell
.\build-installer.ps1
```

Menerbitkan agent, klien, dan panel; membungkusnya menjadi `lab.zip` dan
`panel.zip`; lalu membangun `release\RetrivaSetup.exe` yang menelan keduanya
sebagai resource. Hasilnya satu berkas mandiri — installer berupa folder akan
disalin separuh oleh siapa pun yang memindahkannya lewat flash disk, dan
kekurangannya tidak terlihat sampai pemasangan berjalan.

Urutannya wajib: publish → bungkus → bangun installer. Resource tidak dapat
ditanam ke assembly yang sudah dikompilasi.

**Dua peran, dibagi per mesin bukan per proyek:**

| Peran | Isi | Dipasang di |
|---|---|---|
| Lab | Agent (layanan) + Client (sesi pengguna) | 40 PC mahasiswa |
| Panel | Panel admin, dapat sekaligus menjadi koordinator | PC Asisten, laptop dosen |

Agent dan Client tidak dipisah karena keduanya selalu dipasang bersama — Client
adalah tangan Agent di sesi pengguna. Installer terpisah akan memungkinkan
keadaan "Agent ada, Client tidak": unit tampil online dan menjalankan setiap
perintah, sementara kunci layar, penonton siaran, dan pemantauan ujian diam
tanpa satu galat pun.

**Pemasangan senyap** untuk PC berikutnya. Ketiga nilai ada di layar Konfigurasi
pada panel Superadmin:

```powershell
.\release\RetrivaSetup.exe --silent --role lab `
    --host 192.168.1.252 --port 8443 `
    --secret <kode pendaftaran agent> `
    --thumbprint <sidik jari sertifikat> `
    --log C:\retriva-install.log
```

`--secret` dan `--thumbprint` wajib pada mode senyap. Tanpa keduanya pemasangan
berhasil sempurna dan unitnya tidak pernah muncul di dashboard.

**Memasang di banyak PC sekaligus.** Jalankan sekali di PC koordinator:

```powershell
.\share-installer.ps1
```

Folder `release\` dibagikan hanya-baca lewat jaringan, dan skripnya mencetak
perintah siap tempel untuk tiap PC mahasiswa — dengan alamat, kode pendaftaran,
dan sidik jari yang sudah terisi dari `console.json` koordinator. Tidak ada
penyalinan berkas, dan tiga nilai panjang itu tidak perlu diketik ulang di mesin
mana pun.

Setelah semua terpasang, tutup sharenya: `.\share-installer.ps1 -Remove`

Pemasangan ulang **aman** dijalankan di PC yang sudah terpasang: layanan
dihentikan, berkas ditimpa, token perangkat dipertahankan. Uninstall hanya perlu
bila ingin benar-benar bersih.

**Memeriksa PC yang sudah terpasang**, tanpa mengubah apa pun. `RetrivaSetup.exe`
ikut terpasang di folder program justru untuk ini:

```powershell
& "$env:ProgramFiles\Retriva\Lab\RetrivaSetup.exe" --check --host 192.168.1.252
```

**Mencopot** lewat Apps & features, atau langsung. Data di `ProgramData`
dipertahankan kecuali diminta sebaliknya dengan `--clean-data`: di dalamnya
token perangkat, satu-satunya bukti unit ini pernah disetujui.

Yang gratis dari Inno Setup dan ditulis sendiri di `src/Retriva.Setup`:
uninstaller, entri Add/Remove Programs, dan **pembatalan berurutan** saat
pemasangan gagal di tengah. Yang terakhir paling menentukan — installer buatan
sendiri jarang gagal saat memasang, ia gagal saat gagal memasang.

## Status pengembangan

Sudah terpasang: siaran layar (dosen, aplikasi, dan layar mahasiswa terpilih),
kendali jarak jauh dengan persetujuan mahasiswa, kunci layar, kontrol daya,
percakapan, pembagian berkas, penemuan lab, pendaftaran panel bertingkat,
peta jaringan berbasis pengukuran, mode ujian, dan installer agent.

Belum ada: installer koordinator dan panel admin, uji otomatis, dan self-update.
Dukungan macOS belum diputuskan — `Retriva.Server` sudah portabel, tetapi agent,
klien, dan panel bersandar pada WPF dan Win32 (`SetWindowDisplayAffinity`,
`SendInput`, tangkapan GDI, named pipe). Itu penulisan ulang, bukan pemindahan.
