import { useEffect, useMemo, useState } from 'react';
import {
  Play, StopCircle, AppWindow, Clipboard, Usb, ArrowClockwise, WarningCircle, CaretRight,
} from '@phosphor-icons/react';
import { send } from '../bridge';
import { unitLabel } from '../unitName';

/*
  Mode ujian: mulai sesi, dan baca lini masa per unit.

  CATATAN, BUKAN TUDUHAN — dan itu menentukan hampir seluruh keputusan tampilan
  di berkas ini.

  Retriva dapat mengetahui bahwa jendela aktif berganti ke Chrome. Ia tidak dapat
  mengetahui apakah itu menyontek: mahasiswa bisa saja membuka dokumentasi yang
  memang diizinkan, atau membaca soalnya sendiri di e-learning. Karena itu tidak
  ada satu pun peristiwa di layar ini yang diberi label "pelanggaran", tidak ada
  warna merah untuk peristiwa biasa, dan tidak ada skor kecurangan.

  Yang ditampilkan adalah jumlah dan urutan. Pengajar yang membacanya punya
  konteks yang tidak dimiliki perangkat lunak ini — soal apa yang diberikan, apa
  yang diizinkan, siapa yang sudah bertanya sebelumnya — dan penilaiannya
  miliknya.

  Angka peristiwa yang tinggi dicetak tebal supaya mudah ditemukan, bukan supaya
  terlihat bersalah.
*/

const KIND = {
  Window: { Icon: AppWindow, label: 'aplikasi' },
  Clipboard: { Icon: Clipboard, label: 'papan klip' },
  Usb: { Icon: Usb, label: 'penyimpanan' },
};

const clock = (iso) => {
  const d = new Date(iso);
  return Number.isNaN(d.getTime())
    ? '—'
    : d.toLocaleTimeString('id-ID', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
};

function Elapsed({ since }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);

  const total = Math.max(0, Math.floor((now - new Date(since).getTime()) / 1000));
  const h = String(Math.floor(total / 3600)).padStart(2, '0');
  const m = String(Math.floor((total % 3600) / 60)).padStart(2, '0');
  const s = String(total % 60).padStart(2, '0');
  return <span className="num">{h}:{m}:{s}</span>;
}

export default function ExamMode({ units, exam, timeline, can = () => true }) {
  const [title, setTitle] = useState('');
  const [picked, setPicked] = useState(null);

  const session = exam?.session ?? null;
  const counts = exam?.counts ?? {};
  const summaries = exam?.summaries ?? {};
  const allowed = can('ExamMode');

  /*
    Daftar unit ikut disegarkan berkala, bukan hanya saat layar dibuka.

    Ringkasan "terakhir" yang tidak pernah diperbarui akan membeku pada keadaan
    saat halaman dibuka. Asisten yang membiarkan layar ini terbuka sepanjang
    ujian akan membaca daftar yang sudah usang sejam, dan tidak ada apa pun di
    layar yang memberitahunya.
  */
  useEffect(() => {
    send('examRefresh');
    if (!session) return undefined;
    const t = setInterval(() => send('examRefresh'), 6000);
    return () => clearInterval(t);
  }, [session?.id]);

  // Unit yang dipilih diminta lini masanya, dan diminta ulang berkala selagi
  // sesi berjalan. Lini masa yang membeku pada saat dibuka akan terbaca sebagai
  // mahasiswa yang berhenti beraktivitas.
  useEffect(() => {
    if (!picked || !session) return undefined;
    send('examTimeline', { unitId: picked });
    const t = setInterval(() => send('examTimeline', { unitId: picked }), 4000);
    return () => clearInterval(t);
  }, [picked, session]);

  const online = useMemo(
    () => [...units].filter((u) => u.status !== 'Offline')
      .sort((a, b) => a.unitId.localeCompare(b.unitId, 'id', { numeric: true })),
    [units]);

  const events = timeline?.unitId === picked ? (timeline?.events ?? []) : [];
  const total = Object.values(counts).reduce((n, v) => n + v, 0);

  /*
    Unit yang TIDAK dipantau disebut terpisah.

    Pemantauan berjalan di RetrivaClient, bukan di agent — agent adalah layanan
    di Session 0 dan tidak dapat melihat jendela sesi pengguna. Jadi unit yang
    kliennya tidak berjalan tidak dipantau sama sekali, dan lini masanya kosong.

    Kosong terbaca seperti bersih. Itulah kesalahan pembacaan yang paling mahal
    di layar ini, dan satu-satunya cara mencegahnya adalah menyebutkannya.
  */
  const unwatched = session
    ? online.filter((u) => !(u.unitId in counts) && !u.studentName && !u.studentNim)
    : [];

  return (
    <div className="body">
      <div className="pane" style={{ marginBottom: 14 }}>
        {session ? (
          <>
            <div className="sec">
              <h2>{session.title}</h2>
              <span className="hint">
                dimulai {clock(session.startedAt)} oleh {session.startedBy} · berjalan <Elapsed since={session.startedAt} />
              </span>
              <div className="sp" />
              <button className="btn btn-quiet btn-sm" onClick={() => send('examRefresh')}>
                <ArrowClockwise size={13} />Segarkan
              </button>
              {/*
                  StopCircle, bukan Stop.

                  Ikon "Stop" milik Phosphor adalah persegi kosong bergaris
                  tipis. Pada ukuran 13 px di dalam tombol berisi, ia tidak
                  terbaca sebagai ikon sama sekali — bentuknya persis seperti
                  kotak yang muncul ketika sebuah glif gagal dimuat, jadi tombol
                  itu tampak seperti tombol yang ikonnya hilang. Ikonnya ada;
                  yang salah adalah pilihannya.
              */}
              {allowed && (
                <button className="btn btn-primary btn-sm" onClick={() => send('examEnd')}>
                  <StopCircle size={13} />Akhiri sesi
                </button>
              )}
            </div>

            <div className="net-metrics">
              <div className="metric">
                <div className="k">Unit dipantau</div>
                <div className="v">{online.length}</div>
              </div>
              <div className="metric">
                <div className="k">Peristiwa</div>
                <div className="v">{total}</div>
              </div>
              <div className="metric">
                <div className="k">Unit dengan catatan</div>
                <div className="v">{Object.keys(counts).length}</div>
              </div>
            </div>

            {unwatched.length > 0 && (
              <div className="net-notes">
                <div><WarningCircle size={13} />
                  {unwatched.length} unit belum ada catatan sama sekali. Bisa berarti
                  mahasiswanya tidak melakukan apa pun, bisa juga berarti RetrivaClient
                  tidak berjalan di unit itu — dan unit tanpa klien tidak dipantau:
                  {' '}{unwatched.slice(0, 6).map((u) => u.unitId).join(', ')}
                  {unwatched.length > 6 && ` +${unwatched.length - 6}`}
                </div>
              </div>
            )}
          </>
        ) : (
          <>
            <div className="sec">
              <h2>Belum ada sesi ujian</h2>
              <span className="hint">pemantauan hanya berjalan selama sesi aktif</span>
            </div>

            <div className="hint" style={{ marginBottom: 12, lineHeight: 1.75 }}>
              Selama sesi berjalan, tiap unit mencatat aplikasi yang dibuka, penyalinan
              ke papan klip, dan penyimpanan yang dicolokkan. Isi papan klip tidak
              pernah disimpan — hanya bahwa penyalinan terjadi dan berapa panjangnya.
              Mahasiswa melihat papan di layarnya yang menyebutkan sesi ini dan apa saja
              yang dicatat.
            </div>

            {allowed ? (
              <div className="row2">
                <div className="field">
                  <label>Nama sesi</label>
                  <input value={title} onChange={(e) => setTitle(e.target.value)}
                    onKeyDown={(e) => e.key === 'Enter' && title.trim() && send('examStart', { title })}
                    placeholder="Kuis Minggu 5" />
                </div>
                <div className="field" style={{ display: 'flex', alignItems: 'flex-end' }}>
                  <button className="btn btn-primary" disabled={!title.trim()}
                    onClick={() => send('examStart', { title })}>
                    <Play size={13} />Mulai sesi
                  </button>
                </div>
              </div>
            ) : (
              <div className="hint warn">
                Hanya panel dengan wewenang mode ujian yang dapat memulai sesi.
              </div>
            )}
          </>
        )}
      </div>

      {session && (
        <div className="two">
          <div className="pane">
            <h3>Unit <span className="count">{online.length} aktif</span></h3>

            {online.length === 0 ? (
              <div className="hint">Tidak ada unit tersambung.</div>
            ) : (
              <table className="table">
                <thead>
                  <tr>
                    <th>Unit</th>
                    <th>Terakhir</th>
                    <th style={{ textAlign: 'right' }}>Peristiwa</th>
                  </tr>
                </thead>
                <tbody>
                  {online.map((u) => {
                    const s = summaries[u.unitId];
                    const n = s?.count ?? counts[u.unitId] ?? 0;
                    const meta = KIND[s?.lastKind] ?? KIND.Window;
                    const Icon = meta.Icon;

                    return (
                      <tr key={u.unitId}
                        className={picked === u.unitId ? 'row-on' : undefined}
                        style={{ cursor: 'pointer' }}
                        onClick={() => setPicked(u.unitId)}>
                        <td>
                          <div className="mono">{unitLabel(u)}</div>
                          <div style={{ fontSize: 11.5, color: 'var(--ink-dim)' }}>
                            {u.studentName || 'belum masuk'}
                            {u.isGuest && ' · tamu'}
                          </div>
                        </td>

                        {/*
                            RINGKASAN DI DAFTAR, bukan hanya angka.

                            "PC-07 · 23" tidak memberi tahu apa pun tentang dua
                            puluh tiga peristiwa itu. Membacanya berarti
                            mengklik unitnya, membaca, kembali, lalu mengulangi
                            untuk unit berikutnya — empat puluh kali, selagi
                            ujian berjalan.

                            Peristiwa terakhirnya cukup untuk memindai
                            daftarnya, dan lini masa lengkap tetap satu klik di
                            sebelahnya untuk unit yang memang perlu ditelusuri.
                        */}
                        <td style={{ fontSize: 12, maxWidth: 260 }}>
                          {s ? (
                            <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
                              <Icon size={13} color="var(--ink-muted)" />
                              <span style={{
                                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                              }}>
                                {s.lastDescription}
                              </span>
                              <span style={{ color: 'var(--ink-dim)', flexShrink: 0 }}>
                                {clock(s.lastSeenAt)}
                              </span>
                            </div>
                          ) : (
                            <span style={{ color: 'var(--ink-dim)' }}>belum ada catatan</span>
                          )}
                        </td>

                        {/* Tebal supaya mudah ditemukan, bukan merah supaya
                            terlihat bersalah. Angka tinggi belum berarti apa-apa
                            sampai ada yang membaca lini masanya. */}
                        <td className="mono" style={{ textAlign: 'right', fontWeight: n > 0 ? 600 : 400 }}>
                          {n}
                          <CaretRight size={11} color="var(--ink-dim)"
                            style={{ marginLeft: 6, verticalAlign: 'middle' }} />
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            )}
          </div>

          <div className="pane">
            <h3>
              Lini masa
              {picked && <span className="count">{picked}</span>}
            </h3>

            {!picked ? (
              <div className="hint">Pilih unit di sebelah kiri untuk melihat catatannya.</div>
            ) : events.length === 0 ? (
              <div className="hint">
                Belum ada catatan untuk {picked} dalam sesi ini. Bisa berarti tidak ada
                aktivitas yang dicatat, bisa juga berarti RetrivaClient tidak berjalan di
                unit itu.
              </div>
            ) : (
              <div className="exam-timeline">
                {events.map((e, i) => {
                  const meta = KIND[e.kind] ?? KIND.Window;
                  const Icon = meta.Icon;
                  return (
                    <div className="exam-item" key={i}>
                      <div className="exam-item-icon"><Icon size={13} /></div>
                      <div className="exam-item-text">
                        <b>{e.description}</b>
                        <span>{meta.label} · {clock(e.detectedAt)}</span>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
