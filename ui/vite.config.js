import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'path';

/*
  Dua halaman dari satu project: panel admin dan klien mahasiswa.

  Disatukan supaya keduanya memakai styles.css yang sama. Kalau dipisah jadi dua
  project, sistem desainnya akan bercabang pelan-pelan — satu sisi diperbaiki,
  sisi lain tertinggal — dan setahun lagi keduanya terlihat seperti dua aplikasi
  berbeda yang kebetulan bernama sama.

  Keluarannya ke dist/ di AKAR SOLUSI — sejajar dengan src/ dan ui/, bukan di
  dalam ui/. outDir di vite relatif terhadap root project (ui/), jadi '../dist'
  berarti satu tingkat di atasnya. Kedua csproj menyalin dari sana ke wwwroot
  masing-masing.
*/
export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: '../dist',
    emptyOutDir: true,
    rollupOptions: {
      input: {
        admin: resolve(__dirname, 'index.html'),
        client: resolve(__dirname, 'client.html'),
      },
    },
  },
  server: {
    port: 5173,
    strictPort: true,
  },
});
