import { useEffect, useState } from 'react';
import {
  Power, Moon, LockKey, LockKeyOpen, Broadcast, Funnel, CursorClick,
  ArrowClockwise, XCircle, CheckCircle, Warning, X, WarningDiamond,
} from '@phosphor-icons/react';
import { send, isHosted } from '../bridge';
import { sinceLabel } from '../format';
import Confirm from '../components/Confirm';
import { unitLabel, unitSubLabel } from '../unitName';

const LABEL = { Online: 'Aktif', Offline: 'Mati', Locked: 'Terkunci', Alert: 'Peringatan' };

// Tombol "Login semua" dihapus. Dengan autologon, PC yang menyala langsung
// masuk ke desktop, sehingga tombol itu identik dengan "Nyalakan" — dan tombol
// yang tidak melakukan apa pun yang berbeda hanya menyesatkan operator.
//
// Perintah yang tidak dapat ditarik kembali dengan mudah meminta konfirmasi.
// `needs` menyebut wewenang yang dituntut tindakan itu. Dosen tidak memegang
// PowerControl, jadi tiga tombol daya di bawah tidak muncul untuknya — bukan
// muncul lalu ditolak, yang akan terbaca sebagai aplikasi yang rusak.
const ACTIONS = [
  { id: 'powerOnAll',  Icon: Power,          label: 'Nyalakan', needs: 'PowerControl' },
  { id: 'powerOffAll', Icon: Moon,           label: 'Matikan', needs: 'PowerControl',
    confirm: {
      title: 'Matikan seluruh unit mahasiswa?',
      // Jeda diambil dari host, bukan dituliskan ulang di sini. Dua tempat yang
      // menuliskan angka yang sama sendiri-sendiri akan berselisih pada
      // perubahan pertama — dan yang berbohong adalah dialog tentang mematikan
      // empat puluh komputer.
      body: (n, delay) => (<>Perintah dikirim ke <b>{n} unit</b> yang sedang tersambung.
        Komputer mati dalam <b>{delay} detik</b>, dan masih dapat dibatalkan
        selama hitung mundur berjalan.</>),
      label: 'Matikan semua',
    } },
  { id: 'restartAll',  Icon: ArrowClockwise, label: 'Restart', needs: 'PowerControl',
    confirm: {
      title: 'Nyalakan ulang seluruh unit?',
      body: (n) => (<>Perintah dikirim ke <b>{n} unit</b> yang sedang tersambung.
        Pekerjaan mahasiswa yang belum tersimpan akan hilang.</>),
      label: 'Restart semua',
    } },
  { id: 'lockAll',     Icon: LockKey,        label: 'Kunci semua', needs: 'LockScreen' },
  { id: 'unlockAll',   Icon: LockKeyOpen,    label: 'Buka semua',  needs: 'LockScreen' },
];

export default function Dashboard({ units, thumbs = {}, activity = [], batch = null,
  onDismissBatch, onOpenUnit, onGoShare, can = () => true, settings = null }) {
  const [pending, setPending] = useState(null);

  // Label "terakhir terlihat" harus terus berjalan meski tidak ada data baru.
  const [, tick] = useState(0);
  useEffect(() => {
    const t = setInterval(() => tick((n) => n + 1), 1000);
    return () => clearInterval(t);
  }, []);

  const count = (s) => units.filter((u) => u.status === s).length;

  const onlineCount = units.filter((u) => u.status !== 'Offline').length;

  function run(a) {
    if (a.confirm) { setPending(a); return; }
    send(a.id);
  }

  function confirmPending() {
    if (pending) send(pending.id);
    setPending(null);
  }

  return (
    <div className="body">
      {!isHosted && (
        <div className="notice">
          Mode pratinjau browser — 40 unit di bawah adalah data tiruan dan tombol tidak mengirim apa pun.
        </div>
      )}

      <div className="tools">
        <span className="lab">Seluruh unit mahasiswa</span>
        {ACTIONS.filter((a) => !a.needs || can(a.needs)).map((a) => (
          <button key={a.id} className="btn" onClick={() => run(a)}>
            <a.Icon size={15} />{a.label}
          </button>
        ))}
        <div className="div" />
        {can('PowerControl') && (
          <button className="btn btn-quiet" title="Batalkan hitung mundur daya yang sedang berjalan"
            onClick={() => send('abortAll')}>
            <XCircle size={15} />Batalkan hitung mundur
          </button>
        )}
        <button className="btn btn-quiet" onClick={onGoShare}>
          <Broadcast size={15} />Bagikan layar
        </button>
      </div>

      <Confirm
        open={pending !== null}
        title={pending?.confirm?.title}
        body={pending?.confirm?.body(onlineCount, settings?.shutdownDelay ?? '—')}
        confirmLabel={pending?.confirm?.label}
        onConfirm={confirmPending}
        onCancel={() => setPending(null)}
      />

      {batch && (
        <div className={`notice${batch.failed === 0 && batch.noReply === 0 ? ' ok' : ''}`}
          style={{ display: 'flex', alignItems: 'flex-start', gap: 10 }}>
          {batch.failed === 0 && batch.noReply === 0
            ? <CheckCircle size={16} style={{ flexShrink: 0, marginTop: 1 }} />
            : <Warning size={16} style={{ flexShrink: 0, marginTop: 1 }} />}
          <div style={{ flex: 1 }}>
            <div style={{ fontWeight: 600 }}>
              {batch.action} — {batch.succeeded}/{batch.expected} berhasil
            </div>
            {batch.failedUnits?.length > 0 && (
              <div style={{ marginTop: 3 }}>Gagal: {batch.failedUnits.join(', ')}</div>
            )}
            {batch.silentUnits?.length > 0 && (
              <div style={{ marginTop: 3 }}>Tidak menjawab: {batch.silentUnits.join(', ')}</div>
            )}
          </div>
          <button className="icon-btn" onClick={onDismissBatch}><X size={12} /></button>
        </div>
      )}

      <div className="metrics">
        <div className="metric"><div className="k">Tersambung</div><div className="v">{units.length}</div></div>
        <div className="metric"><div className="k">Aktif</div><div className="v ok">{count('Online')}</div></div>
        <div className="metric"><div className="k">Terkunci</div><div className="v link">{count('Locked')}</div></div>
        <div className="metric"><div className="k">Peringatan</div><div className="v bad">{count('Alert')}</div></div>
        <div className="metric"><div className="k">Mati</div><div className="v">{count('Offline')}</div></div>
      </div>

      <div className="sec">
        <h2>Aktivitas</h2>
        <span className="hint">{activity.length} catatan terakhir</span>
      </div>
      <div className="log">
        {activity.length === 0 ? (
          <div className="empty" style={{ padding: '26px 20px' }}>Belum ada aktivitas tercatat.</div>
        ) : activity.slice(0, 8).map((e, i) => (
          <div className="log-row" key={i}>
            <span className="log-t">{sinceLabel(e.createdAt)}</span>
            <span className={`log-a${/rejected|offline/.test(e.action) ? ' bad' : ''}`}>{e.action}</span>
            <span className="log-d">{e.unitId ? `${e.unitId} · ` : ''}{e.detail ?? ''}</span>
            <span className="log-w">{e.actor}</span>
          </div>
        ))}
      </div>

      <div className="sec">
        <h2>Unit</h2>
        <span className="hint">pratinjau diperbarui tiap 2 detik</span>
        <div className="sp" />
        <button className="btn btn-quiet btn-sm"><Funnel size={14} />Filter</button>
      </div>

      <div className="grid">
        {units.length === 0 ? (
          <div className="empty" style={{ gridColumn: '1 / -1' }}>
            Belum ada unit tersambung.<br />
            Jalankan simulator:<br />
            <code>dotnet run --project src/Retriva.FakeAgent -- --count 40</code>
          </div>
        ) : units.map((u) => {
          const shot = thumbs[u.unitId] ?? u.thumbnail;
          return (
            <div className={`unit${u.helpRequestedAt ? ' asking' : ''}`} key={u.unitId}
              onClick={() => onOpenUnit(u)}>
              <div className="shot">
                <span className={`st ${u.status}`} />
                {/* Penanda diletakkan di atas pratinjau, bukan di baris teks
                    bawah. Di grid 40 kartu, teks kecil di kaki kartu tidak
                    terlihat dari jarak pandang normal seorang asisten yang
                    sedang berdiri di depan kelas. */}
                {u.helpRequestedAt && (
                  <span className="asking-flag" title="Meminta bantuan">
                    <WarningDiamond size={12} weight="fill" />minta bantuan
                  </span>
                )}
                {u.unread > 0 && <span className="unread-flag">{u.unread}</span>}
                {u.status === 'Offline'
                  ? <span className="off">tidak ada sinyal</span>
                  : shot
                    ? <img src={shot} alt={`Layar ${u.unitId}`} />
                    : <span className="off">menunggu</span>}
              </div>
              <div className="unit-foot">
                <div className="unit-r1">
                  {/*
                    Nama pilihan menggantikan unit id bila ada, dan unit id-nya
                    turun ke baris keterangan — tidak hilang.

                    Asisten yang menelusuri masalah selalu butuh unit id: itulah
                    yang muncul di jejak audit, di log agent, dan di stiker pada
                    casing. Alias yang menggantikannya sepenuhnya memaksa orang
                    menerjemahkan bolak-balik antara nama yang dilihatnya dan
                    nama yang tercatat.
                  */}
                  <span className="unit-id">{unitLabel(u)}</span>
                  <span className="unit-meta">
                    {LABEL[u.status] ?? u.status}
                  </span>
                </div>
                {/*
                  Nama mahasiswa di baris tersendiri, di atas keterangan teknis.

                  Ini pertanyaan pertama asisten saat melihat sebuah kartu:
                  siapa yang duduk di situ. Alamat IP dan waktu terakhir terlihat
                  baru dibutuhkan setelah ada yang salah. Datanya sudah mengalir
                  dari agent sejak lama dan berhenti di koordinator — lihat
                  UnitRegistry.ApplyHeartbeat.
                */}
                {u.studentName && (
                  <div className="unit-meta" style={{ color: 'var(--ink)' }}>
                    {u.studentName}
                    {u.studentNim ? ` · ${u.studentNim}` : ''}
                    {u.isGuest ? ' · tamu' : ''}
                  </div>
                )}
                <div className="unit-meta">
                  {unitSubLabel(u) ? `${unitSubLabel(u)} · ` : ''}{u.ipAddress} · {sinceLabel(u.lastSeen)}
                </div>
                <div className="unit-actions">
                  {can('LockScreen') && (
                    <button className="icon-btn" title="Kunci layar"
                      onClick={(e) => { e.stopPropagation(); send('lock', { unitId: u.unitId }); }}>
                      <LockKey size={13} />
                    </button>
                  )}
                  {can('PowerControl') && (
                    <button className="icon-btn" title="Matikan"
                      onClick={(e) => { e.stopPropagation(); send('powerOff', { unitId: u.unitId }); }}>
                      <Power size={13} />
                    </button>
                  )}
                  <button className="icon-btn" title="Kontrol"
                    onClick={(e) => { e.stopPropagation(); onOpenUnit(u); }}>
                    <CursorClick size={13} />
                  </button>
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
