import { useEffect, useMemo, useState } from 'react';
import { Eye, EyeSlash, Copy, PencilSimple, FloppyDisk, X, Plus, Trash, ArrowsClockwise, DownloadSimple, CheckCircle } from '@phosphor-icons/react';
import { send } from '../bridge';

/** Nilai rahasia tersembunyi sampai diminta, supaya tidak terekspos saat siaran layar. */
function Secret({ value }) {
  const [shown, setShown] = useState(false);
  if (!value) return <span style={{ color: 'var(--ink-dim)' }}>—</span>;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
      <code style={{ flex: 1, wordBreak: 'break-all', fontSize: 11.5, color: 'var(--ink-muted)' }}>
        {shown ? value : '•'.repeat(28)}
      </code>
      <button className="icon-btn" title={shown ? 'Sembunyikan' : 'Tampilkan'} onClick={() => setShown(!shown)}>
        {shown ? <EyeSlash size={13} /> : <Eye size={13} />}
      </button>
      <button className="icon-btn" title="Salin" onClick={() => navigator.clipboard?.writeText(value)}>
        <Copy size={13} />
      </button>
    </div>
  );
}

/** Oktet terakhir sebuah alamat, atau null bila bukan alamat IPv4. */
function octetOf(ip) {
  if (!ip) return null;
  const last = ip.slice(ip.lastIndexOf('.') + 1);
  const n = Number(last);
  return Number.isInteger(n) && n > 0 && n < 255 ? n : null;
}

export default function Settings({ units, settings, updateInfo, can = () => true }) {
  const [requireLogin, setRequireLogin] = useState(true);
  const [allowGuest, setAllowGuest] = useState(true);
  const [allowReject, setAllowReject] = useState(false);
  const [saved, setSaved] = useState(false);

  /*
    Nilai konfigurasi disimpan dua kali: yang tersimpan dan yang sedang diedit.

    Bukan kerangkapan yang bisa dihilangkan — justru inilah yang membuat Batal
    dapat mengembalikan keadaan, dan yang membuat "ada perubahan belum
    disimpan" dapat diketahui tanpa menebak.
  */
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(null);

  // Tabel penomoran sebagai daftar berurut, bukan objek. Objek tidak dapat
  // menahan baris yang oktetnya sedang dikosongkan saat diketik ulang.
  // Null, bukan array kosong. Array kosong adalah nilai yang SAH untuk tabel
  // yang sengaja dikosongkan, jadi ia tidak dapat sekaligus berarti "belum
  // disunting" — dan `rows ?? storedRows` akan menahan array kosong itu,
  // menyembunyikan seluruh pemetaan yang tersimpan.
  const [rows, setRows] = useState(null);
  const [renaming, setRenaming] = useState(null);
  const [renameDraft, setRenameDraft] = useState('');

  // Nilai yang tampil selalu mengikuti berkas konfigurasi, bukan tebakan UI.
  // Saat sedang menyunting, kiriman dari server tidak menimpa ketikan orang.
  useEffect(() => {
    if (!settings) return;
    setRequireLogin(settings.requireStudentLogin ?? true);
    setAllowGuest(settings.allowGuestAccess ?? true);
    setAllowReject(settings.studentsMayRejectControl ?? false);
    if (!editing) {
      setDraft(null);
      setRows(null);
    }
  }, [settings, editing]);

  const stored = useMemo(() => ({
    labName: settings?.labName ?? '',
    subnetPrefix: settings?.subnetPrefix ?? '192.168.1.',
    listenPort: settings?.listenPort ?? 8443,
  }), [settings]);

  const storedRows = useMemo(() => Object.entries(settings?.unitNames ?? {})
    .map(([octet, name]) => ({ octet, name }))
    .sort((a, b) => Number(a.octet) - Number(b.octet)), [settings]);

  const view = draft ?? stored;
  const table = rows ?? storedRows;

  // Mesin koordinator saja. console.json bersifat lokal: menyuntingnya dari
  // laptop lain menyimpan berkas yang tidak dibaca koordinator mana pun.
  const editable = can('Configure') && settings?.configEditable !== false;

  function beginEdit() {
    setDraft({ ...stored });
    setRows(storedRows.map((r) => ({ ...r })));
    setEditing(true);
  }

  function cancelEdit() {
    setEditing(false);
    setDraft(null);
    setRows(null);
  }

  function saveEdit() {
    const unitNames = {};
    for (const r of table) {
      const octet = String(r.octet).trim();
      const name = r.name.trim();
      if (!octet || !name) continue;
      unitNames[octet] = name;
    }

    const prefix = view.subnetPrefix.trim();
    send('saveSettings', {
      labName: view.labName.trim(),
      subnetPrefix: prefix.endsWith('.') ? prefix : `${prefix}.`,
      listenPort: Number(view.listenPort),
      unitNames,
    });

    setEditing(false);
    setDraft(null);
    setRows(null);
    setSaved(true);
    setTimeout(() => setSaved(false), 2400);
  }

  function patchToggle(change) {
    send('saveSettings', change);
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  }

  const sorted = useMemo(
    () => [...units].sort((a, b) => a.unitId.localeCompare(b.unitId, 'id', { numeric: true })),
    [units]);

  const online = units.filter((u) => u.status !== 'Offline').length;

  // Baris ganda dan oktet ganda dicari sebelum disimpan, bukan setelahnya.
  // Dua baris dengan oktet sama membuat salah satunya diam-diam menang.
  const problems = useMemo(() => {
    const seenOctet = new Map();
    const seenName = new Map();
    const out = [];

    for (const r of table) {
      const octet = String(r.octet).trim();
      const name = r.name.trim();
      if (!octet && !name) continue;

      if (!octet || Number(octet) < 1 || Number(octet) > 254) {
        out.push(`Oktet "${octet || 'kosong'}" di luar 1–254`);
        continue;
      }
      if (!name) { out.push(`Oktet ${octet} belum diberi nama`); continue; }

      if (seenOctet.has(octet)) out.push(`Oktet ${octet} tercantum dua kali`);
      else seenOctet.set(octet, name);

      if (seenName.has(name)) out.push(`Nama ${name} dipakai dua oktet`);
      else seenName.set(name, octet);
    }
    return out;
  }, [table]);

  // Unit yang mendaftar dari oktet yang belum ada di tabel. Inilah jawaban
  // dari "kenapa PC itu bernama seperti nama mesin Windows-nya".
  const unmapped = useMemo(() => sorted.filter((u) => {
    if (!u.ipAddress?.startsWith(view.subnetPrefix)) return false;
    const o = octetOf(u.ipAddress);
    return o !== null && !table.some((r) => Number(r.octet) === o);
  }), [sorted, table, view.subnetPrefix]);

  const TOGGLES = [
    { k: 'requireStudentLogin', v: requireLogin, set: setRequireLogin,
      t: 'Wajib login mahasiswa', s: 'Harus memasukkan nama dan NIM sebelum tersambung' },
    { k: 'allowGuestAccess', v: allowGuest, set: setAllowGuest,
      t: 'Izinkan akses tamu', s: 'Masuk tanpa akun terdaftar' },
    { k: 'studentsMayRejectControl', v: allowReject, set: setAllowReject,
      t: 'Mahasiswa boleh menolak kontrol', s: 'Menolak permintaan kontrol jarak jauh' },
  ];

  return (
    <div className="body">
      <div className="pane" style={{ marginBottom: 14 }}>
        <div style={{ display: 'flex', alignItems: 'baseline', gap: 10 }}>
          <h3 style={{ flex: 1 }}>
            Unit terdaftar <span className="count">{online}/{units.length} aktif</span>
          </h3>
        </div>

        {/*
          Daftar ini berasal dari unit yang BENAR-BENAR mendaftar, bukan dari
          rumus penomoran.

          Sebelumnya layar ini menampilkan tabel PC-01 / PC-12 / PC-40 dengan
          alamat yang dihitung. Tabel itu terlihat sama meyakinkannya baik
          lab-nya menyala maupun mati, dan sama meyakinkan ketika awalan
          subnetnya salah — karena tidak satu angka pun di dalamnya berasal
          dari jaringan.
        */}
        {units.length === 0 ? (
          <div className="hint">
            Belum ada unit yang mendaftar. Daftar ini terisi sendiri saat agent
            di PC mahasiswa menyambung.
          </div>
        ) : (
          <>
            <table className="table">
              <thead>
                <tr>
                  <th>Nama unit</th>
                  <th>Nama PC (Windows)</th>
                  <th>Alamat IP</th>
                  <th>MAC</th>
                  <th>Status</th>
                  <th style={{ width: 34 }} />
                </tr>
              </thead>
              <tbody>
                {sorted.map((u) => (
                  <tr key={u.unitId} style={{ opacity: u.status === 'Offline' ? 0.55 : 1 }}>
                    <td>
                      {renaming === u.unitId ? (
                        <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                          <input value={renameDraft} autoFocus
                            onChange={(e) => setRenameDraft(e.target.value)}
                            onKeyDown={(e) => {
                              if (e.key === 'Escape') setRenaming(null);
                              if (e.key === 'Enter' && renameDraft.trim()) {
                                send('unitRename', { unitId: u.unitId, alias: renameDraft.trim() });
                                setRenaming(null);
                              }
                            }}
                            style={{ width: '100%', fontSize: 12 }} />
                          <button className="icon-btn" title="Simpan"
                            disabled={!renameDraft.trim()}
                            onClick={() => {
                              send('unitRename', { unitId: u.unitId, alias: renameDraft.trim() });
                              setRenaming(null);
                            }}><FloppyDisk size={13} /></button>
                          <button className="icon-btn" title="Batal"
                            onClick={() => setRenaming(null)}><X size={13} /></button>
                        </div>
                      ) : (
                        <>
                          <span className="mono" style={{ whiteSpace: 'nowrap' }}>{u.unitId}</span>

                          {/*
                              Nama yang diketik mahasiswa disebut DI BAWAH nomor
                              unit, bukan menggantikannya.

                              Layar ini adalah tempat asisten menata denah lab,
                              dan yang ia butuhkan di sini adalah nomor unit yang
                              sebenarnya. Nama dari mahasiswa ditampilkan sebagai
                              keterangan supaya ia tahu unit itu sudah punya nama
                              yang dikenali — dan supaya tidak bertanya-tanya
                              mengapa dashboard menyebutnya berbeda.
                          */}
                          {u.selfName && (
                            <div style={{ fontSize: 11, color: 'var(--ink-dim)' }}>
                              {u.selfName} · dari mahasiswa
                            </div>
                          )}
                        </>
                      )}
                    </td>

                    <td className="mono" style={{ fontSize: 11.5, color: 'var(--ink-muted)' }}>
                      {u.hostName || '—'}
                    </td>
                    <td className="mono" style={{ whiteSpace: 'nowrap' }}>{u.ipAddress || '—'}</td>

                    {/* MAC ditampilkan karena Wake-on-LAN dialamatkan kepadanya.
                        Ketika sebuah PC tidak mau menyala, kolom kosong di sini
                        adalah jawabannya — dan nilai itu sebelumnya tidak muncul
                        di layar mana pun. */}
                    <td className="mono" style={{ fontSize: 11,
                      color: u.macAddress ? 'var(--ink-muted)' : 'var(--warn)' }}>
                      {u.macAddress || 'tidak diketahui'}
                    </td>

                    <td className="mono" style={{ fontSize: 11.5 }}>{u.status}</td>
                    <td>
                      {editable && renaming !== u.unitId && (
                        <button className="icon-btn" title="Ganti nama unit"
                          onClick={() => { setRenaming(u.unitId); setRenameDraft(u.unitId); }}>
                          <PencilSimple size={13} />
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>

            <div className="hint" style={{ marginTop: 8 }}>
              Mengganti nama unit mengubah namanya di seluruh aplikasi, termasuk
              percakapan dan jejak aktivitas yang sudah tercatat. Nama PC di Windows
              tidak ikut berubah.
              {!editable && ' Hanya dapat diubah dari PC Asisten.'}
            </div>
          </>
        )}
      </div>

      <div className="two">
        <div className="pane">
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 4 }}>
            <h3 style={{ flex: 1, margin: 0 }}>Penomoran &amp; jaringan</h3>

            {/*
              Kolom terkunci sampai Edit ditekan.

              Kolom yang selalu terbuka mengundang perubahan yang tidak
              disengaja — satu ketukan tombol di kolom oktet mengubah penomoran
              seluruh lab, dan akibatnya baru muncul saat PC berikutnya
              mendaftar. Batal harus selalu tersedia sampai Simpan ditekan.
            */}
            {!editable ? null : editing ? (
              <>
                <button className="btn btn-quiet" onClick={cancelEdit}>
                  <X size={13} /> Batal
                </button>
                <button className="btn btn-primary" disabled={problems.length > 0} onClick={saveEdit}>
                  <FloppyDisk size={13} /> Simpan
                </button>
              </>
            ) : (
              <button className="btn btn-quiet" onClick={beginEdit}>
                <PencilSimple size={13} /> Edit
              </button>
            )}
          </div>

          <div className="hint" style={{ marginBottom: 12 }}>
            Koordinator memakai tabel ini untuk menamai unit yang baru pertama kali
            mendaftar. Unit yang sudah terdaftar mempertahankan namanya walau alamatnya
            berubah — ganti namanya dari daftar di atas.
          </div>

          {!editable && (
            <div className="hint warn" style={{ marginBottom: 10 }}>
              Hanya dapat diubah di PC Asisten yang menjalankan koordinator.
              Berkas konfigurasi bersifat lokal, jadi perubahan dari panel lain
              tidak akan terbaca oleh lab.
            </div>
          )}

          <div className="field">
            <label>Nama lab</label>
            <input value={view.labName} disabled={!editing}
              onChange={(e) => setDraft({ ...view, labName: e.target.value })} />
          </div>

          <div className="row2">
            <div className="field">
              <label>Awalan subnet</label>
              <input value={view.subnetPrefix} disabled={!editing} placeholder="192.168.1."
                onChange={(e) => setDraft({ ...view, subnetPrefix: e.target.value })} />
            </div>
            <div className="field">
              <label>Port koordinator</label>
              <input type="number" min="1" max="65535" value={view.listenPort} disabled={!editing}
                onChange={(e) => setDraft({ ...view, listenPort: e.target.value })} />
            </div>
          </div>

          {/* Perubahan port TIDAK berlaku sampai koordinator dimulai ulang, dan
              itu dikatakan di tempat perubahannya dilakukan. Asisten yang hanya
              melihat "Tersimpan" akan mengira lab sudah berpindah porta
              sementara empat puluh agent masih menyambung ke yang lama. */}
          <div className="hint" style={{ marginTop: -4, marginBottom: 12 }}>
            Port berlaku setelah koordinator dimulai ulang.
          </div>

          <h4 style={{ fontSize: 12, fontWeight: 600, margin: '0 0 6px' }}>
            Oktet → nama unit
            <span className="count">{table.length} baris</span>
          </h4>

          {/*
            Daftar, bukan rumus.

            Rumus berurutan mensyaratkan barisan yang tak terputus: PC-01 di
            oktet awal, PC-02 satu setelahnya. Lab sungguhan memutusnya — .1
            dihindari karena lazim dipakai gerbang jaringan, sehingga PC-01
            pindah ke .43 sementara PC-02 tetap di .2. Tidak ada oktet awal mana
            pun yang menghasilkan kedua nomor itu sekaligus.
          */}
          {table.length === 0 ? (
            <div className="hint">Belum ada pemetaan. Unit yang mendaftar akan memakai nama mesin Windows-nya.</div>
          ) : (
            <table className="table">
              <thead>
                <tr>
                  <th style={{ width: 96 }}>Oktet</th>
                  <th>Nama unit</th>
                  <th>Alamat penuh</th>
                  <th style={{ width: 30 }} />
                </tr>
              </thead>
              <tbody>
                {table.map((r, i) => (
                  <tr key={i}>
                    <td>
                      <input type="number" min="1" max="254" value={r.octet} disabled={!editing}
                        onChange={(e) => setRows(table.map((x, j) =>
                          j === i ? { ...x, octet: e.target.value } : x))}
                        style={{ width: '100%', fontSize: 12 }} />
                    </td>
                    <td>
                      <input value={r.name} disabled={!editing} placeholder="PC-01"
                        onChange={(e) => setRows(table.map((x, j) =>
                          j === i ? { ...x, name: e.target.value } : x))}
                        style={{ width: '100%', fontSize: 12 }} />
                    </td>
                    <td className="mono" style={{ fontSize: 11.5, color: 'var(--ink-muted)' }}>
                      {view.subnetPrefix}{r.octet}
                    </td>
                    <td>
                      {editing && (
                        <button className="icon-btn" title="Hapus baris"
                          onClick={() => setRows(table.filter((_, j) => j !== i))}>
                          <Trash size={13} />
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {editing && (
            <button className="btn btn-quiet" style={{ marginTop: 8 }}
              onClick={() => setRows([...table, { octet: '', name: '' }])}>
              <Plus size={13} /> Tambah baris
            </button>
          )}

          {/* Kesalahan dicari sebelum Simpan aktif. Dua baris dengan oktet sama
              membuat salah satunya diam-diam menang, dan yang kalah tidak
              meninggalkan jejak apa pun. */}
          {editing && problems.length > 0 && (
            <div className="hint warn" style={{ marginTop: 8 }}>
              {problems.slice(0, 4).map((p, i) => <div key={i}>{p}</div>)}
              {problems.length > 4 && <div>dan {problems.length - 4} lainnya</div>}
            </div>
          )}

          {!editing && unmapped.length > 0 && (
            <div className="hint warn" style={{ marginTop: 8 }}>
              {unmapped.length} unit berada di subnet lab tetapi oktetnya belum dipetakan:
              {' '}{unmapped.slice(0, 4).map((u) => `${u.unitId} (${u.ipAddress})`).join(', ')}
              {unmapped.length > 4 && ` dan ${unmapped.length - 4} lainnya`}.
            </div>
          )}

          <h3 className="mt">Status &amp; Pembaruan Aplikasi</h3>
          <table className="table">
            <tbody>
              <tr><td>Versi Aplikasi</td><td className="mono" style={{ fontWeight: 600, color: 'var(--accent)' }}>v{settings?.appVersion ?? '1.0.0'}</td></tr>
              <tr><td>Peran</td><td className="mono">{settings?.role ?? '—'}</td></tr>
              <tr><td>Transport</td><td className="mono">HTTPS · self-signed</td></tr>
              <tr><td>Folder data</td><td className="mono" style={{ fontSize: 11 }}>{settings?.dataDirectory ?? '—'}</td></tr>
            </tbody>
          </table>

          <div style={{ marginTop: 14, padding: '12px', background: 'var(--surface-2)', borderRadius: 'var(--r)' }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10 }}>
              <div>
                <div style={{ fontSize: 12.5, fontWeight: 600 }}>Pembaruan Aplikasi</div>
                <div style={{ fontSize: 11.5, color: 'var(--ink-dim)' }}>
                  Periksa rilis terbaru dari repositori GitHub resmi.
                </div>
              </div>
              <button className="btn btn-sm" onClick={() => send('checkUpdates')}>
                <ArrowsClockwise size={14} />Periksa Pembaruan
              </button>
            </div>

            {updateInfo && (
              <div style={{ marginTop: 12, padding: '10px 12px', background: 'var(--surface-1)', border: '1px solid var(--border)', borderRadius: 'var(--r)' }}>
                {updateInfo.ready ? (
                  <div>
                    <div style={{ fontSize: 12.5, fontWeight: 600, color: 'var(--ok)', display: 'flex', alignItems: 'center', gap: 6 }}>
                      <CheckCircle size={15} weight="fill" /> Pembaruan v{updateInfo.version} Siap Dipasang!
                    </div>
                    <div style={{ fontSize: 11.5, color: 'var(--ink-muted)', marginTop: 4 }}>
                      Installer telah selesai diunduh di latar belakang. Klik tombol di bawah untuk memasang pembaruan secara otomatis.
                    </div>
                    <button className="btn btn-sm btn-primary" style={{ marginTop: 8 }} onClick={() => send('installUpdateNow')}>
                      <DownloadSimple size={14} />Pasang Pembaruan Sekarang
                    </button>
                  </div>
                ) : (
                  <div>
                    <div style={{ fontSize: 12.5, fontWeight: 600, color: 'var(--accent)', display: 'flex', alignItems: 'center', gap: 6 }}>
                      <ArrowsClockwise size={15} className="spin" /> Pembaruan v{updateInfo.version} Ditemukan
                    </div>
                    <div style={{ fontSize: 11.5, color: 'var(--ink-muted)', marginTop: 4 }}>
                      Paket rilis baru sedang diunduh di latar belakang...
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>
        </div>

        <div className="pane">
          {/*
            Kredensial agent hanya untuk pemegang ManageCredentials — Superadmin.

            Nilai ini membuka pendaftaran unit mana pun ke lab, berumur selamanya,
            dan tidak ada langkah persetujuan yang menahannya seperti pada panel.
            Menampilkannya di setiap laptop dosen berarti memperbanyak salinannya
            sebanyak dosen yang mengajar.
          */}
          {can('ManageCredentials') && (
            <>
              <h3>Pendaftaran agent</h3>
              <div className="hint" style={{ marginBottom: 12 }}>
                Kedua nilai ini ditanamkan installer ke tiap PC mahasiswa. Berbeda dari kode
                pendaftaran panel di layar Panel admin: yang ini berumur selamanya dan dipakai
                agent, bukan panel admin.
              </div>

              <div className="field">
                <label>Sidik jari sertifikat (SHA-256)</label>
                <Secret value={settings?.certificateThumbprint} />
              </div>
              <div className="field">
                <label>Kode pendaftaran agent</label>
                <Secret value={settings?.agentEnrollmentSecret} />
              </div>
            </>
          )}

          <h3 className={can('ManageCredentials') ? 'mt' : undefined}>Akses &amp; keamanan</h3>

          {/* Tanpa Configure, saklar ditampilkan sebagai keadaan saja.
              Disembunyikan sama sekali, dosen tidak punya cara mengetahui
              apakah login mahasiswa sedang diwajibkan — padahal itu justru
              yang perlu ia ketahui sebelum kelas dimulai. */}
          {!can('Configure') && (
            <div className="hint" style={{ marginBottom: 8 }}>
              Hanya dapat diubah dari PC Asisten.
            </div>
          )}

          {TOGGLES.map((tg) => (
            <div key={tg.k} style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '9px 0',
              borderTop: '1px solid var(--divider)' }}>
              <div style={{ flex: 1 }}>
                <div style={{ fontSize: 12.5, fontWeight: 600 }}>{tg.t}</div>
                <div style={{ fontSize: 11.5, color: 'var(--ink-dim)', marginTop: 2 }}>{tg.s}</div>
              </div>
              <button className={`switch${tg.v ? ' on' : ''}`} disabled={!can('Configure')}
                onClick={() => {
                  if (!can('Configure')) return;
                  tg.set(!tg.v);
                  patchToggle({ [tg.k]: !tg.v });
                }} />
            </div>
          ))}

          {saved && (
            <div style={{ fontSize: 11.5, color: 'var(--ok)', marginTop: 10 }}>
              Tersimpan ke console.json
            </div>
          )}

          <h3 className="mt">Versi &amp; Pembaruan</h3>
          <div className="hint" style={{ marginBottom: 12 }}>
            Retriva mendukung pembaruan otomatis di latar belakang dari GitHub Releases atau pengecekan langsung.
          </div>

          <div style={{
            background: 'var(--surface-sunken)',
            borderRadius: 'var(--radius)',
            padding: '12px 14px',
            border: '1px solid var(--border-soft)',
            display: 'flex',
            flexDirection: 'column',
            gap: 10,
          }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
              <div>
                <div style={{ fontSize: 13, fontWeight: 600 }}>
                  Versi Aplikasi: <span style={{ color: 'var(--accent)' }}>v{settings?.appVersion ?? '1.0.0'}</span>
                </div>
                <div style={{ fontSize: 11.5, color: 'var(--ink-dim)', marginTop: 2 }}>
                  {updateInfo?.ready
                    ? `Pembaruan v${updateInfo.version} telah siap dipasang`
                    : updateInfo
                      ? `Sedang mengunduh v${updateInfo.version} di latar belakang...`
                      : 'Memeriksa rilis terbaru secara berkala'}
                </div>
              </div>
              <button className="btn btn-quiet" onClick={() => send('checkUpdates')} title="Periksa apakah ada rilis versi baru di GitHub">
                <ArrowsClockwise size={14} />Periksa Pembaruan
              </button>
            </div>

            {updateInfo?.ready && (
              <div style={{
                background: 'var(--surface)',
                border: '1px solid var(--accent)',
                borderRadius: 'var(--radius-sm)',
                padding: '10px 12px',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'space-between',
                marginTop: 4,
                gap: 12,
              }}>
                <div>
                  <div style={{ fontSize: 12.5, fontWeight: 600, color: 'var(--accent)' }}>
                    Versi {updateInfo.version} telah siap
                  </div>
                  <div style={{ fontSize: 11, color: 'var(--ink-muted)' }}>
                    Klik pasang untuk menerapkan pembaruan tanpa menghapus data dan konfigurasi.
                  </div>
                </div>
                <button className="btn btn-primary" onClick={() => send('installUpdateNow')}>
                  <DownloadSimple size={14} />Pasang Sekarang
                </button>
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
