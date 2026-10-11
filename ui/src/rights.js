/*
  Wewenang panel, sebagaimana dikirim Superadmin.

  APA YANG BERKAS INI LAKUKAN, DAN APA YANG TIDAK.

  Ia menyembunyikan tombol yang akan ditolak. Itu saja. Penegakannya ada di
  Superadmin, yang memeriksa wewenang pada setiap metode hub — pesan panel dapat
  disusun sendiri oleh siapa pun yang mau, jadi tidak ada satu baris pun di sini
  yang menjaga apa pun.

  Gunanya tetap nyata: tombol yang terlihat normal lalu ditolak saat ditekan
  terbaca sebagai aplikasi yang rusak, bukan sebagai batas yang disengaja. Dosen
  yang menekan "Matikan semua" dan mendapat "peran Dosen tidak berwenang" akan
  menyimpulkan Retriva bermasalah, bukan bahwa memang bukan haknya.
*/

/**
 * Wewenang datang sebagai ARRAY nama tunggal, bukan string bendera.
 *
 * Versi sebelumnya mengurai hasil ToString() enum .NET, dan itu gagal dengan
 * cara yang paling tidak kentara: bila nilainya sama persis dengan sebuah
 * anggota bernama, ToString() mengembalikan nama itu saja. Superadmin
 * menghasilkan "Superadmin", bukan daftar wewenangnya — sehingga panel dengan
 * wewenang tertinggi justru kehilangan seluruh menunya.
 *
 * Array tetap diterima dalam bentuk string demi kompatibilitas dengan panel
 * versi lama yang belum dibangun ulang.
 */
export function parseRights(value) {
  if (!value) return new Set();
  if (Array.isArray(value)) return new Set(value);
  return new Set(String(value).split(',').map((s) => s.trim()).filter(Boolean));
}

/**
 * Membuat pemeriksa wewenang.
 *
 * Panel yang belum tersambung tidak memiliki wewenang apa pun, dan itu memang
 * jawaban yang benar: selama belum tertaut, tidak ada perintah yang akan
 * diterima siapa pun.
 */
export function makeCan(rightsText) {
  const set = parseRights(rightsText);
  return (right) => set.has(right);
}

/*
  TIDAK ADA MENU YANG DISEMBUNYIKAN. Kosong, dan itu disengaja.

  Yang membedakan Dosen dari Asisten adalah tombol mana yang ada di dalam
  menu — terutama daya — bukan menu mana yang terlihat. Menyembunyikan seluruh
  menu membuat kedua panel terasa seperti dua aplikasi berbeda, dan dosen yang
  pernah melihat panel asisten akan mengira miliknya rusak.

  Ada satu akibat yang perlu disadari: panel yang belum disetujui memiliki
  wewenang kosong, sehingga seluruh menunya akan tampil tetapi setiap
  tindakannya ditolak. Itu bukan kebohongan diam — penolakannya muncul sebagai
  pemberitahuan bersama alasannya, lencana di kepala jendela menyatakan
  keadaannya, dan layar Panel admin menjelaskan apa yang kurang.

  Konstanta ini dipertahankan, bukan dihapus, supaya penambahan menu yang
  memang harus dijaga punya tempat yang jelas.
*/
export const VIEW_RIGHTS = {};
