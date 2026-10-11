import { useEffect, useRef } from 'react';

/*
  Latar bergerak untuk tiga tempat: pita garis di belakang sapaan beranda,
  topologi bintang di belakang percakapan, dan orbit tata surya sebagai keadaan
  kosong area siaran.

  Disatukan dalam satu komponen karena seluruh bagian yang sulitnya sama —
  kesadaran devicePixelRatio, pembatasan laju, berhenti saat jendela tersembunyi,
  penghormatan pada prefers-reduced-motion, pembacaan warna dari token tema.
  Menulisnya dua kali berarti dua tempat yang harus diperbaiki setiap kali salah
  satunya keliru, dan yang kedua selalu terlupa.

  BATAS KINERJA, dipegang ketat karena ini berjalan di aplikasi yang dibuka
  berjam-jam, bukan di layar masuk yang sekejap:

    · 20 fps. Pada gerak selambat ini tidak ada yang terlihat lebih halus di
      atas itu, sementara biayanya berlipat.
    · Berhenti total saat jendela tidak terlihat.
    · Paling banyak dua kanvas hidup bersamaan, keduanya hanya di beranda.
      Tab yang tidak aktif dilepas React dari pohon, jadi latar percakapan
      tidak pernah berjalan bersama latar beranda.
    · Mati sepenuhnya bila pengguna meminta gerak minimal.
*/

/** Membaca token warna tema lalu memecahnya jadi komponen RGB. */
function readRgb(name, fallback) {
  const raw = getComputedStyle(document.documentElement)
    .getPropertyValue(name).trim() || fallback;

  const hex = raw.replace('#', '');
  if (hex.length === 6) {
    return [
      parseInt(hex.slice(0, 2), 16),
      parseInt(hex.slice(2, 4), 16),
      parseInt(hex.slice(4, 6), 16),
    ];
  }
  const m = raw.match(/(\d+)[,\s]+(\d+)[,\s]+(\d+)/);
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : [128, 128, 128];
}

export default function AmbientCanvas({ variant = 'topology', theme }) {
  const ref = useRef(null);

  useEffect(() => {
    const canvas = ref.current;
    if (!canvas) return undefined;

    if (window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches) return undefined;

    const ctx = canvas.getContext('2d', { alpha: true });
    if (!ctx) return undefined;

    const ink = readRgb('--ink-dim', '#7A7A7A');
    const accent = readRgb('--accent', '#FA0F00');
    const rgba = ([r, g, b], a) => `rgba(${r}, ${g}, ${b}, ${a})`;

    let width = 0;
    let height = 0;

    function resize() {
      const rect = canvas.getBoundingClientRect();
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      width = rect.width;
      height = rect.height;
      canvas.width = Math.max(1, Math.round(width * dpr));
      canvas.height = Math.max(1, Math.round(height * dpr));
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    resize();
    const observer = new ResizeObserver(resize);
    observer.observe(canvas);

    /* ---------------- topologi bintang ---------------- */

    /*
      Dua cincin yang berputar berlawanan arah dengan kecepatan berbeda.

      Arah berlawanan itu yang membuatnya terbaca sebagai mesin, bukan sebagai
      gambar yang diputar. Kecepatannya sangat lambat — satu putaran penuh lebih
      dari satu menit — supaya ia tidak pernah menarik perhatian dari teks yang
      sedang dibaca di atasnya.
    */
    const RINGS = [
      { count: 8, radius: 0.30, speed: 0.055, dot: 2.2 },
      { count: 12, radius: 0.52, speed: -0.032, dot: 1.8 },
    ];

    function drawTopology(t) {
      const cx = width / 2;
      const cy = height / 2;
      const base = Math.min(width, height);

      for (const ring of RINGS) {
        const r = base * ring.radius;

        for (let i = 0; i < ring.count; i++) {
          const angle = (i / ring.count) * Math.PI * 2 + t * ring.speed;
          const x = cx + Math.cos(angle) * r;
          const y = cy + Math.sin(angle) * r;

          ctx.beginPath();
          ctx.moveTo(cx, cy);
          ctx.lineTo(x, y);
          ctx.strokeStyle = rgba(ink, 0.055);
          ctx.lineWidth = 1;
          ctx.stroke();

          ctx.beginPath();
          ctx.arc(x, y, ring.dot, 0, Math.PI * 2);
          ctx.fillStyle = rgba(ink, 0.16);
          ctx.fill();
        }

        // Cincin tipis penghubung, memberi kesan struktur alih-alih titik lepas.
        ctx.beginPath();
        ctx.arc(cx, cy, r, 0, Math.PI * 2);
        ctx.strokeStyle = rgba(ink, 0.045);
        ctx.lineWidth = 1;
        ctx.stroke();
      }

      ctx.beginPath();
      ctx.arc(cx, cy, 3.5, 0, Math.PI * 2);
      ctx.fillStyle = rgba(accent, 0.32);
      ctx.fill();
    }

    /* ---------------- pita garis ---------------- */

    /*
      Puluhan garis tipis yang membentuk pita berpilin.

      Cara kerjanya sama dengan alat blend di Illustrator: dua kurva induk
      didefinisikan, lalu tiap garis adalah interpolasi di antara keduanya.
      Di tempat kedua induk berpotongan, seluruh garis berhimpit dan muncul
      titik jepit — dan justru jepitan itu yang membuat bentuknya terbaca
      sebagai pita berpilin, bukan sekadar tumpukan gelombang sejajar.

      Yang dianimasikan hanya fase kedua induk, dengan kecepatan berbeda.
      Titik jepitnya lalu bergeser sendiri menyusuri lebar layar, dan pitanya
      tampak berpilin tanpa satu pun perhitungan tiga dimensi.
    */
    const LINES = 34;
    const STEPS = 56;

    function drawRibbon(t) {
      const midY = height * 0.52;
      const amp = height * 0.30;

      // Dua kurva induk. Frekuensi sengaja tidak kelipatan bulat supaya
      // perpotongannya tidak pernah berulang di tempat yang sama.
      const parent = (x, phase, freq, lift) =>
        midY + Math.sin(x * freq + phase) * amp * lift;

      ctx.lineWidth = 0.7;

      for (let i = 0; i < LINES; i++) {
        const k = i / (LINES - 1);

        ctx.beginPath();
        for (let s = 0; s <= STEPS; s++) {
          const px = (s / STEPS) * width;
          const u = px / width;

          const a = parent(u, t * 0.42, Math.PI * 2.1, 1);
          const b = parent(u, -t * 0.29 + 1.7, Math.PI * 3.4, 0.72);

          // Interpolasi linear antara kedua induk. Inilah blend-nya.
          const y = a + (b - a) * k;

          if (s === 0) ctx.moveTo(px, y);
          else ctx.lineTo(px, y);
        }

        // Garis di tepi pita sedikit lebih pudar, memberi kesan volume tanpa
        // memakai bayangan atau gradien.
        const edge = Math.sin(k * Math.PI);
        ctx.strokeStyle = rgba(ink, 0.10 + edge * 0.20);
        ctx.stroke();
      }
    }

    /* ---------------- orbit tata surya ---------------- */

    /*
      Lima orbit sebidang, dilihat dari sudut rendah — bukan orbit atom yang
      saling menyilang. Bentuk sebidang jauh lebih tenang dan langsung terbaca
      sebagai tata surya.

      Kecepatannya mengikuti hukum ketiga Kepler: makin jauh orbitnya, makin
      lambat lajunya, sebanding dengan satu per akar jari-jari. Tidak ada yang
      akan menyadarinya secara sadar, tetapi mata mengenali gerak yang patuh
      pada aturan fisik sebagai "benar" — dan gerak yang seragam sebagai palsu.
    */
    const PLANETS = [
      { r: 0.20, size: 1.8, hero: false },
      { r: 0.34, size: 2.2, hero: false },
      { r: 0.50, size: 3.4, hero: true },   // satu yang mencolok
      { r: 0.68, size: 2.0, hero: false },
      { r: 0.88, size: 1.6, hero: false },
    ];

    function drawOrbit(t) {
      const cx = width / 2;
      const cy = height / 2;
      const base = Math.min(width * 0.44, height * 1.15);

      // Rasio elips rendah: sudut pandang hampir sejajar bidang orbit, yang
      // memberi kesan ruang tanpa perlu perspektif sungguhan.
      const flat = 0.26;

      for (const p of PLANETS) {
        const rx = base * p.r;
        const ry = rx * flat;

        ctx.beginPath();
        ctx.ellipse(cx, cy, rx, ry, 0, 0, Math.PI * 2);
        ctx.strokeStyle = rgba(ink, p.hero ? 0.20 : 0.11);
        ctx.lineWidth = p.hero ? 0.9 : 0.7;
        ctx.stroke();

        const speed = 0.42 / Math.sqrt(p.r);
        const a = t * speed;
        const x = cx + Math.cos(a) * rx;
        const y = cy + Math.sin(a) * ry;

        if (p.hero) {
          // Jejak pendek hanya pada planet utama. Memberi arah gerak, dan
          // menahan mata di satu tempat alih-alih menyebar ke lima titik.
          for (let s = 1; s <= 7; s++) {
            const back = a - s * 0.055;
            ctx.beginPath();
            ctx.arc(cx + Math.cos(back) * rx, cy + Math.sin(back) * ry,
                    p.size - s * 0.34, 0, Math.PI * 2);
            ctx.fillStyle = rgba(accent, 0.22 - s * 0.028);
            ctx.fill();
          }
        }

        ctx.beginPath();
        ctx.arc(x, y, p.size, 0, Math.PI * 2);
        ctx.fillStyle = p.hero ? rgba(accent, 0.85) : rgba(ink, 0.42);
        ctx.fill();
      }

      // Matahari. Kecil dan tenang — yang mencolok sudah dipegang satu planet,
      // dan dua titik terang sekaligus akan saling berebut.
      const pulse = (Math.sin(t * 1.1) + 1) / 2;

      ctx.beginPath();
      ctx.arc(cx, cy, 4.5 + pulse * 0.8, 0, Math.PI * 2);
      ctx.fillStyle = rgba(ink, 0.52);
      ctx.fill();

      ctx.beginPath();
      ctx.arc(cx, cy, 10 + pulse * 4, 0, Math.PI * 2);
      ctx.strokeStyle = rgba(ink, 0.13 - pulse * 0.06);
      ctx.lineWidth = 1;
      ctx.stroke();
    }

    /* ---------------- loop ---------------- */

    const start = performance.now();
    let raf = 0;
    let last = 0;
    let running = true;

    function frame(now) {
      if (!running) return;

      // 20 fps. Cukup untuk gerak selambat ini, dan sepertiga biaya 60 fps.
      if (now - last < 50) {
        raf = requestAnimationFrame(frame);
        return;
      }
      last = now;

      const t = (now - start) / 1000;
      ctx.clearRect(0, 0, width, height);

      if (variant === 'orbit') drawOrbit(t);
      else if (variant === 'ribbon') drawRibbon(t);
      else drawTopology(t);

      raf = requestAnimationFrame(frame);
    }

    raf = requestAnimationFrame(frame);

    function onVisibility() {
      if (document.hidden) {
        running = false;
        cancelAnimationFrame(raf);
      } else if (!running) {
        running = true;
        raf = requestAnimationFrame(frame);
      }
    }
    document.addEventListener('visibilitychange', onVisibility);

    return () => {
      running = false;
      cancelAnimationFrame(raf);
      observer.disconnect();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [variant, theme]);

  return <canvas className={`amb amb-${variant}`} ref={ref} aria-hidden="true" />;
}
