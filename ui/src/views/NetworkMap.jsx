import { useMemo } from 'react';
import { WifiHigh, Plugs, Question, WarningCircle } from '@phosphor-icons/react';
import { unitLabel } from '../unitName';

/*
  Peta jaringan, menggantikan diagram topologi.

  YANG DIGANTIKAN DAN MENGAPA.

  Layar sebelumnya menggambar satu router, dua switch bernama SW-1 dan SW-2,
  dan empat puluh unit yang menggantung rapi di bawahnya. Gambar itu indah dan
  seluruhnya karangan: nama switch-nya ditulis di dalam kode, pembagian PC 01–20
  dan 21–40 ditentukan oleh potongan array, dan garis-garisnya digambar tanpa
  satu paket pun diperiksa.

  Switch tak-terkelola memang tidak dapat dideteksi. Ia tidak punya alamat IP,
  tidak menjawab apa pun, dan tidak muncul di traceroute — mendeteksinya
  menuntut LLDP, CDP, atau SNMP, yang hanya ada pada perangkat terkelola.
  Jadi pilihannya bukan antara gambar yang tepat dan gambar yang kasar,
  melainkan antara gambar yang jujur dan gambar yang mengaku tahu.

  Diagram karangan lebih buruk daripada tidak ada diagram. Ketika PC-23
  bermasalah, asisten akan melihat gambarnya menggantung di SW-2 dan pergi
  memeriksa switch yang mungkin tidak ada, atau ada tetapi tidak melayani PC
  itu. Waktunya habis untuk mengejar hal yang tidak pernah diukur.

  Yang tersisa di layar ini hanya yang benar-benar diukur:

    · jenis dan kecepatan tautan  — diukur di unit, dilaporkan agent
    · waktu pulang-pergi          — diukur koordinator, satu jam, dua ujung
    · alamat koordinator          — alamat yang benar-benar dipakai unit itu
    · penerima siaran             — dilaporkan unit yang menerimanya

  Pengelompokannya memakai alamat koordinator, bukan switch. Unit yang
  menjangkau koordinator lewat alamat yang sama berbagi jalur — bukan berarti
  satu switch, tetapi itulah pernyataan terkuat yang datanya sanggup dukung.
*/

function fmtLatency(ms) {
  if (ms === null || ms === undefined) return null;
  return ms < 10 ? `${ms.toFixed(1)} ms` : `${Math.round(ms)} ms`;
}

/** Ambang dipilih untuk jaringan lab kabel, tempat 1 ms adalah hal biasa. */
function latencyTone(ms) {
  if (ms === null || ms === undefined) return 'dim';
  if (ms < 5) return 'ok';
  if (ms < 25) return 'mid';
  return 'warn';
}

export default function NetworkMap({ units }) {
  const online = useMemo(() => units.filter((u) => u.status !== 'Offline'), [units]);

  /*
    Kelompok jalur: unit dikumpulkan menurut alamat koordinator yang dipakainya.

    Di lab dengan satu kartu jaringan, seluruhnya akan masuk satu kelompok — dan
    itu jawaban yang benar, bukan kegagalan pengelompokan. Kelompok kedua muncul
    hanya bila memang ada unit yang datang lewat jalan lain, dan saat itu terjadi
    ia justru hal terpenting di layar ini.
  */
  const paths = useMemo(() => {
    const map = new Map();
    for (const u of online) {
      const key = u.coordinatorHost || 'belum diketahui';
      if (!map.has(key)) map.set(key, []);
      map.get(key).push(u);
    }
    return [...map.entries()]
      .map(([host, list]) => ({
        host,
        list: list.sort((a, b) => a.unitId.localeCompare(b.unitId, 'id', { numeric: true })),
      }))
      .sort((a, b) => b.list.length - a.list.length);
  }, [online]);

  const measured = online.filter((u) => u.latencyMs !== null && u.latencyMs !== undefined);
  const median = useMemo(() => {
    if (measured.length === 0) return null;
    const v = measured.map((u) => u.latencyMs).sort((a, b) => a - b);
    return v[Math.floor(v.length / 2)];
  }, [measured]);

  const wireless = online.filter((u) => u.wired === false);
  const slow = online.filter((u) => u.linkSpeedMbps > 0 && u.linkSpeedMbps < 1000);
  const noMac = units.filter((u) => !u.macAddress);

  return (
    <div className="body">
      <div className="pane" style={{ marginBottom: 14 }}>
        <div className="sec">
          <h2>Jaringan laboratorium</h2>
          <span className="hint">hanya nilai yang terukur</span>
        </div>

        <div className="net-metrics">
          <div className="metric">
            <div className="k">Unit aktif</div>
            <div className="v">{online.length}</div>
          </div>
          <div className="metric">
            <div className="k">Latensi tengah</div>
            <div className="v">{median === null ? '—' : fmtLatency(median)}</div>
          </div>
          <div className="metric">
            <div className="k">Terukur</div>
            <div className="v">{measured.length}<span className="of">/{online.length}</span></div>
          </div>
          <div className="metric">
            <div className="k">Nirkabel</div>
            <div className="v">{wireless.length}</div>
          </div>
          <div className="metric">
            <div className="k">Jalur</div>
            <div className="v">{paths.length}</div>
          </div>
        </div>

        {/* Hal yang perlu ditindaklanjuti disebutkan, bukan dibiarkan tersembunyi
            di dalam tabel yang panjang. Tanpa ini, satu unit Wi-Fi di antara
            empat puluh unit kabel tidak akan pernah ditemukan sampai siarannya
            tersendat. */}
        {(wireless.length > 0 || slow.length > 0 || noMac.length > 0) && (
          <div className="net-notes">
            {wireless.length > 0 && (
              <div><WifiHigh size={13} />
                {wireless.length} unit tersambung nirkabel: {wireless.slice(0, 5).map((u) => u.unitId).join(', ')}
                {wireless.length > 5 && ` +${wireless.length - 5}`} — siaran 1080p menuntut kabel.
              </div>
            )}
            {slow.length > 0 && (
              <div><WarningCircle size={13} />
                {slow.length} unit di bawah 1 Gbps: {slow.slice(0, 5).map((u) => `${u.unitId} (${u.linkSpeedMbps} Mbps)`).join(', ')}
                {slow.length > 5 && ` +${slow.length - 5}`}
              </div>
            )}
            {noMac.length > 0 && (
              <div><WarningCircle size={13} />
                {noMac.length} unit tanpa MAC — Wake-on-LAN tidak akan membangunkannya:
                {' '}{noMac.slice(0, 5).map((u) => u.unitId).join(', ')}
                {noMac.length > 5 && ` +${noMac.length - 5}`}
              </div>
            )}
          </div>
        )}
      </div>

      {online.length === 0 ? (
        <div className="pane">
          <div className="hint">
            Belum ada unit aktif. Peta ini menggambar apa yang terukur, jadi ia
            kosong sampai ada yang menyambung.
          </div>
        </div>
      ) : paths.map((p) => (
        <div className="pane" key={p.host} style={{ marginBottom: 14 }}>
          <div className="sec">
            <h3>
              Jalur <code className="mono">{p.host}</code>
              <span className="count">{p.list.length} unit</span>
            </h3>
          </div>

          {/* Penjelasan pengelompokan ada di layar, bukan hanya di kode. Yang
              membacanya perlu tahu bahwa ini bukan diagram switch — kalau tidak,
              ia akan memperlakukannya sebagai diagram switch. */}
          <div className="hint" style={{ marginBottom: 10 }}>
            Unit di bawah ini menjangkau koordinator lewat alamat yang sama.
            Itu berarti mereka berbagi jalur, bukan berarti mereka berada di switch yang sama —
            switch tak-terkelola tidak dapat dideteksi perangkat lunak.
          </div>

          <div className="net-grid">
            {p.list.map((u) => {
              const tone = latencyTone(u.latencyMs);
              const label = fmtLatency(u.latencyMs);
              return (
                <div className="net-node" key={u.unitId}>
                  <div className="net-node-head">
                    <b>{unitLabel(u)}</b>
                    {u.wired === false
                      ? <WifiHigh size={13} className="t-warn" />
                      : <Plugs size={13} className="t-ok" />}
                  </div>

                  <div className="net-node-ip mono">{u.ipAddress || '—'}</div>

                  <div className={`net-node-lat t-${tone}`}>
                    {label ?? <><Question size={12} /> belum terukur</>}
                  </div>

                  <div className="net-node-link">
                    {u.linkSpeedMbps > 0 ? `${u.linkSpeedMbps} Mbps` : 'kecepatan tidak dilaporkan'}
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      ))}

      <div className="pane">
        <h3>Yang tidak dapat diukur</h3>
        <div className="hint" style={{ lineHeight: 1.75 }}>
          Switch tak-terkelola tidak punya alamat, tidak menjawab, dan tidak muncul
          di jalur paket — mendeteksinya menuntut LLDP, CDP, atau SNMP, yang hanya
          tersedia pada perangkat terkelola. Layar ini sengaja tidak menggambarnya.
          Diagram switch yang dikarang akan membuat asisten memeriksa perangkat yang
          belum tentu ada, atau ada tetapi tidak melayani unit yang sedang bermasalah.
        </div>
      </div>
    </div>
  );
}
