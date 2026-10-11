import { ArrowSquareOut } from '@phosphor-icons/react';
import { send } from '../bridge';

/*
  Teks pesan dengan tautan yang dapat ditekan.

  TIDAK PERNAH TERBUKA SENDIRI, dan itu bukan kehati-hatian berlebihan.

  Asisten dapat mengirim pengumuman ke seluruh kelas. Kalau tautan di dalamnya
  terbuka otomatis, satu pesan akan membuka empat puluh peramban serentak di
  empat puluh PC — dan bila alamatnya salah tempel, empat puluh mahasiswa
  melihat halaman yang tidak seharusnya mereka lihat, tanpa satu pun dari mereka
  memilih untuk membukanya.

  Karena itu yang ditekan mahasiswa, dan alamat penuhnya terlihat sebelum ia
  menekan. Teks tautan yang berbeda dari tujuannya adalah bentuk penipuan paling
  tua di internet; di sini keduanya selalu satu dan sama, karena tautannya
  ditemukan dari teksnya sendiri, bukan dari markup.
*/

/*
  Hanya http dan https, dan hanya yang diawali skema.

  Menebak "www." tanpa skema terasa membantu sampai ia salah menebak: kalimat
  yang berisi "lihat di www.contoh.com/berkas" masih dapat ditangani, tetapi
  penulisan seperti "modul5.zip" atau "PC-01.local" juga menyerupai domain dan
  akan berubah menjadi tautan yang tidak pernah dimaksudkan siapa pun.

  Skema lain sengaja ditolak: file: membuka berkas lokal, dan skema aplikasi
  seperti ms-msdt: pernah menjadi jalan masuk eksekusi kode. Yang dibutuhkan
  praktikum hanyalah dua ini.
*/
const URL_PATTERN = /(https?:\/\/[^\s<>"')\]]+)/gi;

/** Membuang tanda baca di ujung yang hampir selalu milik kalimat, bukan alamat. */
function trimTrailingPunctuation(url) {
  return url.replace(/[.,;:!?]+$/, '');
}

/** Alamat panjang dipendekkan di tengah supaya domainnya tetap terbaca. */
function shorten(url) {
  if (url.length <= 52) return url;
  return `${url.slice(0, 34)}…${url.slice(-14)}`;
}

export default function LinkedText({ text }) {
  if (!text) return null;

  const parts = String(text).split(URL_PATTERN);

  return parts.map((part, i) => {
    // split dengan pola bertanda kurung menaruh hasil tangkapan di indeks ganjil.
    if (i % 2 === 0) return part;

    const url = trimTrailingPunctuation(part);
    const tail = part.slice(url.length);

    return (
      <span key={i}>
        <button className="cl-link" type="button"
          title={url}
          onClick={() => send('openLink', { url })}>
          {shorten(url)}<ArrowSquareOut size={11} />
        </button>
        {tail}
      </span>
    );
  });
}
