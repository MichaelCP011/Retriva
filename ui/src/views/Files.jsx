import { useState } from 'react';
import {
  TrayArrowUp, Plus, X, UploadSimple, FileText, CheckCircle, WarningCircle, Trash,
} from '@phosphor-icons/react';
import { send } from '../bridge';

const sizeLabel = (bytes) => {
  if (!bytes && bytes !== 0) return '';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`;
};

/*
  Pembagian berkas dengan model TARIK.

  Panel tidak mengirim isi berkas ke mana pun. Ia menitipkan salinan ke lemari
  Superadmin, lalu tiap agent mengunduhnya sendiri dan memverifikasi sidik
  SHA-256-nya. Karena itu yang ditampilkan di sini adalah kemajuan NYATA yang
  dilaporkan tiap unit — bukan bilah yang bergerak sendiri berdasarkan pewaktu,
  seperti versi mockup sebelumnya yang selalu mencapai 100% bahkan ketika tidak
  ada satu unit pun yang tersambung.
*/
export default function Files({
  units = [], picked = [], transfers = {}, staging = {}, onForget = () => {},
}) {
  const [selected, setSelected] = useState(new Set());
  const [confirming, setConfirming] = useState(false);

  const online = units.filter((u) => u.status !== 'Offline');
  const targets = selected.size === 0 ? online : online.filter((u) => selected.has(u.unitId));

  const active = Object.values(transfers);
  const busy = Object.keys(staging).length > 0;

  function toggle(unitId) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(unitId)) next.delete(unitId); else next.add(unitId);
      return next;
    });
  }

  function start() {
    if (!picked.length || targets.length === 0) return;
    send('fileSend', {
      paths: picked.map((f) => f.path),
      targets: selected.size === 0 ? null : [...selected],
    });
  }

  return (
    <div className="body">
      <div className="two">
        <div>
          <div className="drop" onClick={() => send('filePick')}>
            <TrayArrowUp size={26} color="var(--ink-muted)" />
            <div className="t">Klik untuk memilih berkas</div>
            <div className="s">Dialog Windows akan terbuka — bisa banyak berkas sekaligus</div>
          </div>

          {picked.length > 0 && (
            <div className="pane" style={{ marginTop: 16 }}>
              <div className="sec">
                <h2>Berkas dipilih</h2>
                <span className="hint">
                  {picked.length} berkas · {sizeLabel(picked.reduce((n, f) => n + (f.size ?? 0), 0))}
                </span>
                <div className="sp" />
                <button className="btn btn-quiet btn-sm" onClick={() => send('filePick')}>
                  <Plus size={13} />Tambah
                </button>
              </div>

              {picked.map((f) => {
                const copy = staging[f.path];
                const pct = copy && copy.total
                  ? Math.round((copy.copied / copy.total) * 100)
                  : 0;

                return (
                  <div className="frow" key={f.path}>
                    <FileText size={17} color="var(--ink-muted)" />
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <div className="n">{f.name}</div>
                      <div className="s">
                        {copy
                          ? `menyalin ke lemari · ${sizeLabel(copy.copied)} dari ${sizeLabel(copy.total)}`
                          : sizeLabel(f.size)}
                      </div>
                      {/* Berkas besar butuh menit-menitan disalin sebelum unit
                          mana pun mulai mengunduh. Tanpa bilah ini, jeda itu
                          tidak terwakili apa pun di layar. */}
                      {copy && (
                        <div className="bar-track" style={{ marginTop: 6 }}>
                          <div className="bar-fill" style={{ width: `${pct}%` }} />
                        </div>
                      )}
                    </div>

                    {copy ? (
                      <span className="num">{pct}%</span>
                    ) : (
                      <button className="btn btn-quiet btn-sm" title="Buang dari daftar"
                        onClick={() => onForget(f.path)}>
                        <X size={13} />
                      </button>
                    )}
                  </div>
                );
              })}

              {/* Terkunci selagi menyalin. Menekannya lagi di tengah penyalinan
                  akan menitipkan berkas yang sama untuk kedua kalinya — persis
                  penggandaan yang sedang diperbaiki di sini. */}
              <button className="btn btn-primary" style={{ marginTop: 12 }}
                onClick={start} disabled={targets.length === 0 || busy}>
                <UploadSimple size={15} />
                {busy
                  ? 'Menyalin ke lemari…'
                  : `Bagikan ke ${targets.length} unit`}
              </button>

              <div style={{ fontSize: 11.5, color: 'var(--ink-dim)', marginTop: 8, lineHeight: 1.5 }}>
                Berkas hilang dari daftar ini begitu berangkat. Riwayat lengkapnya
                ada di Pembagian berjalan.
              </div>
            </div>
          )}

          {active.length > 0 && (
            <div className="pane" style={{ marginTop: 16 }}>
              <div className="sec">
                <h2>Pembagian berjalan</h2>
                <div className="sp" />
                {confirming ? (
                  <>
                    <button className="btn btn-sm btn-primary"
                      onClick={() => { send('fileClear'); setConfirming(false); }}>
                      <Trash size={13} />Ya, buang semua
                    </button>
                    <button className="btn btn-sm btn-quiet" onClick={() => setConfirming(false)}>
                      Batal
                    </button>
                  </>
                ) : (
                  <button className="btn btn-sm" onClick={() => setConfirming(true)}>
                    <Trash size={13} />Kosongkan lemari
                  </button>
                )}
              </div>

              {active.map((t) => {
                const rows = Object.values(t.units ?? {});
                const done = rows.filter((r) => r.status === 'selesai').length;
                const failed = rows.filter((r) => r.status === 'gagal').length;
                const overall = rows.length
                  ? Math.round(rows.reduce((n, r) => n + (r.percent ?? 0), 0) / rows.length)
                  : 0;

                return (
                  <div key={t.transferId} style={{ marginBottom: 16 }}>
                    <div className="frow" style={{ borderBottom: 'none', paddingBottom: 4 }}>
                      <FileText size={17} color="var(--ink-muted)" />
                      <div style={{ flex: 1, minWidth: 0 }}>
                        <div className="n">{t.fileName}</div>
                        <div className="s">
                          {sizeLabel(t.sizeBytes)} · {done} selesai
                          {failed > 0 && <span style={{ color: 'var(--accent)' }}> · {failed} gagal</span>}
                          {' '}dari {rows.length} unit
                        </div>
                      </div>
                      <span className="num">{overall}%</span>
                    </div>

                    <div className="bar-track"><div className="bar-fill" style={{ width: `${overall}%` }} /></div>

                    {/* Unit yang gagal disebut satu per satu. Angka gabungan
                        menyembunyikan PC mana yang bermasalah, padahal justru
                        itu yang perlu dihampiri asisten. */}
                    {rows.filter((r) => r.status === 'gagal').map((r) => (
                      <div key={r.unitId} className="diag" style={{ marginTop: 8, marginBottom: 0 }}>
                        <WarningCircle size={13} />
                        <span className="nm">{r.unitId}</span>
                        <span>{r.detail ?? 'gagal'}</span>
                      </div>
                    ))}
                  </div>
                );
              })}
            </div>
          )}
        </div>

        <div className="pane">
          <div className="sec">
            <h2>Tujuan</h2>
            <span className="hint">
              {selected.size === 0 ? `semua (${online.length})` : `${selected.size} dipilih`}
            </span>
            <div className="sp" />
            {selected.size > 0 && (
              <button className="btn btn-quiet btn-sm" onClick={() => setSelected(new Set())}>
                <X size={13} />Semua unit
              </button>
            )}
          </div>

          <div className="targets">
            {units.length === 0 ? (
              <div className="empty" style={{ padding: '20px 12px' }}>Belum ada unit tersambung.</div>
            ) : units.map((u) => {
              const off = u.status === 'Offline';
              const row = active.flatMap((t) => Object.values(t.units ?? {}))
                .find((r) => r.unitId === u.unitId);
              return (
                <label className="trow" key={u.unitId}>
                  <input type="checkbox" disabled={off}
                    checked={!off && (selected.size === 0 || selected.has(u.unitId))}
                    onChange={() => toggle(u.unitId)} />
                  <span className={`st plain ${u.status}`} />
                  <span className="id">{u.unitId}</span>
                  <span className="s">
                    {row?.status === 'selesai' ? <CheckCircle size={13} weight="fill" />
                      : row ? `${row.percent}%` : ''}
                  </span>
                </label>
              );
            })}
          </div>

          <div style={{ fontSize: 11.5, color: 'var(--ink-dim)', marginTop: 10, lineHeight: 1.5 }}>
            Berkas dititipkan ke Superadmin, lalu tiap unit mengunduhnya sendiri
            dan memeriksa sidik SHA-256-nya. Unit yang lambat tidak menahan yang lain.
          </div>
        </div>
      </div>
    </div>
  );
}
