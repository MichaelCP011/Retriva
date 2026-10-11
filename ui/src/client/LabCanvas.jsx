import { useEffect, useRef } from 'react';

/*
  Peta laboratorium yang hidup.

  Ini bukan latar dekoratif. Yang digambar adalah susunan meja lab yang
  sebenarnya — 40 unit dalam lima baris — dengan garis yang menjalar dari
  Superadmin ke tiap unit saat sambungan terbentuk, dan unit tempat mahasiswa
  itu duduk menyala berdenyut.

  Perbedaannya dengan partikel melayang penting: partikel tidak berarti apa-apa
  dan siapa pun tahu itu tempelan. Peta ini memperlihatkan hal yang justru
  paling mahal dibangun di aplikasi ini — jaringan 40 mesin yang saling terhubung
  — kepada orang yang tidak akan pernah melihat kodenya.

  BATAS KINERJA YANG DIPEGANG KETAT:

  PC lab menjalankan agent, klien, dan penerima siaran sekaligus di i5-9400
  dengan GT 730. Karena itu animasi ini dibatasi 30 fps, berhenti total saat
  jendela tidak terlihat, dan tidak pernah berjalan setelah mahasiswa masuk —
  layar masuk bersifat sementara, dan animasi abadi di latar aplikasi yang
  dibuka berjam-jam adalah cara paling halus membuat komputer terasa lambat.
*/

const ROWS = 5;
const COLS = 8;

export default function LabCanvas({ unitId, connected }) {
  const ref = useRef(null);

  useEffect(() => {
    const canvas = ref.current;
    if (!canvas) return undefined;

    const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches;
    const ctx = canvas.getContext('2d', { alpha: true });
    if (!ctx) return undefined;

    /* ---------------- node ---------------- */

    // Nomor unit yang ditempati, dipakai untuk menandai satu node sebagai
    // "kamu di sini". Diambil dari "PC-07" -> 7.
    const mine = Number.parseInt(String(unitId ?? '').replace(/\D+/g, ''), 10);

    const nodes = [];
    for (let r = 0; r < ROWS; r++) {
      for (let c = 0; c < COLS; c++) {
        const n = r * COLS + c + 1;
        nodes.push({
          n,
          gx: c,
          gy: r,
          // Fase acak supaya denyutnya tidak serempak. Denyut serentak terbaca
          // sebagai animasi; denyut bergeser terbaca sebagai empat puluh mesin
          // yang masing-masing hidup sendiri.
          phase: Math.random() * Math.PI * 2,
          // Urutan menyala dibuat menyebar dari tengah, bukan berurutan nomor.
          delay: Math.random() * 0.55,
          mine: n === mine,
        });
      }
    }

    let width = 0;
    let height = 0;
    let dpr = 1;

    function resize() {
      const rect = canvas.getBoundingClientRect();
      dpr = Math.min(window.devicePixelRatio || 1, 2);
      width = rect.width;
      height = rect.height;
      canvas.width = Math.max(1, Math.round(width * dpr));
      canvas.height = Math.max(1, Math.round(height * dpr));
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    resize();
    const observer = new ResizeObserver(resize);
    observer.observe(canvas);

    /* ---------------- gambar ---------------- */

    const start = performance.now();
    let raf = 0;
    let lastDraw = 0;
    let running = true;

    function positionOf(node) {
      // Peta ditempatkan di bagian bawah supaya tidak bertabrakan dengan judul.
      const padX = width * 0.12;
      const padTop = height * 0.42;
      const padBottom = height * 0.14;

      const usableW = Math.max(1, width - padX * 2);
      const usableH = Math.max(1, height - padTop - padBottom);

      return {
        x: padX + (usableW * node.gx) / (COLS - 1),
        y: padTop + (usableH * node.gy) / (ROWS - 1),
      };
    }

    function hubPosition() {
      return { x: width / 2, y: height * 0.30 };
    }

    function draw(now) {
      if (!running) return;

      // Dibatasi 30 fps. Di atas itu tidak ada yang terlihat lebih halus pada
      // gerak selambat ini, sementara biayanya berlipat.
      if (now - lastDraw < 33) {
        raf = requestAnimationFrame(draw);
        return;
      }
      lastDraw = now;

      const t = (now - start) / 1000;
      ctx.clearRect(0, 0, width, height);

      const hub = hubPosition();

      // Garis digambar lebih dulu supaya node duduk di atasnya.
      for (const node of nodes) {
        const appear = Math.min(1, Math.max(0, (t - node.delay) / 1.1));
        if (appear <= 0) continue;

        const p = positionOf(node);
        const eased = 1 - Math.pow(1 - appear, 3);

        ctx.beginPath();
        ctx.moveTo(hub.x, hub.y);
        ctx.lineTo(
          hub.x + (p.x - hub.x) * eased,
          hub.y + (p.y - hub.y) * eased,
        );
        ctx.strokeStyle = node.mine
          ? `rgba(250, 15, 0, ${0.34 * eased})`
          : `rgba(255, 255, 255, ${0.075 * eased})`;
        ctx.lineWidth = node.mine ? 1.4 : 1;
        ctx.stroke();
      }

      for (const node of nodes) {
        const appear = Math.min(1, Math.max(0, (t - node.delay) / 1.1));
        if (appear <= 0) continue;

        const p = positionOf(node);
        const pulse = reduced ? 0.5 : (Math.sin(t * 1.5 + node.phase) + 1) / 2;

        if (node.mine) {
          // Cincin yang mengembang lalu memudar, menandai "kamu di sini".
          const ring = reduced ? 0 : (t * 0.7) % 1;
          if (!reduced) {
            ctx.beginPath();
            ctx.arc(p.x, p.y, 4 + ring * 22, 0, Math.PI * 2);
            ctx.strokeStyle = `rgba(250, 15, 0, ${(1 - ring) * 0.5 * appear})`;
            ctx.lineWidth = 1.4;
            ctx.stroke();
          }

          ctx.beginPath();
          ctx.arc(p.x, p.y, 4.5, 0, Math.PI * 2);
          ctx.fillStyle = `rgba(250, 15, 0, ${appear})`;
          ctx.fill();
        } else {
          const alpha = (0.2 + pulse * 0.32) * appear * (connected ? 1 : 0.55);
          ctx.beginPath();
          ctx.arc(p.x, p.y, 2.6, 0, Math.PI * 2);
          ctx.fillStyle = `rgba(255, 255, 255, ${alpha})`;
          ctx.fill();
        }
      }

      // Simpul pusat: Superadmin. Digambar terakhir supaya berada paling atas.
      const hubPulse = reduced ? 0.5 : (Math.sin(t * 1.1) + 1) / 2;
      ctx.beginPath();
      ctx.arc(hub.x, hub.y, 6 + hubPulse * 1.5, 0, Math.PI * 2);
      ctx.fillStyle = 'rgba(255, 255, 255, 0.92)';
      ctx.fill();

      ctx.beginPath();
      ctx.arc(hub.x, hub.y, 13 + hubPulse * 4, 0, Math.PI * 2);
      ctx.strokeStyle = `rgba(255, 255, 255, ${0.16 - hubPulse * 0.08})`;
      ctx.lineWidth = 1;
      ctx.stroke();

      raf = requestAnimationFrame(draw);
    }

    raf = requestAnimationFrame(draw);

    // Berhenti total saat jendela tidak terlihat. Tanpa ini, kanvas terus
    // menggambar di belakang jendela lain dan memakan CPU untuk sesuatu yang
    // tidak dilihat siapa pun.
    function onVisibility() {
      if (document.hidden) {
        running = false;
        cancelAnimationFrame(raf);
      } else if (!running) {
        running = true;
        raf = requestAnimationFrame(draw);
      }
    }
    document.addEventListener('visibilitychange', onVisibility);

    return () => {
      running = false;
      cancelAnimationFrame(raf);
      observer.disconnect();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [unitId, connected]);

  return <canvas className="cw-canvas" ref={ref} aria-hidden="true" />;
}
