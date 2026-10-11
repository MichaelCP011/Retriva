# Logo Retriva

Dua aplikasi, dua logo. Dipisah supaya tidak ada yang keliru membuka atau
memasang yang salah — di lab, panel admin dan klien mahasiswa akan berdampingan
di taskbar yang sama.

```
assets/
  client/   app.ico    mark.png       ← Retriva Mahasiswa
  admin/    app.ico    mark.png       ← Retriva Panel Admin
ui/src/assets/
  logo-client.svg                     ← layar masuk & rail klien
  logo-admin.svg                      ← rail panel admin
```

| Berkas | Dipakai di | Format |
|---|---|---|
| `app.ico` | ikon jendela dan taskbar | ICO multi-ukuran: 16, 24, 32, 48, 64, 128, 256 |
| `mark.png` | lambang di bingkai jendela | PNG transparan, 36×36 (72×72 untuk HiDPI) |
| `logo-*.svg` | antarmuka web | SVG, viewBox 64×64 |

## Yang menentukan berhasil atau tidak

**Harus terbaca pada 18 piksel.** Itu ukuran tampil `mark.png` di bingkai
jendela. Kecilkan sungguhan sampai 18 piksel sebelum memutuskan final — detail
setipis rambut akan lenyap dan tidak ada yang menyadarinya sampai terpasang.

**Harus terbaca di atas `#1B1B1B`.** Bingkai jendela dan rail keduanya gelap,
di tema terang maupun gelap. Logo gelap pekat akan hilang di sana.

**Huruf wajib di-outline menjadi path** sebelum ekspor SVG. Font yang dipakai
desainer tidak ada di PC laboratorium, dan teks yang belum di-outline akan
tergantikan font lain — biasanya menjadi jelek tanpa ada yang tahu sebabnya.

**Tanpa gradien.** Seluruh sistem desain aplikasi ini tidak memakai satu pun;
logo bergradien akan langsung terlihat berasal dari tempat lain.

## Membedakan keduanya

Sebaiknya bukan dua bentuk yang sama sekali berbeda, melainkan satu keluarga
dengan pembeda yang jelas pada ukuran kecil — misalnya bentuk dasar yang sama
dengan warna berbeda, atau satu elemen yang ada di salah satunya saja. Dua logo
yang tidak berhubungan membuat keduanya terasa seperti dua produk.

Bila berkasnya belum ada, aplikasi tetap berjalan memakai penanda sementara
berupa kotak merah berhuruf R.

---

## Catatan pemindahan

Logo yang sebelumnya berada langsung di `assets/` (`retriva.ico`,
`retriva-mark.png`) sudah disalin ke `client/` dan `admin/` dengan nama baku
`app.ico` dan `mark.png`. Keduanya kini memakai gambar yang sama sebagai titik
awal — ganti isi `admin/` begitu versi khusus panel admin siap.

Dua berkas di akar `assets/` sudah tidak dirujuk oleh build mana pun dan aman
dihapus.
