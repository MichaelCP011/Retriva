// Tema disimpan lokal di WebView2 supaya pilihan bertahan antar sesi.
// Sengaja tidak lewat console.json: ini preferensi tampilan per operator,
// bukan konfigurasi laboratorium yang perlu dibagikan antar PC admin.
const KEY = 'retriva.theme';

export function readTheme() {
  try {
    const saved = localStorage.getItem(KEY);
    if (saved === 'dark' || saved === 'light') return saved;
  } catch { /* penyimpanan tidak tersedia */ }
  return 'light';
}

export function applyTheme(theme) {
  document.documentElement.setAttribute('data-theme', theme);
  try { localStorage.setItem(KEY, theme); } catch { /* diabaikan */ }
}
