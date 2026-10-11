/*
    BERKAS INI SUDAH TIDAK DIPAKAI. Aman dihapus.

    Dulu penonton siaran adalah jendela WPF tersendiri dengan Image dan bilah
    status. Ia digantikan jalur di ClientShell: frame disajikan sebagai gambar
    biasa kepada WebView2 lewat WebResourceRequested, sehingga siarannya dapat
    ditata dengan CSS dan mahasiswa tetap dapat memakai menu selagi menonton.

    KODE MATI INI SEMPAT MENYESATKAN PENELUSURAN BERJAM-JAM. Saat mencari sebab
    layar hitam, saya menambahkan angka diagnostik ke bilah status di berkas ini
    — bilah status pada jendela yang tidak pernah dibuat siapa pun. Setiap
    perbaikan di sini tampak masuk akal dan tidak satu pun berpengaruh.

    Petunjuk yang akhirnya membongkarnya justru datang dari keluhan pertama:
    "ikon gambar rusak". WPF tidak pernah menggambar ikon semacam itu — itu
    perilaku <img> di web, dan dari situ jelas penonton yang sebenarnya ada di
    WebView2, bukan di sini.

    Dibiarkan sebagai penanda alih-alih dihapus karena folder yang di-mount
    menolak penghapusan. Isinya dikosongkan supaya tidak ada yang mengiranya
    masih hidup.
*/
