import { useEffect, useRef, useState } from 'react';
import {
  ArrowLeft, LockKey, LockKeyOpen, CursorClick, Eye,
  ChatTeardropText, ArrowClockwise, Power, XCircle, CornersOut, CornersIn, WarningDiamond,
} from '@phosphor-icons/react';
import { send } from '../bridge';

/*
  Koordinat dikirim sebagai pecahan per-sepuluh-ribu, bukan piksel.

  Panel ini melihat JPEG yang sudah diperkecil dan tidak tahu resolusi fisik
  layar mahasiswa. Mengirim piksel berarti menebak resolusi itu, dan tebakan
  tersebut salah pada layar dengan penskalaan DPI berbeda. Dengan pecahan,
  klien yang melakukan konversi — dan hanya klien yang benar-benar tahu ukuran
  layarnya sendiri.
*/
function fractionOf(event, element) {
  const box = element.getBoundingClientRect();
  if (box.width <= 0 || box.height <= 0) return null;
  return {
    x: Math.round(Math.min(1, Math.max(0, (event.clientX - box.left) / box.width)) * 10000),
    y: Math.round(Math.min(1, Math.max(0, (event.clientY - box.top) / box.height)) * 10000),
  };
}

export default function RemoteControl({ unit, thumb, activity = [], remote, thread = [],
  onOpenChat, onBack, can = () => true }) {
  /*
    Nama pilihan unit, disunting di tempat.

    Disimpan hanya saat Enter atau saat fokus lepas, bukan pada setiap ketikan:
    menyimpan per huruf berarti empat puluh perjalanan ke koordinator untuk satu
    nama, dan jejak audit yang berisi "PC-01 diberi nama P", "Pa", "Pak"…
  */
  const [aliasDraft, setAliasDraft] = useState(unit?.alias ?? '');

  useEffect(() => { setAliasDraft(unit?.alias ?? ''); }, [unit?.unitId, unit?.alias]);

  function saveAlias() {
    const next = aliasDraft.trim();
    if (next === (unit?.alias ?? '')) return;
    send('unitAlias', { unitId: unit.unitId, alias: next });
  }
  // Status terkunci berasal dari heartbeat agent, bukan dari tebakan UI.
  // Nilai lokal hanya menjembatani jeda beberapa detik sampai denyut berikutnya,
  // supaya tombol terasa responsif tanpa berbohong tentang keadaan sebenarnya.
  const [locked, setLocked] = useState(unit?.status === 'Locked');
  useEffect(() => {
    if (unit) setLocked(unit.status === 'Locked');
  }, [unit?.status, unit?.unitId]);
  const [showChat, setShowChat] = useState(false);
  const [draft, setDraft] = useState('');

  // Percakapan diambil dari sumber yang sama dengan halaman Pesan. Menyimpan
  // salinannya di sini berarti dua tempat menampilkan riwayat berbeda untuk
  // mahasiswa yang sama, dan yang mana yang benar jadi tidak jelas.
  useEffect(() => {
    if (showChat && unit) onOpenChat?.(unit.unitId);
  }, [showChat, unit?.unitId]);

  const [full, setFull] = useState(false);
  const stageRef = useRef(null);
  const lastMove = useRef(0);

  const live = remote && unit && remote.unitId === unit.unitId ? remote : null;
  const controlling = live?.allowInput === true;

  // Pastikan pelepasan mouse selalu terkirim meski kursor bergerak keluar panggung saat menyeret
  useEffect(() => {
    if (!controlling) return undefined;
    const onWindowMouseUp = (e) => {
      if (stageRef.current) {
        const point = fractionOf(e, stageRef.current);
        if (point) input({ kind: 'MouseUp', ...point, button: e.button });
      }
    };
    window.addEventListener('mouseup', onWindowMouseUp);
    return () => window.removeEventListener('mouseup', onWindowMouseUp);
  }, [controlling]);

  // Fokus dipindah ke panggung begitu kendali aktif, kalau tidak ketikan pertama
  // masuk ke dokumen dan hilang tanpa jejak.
  useEffect(() => {
    if (controlling) stageRef.current?.focus();
  }, [controlling, full]);

  // Layar penuh dilepas otomatis saat sesi berakhir. Tanpa ini panel tertinggal
  // menutupi seluruh jendela sambil menampilkan frame beku dari sesi yang mati.
  useEffect(() => {
    if (!live) setFull(false);
  }, [live]);

  // Esc keluar dari layar penuh. Tombol itu tidak diteruskan ke mahasiswa,
  // karena jalan keluar bagi admin harus selalu ada di bawah jarinya.
  useEffect(() => {
    if (!full) return undefined;
    const onEsc = (e) => { if (e.key === 'Escape') setFull(false); };
    window.addEventListener('keydown', onEsc, true);
    return () => window.removeEventListener('keydown', onEsc, true);
  }, [full]);

  if (!unit) {
    return (
      <div className="body">
        <div className="pane">
          <div className="empty">
            Belum ada unit dipilih.<br />
            Pilih salah satu unit di dashboard untuk membuka sesi kontrol.
          </div>
          <div style={{ textAlign: 'center' }}>
            <button className="btn" onClick={onBack}><ArrowLeft size={15} />Kembali ke dashboard</button>
          </div>
        </div>
      </div>
    );
  }

  function toggleLock() {
    send(locked ? 'unlock' : 'lock', { unitId: unit.unitId });
    setLocked(!locked);
  }
  function sendChat() {
    if (!draft.trim()) return;
    send('chatSend', { unitId: unit.unitId, text: draft.trim() });
    setDraft('');
  }

  /* ---------------- input jarak jauh ---------------- */

  function input(payload) {
    if (!controlling) return;
    send('remoteInput', payload);
  }

  function onMouseMove(event) {
    // Dibatasi sekitar 50 peristiwa per detik. Browser dapat memancarkan
    // mousemove jauh lebih rapat daripada itu, dan setiap peristiwa menempuh
    // jembatan WebView2, hub SignalR, pipa bernama, lalu SendInput — laju
    // penuh hanya menambah antrean tanpa menambah ketepatan.
    const now = performance.now();
    if (now - lastMove.current < 20) return;
    lastMove.current = now;

    const point = fractionOf(event, event.currentTarget);
    if (point) input({ kind: 'MouseMove', ...point });
  }

  function onMouseDown(event) {
    const point = fractionOf(event, event.currentTarget);
    if (point) input({ kind: 'MouseDown', ...point, button: event.button });
  }

  function onMouseUp(event) {
    const point = fractionOf(event, event.currentTarget);
    if (point) input({ kind: 'MouseUp', ...point, button: event.button });
  }

  function onWheel(event) {
    event.preventDefault();
    // Windows memakai kelipatan 120 dengan arah berlawanan dari deltaY browser.
    input({ kind: 'MouseWheel', wheelDelta: event.deltaY > 0 ? -120 : 120 });
  }

  function onKeyDown(event) {
    if (!controlling) return;
    event.preventDefault();
    input({ kind: 'KeyDown', keyCode: event.keyCode });
  }

  function onKeyUp(event) {
    if (!controlling) return;
    event.preventDefault();
    input({ kind: 'KeyUp', keyCode: event.keyCode });
  }

  const unitLog = activity.filter((e) => e.unitId === unit.unitId).slice(0, 10);
  const ratio = live?.width > 0 && live?.height > 0 ? `${live.width}/${live.height}` : '16/10';

  /*
    Mode layar penuh melepas panggung dari tata letak panel dan menempelkannya
    ke seluruh jendela.

    Dibutuhkan karena panggung yang tertanam hanya memakai sekitar setengah
    lebar panel: layar 1920 mahasiswa menyusut ke sekitar 700 piksel, dan pada
    ukuran itu teks di layarnya tidak terbaca sehingga tidak ada yang benar-benar
    dapat dikerjakan. Panel tetap dirender di belakangnya, jadi keluar dari mode
    ini mengembalikan semuanya tanpa memuat ulang apa pun.
  */
  if (full && live) {
    return (
      <div className="remote-full">
        <div className="remote-full-bar">
          <span className="nm">{unit.unitId}</span>
          <span>{live.width}x{live.height}</span>
          <span>{live.frames ?? 0} frame</span>
          <span className={controlling ? 'on' : ''}>
            {controlling ? 'kendali penuh' : 'lihat saja'}
          </span>
          <div className="sp" />
          <button className="btn btn-sm" onClick={() => setFull(false)}>
            <CornersIn size={14} />Keluar (Esc)
          </button>
          <button className="btn btn-sm btn-primary" onClick={() => send('remoteEnd')}>
            <XCircle size={14} />Akhiri sesi
          </button>
        </div>

        <div
          ref={stageRef}
          className={`stage${controlling ? ' controlling' : ''}`}
          style={{ aspectRatio: ratio, maxHeight: 'calc(100vh - 64px)', margin: '0 auto', outline: 'none' }}
          tabIndex={controlling ? 0 : -1}
          onMouseMove={controlling ? onMouseMove : undefined}
          onMouseDown={controlling ? onMouseDown : undefined}
          onMouseUp={controlling ? onMouseUp : undefined}
          onWheel={controlling ? onWheel : undefined}
          onKeyDown={controlling ? onKeyDown : undefined}
          onKeyUp={controlling ? onKeyUp : undefined}
          onContextMenu={(e) => controlling && e.preventDefault()}
        >
          {live.dataUrl
            ? <img src={live.dataUrl} alt={`Layar langsung ${unit.unitId}`} draggable={false}
                style={{ width: '100%', height: '100%', display: 'block', pointerEvents: 'none' }} />
            : <div className="preview-ph">Menunggu frame pertama…</div>}
        </div>
      </div>
    );
  }

  return (
    <div className="body">
      <button className="btn btn-quiet btn-sm" style={{ marginBottom: 12 }} onClick={onBack}>
        <ArrowLeft size={14} />Kembali ke dashboard
      </button>

      {/*
        Blokir ditampilkan sebagai keterangan di atas, bukan sebagai tombol
        yang tenggelam di antara tombol lain.

        Unit yang diblokir tidak akan menuruti perintah apa pun, jadi seluruh
        tombol di bawahnya tidak berarti. Tanpa keterangan ini, asisten menekan
        "Kunci layar" pada unit yang sudah diblokirnya sendiri lalu mengira
        aplikasinya rusak.
      */}
      {/*
        Penyunting nama unit.

        Blokir DIHAPUS dari sini, dan itu keputusan yang disengaja. Unit yang
        menolak menyambung tanpa cara mendiagnosanya adalah kegagalan yang jauh
        lebih mahal daripada yang dicegahnya: asisten melihat PC mati di grid,
        memeriksa kabel, memeriksa agent, dan tidak ada satu pun tempat yang
        menyebut bahwa ia sendiri yang memblokirnya bulan lalu.

        Alamat IP dan nama unit sudah terlihat di panel, jadi mahasiswa yang
        bermasalah dapat ditangani sebagaimana mestinya — dengan berbicara
        kepadanya, bukan dengan tombol yang menyisakan misteri.
      */}
      {can('ManageUnits') && (
        <div className="pane" style={{ marginBottom: 12 }}>
          <div className="field" style={{ margin: 0 }}>
            <label>Nama unit ini</label>
            <input
              value={aliasDraft}
              placeholder={`Kosongkan untuk memakai ${unit.unitId}`}
              onChange={(e) => setAliasDraft(e.target.value)}
              onBlur={saveAlias}
              onKeyDown={(e) => { if (e.key === 'Enter') { saveAlias(); e.currentTarget.blur(); } }} />
            <div className="hint" style={{ marginTop: 4 }}>
              Melekat pada perangkatnya, bukan pada alamat IP — jadi tidak
              berpindah ke PC lain saat DHCP memberi nomor baru.
            </div>
          </div>

          {/*
            Setelan proyektor.

            Dinyalakan HANYA untuk PC yang tercolok proyektor. Dosen yang
            menyiarkan dari laptopnya tidak perlu berjalan ke PC dosen untuk
            membuka jendelanya, dan tidak perlu memindahkan kabel HDMI — yang
            justru pekerjaan yang ingin dihapus aplikasi ini.

            Dinyalakan untuk seluruh kelas, empat puluh layar mahasiswa direbut
            siaran di tengah pekerjaan mereka.
          */}
          {/*
            Saklar, bukan kotak centang.

            Memakai komponen .switch yang sama dengan layar Konfigurasi. Kotak
            centang mentah tidak punya gaya di tema ini: keadaan tercentang dan
            tidak tercentang tampak sama persis, sehingga tombolnya terlihat
            selalu menyala.
          */}
          <div className="row" style={{ marginTop: 14, display: 'flex', gap: 10 }}>
            <button
              className={`switch${unit.autoFullscreen ? ' on' : ''}`}
              aria-pressed={unit.autoFullscreen ? 'true' : 'false'}
              onClick={() => send('unitFullscreen', {
                unitId: unit.unitId,
                fullscreen: !unit.autoFullscreen,
              })} />
            <div style={{ flex: 1 }}>
              <div className="panel-name">Buka siaran layar penuh otomatis</div>
              <div className="hint">
                Untuk PC yang tercolok proyektor. Begitu ada siaran, unit ini
                menampilkannya memenuhi layar — tanpa menu dan tanpa pratinjau kecil.
              </div>
            </div>
          </div>
        </div>
      )}

      <div className="two-wide">
        <div className="pane">
          <div className="toolbar">
            <button className={`btn btn-sm${locked ? ' btn-primary' : ''}`} onClick={toggleLock}>
              {locked ? <LockKeyOpen size={14} /> : <LockKey size={14} />}
              {locked ? 'Buka kunci' : 'Kunci layar'}
            </button>

            {!live ? (
              <>
                <button className="btn btn-sm"
                  onClick={() => send('remoteBegin', { unitId: unit.unitId, allowInput: false, fps: 15 })}>
                  <Eye size={14} />Lihat langsung
                </button>
                <button className="btn btn-sm"
                  onClick={() => send('remoteBegin', { unitId: unit.unitId, allowInput: true, fps: 20 })}>
                  <CursorClick size={14} />Ambil alih
                </button>
              </>
            ) : (
              <>
                <button className="btn btn-sm btn-primary" onClick={() => send('remoteEnd')}>
                  <XCircle size={14} />Akhiri sesi
                </button>
                <button className="btn btn-sm" onClick={() => setFull(!full)}>
                  {full ? <CornersIn size={14} /> : <CornersOut size={14} />}
                  {full ? 'Keluar layar penuh' : 'Layar penuh'}
                </button>
                {!controlling && (
                  <button className="btn btn-sm"
                    onClick={() => send('remoteBegin', { unitId: unit.unitId, allowInput: true, fps: 20 })}>
                    <CursorClick size={14} />Ambil alih
                  </button>
                )}
              </>
            )}

            <button className={`btn btn-sm${showChat ? ' btn-primary' : ''}`} onClick={() => setShowChat(!showChat)}>
              <ChatTeardropText size={14} />Pesan
            </button>
            {unit.helpRequestedAt && (
              <button className="btn btn-sm btn-primary"
                onClick={() => send('helpAck', { unitId: unit.unitId })}>
                <WarningDiamond size={14} />Tandai ditangani
              </button>
            )}
            {can('PowerControl') && (
              <>
                <button className="btn btn-sm" onClick={() => send('restart', { unitId: unit.unitId })}>
                  <ArrowClockwise size={14} />Restart
                </button>
                <button className="btn btn-sm" onClick={() => send('powerOff', { unitId: unit.unitId })}>
                  <Power size={14} />Matikan
                </button>
              </>
            )}
          </div>

          {showChat && (
            <div style={{ background: 'var(--surface-2)', borderRadius: 'var(--r)', padding: 10, marginBottom: 12 }}>
              <div style={{ maxHeight: 110, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 6, marginBottom: 8 }}>
                {thread.length === 0
                  ? <div style={{ fontSize: 12, color: 'var(--ink-dim)' }}>Belum ada pesan.</div>
                  : thread.map((m, i) => (
                    <div className={`msg ${m.fromAdmin ? 'me' : 'them'}`} key={i} style={{ maxWidth: '80%' }}>
                      {m.text}
                      <div className="msg-t">
                        {m.displayName} · {new Date(m.sentAt).toLocaleTimeString('id-ID',
                          { hour: '2-digit', minute: '2-digit' })}
                      </div>
                    </div>
                  ))}
              </div>
              <div style={{ display: 'flex', gap: 6 }}>
                <input value={draft} onChange={(e) => setDraft(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && sendChat()}
                  placeholder={`Pesan cepat ke ${unit.unitId}`}
                  style={{ flex: 1, background: 'var(--surface-1)', border: '1px solid var(--border)',
                    borderRadius: 'var(--r)', padding: '6px 10px', fontSize: 12.5, outline: 'none' }} />
                <button className="btn btn-sm btn-primary" onClick={sendChat}>Kirim</button>
              </div>
            </div>
          )}

          {/*
            Panggung punya dua peran yang berbeda tajam. Tanpa sesi, ia hanya
            memuat pratinjau 0,5 fps dari heartbeat. Dengan sesi, ia menjadi
            permukaan masukan — rasio aspeknya dipaksa sama dengan frame yang
            masuk supaya tidak ada bilah hitam, karena bilah itu akan menggeser
            pemetaan koordinat dan membuat kursor mendarat di tempat yang salah.
          */}
          <div
            ref={stageRef}
            className={`stage${controlling ? ' controlling' : ''}`}
            style={{ aspectRatio: ratio, marginBottom: 0, outline: 'none' }}
            tabIndex={controlling ? 0 : -1}
            onMouseMove={controlling ? onMouseMove : undefined}
            onMouseDown={controlling ? onMouseDown : undefined}
            onMouseUp={controlling ? onMouseUp : undefined}
            onWheel={controlling ? onWheel : undefined}
            onKeyDown={controlling ? onKeyDown : undefined}
            onKeyUp={controlling ? onKeyUp : undefined}
            onContextMenu={(e) => controlling && e.preventDefault()}
          >
            {live
              ? (live.dataUrl
                ? <img src={live.dataUrl} alt={`Layar langsung ${unit.unitId}`}
                    draggable={false}
                    style={{ width: '100%', height: '100%', display: 'block', pointerEvents: 'none' }} />
                : <div className="preview-ph">Menunggu frame pertama dari {unit.unitId}…</div>)
              : (thumb
                ? <img src={thumb} alt={`Layar ${unit.unitId}`}
                    style={{ width: '100%', height: '100%', objectFit: 'contain' }} />
                : `Menunggu pratinjau dari ${unit.unitId}`)}

            {live && (
              <div className={`live${controlling ? '' : ' quiet'}`}>
                <b />
                {controlling
                  ? `KENDALI · ${live.frames ?? 0} frame`
                  : `LIHAT SAJA · ${live.frames ?? 0} frame`}
              </div>
            )}
            {!live && thumb && <div className="live quiet"><b />pratinjau · 0,5 fps</div>}

            {locked && (
              <div className="live" style={{
                top: 8,
                right: 8,
                left: 'auto',
                background: 'rgba(234, 88, 12, 0.85)',
                borderColor: '#F97316',
                color: '#FFF',
                zIndex: 2,
              }}>
                <LockKey size={13} weight="fill" />
                LAYAR MAHASISWA TERKUNCI (MODE INSPEKSI)
              </div>
            )}
          </div>

          {controlling && (
            <div className="diag" style={{ marginTop: 10, marginBottom: 0 }}>
              <span className="nm">Kendali aktif</span>
              <span>klik panggung dulu agar ketikan terkirim</span>
              <span className="warn">jendela ber-hak admin tidak dapat dikendalikan</span>
            </div>
          )}
        </div>

        <div>
          <div className="pane" style={{ marginBottom: 16 }}>
            <h3>Info sesi</h3>
            <div className="kv">
              <div>Status <b>{unit.status}</b></div>
              <div>Alamat <span className="mono">{unit.ipAddress}</span></div>
              <div>Host <span className="mono">{unit.hostName}</span></div>
              <div>Terakhir <span className="mono">{new Date(unit.lastSeen).toLocaleTimeString('id-ID')}</span></div>
              {live && (
                <div>Aliran <span className="mono">
                  {live.width}x{live.height} · {live.frames ?? 0} frame
                </span></div>
              )}
            </div>
          </div>

          <div className="pane">
            <h3>Riwayat unit</h3>
            {unitLog.length === 0 ? (
              <div style={{ fontSize: 12, color: 'var(--ink-dim)' }}>Belum ada catatan untuk unit ini.</div>
            ) : unitLog.map((e, i) => (
              <div className="log-row" key={i}
                style={{ gridTemplateColumns: '62px 1fr', padding: '5px 0', borderTop: i ? '1px solid var(--divider)' : 'none' }}>
                <span className="log-t">{new Date(e.createdAt).toLocaleTimeString('id-ID')}</span>
                <span className="log-a">{e.action}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
