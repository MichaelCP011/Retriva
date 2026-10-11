; ============================================================================
;  BERKAS INI SUDAH TIDAK DIPAKAI.
;
;  Isinya dulu skrip Inno Setup untuk installer agent, dan ia bekerja. Yang
;  tidak dapat dilakukannya adalah tampil seperti Retriva: wizard Inno bergaya
;  Windows lama dan hanya dapat disesuaikan sedikit, sedangkan desain installer
;  yang disepakati menuntut rail langkah, kartu pemilihan peran, dan daftar
;  prasyarat dengan bahasa desain yang sama seperti panel admin.
;
;  Penggantinya ada di src/Retriva.Setup — installer WPF penuh. Yang selama ini
;  gratis dari Inno ditulis sendiri di sana:
;
;     SystemActions.cs   layanan, firewall, Add/Remove Programs
;     InstallPlan.cs     pembatalan berurutan saat gagal di tengah
;     Installer.cs       urutan langkah per peran, dan uninstall
;     Payload.cs         muatan ZIP yang ditanam ke dalam exe
;
;  Yang ketiga paling menentukan. Installer buatan sendiri jarang gagal saat
;  memasang; ia gagal saat GAGAL memasang — layanan sudah terdaftar sementara
;  berkasnya belum lengkap, dan mesin tertinggal dalam keadaan yang tidak dapat
;  diperbaiki dengan memasang ulang maupun dengan uninstall.
;
;  ---------------------------------------------------------------------------
;  CATATAN UNTUK YANG MEMPERTIMBANGKAN KEMBALI KE INNO
;
;  Tiga kegagalan yang muncul saat skrip ini dipakai BUKAN kesalahan Inno,
;  melainkan kesalahan menulis skripnya. Ketiganya layak diingat karena
;  ketiganya sunyi:
;
;    1. .iss tanpa BOM UTF-8 diperlakukan sebagai ANSI.
;    2. Komentar Pascal { } tidak bersarang — sebuah '}' di dalam komentar
;       menutupnya di tempat itu juga, dan sisa barisnya dibaca sebagai kode.
;    3. LoadStringFromFile menerima "var S: AnsiString". Parameter var menuntut
;       tipe sama persis; String di Pascal Script Unicode adalah UTF-16, dan
;       galatnya menunjuk ke kata "then" alih-alih ke variabelnya.
;
;  Berkas ini dibiarkan berisi catatan alih-alih dihapus supaya alasan
;  perpindahannya tetap dapat ditemukan oleh yang mencarinya nanti.
; ============================================================================
