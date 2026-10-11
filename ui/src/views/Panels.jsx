import { useEffect, useState } from 'react';
import { send } from '../bridge';

/*
  Layar Panel admin.

  Isinya berbeda menurut keadaan tautan, bukan menurut peran semata:

    belum tertaut          -> formulir penautan
    tertaut, belum nyambung-> tombol coba lagi
    tertaut, menunggu      -> keterangan menunggu persetujuan
    Superadmin             -> kode pendaftaran dan daftar persetujuan

  Semuanya di satu tempat karena semuanya menjawab pertanyaan yang sama: panel
  ini bagian dari lab mana, dan apa wewenangnya. Menyebarnya ke beberapa layar
  membuat orang yang panelnya tidak tersambung harus menebak ke mana mencari.
*/

/** Sisa waktu jendela pendaftaran, dihitung ulang tiap detik. */
function useCountdown(expiresAt) {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!expiresAt) return undefined;
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, [expiresAt]);

  if (!expiresAt) return null;
  const left = Math.max(0, new Date(expiresAt).getTime() - now);
  const m = Math.floor(left / 60000);
  const s = Math.floor((left % 60000) / 1000);
  return `${m}:${String(s).padStart(2, '0')}`;
}

const ROLE_NOTE = {
  Dosen: 'Mengajar: siaran, chat, kunci layar, kendali jarak jauh.',
  Asisten: 'Merawat: seluruh wewenang Dosen ditambah daya dan konfigurasi.',
};

/* ---------------- belum tertaut: formulir penautan ---------------- */

function JoinForm({ link, labs }) {
  const [manual, setManual] = useState(false);
  const [host, setHost] = useState(link?.host ?? '');

  /*
    SATU LAYAR, SATU KEPUTUSAN, SATU TOMBOL.

    Versi sebelumnya menampilkan kode perangkat, pilihan peran, dan tiga
    paragraf keterangan sekaligus. Semuanya benar dan semuanya tidak perlu:
    dosen hanya ingin masuk ke labnya, dan setiap hal tambahan di layar ini
    adalah satu hal lagi yang harus ia putuskan sebelum boleh mengajar.

    Peran tidak lagi ditanyakan di sini. Yang menentukannya memang asisten saat
    menyetujui, jadi menanyakannya ke dosen hanya memindahkan keputusan ke orang
    yang tidak berwenang mengambilnya — lalu mengabaikan jawabannya.
  */
  return (
    <div className="pane">
      <h3>Belum tertaut ke lab</h3>
      <div className="hint">
        Pilih lab, lalu minta asisten menyetujuinya di PC Asisten.
      </div>

      {labs.length === 0 ? (
        <div className="empty" style={{ padding: '28px 12px' }}>
          Mencari lab di jaringan…
          <div className="hint" style={{ marginTop: 8 }}>
            Pastikan PC Asisten menyala dan berada di jaringan yang sama.
          </div>
        </div>
      ) : labs.map((l) => (
        <div key={`${l.host}:${l.port}`} className="panel-row">
          <span className={`badge-dot${l.accepting ? '' : ' off'}`} />
          <div style={{ flex: 1 }}>
            <div className="panel-name">{l.labName}</div>
            <div className="hint">
              {l.host}{l.accepting ? '' : ' · sedang tidak menerima'}
            </div>
          </div>
          <button className="btn btn-primary" disabled={!l.accepting}
            title={l.accepting
              ? 'Kirim permintaan bergabung'
              : 'Minta asisten menekan "Terima permintaan bergabung"'}
            onClick={() => send('linkJoin', { host: l.host, port: l.port })}>
            Gabung
          </button>
        </div>
      ))}

      {link?.rejectReason && (
        <div className="notice" style={{ marginTop: 14 }}>{link.rejectReason}</div>
      )}

      {/* Dua jalan keluar, sengaja dibuat tenang: keduanya jarang dipakai, dan
          yang jarang dipakai tidak boleh ikut menarik perhatian setiap hari. */}
      <div style={{ display: 'flex', gap: 8, marginTop: 18 }}>
        <button className="btn btn-quiet" onClick={() => setManual(!manual)}>
          Lab tidak muncul?
        </button>
        {!link?.hostsLab && (
          <button className="btn btn-quiet"
            title="Pilih bila tidak ada PC Asisten lain. Berlaku setelah Retriva dibuka ulang."
            onClick={() => send('linkBecomeSuperadmin')}>
            Mesin ini yang menjalankan lab
          </button>
        )}
      </div>

      {manual && (
        <div className="field" style={{ marginTop: 12 }}>
          <label>Alamat PC Asisten</label>
          <input value={host} placeholder="192.168.1.252"
            onChange={(e) => setHost(e.target.value)} />
          <button className="btn" style={{ width: '100%', marginTop: 10 }}
            disabled={host.trim().length === 0}
            onClick={() => send('linkJoin', { host: host.trim() })}>
            Gabung ke alamat ini
          </button>
        </div>
      )}
    </div>
  );
}

/* ---------------- sudah tertaut: keadaan tautan ---------------- */

function LinkState({ link }) {
  /*
    "Menunggu persetujuan" HANYA bila memang itu yang terjadi.

    Versi sebelumnya menyimpulkannya dari "tersambung tetapi belum siap" — dan
    keadaan itu juga muncul saat pendaftaran DITOLAK. Token tidak cocok,
    pendaftaran tertutup, kode kedaluwarsa: semuanya tampil sebagai "menunggu
    persetujuan", termasuk di panel Superadmin yang tidak menunggu siapa pun.

    Alasan penolakan sudah dikirim sejak awal; yang salah hanyalah layar ini
    tidak pernah menampilkannya. Sekarang alasan yang ada selalu menang atas
    tebakan.
  */
  const rejected = Boolean(link?.rejectReason);
  const pending = link?.connected && !link?.ready && !rejected;

  return (
    <div className="pane">
      <h3>Tautan lab</h3>

      <table className="table">
        <tbody>
          <tr><td>Lab</td><td className="mono">{link?.labName ?? '—'}</td></tr>
          {/*
              Label diperjelas: ini alamat yang dipakai PANEL INI, bukan alamat lab.

              Di mesin Superadmin nilainya selalu 127.0.0.1 — panel menyambung
              ke koordinatornya sendiri lewat loopback. Tanpa keterangan itu,
              siapa pun yang membuka layar ini untuk mencari "alamat lab" akan
              menyalin 127.0.0.1 ke PC mahasiswa, dan di sana angka itu berarti
              "mesin ini sendiri".
          */}
          <tr>
            <td>Panel ini menyambung ke</td>
            <td className="mono">{link?.host}:{link?.port}</td>
          </tr>
          <tr><td>Peran</td><td className="mono">{link?.role ?? '—'}</td></tr>
          <tr><td>Sambungan</td>
            <td className="mono">{link?.connected ? 'tersambung' : 'terputus'}</td></tr>

          {/* Hanya di mesin yang menjalankan lab. Panel dosen tidak melayani
              siapa pun, jadi alamatnya tidak berguna bagi mesin mana pun. */}
          {link?.hostsLab && (link?.labAddresses?.length ?? 0) > 0 && (
            <tr>
              <td>Alamat untuk unit lain</td>
              <td className="mono">
                {link.labAddresses.map((a) => (
                  <div key={a}>{a}:{link.port}</div>
                ))}
              </td>
            </tr>
          )}
        </tbody>
      </table>

      {link?.hostsLab && (link?.labAddresses?.length ?? 0) > 0 && (
        <div className="hint" style={{ marginTop: 10, lineHeight: 1.7 }}>
          Isikan salah satu alamat di atas saat memasang Retriva di PC mahasiswa —
          yang satu jaringan dengan PC itu. Jangan pakai 127.0.0.1: di mesin lain
          angka itu berarti mesinnya sendiri, dan agent tidak akan menemukan
          koordinator mana pun.
        </div>
      )}

      {pending && (
        <div className="notice" style={{ marginTop: 14 }}>
          Menunggu persetujuan dari PC Asisten. Panel ini akan aktif sendiri begitu disetujui —
          tidak perlu memasukkan kode lagi.
        </div>
      )}

      {rejected && (
        <div className="notice" style={{ marginTop: 14 }}>{link.rejectReason}</div>
      )}

      {(!link?.connected || rejected) && (
        <button className="btn" style={{ width: '100%', marginTop: 12 }}
          onClick={() => send('linkRetry')}>
          Coba sambungkan lagi
        </button>
      )}

      {link?.thumbprint && (
        <div className="field" style={{ marginTop: 18 }}>
          <label>Sidik jari sertifikat lab</label>
          <code style={{ fontSize: 10.5, wordBreak: 'break-all', color: 'var(--ink-muted)' }}>
            {link.thumbprint}
          </code>
        </div>
      )}

      {/*
        Lab yang dikenali panel ini — semuanya, bukan hanya yang sedang dipakai.

        Dosen mengajar di lebih dari satu ruangan. Panel menyimpan setiap lab
        yang pernah menyetujuinya, dan menyambung sendiri begitu mendengar
        salah satunya. Berpindah ruangan tidak menuntut apa pun; kembali ke
        ruangan kemarin juga tidak.
      */}
      {!link?.hostsLab && (link?.knownLabs?.length ?? 0) > 0 && (
        <>
          <h3 className="mt">Lab yang dikenali</h3>
          <div className="hint">
            Panel ini menyambung sendiri ke lab mana pun di daftar ini begitu terdengar
            di jaringan. Tidak perlu mendaftar ulang saat berpindah ruangan.
          </div>

          {link.knownLabs.map((l) => (
            <div key={l.thumbprint} className="panel-row">
              <span className={`badge-dot${l.current ? '' : ' off'}`} />
              <div style={{ flex: 1 }}>
                <div className="panel-name">{l.labName}</div>
                <div className="hint">{l.host}{l.current ? ' · sedang dipakai' : ''}</div>
              </div>
              <button className="btn btn-quiet"
                title="Perlu bila PC Asisten lab ini diinstal ulang — sertifikatnya berganti."
                onClick={() => send('linkReset', { thumbprint: l.thumbprint })}>
                Lupakan
              </button>
            </div>
          ))}
        </>
      )}
    </div>
  );
}

/* ---------------- Superadmin: pendaftaran dan persetujuan ---------------- */

function EnrollmentPane({ enrollment }) {
  const left = useCountdown(enrollment?.open ? enrollment.expiresAt : null);

  return (
    <div className="pane">
      <h3>Pendaftaran panel</h3>
      <div className="hint">
        Nyalakan sebentar saat akan mendaftarkan laptop dosen. Selama menyala, lab ini
        terlihat oleh panel di jaringan dan permintaan mereka masuk ke daftar di sebelah —
        masuk daftar saja, belum berwenang apa pun sampai disetujui.
      </div>

      {enrollment?.open ? (
        <>
          <div className="notice ok" style={{ marginTop: 12 }}>
            Menerima permintaan bergabung · menutup sendiri dalam {left}
          </div>
          <button className="btn" style={{ width: '100%', marginTop: 12 }}
            onClick={() => send('enrollmentClose')}>
            Berhenti menerima
          </button>
        </>
      ) : (
        <button className="btn btn-primary" style={{ width: '100%', marginTop: 12 }}
          onClick={() => send('enrollmentOpen')}>
          Terima permintaan bergabung
        </button>
      )}

      {enrollment?.thumbprint && (
        <div className="field" style={{ marginTop: 18 }}>
          <label>Sidik jari sertifikat lab</label>
          <code style={{ fontSize: 10.5, wordBreak: 'break-all', color: 'var(--ink-muted)' }}>
            {enrollment.thumbprint}
          </code>
          <div className="hint" style={{ marginTop: 6 }}>
            Panel yang baru mendaftar menampilkan nilai yang sama. Berbeda berarti ada yang
            menjawab selain lab ini.
          </div>
        </div>
      )}
    </div>
  );
}

function ApprovalPane({ panels }) {
  /*
    "Menunggu persetujuan" berisi permohonan, BUKAN pencabutan.

    Keduanya berperan Pending — sama-sama tidak boleh melakukan apa pun — tetapi
    artinya berlawanan. Yang satu orang yang sedang menunggu jawaban; yang lain
    keputusan yang baru saja diambil asisten sendiri. Ditampilkan bersama,
    mencabut wewenang sebuah panel langsung memunculkannya kembali di daftar
    tunggu, seolah ia memohon masuk lagi sedetik setelah dikeluarkan.
  */
  const pending = panels.filter((p) => p.role === 'Pending' && !p.revoked);
  const known = panels.filter((p) => p.role !== 'Pending' || p.revoked);

  return (
    <div className="pane">
      <h3>
        Menunggu persetujuan
        {pending.length > 0 && <span className="pill">{pending.length}</span>}
      </h3>

      {pending.length === 0 ? (
        <div className="hint" style={{ padding: '10px 0' }}>Tidak ada panel yang menunggu.</div>
      ) : pending.map((p) => (
        <div key={p.deviceId} className="panel-row">
          <div style={{ flex: 1 }}>
            <div className="panel-name">{p.displayName}</div>
            <div className="hint">{p.wired ? 'Kabel' : 'Wi-Fi'} · {p.deviceId.slice(0, 8)}</div>
          </div>
          {['Dosen', 'Asisten'].map((role) => (
            <button key={role} title={ROLE_NOTE[role]}
              className={`btn${p.requestedRole === role ? ' btn-primary' : ''}`}
              onClick={() => send('panelApprove', { deviceId: p.deviceId, role })}>
              {role}
            </button>
          ))}
        </div>
      ))}

      <h3 className="mt">Panel terdaftar</h3>
      {known.length === 0 ? (
        <div className="hint" style={{ padding: '10px 0' }}>Belum ada.</div>
      ) : known.map((p) => (
        <div key={p.deviceId} className="panel-row">
          <span className={`badge-dot${p.online && !p.revoked ? '' : ' off'}`} />
          <div style={{ flex: 1 }}>
            <div className="panel-name">{p.displayName}</div>
            <div className="hint">
              {p.revoked ? 'Wewenang dicabut' : p.role} · {p.wired ? 'kabel' : 'Wi-Fi'}
              {p.online ? '' : ' · tidak tersambung'}
            </div>
          </div>

          {/* Panel yang dicabut ditawari pemulihan di tempatnya sendiri, bukan
              dilempar kembali ke daftar tunggu. Asisten yang mencabutnya
              kemarin adalah orang yang sama yang mungkin mengembalikannya
              hari ini, dan ia mencarinya di baris tempat ia meninggalkannya. */}
          {p.revoked
            ? ['Dosen', 'Asisten'].map((role) => (
                <button key={role} className="btn" title={ROLE_NOTE[role]}
                  onClick={() => send('panelApprove', { deviceId: p.deviceId, role })}>
                  {role}
                </button>
              ))
            : p.role !== 'Superadmin' && (
                // Superadmin tidak dapat mencabut dirinya sendiri. Kalau bisa,
                // satu klik meninggalkan lab tanpa seorang pun yang berwenang
                // menyetujui siapa pun, dan tidak ada jalan keluar selain
                // menghapus basis datanya.
                <button className="btn btn-quiet"
                  onClick={() => send('panelRevoke', { deviceId: p.deviceId })}>
                  Cabut
                </button>
              )}
        </div>
      ))}
    </div>
  );
}

export default function Panels({ panels, enrollment, link, labs = [] }) {
  const isSuperadmin = link?.role === 'Superadmin';

  // Diminta saat layar dibuka dan setiap kali peran berubah — persetujuan yang
  // baru turun membuat daftar ini berarti, padahal sebelumnya ditolak.
  useEffect(() => { send('panelsRefresh'); }, [link?.role]);

  /*
    Mesin yang menjalankan lab TIDAK PERNAH melihat formulir penautan.

    Bukan sekadar kerapian. Pada jalankan pertama ia belum memegang token —
    tokennya baru terbit setelah panelnya mendaftar lewat loopback — sehingga
    "sudah tertaut" masih bernilai salah selama beberapa ratus milidetik
    pertama. Menilai dari itu saja, Superadmin akan disodori formulir yang
    memintanya mendaftar ke dirinya sendiri.

    Yang menentukan adalah hostsLab: pertanyaan "apakah mesin ini menjalankan
    lab" sudah dijawab saat penyiapan dan tidak bergantung pada sambungan.
  */
  const needsJoin = !link?.hostsLab && !link?.linked;

  return (
    <div className="body">
      <div className="two">
        {needsJoin ? <JoinForm link={link} labs={labs} /> : <LinkState link={link} />}
        {isSuperadmin
          ? <ApprovalPane panels={panels} />
          : <EnrollmentPaneHidden />}
      </div>
      {isSuperadmin && (
        <div className="two" style={{ marginTop: 16 }}>
          <EnrollmentPane enrollment={enrollment} />
          <div />
        </div>
      )}
    </div>
  );
}

/*
  Kolom kanan untuk panel non-Superadmin.

  Sengaja menjelaskan apa yang TIDAK ada di sini dan di mana tempatnya, bukan
  dibiarkan kosong. Ruang kosong terbaca sebagai sesuatu yang gagal dimuat.
*/
function EnrollmentPaneHidden() {
  return (
    <div className="pane">
      <h3>Persetujuan panel</h3>
      <div className="hint">
        Pendaftaran dan persetujuan panel dilakukan di PC Asisten — mesin yang menjalankan lab.
        Panel ini tidak menyimpan kredensial siapa pun selain miliknya sendiri.
      </div>
    </div>
  );
}
