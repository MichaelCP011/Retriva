/*
  Pemeriksa sintaks JSX, dijalankan sebelum menyerahkan perubahan UI.

  ADA KARENA PEMERIKSAAN KURUNG TIDAK CUKUP.

  Tiga kali berturut-turut berkas UI diserahkan dalam keadaan tidak dapat
  di-build: komentar baris ganda di dalam tag, komentar JSX di posisi ekspresi,
  dan const yang dibaca sebelum dideklarasikan. Ketiganya lolos dari pemeriksaan
  jumlah kurung buka-tutup, dan dua di antaranya baru ketahuan saat orang lain
  menjalankan npm run build.

  Babel sudah ada di node_modules sebagai dependensi Vite, jadi ini tidak
  menambah apa pun untuk dipasang.

    node check-jsx.mjs
*/
import { parse } from '@babel/parser';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (p.endsWith('.jsx') || p.endsWith('.js')) out.push(p);
  }
  return out;
}

let bad = 0;
for (const file of walk('src')) {
  try {
    parse(readFileSync(file, 'utf8'), { sourceType: 'module', plugins: ['jsx'] });
  } catch (e) {
    bad++;
    console.log(`GALAT  ${file}:${e.loc?.line}:${e.loc?.column}  ${e.message.split('. (')[0]}`);
  }
}

console.log(bad === 0
  ? 'Semua berkas JSX/JS terurai bersih.'
  : `${bad} berkas bermasalah — npm run build akan gagal.`);

process.exit(bad === 0 ? 0 : 1);
