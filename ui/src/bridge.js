// Jembatan tunggal antara UI React dan host .NET.
//
// Semua komunikasi lewat sini, tidak ada komponen yang memanggil
// window.chrome.webview secara langsung. Dengan begitu UI tetap bisa
// dijalankan di browser biasa (npm run dev) memakai data tiruan.

const host = window.chrome?.webview;
export const isHosted = Boolean(host);

const listeners = new Map();

if (isHosted) {
  host.addEventListener('message', (event) => {
    const msg = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
    const handlers = listeners.get(msg.type);
    if (handlers) handlers.forEach((fn) => fn(msg));
  });
}

/** Mendaftarkan handler untuk satu jenis pesan dari host. Mengembalikan fungsi pembatal. */
export function on(type, handler) {
  if (!listeners.has(type)) listeners.set(type, new Set());
  listeners.get(type).add(handler);
  return () => listeners.get(type)?.delete(handler);
}

/** Mengirim pesan ke host .NET. Diabaikan diam-diam saat dijalankan di browser. */
export function send(type, payload = {}) {
  if (!isHosted) {
    console.info('[bridge] tidak ada host, pesan diabaikan:', type, payload);
    return;
  }
  host.postMessage(JSON.stringify({ type, ...payload }));
}

/** Data tiruan untuk pengembangan UI di browser tanpa menjalankan Retriva.Admin. */
export function mockUnits() {
  return Array.from({ length: 40 }, (_, i) => {
    const n = i + 1;
    const status =
      n % 9 === 0 ? 'Offline' : n % 7 === 0 ? 'Locked' : n === 13 ? 'Alert' : 'Online';
    return {
      unitId: `PC-${String(n).padStart(2, '0')}`,
      status,
      ipAddress: `192.168.1.${n}`,
      hostName: `LAB-PC-${String(n).padStart(2, '0')}`,

      // Satu unit sengaja tanpa MAC. Layar Konfigurasi menandai keadaan itu
      // karena Wake-on-LAN bergantung padanya, dan penandanya perlu ikut
      // teruji saat UI dikerjakan tanpa lab yang menyala.
      macAddress: n === 5 ? '' : `AA:BB:CC:00:${String(n).padStart(2, '0')}:01`,

      // Bahan peta jaringan. Satu unit nirkabel dan satu unit belum terukur
      // sengaja disisipkan: keduanya adalah keadaan yang ditandai peta, dan
      // penandanya perlu ikut teruji saat UI dikerjakan tanpa lab yang menyala.
      wired: n !== 17,
      linkSpeedMbps: n === 17 ? 144 : (n % 11 === 0 ? 100 : 1000),
      networkAdapter: n === 17 ? 'Intel Wi-Fi 6 AX201' : 'Realtek PCIe GbE',
      coordinatorHost: n > 30 ? '192.168.1.253' : '192.168.1.252',
      latencyMs: n % 13 === 0 ? null : 0.4 + (n % 7) * 1.8,

      lastSeen: new Date().toISOString(),
    };
  });
}
