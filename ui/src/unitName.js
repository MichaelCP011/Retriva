/*
  Satu tempat yang memutuskan nama apa yang ditulis untuk sebuah unit.

  URUTANNYA ADALAH ATURAN, DAN ATURAN ITU HANYA BERLAKU BILA HANYA ADA SATU
  SALINANNYA.

  Ada tiga nama yang bisa dimiliki satu unit, dan ketiganya datang dari pihak
  yang berbeda:

    alias     — ditetapkan asisten dari panel, mengikuti denah lab
    selfName  — diketik mahasiswa di unitnya sendiri
    unitId    — hasil deteksi agent, selalu ada

  Alias menang karena asisten yang menyusun denahnya, dan mahasiswa tidak boleh
  menggeser penamaan yang sudah disepakati satu lab. selfName menang atas unitId
  karena "Laptop Sinta" lebih berguna daripada "DESKTOP-4F9K2M1" bagi siapa pun
  yang membacanya.

  Ditulis sebagai fungsi bersama, bukan diulang sebagai `u.alias || u.unitId` di
  tiap layar. Bentuk yang diulang itu sudah ada di enam berkas sebelum ini, dan
  menambahkan selfName ke lima di antaranya akan menghasilkan satu layar yang
  diam-diam memakai aturan lain — unit yang sama tampil dengan dua nama berbeda
  di dua tab, tanpa satu pun galat yang menjelaskan mengapa.
*/

/** Nama yang ditampilkan untuk sebuah unit. */
export const unitLabel = (u) => u?.alias || u?.selfName || u?.unitId || '—';

/**
 * Keterangan kecil di bawah nama: apa yang TIDAK terpakai sebagai nama utama.
 *
 * Mengembalikan null bila tidak ada yang perlu disebutkan, supaya pemanggil
 * dapat menghilangkan barisnya sama sekali alih-alih menampilkan baris kosong.
 */
export const unitSubLabel = (u) => {
  if (!u) return null;

  // Unit id selalu disebut bila bukan ia yang tampil sebagai nama utama.
  // Asisten menelusuri masalah dengan nomor unit, bukan dengan nama panggilan.
  if (u.alias || u.selfName) return u.unitId;
  return null;
};

/** Nama diketik mahasiswa dan belum ditimpa asisten — dipakai untuk menandainya. */
export const isSelfNamed = (u) => Boolean(!u?.alias && u?.selfName);
