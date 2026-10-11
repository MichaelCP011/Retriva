import { useEffect, useMemo, useState } from 'react';
import {
  Monitor, FrameCorners, AppWindow, Play, StopCircle, Snowflake,
  MagnifyingGlass, ArrowClockwise, Users,
} from '@phosphor-icons/react';
import { send } from '../bridge';
import { unitLabel } from '../unitName';

const LABEL = { Online: 'Aktif', Offline: 'Mati', Locked: 'Terkunci', Alert: 'Peringatan' };

/*
  Pemilih sumber tampilan.

  Sebelumnya semua sumber — layar, monitor, dan puluhan window — ditumpuk
  dalam satu daftar radio. Bentuk itu memperlakukan "seluruh layar" setara
  dengan window ke-tiga puluh, padahal hampir setiap siaran memakai pilihan
  pertama. Yang sering dipakai jadi harus dicari.

  Karena itu pilihannya dipecah dua tingkat: tiga tombol besar untuk mode,
  lalu pemilih rinci yang hanya muncul bila modenya memang butuh. Mode
  "Seluruh layar" selesai dalam satu klik; daftar window yang panjang hanya
  terlihat oleh orang yang memang mencarinya.
*/
export default function ScreenShare({
  units, broadcasting, setBroadcasting, preview, stats, watching, sources = [], info, labState,
  can = () => true,
}) {
  const [mode, setMode] = useState('screen');   // screen | app | student

  /*
    Unit yang layarnya akan disiarkan, saat mode Mahasiswa.

    Dideklarasikan DI SINI bersama state lain, bukan di dekat tempat
    pemakaiannya. Versi sebelumnya meletakkannya di tengah berkas, di bawah
    MODES yang sudah membaca onlineUnits — dan const dalam JavaScript tidak
    dapat dibaca sebelum barisnya dijalankan.

    Akibatnya bukan peringatan melainkan ReferenceError saat render, dan React
    yang gagal render menampilkan halaman putih kosong tanpa satu pun pesan di
    layar. Log jaringan tetap bersih, karena memang tidak ada yang salah di
    jaringan.
  */
  const [presenter, setPresenter] = useState(null);
  const [appIndex, setAppIndex] = useState(null);

  /** Unit yang sedang tersambung; dipakai pemilih Mahasiswa dan label mode. */
  const onlineUnits = units.filter((u) => u.status !== 'Offline');
  const [query, setQuery] = useState('');
  const [resolution, setResolution] = useState('1080');
  const [fps, setFps] = useState('15');

  /*
    Kualitas JPEG. Batas atas 98, bukan 100.

    Di atas sekitar 95, JPEG hampir tidak menambah detail yang terlihat
    sementara ukurannya naik tajam; pada 100 ia berhenti membuang informasi
    sama sekali dan berkasnya membengkak berkali lipat untuk perbedaan yang
    tidak dapat dilihat di layar proyektor.
  */
  const [quality, setQuality] = useState('92');
  const [frozen, setFrozen] = useState(false);

  /*
    Yang disimpan adalah unit yang DIKECUALIKAN, bukan yang dipilih.

    Kalau yang disimpan daftar pilihan, unit yang baru menyala tidak akan ada
    di dalamnya dan otomatis terlewat dari siaran — padahal harapan wajarnya
    adalah semua ikut kecuali yang sengaja dilepas.
  */
  const [excluded, setExcluded] = useState(() => new Set());

  const online = units.filter((u) => u.status !== 'Offline');
  const receivers = online.filter((u) => !excluded.has(u.unitId));
  const allSelected = receivers.length === online.length;

  function toggle(unitId) {
    setExcluded((prev) => {
      const next = new Set(prev);
      if (next.has(unitId)) next.delete(unitId); else next.add(unitId);
      return next;
    });
  }
  function toggleAll() {
    setExcluded(allSelected ? new Set(online.map((u) => u.unitId)) : new Set());
  }

  const screen = sources.find((s) => s.kind === 'FullScreen') ?? null;
  const monitors = useMemo(() => sources.filter((s) => s.kind === 'Monitor'), [sources]);
  const apps = useMemo(() => sources.filter((s) => s.kind === 'Window'), [sources]);

  const visibleApps = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q ? apps.filter((a) => a.label.toLowerCase().includes(q)) : apps;
  }, [apps, query]);

  // Indeks yang benar-benar dikirim ke host. Bila mode butuh pilihan rinci
  // tetapi belum ada yang dipilih, nilainya null — dan tombol mulai dikunci,
  // supaya tidak ada siaran yang berjalan dengan sumber yang tidak disengaja.
  // Mode student tidak punya sumber tangkapan di panel ini — yang menangkap
  // adalah unit mahasiswanya sendiri.
  const chosen = mode === 'screen' ? screen
    : mode === 'student' ? null
      : (apps.find((a) => a.index === appIndex) ?? null);

  /*
    Pratinjau sumber dinyalakan setiap pilihan berubah.

    Tanpa ini, pemilih sumber meminta orang memilih tanpa melihat: nama window
    saja tidak cukup untuk membedakan yang berisi slide dari yang berisi catatan
    pribadi. Menyalakan siaran dulu untuk memeriksanya berarti seluruh kelas
    terlanjur melihat apa pun yang salah dipilih.
  */
  useEffect(() => {
    if (broadcasting) return undefined;
    if (!chosen) { send('previewStop'); return undefined; }

    send('previewSource', { sourceIndex: chosen.index });
    return () => send('previewStop');
  }, [chosen?.index, broadcasting]);

  /*
    Monitor DIGANTI Mahasiswa.

    Memilih monitor tertentu nyaris tidak pernah dipakai: dosen dengan dua layar
    hampir selalu ingin yang sedang ia pandangi, dan "Seluruh layar" sudah
    memberikan itu. Sementara menyiarkan layar mahasiswa adalah hal yang tidak
    dapat dilakukan sama sekali sebelumnya — satu-satunya jalannya memindahkan
    kabel proyektor ke mejanya.

    Tiga pilihan tetap tiga; yang berubah kegunaannya.
  */
  const MODES = [
    { id: 'screen', icon: FrameCorners, title: 'Seluruh layar', note: 'Semua yang terlihat' },
    { id: 'app', icon: AppWindow, title: 'Aplikasi', note: `${apps.length} jendela` },
    { id: 'student', icon: Users, title: 'Mahasiswa', note: `${onlineUnits.length} unit aktif` },
  ];

  function start() {
    /*
      Presentasi mahasiswa memakai jalur yang berbeda.

      Pada siaran biasa, panel ini yang menangkap layar. Di sini panel tidak
      menangkap apa pun — ia menyuruh unit sumber mengalirkan layarnya ke relay,
      lalu meminta unit lain membuka penontonnya.
    */
    if (mode === 'student') {
      if (!presenter) return;
      send('broadcastUnit', { unitId: presenter });
      setBroadcasting(true); setFrozen(false);
      return;
    }

    if (!chosen || receivers.length === 0) return;
    send('broadcastStart', {
      sourceIndex: chosen.index,
      resolution: Number(resolution),
      fps: Number(fps),
      quality: Number(quality),
      // Dikirim hanya bila memang sebagian. Null berarti semua unit online,
      // termasuk yang menyala setelah siaran dimulai.
      targets: allSelected ? null : receivers.map((u) => u.unitId),
    });
    setBroadcasting(true); setFrozen(false);
  }
  function stop() { send('broadcastStop'); setBroadcasting(false); setFrozen(false); }
  function freeze() { send('broadcastFreeze', { frozen: !frozen }); setFrozen(!frozen); }

  /*
    Siaran milik panel LAIN, ditampilkan sebelum tombol apa pun ditekan.

    Superadmin hanya mengizinkan satu sumber pada satu waktu. Tanpa keterangan
    ini, dosen menekan "Mulai siaran" lalu menerima penolakan tanpa mengerti
    sebabnya — dan yang tersedia hanya menekannya lagi.
  */
  const heldByOther = labState?.active && !broadcasting;



  /*
    Unit yang akan membuka siaran layar penuh — biasanya PC proyektor.

    Ditampilkan DI SINI, bukan hanya di layar detail unit tempat setelannya
    dinyalakan. Pertanyaan "apakah proyektor akan menyala" muncul tepat pada
    saat dosen hendak menekan Mulai siaran, dan jawabannya tidak boleh menuntut
    ia membuka layar lain untuk memeriksanya satu per satu.

    Kalau kosong, keterangannya menyebut ke mana harus pergi. Setelan yang tidak
    dapat ditemukan sama saja dengan setelan yang tidak ada.
  */
  const projectors = units.filter((u) => u.autoFullscreen);

  return (
    <div className="body">
      {/*
        Jumlah penerima ditampilkan selagi MENYIARKAN, bukan hanya saat panel
        lain yang menyiarkan.

        Ini pertanyaan yang paling ingin dijawab dosen di depan kelas, dan
        angkanya sekarang berasal dari unit yang benar-benar melaporkan telah
        merakit frame utuh — bukan dari tebakan sisi pengirim.
      */}
      {broadcasting && labState?.active && (
        <div className={`notice${labState.receivers > 0 ? ' ok' : ''}`}>
          <b>{labState.receivers ?? 0} unit menerima siaran</b>
          {labState.mbps > 0 ? ` · ${labState.mbps} Mbps` : ''}
          {labState.receivers === 0 && ' — belum ada unit yang melaporkan frame utuh.'}
        </div>
      )}

      {can('ManageUnits') && (
        <div className={`notice${projectors.length > 0 ? ' ok' : ''}`}>
          {projectors.length > 0 ? (
            <>
              <b>Layar penuh otomatis:</b>{' '}
              {projectors.map(unitLabel).join(', ')} — unit ini
              membuka siaran sendiri begitu dimulai.
            </>
          ) : (
            <>
              <b>Belum ada unit proyektor.</b> Bila proyektor tercolok ke sebuah PC,
              buka unit itu dari Beranda lalu centang &ldquo;Buka siaran layar penuh
              otomatis&rdquo; — siaran akan terbuka sendiri di sana tanpa memindahkan
              kabel HDMI.
            </>
          )}
        </div>
      )}

      {heldByOther && (
        <div className="notice">
          <b>{labState.heldBy}</b> sedang menyiarkan
          {labState.sourceLabel ? ` — ${labState.sourceLabel}` : ''}
          {labState.mbps > 0 ? ` · ${labState.mbps} Mbps` : ''}.
          Hentikan siarannya dulu, atau minta ia berhenti.
        </div>
      )}

      <div className="two">
        <div>
          <div className="pane" style={{ marginBottom: 16 }}>
            <div className="sec">
              <h2>Sumber tampilan</h2>
              <div className="sp" />
              <button className="btn btn-quiet btn-sm" disabled={broadcasting}
                onClick={() => send('listSources')}>
                <ArrowClockwise size={14} />Muat ulang
              </button>
            </div>

            <div className="modes">
              {MODES.map((m) => {
                const Icon = m.icon;
                const active = mode === m.id;
                return (
                  <button key={m.id} type="button"
                    className={`mode${active ? ' on' : ''}`}
                    disabled={broadcasting}
                    onClick={() => setMode(m.id)}>
                    <Icon size={22} weight={active ? 'fill' : 'regular'} />
                    <b>{m.title}</b>
                    <span>{m.note}</span>
                  </button>
                );
              })}
            </div>

            {mode === 'student' && (
              <div className="picker col">
                {onlineUnits.length === 0
                  ? <div className="empty" style={{ padding: 14 }}>Belum ada unit tersambung.</div>
                  : onlineUnits.map((u) => (
                    <button key={u.unitId} type="button" disabled={broadcasting}
                      className={`chip${presenter === u.unitId ? ' on' : ''}`}
                      onClick={() => setPresenter(u.unitId)}>
                      <Users size={14} />
                      {u.studentName || unitLabel(u)}
                      <span className="s">{u.studentName ? unitLabel(u) : u.unitId}</span>
                    </button>
                  ))}

                <div className="hint" style={{ marginTop: 8 }}>
                  Layar mahasiswa yang dipilih dialirkan ke Superadmin, lalu
                  dipancarkan ke unit lain. Mahasiswa itu melihat papan pemberitahuan
                  selama layarnya ditampilkan, dan unitnya sendiri tidak ikut membuka
                  penonton.
                </div>
              </div>
            )}

            {mode === 'app' && (
              <div className="picker col">
                <div className="search">
                  <MagnifyingGlass size={14} />
                  <input value={query} disabled={broadcasting}
                    placeholder="Cari jendela aplikasi…"
                    onChange={(e) => setQuery(e.target.value)} />
                </div>
                <div className="applist">
                  {visibleApps.length === 0
                    ? <div className="empty" style={{ padding: 14 }}>
                        {apps.length === 0 ? 'Belum ada jendela terdaftar.' : 'Tidak ada yang cocok.'}
                      </div>
                    : visibleApps.map((a) => (
                      <button key={a.index} type="button" disabled={broadcasting}
                        className={`chip wide${(chosen?.index === a.index) ? ' on' : ''}`}
                        onClick={() => setAppIndex(a.index)}>
                        <AppWindow size={14} />
                        <span className="nm">{a.label}</span>
                        <span className="s">{a.size}</span>
                      </button>
                    ))}
                </div>
                <div className="note">
                  Jendela yang diminimalkan tidak dapat ditangkap. Pastikan aplikasinya
                  terbuka sebelum siaran dimulai.
                </div>
              </div>
            )}
          </div>

          <div className="pane">
            <h3>Pratinjau</h3>
            <div className="stage">
              {broadcasting
                ? <div className="live"><b />{frozen ? `BEKU · ${receivers.length} PC` : `LIVE · ${receivers.length} PC`}</div>
                : <div className="live quiet"><b />siaran belum dimulai</div>}

              {/* Pratinjau menampilkan frame yang BENAR-BENAR dikirim.
                  Kotak berisi teks tidak membedakan siaran yang berjalan dari
                  yang gagal — dan perbedaan itu justru yang paling perlu terlihat. */}
              {preview
                ? <img src={preview} alt="Pratinjau siaran"
                    style={{ width: '100%', height: '100%', objectFit: 'contain' }} />
                : <div className="preview-ph">
                    {chosen ? chosen.label : 'Pilih sumber tampilan di atas'}
                  </div>}
            </div>

            {/* Persegi tangkapan yang sebenarnya. Ketika hasil siaran tidak
                sesuai pilihan, angka ini yang menunjukkan apakah salahnya di
                pemilihan sumber atau di koordinat tangkapan. */}
            {broadcasting && (info || stats) && (
              <div className="diag">
                {info && <><span>{info.kind}</span><span className="nm">{info.target}</span></>}
                {stats?.rect && <span>{stats.rect}</span>}
                {stats?.minimized && <span className="warn">jendela diminimalkan — gambar tertahan</span>}
                {stats?.method && (
                  <span className={stats.method.startsWith('jendela') ? 'ok' : 'warn'}>
                    via {stats.method}
                  </span>
                )}
              </div>
            )}

            {/*
                Presentasi mahasiswa diukur di PENERIMA, bukan di pengirim.

                Panel tidak menangkap apa pun di sini, jadi "frame terkirim"
                dan "Mbps keluar" tidak ada. Menampilkan baris pengirim yang
                berisi nol untuk siaran yang berjalan lancar adalah kebohongan
                yang persis membalik kesimpulan pembacanya.
            */}
            {broadcasting && watching && (
              <div style={{ fontFamily: 'var(--mono)', fontSize: 11.5,
                color: 'var(--ink-muted)', marginBottom: 12 }}>
                {watching.framesSeen > 0
                  ? `${watching.framesSeen} frame diterima · ${watching.packetsSeen} paket · terpantau dari relay`
                  : 'menunggu frame pertama dari unit sumber'}
              </div>
            )}

            {broadcasting && stats && (
              <div style={{ fontFamily: 'var(--mono)', fontSize: 11.5,
                color: 'var(--ink-muted)', marginBottom: 12 }}>
                {stats.framesSent} frame · {stats.actualFps?.toFixed(1)} fps ·
                {/* Mbps sesungguhnya, bukan perkiraan. Inilah angka yang
                    menentukan apakah kualitas yang dipilih sanggup dipikul
                    jaringan — dan satu-satunya cara menakarnya tanpa menebak. */}
                {' '}{stats.framesSent > 0 && stats.actualFps > 0
                  ? ((stats.bytesSent / stats.framesSent) * stats.actualFps * 8 / 1e6).toFixed(1)
                  : '0.0'} Mbps ·
                {' '}{(stats.bytesSent / 1024 / 1024).toFixed(1)} MB ·
                {' '}{stats.sendErrors} error
              </div>
            )}

            <div className="row3" style={{ marginBottom: 12 }}>
              <div className="field">
                <label>Resolusi</label>
                <select value={resolution} onChange={(e) => setResolution(e.target.value)} disabled={broadcasting}>
                  <option value="480">480p</option>
                  <option value="720">720p (HD)</option>
                  <option value="1080">1080p (Full HD)</option>
                </select>
              </div>
              <div className="field">
                <label>Refresh rate</label>
                <select value={fps} onChange={(e) => setFps(e.target.value)} disabled={broadcasting}>
                  <option value="10">10 fps</option>
                  <option value="15">15 fps</option>
                  <option value="20">20 fps</option>
                </select>
              </div>
              <div className="field">
                <label>Kualitas</label>
                <select value={quality} onChange={(e) => setQuality(e.target.value)} disabled={broadcasting}>
                  <option value="70">Hemat (70)</option>
                  <option value="80">Standar (80)</option>
                  <option value="92">Tinggi (92)</option>
                  <option value="98">Maksimum (98)</option>
                </select>
              </div>
            </div>

            <div style={{ display: 'flex', gap: 8 }}>
              {/* Mode Mahasiswa punya syarat sendiri: yang perlu dipilih adalah
                  unit sumbernya, bukan sumber tangkapan di panel ini. */}
              {!broadcasting ? (
                <button className="btn btn-primary" onClick={start}
                  disabled={mode === 'student'
                    ? !presenter
                    : (!chosen || receivers.length === 0)}>
                  <Play size={15} />Mulai siaran
                </button>
              ) : (
                <>
                  <button className="btn btn-primary" onClick={stop}><StopCircle size={15} />Hentikan siaran</button>
                  <button className="btn" onClick={freeze}>
                    <Snowflake size={15} />{frozen ? 'Lanjutkan' : 'Bekukan tampilan'}
                  </button>
                </>
              )}
            </div>
            <div style={{ fontSize: 11.5, color: 'var(--ink-dim)', marginTop: 10 }}>
              {mode === 'app'
                ? 'Hanya jendela terpilih yang disiarkan — panel Retriva tidak ikut terlihat.'
                : 'Jendela Retriva ditutup otomatis dari siaran, seperti OBS.'}
            </div>
          </div>
        </div>

        <div className="pane">
          <div className="sec">
            <h2>Penerima</h2>
            <span className="hint">{receivers.length} dari {online.length} aktif</span>
            <div className="sp" />
            <button className="btn btn-quiet btn-sm" disabled={broadcasting || online.length === 0}
              onClick={toggleAll}>
              {allSelected ? 'Kosongkan' : 'Pilih semua'}
            </button>
          </div>

          <div className="targets">
            {units.length === 0 ? (
              <div className="empty" style={{ padding: '20px 12px' }}>Belum ada unit tersambung.</div>
            ) : units.map((u) => {
              const off = u.status === 'Offline';
              return (
                <label className="trow" key={u.unitId}
                  style={{ cursor: off || broadcasting ? 'default' : 'pointer' }}>
                  <input type="checkbox" disabled={off || broadcasting}
                    checked={!off && !excluded.has(u.unitId)}
                    onChange={() => toggle(u.unitId)} />
                  <span className={`st plain ${u.status}`} />
                  <span className="id">{u.unitId}</span>
                  <span className="s">{LABEL[u.status] ?? u.status}</span>
                </label>
              );
            })}
          </div>

          <div style={{ fontSize: 11.5, color: 'var(--ink-dim)', marginTop: 10, lineHeight: 1.5 }}>
            Aliran multicast tetap satu untuk semua, jadi memilih sebagian tidak
            menghemat bandwidth — yang dibatasi adalah siapa yang jendelanya terbuka.
          </div>
        </div>
      </div>
    </div>
  );
}
