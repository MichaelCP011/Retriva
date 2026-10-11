/*
    BERKAS INI SUDAH TIDAK DIPAKAI. Aman dihapus.

    Isinya dulu adalah pembuktian dua arah berbasis HMAC yang mengikat kode
    pendaftaran ke sidik jari sertifikat koordinator. Secara kriptografis benar,
    dan sepenuhnya dibuang — bukan karena salah, melainkan karena menyelesaikan
    masalah yang tidak ada di lab ini.

    Kode itu menuntut asisten membacakan dua belas karakter dan dosen
    mengetiknya ulang. Yang dibelanya adalah "panel asing masuk ke daftar" —
    padahal persetujuan Superadmin sudah membela hal yang sama, dan membelanya
    lebih baik: kode dapat bocor, sedangkan persetujuan menuntut seseorang
    menekan tombol sambil melihat siapa yang meminta.

    Penggantinya ada di PanelRegistry.Register: token dicari berdasarkan sidik
    jari yang benar-benar dilihat pada sambungan. Koordinator asing tidak cocok
    dengan satu pun lab yang dikenal panel, jadi tidak menerima token apa pun.

    Dibiarkan sebagai penanda alih-alih dihapus karena folder yang di-mount
    menolak penghapusan. Isinya dikosongkan supaya tidak ada yang membacanya
    lalu mengira mekanisme ini masih menjaga sesuatu.
*/
