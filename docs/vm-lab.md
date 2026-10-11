# Menyiapkan VM sebagai PC mahasiswa

Sekali saja. Setelah ini, setiap pengujian berikutnya cukup satu perintah.

Seluruh langkah di bawah dikerjakan **di PC Superadmin** — mesin yang sama
tempat kode ditulis dan koordinator berjalan.

---

## Mengapa repot membuat VM

Bukan karena VM lebih mirip PC mahasiswa. Justru kurang mirip.

Alasannya **snapshot**. Pemasangan yang gagal separuh jalan meninggalkan mesin
dalam keadaan yang bukan sebelum maupun sesudah: layanan terdaftar tapi tidak
jalan, token perangkat tertulis tapi tidak dikenali koordinator, aturan firewall
separuh. Di PC fisik, memulihkannya berarti mencopot pemasangan dan berharap
uninstall-nya benar — artinya menguji uninstall setiap kali hendak menguji hal
lain.

Di VM, ia dikembalikan dalam lima detik, dan keadaannya identik dengan
pemasangan pertama, setiap kali. Bug yang "hanya muncul pada pemasangan bersih"
akhirnya bisa direproduksi dengan andal.

**Yang tetap menuntut PC fisik:** Wake-on-LAN, karena VM tidak punya kartu
jaringan yang benar-benar tidur, dan pengukuran kecepatan tautan. Itu saja.
Siaran, kendali jarak jauh, mode ujian, berbagi berkas, dan pemasangan berjalan
sungguhan di sini — lewat switch yang sama dengan lab.

---

## 1. Buat VM-nya

VirtualBox sudah terpasang di mesin Anda (adapter `192.168.56.1` di log
koordinator adalah miliknya).

Buat VM baru:

| Setelan | Nilai |
|---|---|
| Nama | `Retriva-Mahasiswa` |
| Jenis | Microsoft Windows / Windows 11 64-bit |
| RAM | 4096 MB (minimal 3072) |
| CPU | 2 inti |
| Disk | 60 GB, dialokasikan dinamis |

Nama VM-nya harus persis `Retriva-Mahasiswa`, atau sebutkan namanya nanti
lewat `-Vm 'nama lain'`.

Pasang Windows 11 seperti biasa. Buat akun lokal bernama **`lab`** dengan kata
sandi **`lab`** — dua nilai itu dipakai `dev-vm.ps1` untuk menjalankan
pemasangan di dalam VM tanpa Anda perlu mengetik apa pun.

> Kata sandi selemah itu hanya boleh di VM uji yang tidak menyimpan apa pun.
> Jangan pakai pola ini di PC lab sungguhan.

---

## 2. Setel jaringannya ke Bridged

Ini setelan paling menentukan di seluruh dokumen ini.

**Settings → Network → Adapter 1:**

- Attached to: **Bridged Adapter**
- Name: kartu jaringan yang tersambung ke router lab — yang punya alamat
  `192.168.1.x`, bukan adapter VirtualBox atau VPN

Dengan Bridged, VM menjadi node sungguhan di jaringan lab: ia mendapat alamat
sendiri dari router, dan multicast melewati switch yang sama dengan PC
mahasiswa asli.

**Jangan pakai NAT.** NAT memberi VM akses internet tetapi host tidak dapat
menghubunginya, dan multicast tidak melewatinya sama sekali. Siaran dan kendali
jarak jauh akan diam tanpa satu pun galat — persis jenis kegagalan yang paling
mahal ditelusuri.

Host-only (`192.168.56.x`) berfungsi bila router lab sedang tidak tersedia,
tetapi ia tidak menguji switch yang sebenarnya.

---

## 3. Pasang prasyarat di dalam VM

Retriva diterbitkan tanpa runtime bawaan (`--self-contained false`), jadi VM
butuh dua hal. Keduanya perlu internet **sekali saja** — sesudah itu snapshot
menyimpannya selamanya.

Di dalam VM:

1. **.NET 10 Desktop Runtime (x64)** — dari dotnet.microsoft.com
2. **WebView2 Runtime** — biasanya sudah ada di Windows 11; kalau belum, ambil
   Evergreen Standalone Installer dari Microsoft
3. **Guest Additions** — menu Devices → Insert Guest Additions CD image, lalu
   jalankan penginstalnya

Guest Additions bukan opsional: tanpanya `dev-vm.ps1` tidak dapat menjalankan
pemasangan di dalam VM, dan skrip akan jatuh ke mode manual.

Lalu matikan Windows Update supaya ia tidak menghidupkan ulang VM di tengah
pengujian, dan **setel profil jaringan ke Private** — Windows memblokir
multicast masuk pada profil Public, dan itu sudah pernah membuat siaran diam.

---

## 4. Ambil snapshot

VM dalam keadaan mati, bersih, prasyarat lengkap, Retriva **belum** terpasang.

Di PowerShell PC Superadmin:

```powershell
VBoxManage snapshot "Retriva-Mahasiswa" take "bersih"
```

Inilah titik yang dikembalikan setiap pengujian. Ambil ulang bila suatu saat
Anda mengubah prasyaratnya.

---

## 5. Jalankan loopnya

PowerShell **sebagai Administrator**, di folder `retriva`:

```powershell
.\dev-vm.ps1
```

Yang terjadi:

1. Build UI dan installer
2. Bagikan `release\` lewat jaringan lokal, hanya-baca
3. Baca alamat, kode pendaftaran, dan sidik jari dari konfigurasi koordinator
4. Kembalikan VM ke snapshot `bersih`
5. Jalankan VM, tunggu sampai siap
6. Pasang Retriva di dalamnya tanpa dialog

Unit akan muncul di dashboard Superadmin beberapa detik kemudian, dan
RetrivaClient berjalan otomatis di VM.

Varian yang berguna:

```powershell
.\dev-vm.ps1 -SkipBuild    # tidak ada perubahan kode, hanya pasang ulang
.\dev-vm.ps1 -NoRevert     # uji pembaruan DI ATAS versi lama, bukan pemasangan bersih
.\dev-vm.ps1 -ShareOnly    # jangan sentuh VM; cetak perintahnya untuk PC fisik
```

`-ShareOnly` juga yang dipakai untuk memasang ke PC mahasiswa sungguhan: ia
mencetak satu baris perintah yang tinggal ditempel di PC itu. Tanpa flash disk,
tanpa internet, tanpa mengetik sidik jari.

---

## Kapan tetap perlu PC fisik

Setelah sebuah fitur berjalan di VM, uji di PC fisik hanya untuk:

- Wake-on-LAN
- kecepatan tautan yang dilaporkan di peta jaringan
- penerimaan akhir sebelum dipakai di kelas sungguhan

Iterasinya di VM; pembuktian akhirnya di lab.

---

## Kalau sesuatu tidak jalan

Di **tiap** mesin — host maupun VM:

**Start → ketik "Retriva" → Retriva — Kumpulkan log**

Sebuah zip muncul di Desktop berisi seluruh log, konfigurasi (kode pendaftaran
dan token sudah disamarkan), status layanan, aturan firewall, dan alamat
jaringan. Kirimkan berkas itu apa adanya.

Log pemasangan di dalam VM ada di `C:\retriva-install.log`.

---

## Untuk bug yang tidak butuh VM sama sekali

Sebagian besar bug tidak butuh mesin kedua. Berbagi berkas, mode ujian,
penamaan unit, obrolan, dan seluruh perilaku antarmuka dapat diuji dalam
hitungan detik di satu mesin:

```powershell
.\run-dev.ps1 -UiDev
```

Empat peran, empat jendela, folder data terpisah, hot-reload untuk UI. Pakai
ini lebih dulu; naikkan ke VM hanya ketika bugnya benar-benar melibatkan dua
mesin.
