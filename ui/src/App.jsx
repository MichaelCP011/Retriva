import { useEffect, useState } from 'react';
import { ArrowsClockwise, Broadcast, Sun, Moon, CheckCircle } from '@phosphor-icons/react';
import { on, send, isHosted, mockUnits } from './bridge';
import { TITLES } from './nav';
import { IconRail, NavPanel } from './components/Sidebar';
import { readTheme, applyTheme } from './theme';
import { makeCan } from './rights';

import Dashboard from './views/Dashboard';
import ScreenShare from './views/ScreenShare';
import RemoteControl from './views/RemoteControl';
import Chat from './views/Chat';
import Files from './views/Files';
import NetworkMap from './views/NetworkMap';
import ExamMode from './views/ExamMode';
import Panels from './views/Panels';
import Settings from './views/Settings';

export default function App() {
  const [view, setView] = useState('dashboard');
  const [units, setUnits] = useState(() => (isHosted ? [] : mockUnits()));
  const [thumbs, setThumbs] = useState({});
  const [activity, setActivity] = useState([]);
  const [settings, setSettings] = useState(null);
  const [updateInfo, setUpdateInfo] = useState(null);
  const [selected, setSelected] = useState(null);
  const [broadcasting, setBroadcasting] = useState(false);
  const [theme, setTheme] = useState(readTheme);
  const [toasts, setToasts] = useState([]);
  const [batch, setBatch] = useState(null);
  const [bcPreview, setBcPreview] = useState(null);
  const [bcStats, setBcStats] = useState(null);

  // Hitungan penerimaan saat layar mahasiswa yang disiarkan. Terpisah dari
  // bcStats karena artinya berlawanan: yang satu diukur di pengirim, yang ini
  // di penerima. Menyatukannya berarti panel menampilkan "0 fps · 0.0 Mbps"
  // untuk siaran yang sebenarnya berjalan lancar.
  const [bcWatch, setBcWatch] = useState(null);

  // Keadaan sesi ujian dan lini masa satu unit. Terpisah karena waktunya
  // berbeda: sesinya berubah jarang, lini masanya diminta ulang tiap beberapa
  // detik selama sebuah unit dipilih.
  const [exam, setExam] = useState(null);
  const [examTimeline, setExamTimeline] = useState(null);
  const [sources, setSources] = useState([]);
  const [bcInfo, setBcInfo] = useState(null);

  // Keadaan siaran lab — termasuk siaran milik panel lain.
  const [bcState, setBcState] = useState(null);

  /*
    Keadaan sambungan ke Superadmin: peran panel ini, wewenangnya, dan nama lab.

    Panel tidak lagi membaca keadaan lab dari objek yang kebetulan ada di proses
    yang sama — semuanya lewat satu sambungan. Kalau sambungan itu putus atau
    belum disetujui, satu-satunya hal yang jujur untuk ditampilkan adalah
    itu, bukan daftar unit kosong yang terlihat sama persis dengan lab yang
    memang sedang libur.
  */
  const [link, setLink] = useState(null);
  const [panels, setPanels] = useState([]);
  const [enrollment, setEnrollment] = useState(null);
  const [labs, setLabs] = useState([]);

  /*
    Sesi kendali disimpan sebagai satu objek atau null, bukan sebagai beberapa
    boolean terpisah. Keadaan seperti "sedang mengendalikan tapi tidak ada unit"
    tidak mungkin diwakili, jadi tidak mungkin terjadi.
  */
  const [remote, setRemote] = useState(null);

  /*
    Percakapan per unit. Diisi dari riwayat yang dikirim host saat sebuah
    percakapan dibuka, lalu ditambah pesan yang datang setelahnya — bukan
    disimpan sebagai satu-satunya sumber kebenaran di sini.
  */
  const [threads, setThreads] = useState({});
  const [picked, setPicked] = useState([]);

  /*
    Penyalinan ke lemari, disusun sebagai sourcePath -> { copied, total }.

    Berkas 7 GB butuh menit-menitan disalin sebelum satu unit pun mulai
    mengunduh. Tanpa keadaan ini, jeda itu tidak terwakili apa pun di layar:
    tombol ditekan, lalu tidak ada yang terjadi sampai pembagian tiba-tiba
    muncul. Dulu hal itu tersamarkan karena panelnya ikut membeku — sekarang
    panelnya tetap hidup, jadi diamnya justru lebih terlihat.
  */
  const [staging, setStaging] = useState({});

  /*
    Kemajuan pembagian, disusun sebagai transferId -> { berkas, units: {unitId -> baris} }.
    Bentuk bersarang ini dipilih karena laporan datang per unit per berkas, dan
    panel perlu menampilkan keduanya: ringkasan satu berkas, dan unit mana yang
    gagal. Disimpan datar, salah satunya harus dihitung ulang tiap render.
  */
  const [transfers, setTransfers] = useState({});

  // Angka pada menu Chat dihitung dari unit, bukan disimpan terpisah. Menyimpan
  // hitungan sendiri berarti ada dua sumber yang bisa berselisih, dan yang salah
  // biasanya yang terlihat.
  const pendingChat = units.reduce(
    (n, u) => n + (u.unread ?? 0) + (u.helpRequestedAt ? 1 : 0), 0);

  // Panel yang menunggu persetujuan dihitung dari daftarnya, sama seperti chat.
  // Angka yang disimpan terpisah akan berselisih dengan daftarnya suatu saat,
  // dan yang salah selalu yang terlihat.
  const pendingPanels = panels.filter((p) => p.role === 'Pending').length;

  /*
    Pemeriksa wewenang, dipakai untuk menyembunyikan tombol yang akan ditolak.

    Di luar Retriva.Admin — yaitu saat UI dibuka langsung di browser untuk
    menggarap tampilan — tidak ada yang mengirim wewenang, dan menyembunyikan
    semuanya akan membuat seluruh aplikasi tampak kosong. Di sana semuanya
    dibuka; tidak ada risikonya, karena tidak ada lab di seberangnya.
  */
  const can = isHosted ? makeCan(link?.rights) : () => true;

  /*
    Unit terpilih diambil ULANG dari daftar yang hidup, bukan dari potret.

    setSelected menyimpan objek unit apa adanya pada saat kartunya diklik, dan
    objek itu tidak pernah berubah lagi. Akibatnya seluruh layar detail
    membeku pada keadaan satu momen: status tetap "Aktif" setelah PC-nya mati,
    nama mahasiswa tetap kosong setelah ia login, dan saklar layar penuh
    menampilkan nilai lama sehingga terlihat menyala terus.

    Yang terakhir itu paling menipu, karena tombolnya memang bereaksi — nilainya
    tersimpan di koordinator dengan benar — hanya tampilannya yang membaca
    salinan basi. Ditekan sepuluh kali, terlihat menyala sepuluh kali.

    Potretnya tetap dipakai sebagai cadangan supaya layar tidak mendadak kosong
    bila unitnya menghilang dari daftar.
  */
  const selectedUnit = selected
    ? units.find((u) => u.unitId === selected.unitId) ?? selected
    : null;

  useEffect(() => { applyTheme(theme); }, [theme]);

  useEffect(() => {
    if (!isHosted) return;
    const offUnits = on('units', (m) => setUnits(m.units ?? []));
    const offLink = on('linkStatus', (m) => setLink(m));
    const offPanels = on('panels', (m) => setPanels(m.panels ?? []));
    const offEnroll = on('enrollment', (m) => setEnrollment(m));
    const offLabs = on('labs', (m) => setLabs(m.labs ?? []));
    const offSettings = on('settings', (m) => setSettings(m.settings ?? null));
    const offExam = on('exam', (m) => setExam(m));
    const offExamLine = on('examTimeline', (m) => setExamTimeline(m));
    const offActivity = on('activity', (m) => setActivity(m.entries ?? []));
    const offThumb = on('thumbnail', (m) =>
      setThumbs((prev) => ({ ...prev, [m.unitId]: m.dataUrl })));
    const offBatch = on('batchResult', (m) => setBatch(m));
    const offSrc = on('captureSources', (m) => setSources(m.sources ?? []));
    const offInfo = on('broadcastInfo', (m) => setBcInfo(m));
    const offBcState = on('broadcastState', (m) => setBcState(m));
    // Siaran dapat berhenti tanpa diminta — jendela target ditutup, misalnya.
    // Tanpa pesan ini tombolnya tetap berbunyi "Hentikan siaran".
    const offRemoteStart = on('remoteStarted', (m) =>
      setRemote({ unitId: m.unitId, allowInput: m.allowInput, frames: 0 }));

    const offRemoteFrame = on('remoteFrame', (m) => setRemote((prev) =>
      // Frame yang datang untuk unit lain diabaikan. Itu berarti sesi sudah
      // berpindah dan paket ini sisa dari sesi sebelumnya.
      prev && prev.unitId === m.unitId
        ? { ...prev, dataUrl: m.dataUrl, width: m.width, height: m.height, frames: m.frames }
        : prev));

    const offRemoteEnd = on('remoteEnded', () => setRemote(null));

    // Riwayat di UI dikosongkan hanya setelah host memastikan penghapusannya
    // berhasil. Dikosongkan lebih dulu, layar akan tampak bersih meski basis
    // datanya gagal dihapus — kebohongan yang persis pada topik kerahasiaan.
    const offReset = on('chatReset', () => setThreads({}));

    const offPicked = on('filesPicked', (m) => setPicked((prev) => {
      // Berkas yang sama tidak digandakan bila dipilih dua kali.
      const seen = new Set(prev.map((f) => f.path));
      return [...prev, ...(m.files ?? []).filter((f) => !seen.has(f.path))];
    }));

    const offStaging = on('fileStaging', (m) => setStaging((prev) => ({
      ...prev,
      [m.sourcePath]: { fileName: m.fileName, copied: m.copied, total: m.total },
    })));

    const drop = (sourcePath) => {
      setStaging((prev) => {
        if (!(sourcePath in prev)) return prev;
        const next = { ...prev };
        delete next[sourcePath];
        return next;
      });
    };

    // Berkas yang gagal dibaca TETAP di daftar pilihan. Ia belum terkirim ke
    // mana pun, dan membuangnya diam-diam akan membuat kegagalannya lenyap
    // dari layar bersama berkasnya.
    const offStagingFailed = on('fileStagingFailed', (m) => drop(m.sourcePath));

    const offStarted = on('fileStarted', (m) => {
      drop(m.sourcePath);

      /*
        Berkas yang sudah berangkat DIKELUARKAN dari daftar pilihan.

        Sebelumnya daftar itu hanya dikosongkan oleh "Kosongkan lemari", yang
        merupakan tindakan yang sama sekali berbeda dan jarang dilakukan.
        Akibatnya, menambah satu berkas lalu menekan Bagikan akan mengirim
        ulang setiap berkas yang sudah pernah dikirim sebelumnya — tanpa
        peringatan apa pun, karena dari sudut pandang program itu memang
        persis yang diminta.

        Yang dilihat asisten adalah berkas yang menggandakan diri di PC
        mahasiswa. Riwayat "Pembagian berjalan" di bawahnya sudah mencatat apa
        yang terkirim, jadi daftar pilihan tidak perlu merangkap sebagai
        riwayat — tugasnya hanya menyimpan apa yang BELUM berangkat.
      */
      if (m.sourcePath) setPicked((prev) => prev.filter((f) => f.path !== m.sourcePath));

      setTransfers((prev) => ({
        ...prev,
        [m.transferId]: {
          transferId: m.transferId,
          fileName: m.fileName,
          sizeBytes: m.sizeBytes,
          units: Object.fromEntries((m.units ?? []).map((unitId) => [
            unitId, { unitId, percent: 0, status: 'menunggu' },
          ])),
        },
      }));
    });

    const offProgress = on('fileProgress', (m) => setTransfers((prev) => {
      const t = prev[m.transferId];
      if (!t) return prev;
      return {
        ...prev,
        [m.transferId]: {
          ...t,
          units: {
            ...t.units,
            [m.unitId]: {
              unitId: m.unitId, percent: m.percent, status: m.status, detail: m.detail,
            },
          },
        },
      };
    }));

    const offFilesReset = on('filesReset', () => { setTransfers({}); setPicked([]); });

    const offHistory = on('chatHistory', (m) =>
      setThreads((prev) => ({ ...prev, [m.unitId]: m.messages ?? [] })));

    const offChatMsg = on('chatMessage', (m) => setThreads((prev) => ({
      ...prev,
      [m.unitId]: [...(prev[m.unitId] ?? []), {
        fromAdmin: m.fromAdmin, displayName: m.displayName, text: m.text, sentAt: m.sentAt,
      }],
    })));

    const offEnd = on('broadcastEnded', () => {
      setBroadcasting(false);
      setBcPreview(null);
      setBcStats(null);
      setBcWatch(null);
    });
    const offBc = on('broadcastPreview', (m) => {
      setBcPreview(m.dataUrl ?? null);
      setBcStats(m.stats ?? null);
      // Saat mahasiswa yang presentasi, panel tidak menangkap apa pun dan
      // "stats" pengirim tidak ada. Yang datang justru hitungan penerimaan.
      setBcWatch(m.watching ?? null);
    });
    const offToast = on('toast', (m) => {
      const id = Math.random();
      setToasts((prev) => [...prev, { id, text: m.text, warning: m.warning }]);
      setTimeout(() => setToasts((prev) => prev.filter((t) => t.id !== id)), 3200);
    });
    const offUpdateAvail = on('updateAvailable', (m) =>
      setUpdateInfo({ version: m.version, ready: false, title: m.title }));
    const offUpdateReady = on('updateReady', (m) =>
      setUpdateInfo((prev) => ({ ...prev, version: m.version, ready: true, installerPath: m.installerPath })));
    send('ready');
    return () => { offUnits(); offLink(); offPanels(); offEnroll(); offLabs(); offSettings(); offActivity(); offThumb(); offToast(); offBatch(); offBc(); offSrc(); offInfo(); offBcState(); offEnd(); offExam(); offExamLine();
      offRemoteStart(); offRemoteFrame(); offRemoteEnd(); offHistory(); offChatMsg(); offReset();
      offPicked(); offStarted(); offProgress(); offFilesReset();
      offStaging(); offStagingFailed();
      offUpdateAvail(); offUpdateReady(); };
  }, []);

  function openUnit(unit) {
    setSelected(unit);
    setView('remote');
  }

  const online = units.filter((u) => u.status === 'Online').length;
  const [title, subFn] = TITLES[view] ?? ['', () => ''];

  return (
    <div className="app">
      <IconRail view={view} onNavigate={setView} />
      <NavPanel badges={{ chat: pendingChat, panels: pendingPanels }} view={view} onNavigate={setView}
        unitCount={units.length} online={online} labName={link?.labName} can={can}
        panelRole={link?.ready ? link.role : null} appVersion={settings?.appVersion} />

      <div className="main">
        <div className="bar">
          <h1>{title}</h1>
          <span className="meta">{subFn(units.length, selectedUnit)}</span>
          <div className="sp" />
          <LinkBadge link={link} onOpenPanels={() => setView('panels')} />
          {updateInfo?.ready && (
            <button className="btn btn-primary" title={`Versi ${updateInfo.version} telah diunduh. Pasang sekarang.`}
              onClick={() => send('installUpdateNow')}>
              <CheckCircle size={15} />Pasang v{updateInfo.version}
            </button>
          )}
          <button className="btn btn-quiet"
            title={theme === 'dark' ? 'Beralih ke tema terang' : 'Beralih ke tema gelap'}
            onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}>
            {theme === 'dark' ? <Sun size={15} /> : <Moon size={15} />}
            {theme === 'dark' ? 'Terang' : 'Gelap'}
          </button>
          <button className="btn btn-quiet" onClick={() => send('ping')}>
            <ArrowsClockwise size={15} />Pindai ulang
          </button>
          <button className={`btn ${broadcasting ? '' : 'btn-primary'}`}
            onClick={() => setView('screenshare')}>
            <Broadcast size={15} />{broadcasting ? 'Siaran berjalan' : 'Mulai siaran'}
          </button>
        </div>

        {view === 'dashboard' && (
          <Dashboard units={units} thumbs={thumbs} activity={activity} batch={batch} can={can}
            settings={settings}
            onDismissBatch={() => setBatch(null)}
            onOpenUnit={openUnit} onGoShare={() => setView('screenshare')} />
        )}
        {view === 'screenshare' && (
          <ScreenShare units={units} broadcasting={broadcasting} setBroadcasting={setBroadcasting}
            preview={bcPreview} stats={bcStats} watching={bcWatch} sources={sources} info={bcInfo} labState={bcState}
            can={can} />
        )}
        {view === 'remote' && (
          <RemoteControl unit={selectedUnit} thumb={selected ? thumbs[selected.unitId] : null}
            activity={activity} remote={remote} can={can}
            thread={selected ? threads[selected.unitId] ?? [] : []}
            onOpenChat={(unitId) => send('chatOpen', { unitId })}
            onBack={() => setView('dashboard')} />
        )}
        {view === 'chat'     && (
          <Chat units={units} threads={threads}
            onOpen={(unitId) => send('chatOpen', { unitId })} />
        )}
        {view === 'files'    && (
          <Files units={units} picked={picked} transfers={transfers} staging={staging}
            onForget={(path) => setPicked((prev) => prev.filter((f) => f.path !== path))} />
        )}
        {view === 'network'  && <NetworkMap units={units} />}
        {view === 'exam'     && <ExamMode units={units} exam={exam} timeline={examTimeline} can={can} />}
        {view === 'panels'   && <Panels panels={panels} enrollment={enrollment} link={link} labs={labs} />}
        {view === 'settings' && <Settings units={units} settings={settings} updateInfo={updateInfo} can={can} />}
      </div>

      {toasts.length > 0 && (
        <div className="toast-wrap">
          {toasts.map((t) => (
            <div className={`toast${t.warning ? ' warn' : ''}`} key={t.id}>{t.text}</div>
          ))}
        </div>
      )}
    </div>
  );
}

/*
  Keadaan sambungan ke Superadmin, di kepala jendela.

  Nama lab ikut ditampilkan, bukan hanya titik hijau. Begitu ada lebih dari
  satu lab di gedung yang sama, panel dosen dapat menyambung ke koordinator
  yang salah tanpa satu pun tanda — sampai perintah kunci layar mendarat di
  kelas orang lain. Nama lab membuatnya kentara sebelum itu terjadi.
*/
function LinkBadge({ link, onOpenPanels }) {
  if (!link) return null;

  // Empat keadaan, empat kalimat berbeda. Digabung jadi "tidak tersambung",
  // ketiganya menuntun ke tindakan yang salah: yang belum tertaut akan
  // memeriksa kabel, yang menunggu persetujuan akan memasukkan kode lagi.
  // Alasan penolakan yang ada selalu menang atas tebakan. "Menunggu
  // persetujuan" hanya benar bila memang tidak ada yang menolak.
  const state = link.ready
    ? `${link.role} · ${link.labName}`
    : link.rejectReason
      ? 'Ditolak koordinator'
      : !link.linked && !link.hostsLab
        ? 'Belum tertaut ke lab'
        : !link.connected
          ? 'Terputus dari koordinator'
          : 'Menunggu persetujuan';

  const healthy = link.ready;

  return (
    <button className={`link-badge${healthy ? ' ok' : ' warn'}`}
      title={link.rejectReason ?? state}
      onClick={healthy ? undefined : onOpenPanels}>
      <span className="dot" />
      {state}
    </button>
  );
}
