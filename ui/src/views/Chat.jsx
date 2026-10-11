import { useEffect, useRef, useState } from 'react';
import {
  PaperPlaneRight, Megaphone, WarningDiamond, Check, MagnifyingGlass, Trash,
} from '@phosphor-icons/react';
import { send } from '../bridge';

const time = (iso) => {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString('id-ID', { hour: '2-digit', minute: '2-digit' });
};

const waited = (iso) => {
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return `${Math.floor(seconds)} detik`;
  return `${Math.floor(seconds / 60)} menit`;
};

/*
  Percakapan admin dengan mahasiswa.

  Riwayatnya tidak disimpan di sini melainkan diminta ke host setiap kali sebuah
  percakapan dibuka. Menyimpannya di state React berarti percakapan hilang
  setiap kali panel dimuat ulang, dan yang lebih buruk: dua panel admin akan
  menampilkan riwayat yang berbeda untuk mahasiswa yang sama.
*/
export default function Chat({ units, threads, onOpen }) {
  const [activeId, setActiveId] = useState(null);
  const [draft, setDraft] = useState('');
  const [toEveryone, setToEveryone] = useState(false);
  const [query, setQuery] = useState('');

  // Konfirmasi dua langkah, bukan dialog. Penghapusan ini tidak dapat
  // dibatalkan dan tidak boleh terjadi karena salah klik — tetapi juga tidak
  // perlu sampai menghentikan seluruh layar, karena tindakannya rutin
  // setiap pergantian kelas.
  const [confirming, setConfirming] = useState(false);
  useEffect(() => {
    if (!confirming) return undefined;
    const t = setTimeout(() => setConfirming(false), 4000);
    return () => clearTimeout(t);
  }, [confirming]);
  const bodyRef = useRef(null);

  const active = activeId ?? units[0]?.unitId ?? null;
  const thread = active ? threads[active] ?? [] : [];

  // Percakapan dibuka sekali saat pilihan berubah. Host membalas dengan riwayat
  // dan menandai pesan masuk sudah dibaca.
  useEffect(() => {
    if (active) onOpen(active);
  }, [active]);

  // Selalu menggulung ke pesan terbaru. Tanpa ini, pesan baru masuk di bawah
  // lipatan dan percakapan tampak berhenti.
  useEffect(() => {
    const el = bodyRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [thread.length, active]);

  function sendMsg() {
    const text = draft.trim();
    if (!text) return;
    if (!toEveryone && !active) return;

    send('chatSend', toEveryone ? { text, toEveryone: true } : { unitId: active, text });
    setDraft('');

    /*
      Mode pengumuman padam sendiri setelah satu kali kirim.

      Sebagai sakelar yang menyala terus, ia menunggu satu kelalaian saja: asisten
      menyiarkan pengumuman, lalu membalas pertanyaan pribadi seorang mahasiswa —
      dan balasan itu masuk ke layar seluruh kelas. Mode yang jangkauannya luas
      tidak boleh lengket.
    */
    if (toEveryone) setToEveryone(false);
  }

  const waiting = units
    .filter((u) => u.helpRequestedAt)
    .sort((a, b) => new Date(a.helpRequestedAt) - new Date(b.helpRequestedAt));

  /*
    Daftar diurutkan menurut yang butuh perhatian, bukan menurut nomor PC.

    Dengan 40 unit, urutan nomor berarti asisten harus memindai seluruh kolom
    untuk menemukan siapa yang menunggu. Yang meminta bantuan naik paling atas,
    lalu yang punya pesan belum dibaca, sisanya menurut nomor seperti biasa.
  */
  const listed = units
    .filter((u) => {
      const q = query.trim().toLowerCase();
      return !q || u.unitId.toLowerCase().includes(q)
                || (u.hostName ?? '').toLowerCase().includes(q);
    })
    .sort((a, b) => {
      const rank = (u) => (u.helpRequestedAt ? 0 : u.unread > 0 ? 1 : 2);
      return rank(a) - rank(b) || a.unitId.localeCompare(b.unitId);
    });

  if (!units.length) {
    return (
      <div className="body">
        <div className="pane">
          <div className="empty">
            Belum ada unit tersambung.<br />
            Percakapan tersedia setelah ada agent yang terdaftar.
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="body body-fill">
      {/*
        Antrean minta bantuan berada di atas segalanya dan diurutkan dari yang
        paling lama menunggu. Diurutkan menurut nomor PC, mahasiswa di PC-38
        akan selalu dilayani terakhir betapa pun lamanya ia menunggu.
      */}
      {waiting.length > 0 && (
        <div className="helpbar">
          <WarningDiamond size={16} weight="fill" />
          <b>{waiting.length} menunggu bantuan</b>
          {waiting.slice(0, 6).map((u) => (
            <button key={u.unitId} className="helpchip" onClick={() => setActiveId(u.unitId)}>
              {u.unitId}<span>{waited(u.helpRequestedAt)}</span>
            </button>
          ))}
          {waiting.length > 6 && <span className="s">+{waiting.length - 6} lagi</span>}
        </div>
      )}

      <div className="chat-toolbar">
        <span className="s">
          Percakapan bersifat sementara — kosongkan sebelum kelas berikutnya masuk.
        </span>
        <div className="sp" />
        {confirming ? (
          <>
            <button className="btn btn-sm btn-primary"
              onClick={() => { send('chatClear'); setConfirming(false); }}>
              <Trash size={13} />Ya, hapus semua
            </button>
            <button className="btn btn-sm btn-quiet" onClick={() => setConfirming(false)}>Batal</button>
          </>
        ) : (
          <button className="btn btn-sm" onClick={() => setConfirming(true)}>
            <Trash size={13} />Reset sesi kelas
          </button>
        )}
      </div>

      <div className="chat">
        <div className="chat-list">
          {/* Setiap unit selalu ada di daftar, termasuk yang belum pernah
              berkirim pesan — asisten sering perlu memulai percakapan lebih
              dulu. Pencarian ada supaya daftar 40 baris tetap bisa dipakai. */}
          <div className="chat-search">
            <MagnifyingGlass size={13} />
            <input value={query} onChange={(e) => setQuery(e.target.value)}
              placeholder="Cari unit…" />
          </div>

          {listed.length === 0 && (
            <div className="empty" style={{ padding: 16, fontSize: 12 }}>Tidak ada yang cocok.</div>
          )}

          {listed.map((u) => {
            const last = (threads[u.unitId] ?? []).at(-1);
            return (
              <button key={u.unitId}
                className={`chat-item${active === u.unitId ? ' on' : ''}`}
                onClick={() => setActiveId(u.unitId)}>
                <div className="chat-id">
                  {u.unitId}
                  {u.helpRequestedAt && <WarningDiamond size={12} weight="fill" className="hand" />}
                  {u.unread > 0 && <span className="badge">{u.unread}</span>}
                </div>
                <div className="chat-prev">{last ? last.text : 'Belum ada pesan'}</div>
              </button>
            );
          })}
        </div>

        <div className="thread">
          <div className="thread-head">
            <span>{active}</span>
            {units.find((u) => u.unitId === active)?.helpRequestedAt && (
              <button className="btn btn-sm btn-primary" style={{ marginLeft: 'auto' }}
                onClick={() => send('helpAck', { unitId: active })}>
                <Check size={13} />Tandai ditangani
              </button>
            )}
          </div>

          <div className="thread-body" ref={bodyRef}>
            {thread.length === 0
              ? <div style={{ fontSize: 12.5, color: 'var(--ink-dim)' }}>Belum ada pesan di percakapan ini.</div>
              : thread.map((m, i) => (
                  <div className={`msg ${m.fromAdmin ? 'me' : 'them'}`} key={i}>
                    {m.text}
                    <div className="msg-t">{m.displayName} · {time(m.sentAt)}</div>
                  </div>
                ))}
          </div>

          <div className="thread-input">
            <button className={`btn btn-sm${toEveryone ? ' btn-primary' : ''}`}
              title="Kirim ke seluruh kelas"
              onClick={() => setToEveryone(!toEveryone)}>
              <Megaphone size={14} />
            </button>
            <input value={draft} onChange={(e) => setDraft(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && sendMsg()}
              placeholder={toEveryone ? 'Pengumuman untuk seluruh kelas' : `Tulis pesan ke ${active ?? '—'}`} />
            <button className="btn btn-primary btn-sm" onClick={sendMsg}>
              <PaperPlaneRight size={14} />Kirim
            </button>
          </div>

          {toEveryone && (
            <div style={{ fontSize: 11.5, color: 'var(--warn)', padding: '0 12px 10px' }}>
              Mode pengumuman aktif — pesan masuk ke layar seluruh mahasiswa yang tersambung.
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
