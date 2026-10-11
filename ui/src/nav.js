import {
  SquaresFour, Broadcast, CursorClick, ChatTeardropText,
  TrayArrowUp, Graph, SlidersHorizontal, ShieldCheck, Eye,
} from '@phosphor-icons/react';

// Satu keluarga ikon, satu ketebalan. Tidak ada emoji di antarmuka.
export const NAV = [
  { group: 'Monitoring', items: [
    { id: 'dashboard',   Icon: SquaresFour,       label: 'Dashboard' },
    { id: 'screenshare', Icon: Broadcast,         label: 'Bagikan layar' },
    { id: 'remote',      Icon: CursorClick,       label: 'Kontrol jarak jauh' },
  ]},
  { group: 'Komunikasi', items: [
    // Tidak ada angka bawaan di sini. Versi sebelumnya menulis badge: 3 sejak
    // masa mockup, dan angka itu bertahan tanpa pernah tersambung ke apa pun —
    // menampilkan 3 saat tidak ada pesan sama sekali.
    { id: 'chat',  Icon: ChatTeardropText, label: 'Chat mahasiswa' },
    { id: 'files', Icon: TrayArrowUp,      label: 'Transfer berkas' },
  ]},
  { group: 'Sistem', items: [
    { id: 'exam',     Icon: Eye,               label: 'Mode ujian' },
    { id: 'network',  Icon: Graph,             label: 'Jaringan lab' },

    /*
      Terlihat oleh semua panel, bukan hanya Superadmin.

      Di sinilah panel yang belum tertaut memasukkan kode pendaftaran, jadi
      menyembunyikannya dari non-Superadmin akan mengubur satu-satunya jalan
      keluar dari keadaan "belum tertaut". Isinya yang berbeda menurut keadaan,
      bukan keberadaannya.
    */
    { id: 'panels',   Icon: ShieldCheck,       label: 'Panel admin' },

    { id: 'settings', Icon: SlidersHorizontal, label: 'Konfigurasi' },
  ]},
];

export const TITLES = {
  dashboard:   ['Dashboard laboratorium', (n) => `${n}/42 unit`],
  screenshare: ['Bagikan layar',          () => 'siaran ke unit mahasiswa'],
  remote:      ['Kontrol jarak jauh',     (_, pc) => (pc ? `${pc.unitId} · ${pc.ipAddress}` : 'belum ada unit dipilih')],
  chat:        ['Chat mahasiswa',         () => 'percakapan pribadi per unit'],
  files:       ['Transfer berkas',        () => 'kirim berkas ke unit mahasiswa'],
  // Subjudul lama berbunyi "router · 2 switch · 40 unit" — tiga angka yang
  // tidak satu pun berasal dari jaringan. Yang menggantikannya dihitung dari
  // unit yang benar-benar tersambung.
  exam:        ['Mode ujian',            () => 'catatan aktivitas, bukan penilaian'],
  network:     ['Jaringan laboratorium', (n) => `${n} unit aktif · nilai terukur`],
  panels:      ['Panel admin',            () => 'pendaftaran dan persetujuan'],
  settings:    ['Konfigurasi sistem',     () => 'koneksi dan akses'],
};

/** Ikon dipakai juga di rail kiri, dikelompokkan per fungsi besar. */
export const RAIL = [
  { id: 'dashboard',   Icon: SquaresFour,       title: 'Monitoring' },
  { id: 'screenshare', Icon: Broadcast,         title: 'Siaran layar' },
  { id: 'chat',        Icon: ChatTeardropText,  title: 'Komunikasi' },
  { id: 'files',       Icon: TrayArrowUp,       title: 'Berkas' },
];

export const RAIL_BOTTOM = [
  { id: 'settings', Icon: SlidersHorizontal, title: 'Konfigurasi' },
];
