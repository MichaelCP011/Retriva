/** Waktu relatif ringkas: "baru saja", "12 dtk", "3 mnt", "2 jam". */
export function sinceLabel(iso) {
  if (!iso) return '—';
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 5) return 'baru saja';
  if (seconds < 60) return `${Math.floor(seconds)} dtk lalu`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} mnt lalu`;
  return `${Math.floor(seconds / 3600)} jam lalu`;
}
