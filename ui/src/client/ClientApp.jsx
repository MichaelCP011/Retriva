import { useEffect, useRef, useState } from 'react';
import {
  House, ChatTeardropText, FolderOpen, WarningDiamond, PaperPlaneRight,
  Sun, Moon, SignOut, ArrowSquareOut, ArrowRight, Clock, Broadcast,
  CornersOut, CornersIn, X, MagnifyingGlass,
} from '@phosphor-icons/react';
import { on, send, isHosted } from '../bridge';
import LabCanvas from './LabCanvas.jsx';
import LinkedText from './LinkedText.jsx';
import AmbientCanvas from './AmbientCanvas.jsx';
import { applyTheme, readTheme } from '../theme';
import logo from '../assets/logo-client.svg';

const time = (iso) => {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString('id-ID', { hour: '2-digit', minute: '2-digit' });
};

const sizeLabel = (bytes) => {
  if (!bytes && bytes !== 0) return '';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
};

/*
  Sapaan mengikuti jam, bukan satu kalimat tetap.

  "Selamat datang" yang sama sepanjang hari terbaca sebagai tempelan. Praktikum
  di lab ini berjalan pagi sampai sore, jadi menyesuaikannya membuat aplikasi
  terasa hadir pada saat itu — dan itu perbedaan antara sapaan yang berarti
  dengan basa-basi.
*/
function greeting() {
  const h = new Date().getHours();
  if (h < 11) return 'Selamat pagi';
  if (h < 15) return 'Selamat siang';
  if (h < 18) return 'Selamat sore';
  return 'Selamat malam';
}

const firstName = (full) => (full ?? '').trim().split(/\s+/)[0] || 'Mahasiswa';

const initials = (full) => (full ?? '?')
  .trim().split(/\s+/).slice(0, 2).map((w) => w[0]).join('').toUpperCase();

const dayLabel = (iso) => {
  const d = new Date(iso);
  const today = new Date();
  const same = d.toDateString() === today.toDateString();
  return same ? 'Hari ini' : d.toLocaleDateString('id-ID', { weekday: 'long', day: 'numeric', month: 'long' });
};

/*
  Keluarga jenis berkas.

  Tiap keluarga dapat satu warna dari token yang sudah ada, bukan warna baru.
  Gunanya praktis: mahasiswa mencari berkasnya dengan memindai, dan blok warna
  jauh lebih cepat dipindai daripada teks ekstensi yang semuanya seragam.
*/
const FAMILY = {
  doc:   { ext: ['doc', 'docx', 'pdf', 'txt', 'md', 'rtf'], tone: 'doc' },
  sheet: { ext: ['xls', 'xlsx', 'csv', 'tsv'], tone: 'sheet' },
  slide: { ext: ['ppt', 'pptx', 'key'], tone: 'slide' },
  code:  { ext: ['sql', 'py', 'js', 'ts', 'java', 'c', 'cpp', 'cs', 'json', 'xml', 'html', 'css'], tone: 'code' },
  image: { ext: ['png', 'jpg', 'jpeg', 'gif', 'svg', 'webp', 'bmp'], tone: 'image' },
  pack:  { ext: ['zip', 'rar', '7z', 'tar', 'gz'], tone: 'pack' },
};

function toneOf(fileName) {
  const ext = (fileName.split('.').pop() ?? '').toLowerCase();
  for (const f of Object.values(FAMILY)) if (f.ext.includes(ext)) return f.tone;
  return 'other';
}

/*
  Angka yang naik dari nol saat pertama tampil.

  Bukan sekadar gaya: mata tertarik pada perubahan, dan angka yang bergerak
  menarik perhatian ke hal yang memang perlu diperhatikan. Berhenti seketika
  bila pengguna meminta gerak minimal.
*/
function useCountUp(target, duration = 900) {
  const [value, setValue] = useState(0);

  useEffect(() => {
    if (window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches) {
      setValue(target);
      return undefined;
    }
    if (target <= 0) { setValue(0); return undefined; }

    let raf = 0;
    const start = performance.now();
    const step = (now) => {
      const t = Math.min(1, (now - start) / duration);
      // Expo-out, kurva yang sama dengan seluruh gerak lain di aplikasi ini.
      setValue(Math.round(target * (1 - Math.pow(2, -10 * t))));
      if (t < 1) raf = requestAnimationFrame(step);
      else setValue(target);
    };
    raf = requestAnimationFrame(step);
    return () => cancelAnimationFrame(raf);
  }, [target, duration]);

  return value;
}

/*
  Lama sesi berjalan, dihitung sejak mahasiswa masuk.

  Angka nyata yang bergerak tiap detik memberi kesan aplikasi yang hadir dan
  mengawasi sesuatu — dan kebetulan memang itu yang dilakukannya.
*/
function useElapsed(since) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);

  const total = Math.max(0, Math.floor((now - since) / 1000));
  const h = String(Math.floor(total / 3600)).padStart(2, '0');
  const m = String(Math.floor((total % 3600) / 60)).padStart(2, '0');
  const sec = String(total % 60).padStart(2, '0');
  return `${h}:${m}:${sec}`;
}

const NAV = [
  { id: 'home', Icon: House, label: 'Beranda' },
  { id: 'chat', Icon: ChatTeardropText, label: 'Pesan' },
  { id: 'files', Icon: FolderOpen, label: 'Berkas' },
];

const TITLES = {
  home: 'Beranda',
  chat: 'Pesan dengan asisten',
  files: 'Berkas dari asisten',
};

/*
  Antarmuka mahasiswa.

  Memakai bahasa desain yang sama dengan panel admin — rail gelap sebagai
  jangkar visual, permukaan terang, aksen merah yang dipakai hemat — supaya
  keduanya terbaca sebagai satu produk. Yang berbeda hanya kepadatannya: panel
  admin adalah perkakas kerja yang dipelototi berjam-jam, sedangkan ini dibuka
  sekali di awal praktikum lalu diabaikan.

  Penonton siaran TIDAK ada di sini. Ia dirender WPF asli di lapisan atas jendela
  yang sama — 15 fps gambar melewati jembatan WebView2 sebagai base64 berarti
  sekitar 5 MB per detik, dan itu tidak akan sanggup.
*/
/*
  Pratinjau siaran.

  Gambarnya dimuat ulang dari alamat yang sama berulang kali dengan penanda
  waktu di belakangnya, karena host menjawab tiap permintaan dengan frame
  terkini dari memori. Pemuatan berikutnya baru dimulai setelah yang sekarang
  selesai — bukan lewat pewaktu tetap. Dengan begitu laju tampilnya menyesuaikan
  kemampuan mesin sendiri, dan antrean permintaan tidak pernah menumpuk pada PC
  yang sedang berat.
*/
function LiveFrame({ url, onLoad }) {
  const ref = useRef(null);
  const alive = useRef(true);

  useEffect(() => {
    alive.current = true;
    const img = ref.current;
    if (!img) return undefined;

    let timer = 0;
    const next = () => {
      if (!alive.current) return;
      // Jeda kecil menahan laju di sekitar 15 fps, laju siaran yang dikirim.
      timer = setTimeout(() => {
        if (alive.current && ref.current) ref.current.src = `${url}?t=${Date.now()}`;
      }, 66);
    };

    img.addEventListener('load', () => { onLoad?.(); next(); });
    img.addEventListener('error', next);
    img.src = `${url}?t=${Date.now()}`;

    return () => { alive.current = false; clearTimeout(timer); };
  }, [url]);

  return <img ref={ref} className="cl-live-img" alt="Siaran layar dosen" draggable={false} />;
}

export default function ClientApp() {
  const [theme, setTheme] = useState(readTheme);
  const [tab, setTab] = useState('home');

  const [state, setState] = useState({ connected: false, unitId: null, teacher: null });
  const [student, setStudent] = useState(null);
  const [helpRaised, setHelpRaised] = useState(false);
  const [messages, setMessages] = useState([]);
  const [files, setFiles] = useState([]);
  const [draft, setDraft] = useState('');
  const [seen, setSeen] = useState(0);

  /// Kata kunci penyaring daftar berkas. Lihat catatan di layar Berkas.
  const [fileQuery, setFileQuery] = useState('');

  /*
    Nama unit yang diketik mahasiswa, dan apakah kotaknya sedang dibuka.

    Draf disimpan terpisah dari state.selfName supaya pembaruan keadaan yang
    datang di tengah pengetikan tidak menimpa apa yang sedang diketik. Keadaan
    itu tiba tiap beberapa detik, jadi tanpa pemisahan ini nama yang panjang
    tidak akan pernah selesai diketik.
  */
  const [nameDraft, setNameDraft] = useState(null);

  /// Judul sesi ujian yang berjalan, null bila tidak ada.
  const [exam, setExam] = useState(null);

  const [nim, setNim] = useState('');
  const [name, setName] = useState('');

  /*
    Jeda singkat yang disengaja saat masuk.

    Bukan penundaan palsu untuk gaya-gayaan: sambungan ke agent memang perlu
    beberapa ratus milidetik, dan tanpa keadaan antara ini tombolnya terasa
    "mati" lalu layar berganti mendadak. Momen ini juga tempat memperlihatkan
    unit terkunci — satu detik yang membuat mahasiswa tahu apa yang barusan
    terjadi, alih-alih tiba-tiba berada di layar lain.
  */
  const [signing, setSigning] = useState(false);

  /// Permintaan izin kendali yang sedang ditampilkan, null bila tidak ada.
  /// Penjaga masuk otomatis. Lihat catatan panjang di efeknya.
  const autoSignedIn = useRef(false);

  const [consent, setConsent] = useState(null);
  const [consentLeft, setConsentLeft] = useState(0);

  const bodyRef = useRef(null);

  // Waktu masuk dipakai untuk penghitung lama sesi. Disimpan sekali, bukan
  // dihitung ulang tiap render, supaya angkanya tidak melompat mundur.
  const startedAt = useRef(Date.now());
  const [atBottom, setAtBottom] = useState(true);

  // Berkas yang baru mendarat disorot sekali lalu tenang kembali. Sorotan yang
  // menetap akan jadi kebisingan; sorotan sekali menarik mata tepat ke hal yang
  // barusan berubah.
  const [freshFile, setFreshFile] = useState(null);

  /*
    Siaran sebagai objek atau null, bukan beberapa boolean terpisah.

    Frame-nya TIDAK melewati jembatan pesan: host menyajikannya sebagai sumber
    daya HTTP, jadi yang disimpan di sini cuma alamatnya. Gambar dimuat ulang
    dari alamat itu pada laju yang kita tentukan sendiri.
  */
  const [live, setLive] = useState(null);
  const [liveFull, setLiveFull] = useState(false);

  /*
    Layar penuh dinyalakan SIARAN, bukan oleh orang di depan mesin ini.

    Menentukan apakah jendelanya boleh berada di atas jendela lain. PC proyektor
    memang harus menutupi segalanya; PC mahasiswa tidak — ia hanya ingin
    gambarnya besar, dan tetap harus bisa berpindah ke editornya sambil
    mengikuti demo.
  */
  const [liveAuto, setLiveAuto] = useState(false);

  /*
    Membuka layar penuh atas kehendak orang di depan mesin ini.

    Penanda otomatis DILEPAS di sini, tidak sekadar dibiarkan. Mahasiswa yang
    menekan Esc saat siaran proyektor berjalan lalu membukanya lagi sendiri
    akan mendapat jendela yang menempel di atas segalanya — dan tidak punya cara
    berpindah ke editornya selain menutup siarannya.
  */
  function openLive() {
    setLiveAuto(false);
    setLiveFull(true);
  }

  /*
    Jendela dikabari setiap mode layar penuh berubah.

    Overlay di halaman hanya menutupi WebView2. Bilah judul aplikasi dan taskbar
    Windows berada di luar jangkauannya, dan keduanya memotong gambar siaran —
    dua garis yang tidak berarti apa-apa bagi mahasiswa di belakang kelas, dan
    membuat presentasi terlihat seperti tangkapan layar.

    Host yang menghilangkan keduanya, tetapi ia tidak dapat menebak kapan: mode
    ini dapat dibuka dengan klik ganda, ditutup dengan Esc, atau dinyalakan
    sendiri oleh siaran di PC proyektor. Jadi halaman yang mengabarkan.
  */
  useEffect(() => {
    send('presentation', { on: liveFull, onTop: liveFull && liveAuto });
  }, [liveFull, liveAuto]);

  /*
    Bilah kendali di layar penuh menyembunyikan diri saat mouse diam.

    Sebelumnya ia mendorong gambar ke bawah dan memakan tinggi, sehingga
    siarannya terpotong. Menaruhnya sebagai lapisan di atas gambar
    mengembalikan seluruh tinggi layar ke isinya — dan menyembunyikannya saat
    tidak dipakai membuat menonton benar-benar tanpa gangguan, sama seperti
    pemutar video mana pun.
  */
  const [barShown, setBarShown] = useState(true);
  const barTimer = useRef(0);

  useEffect(() => {
    if (!liveFull) return undefined;

    const wake = () => {
      setBarShown(true);
      clearTimeout(barTimer.current);
      barTimer.current = setTimeout(() => setBarShown(false), 2600);
    };

    wake();
    window.addEventListener('mousemove', wake);
    window.addEventListener('keydown', wake);
    return () => {
      clearTimeout(barTimer.current);
      window.removeEventListener('mousemove', wake);
      window.removeEventListener('keydown', wake);
    };
  }, [liveFull]);

  const elapsed = useElapsed(startedAt.current);
  const msgCount = useCountUp(messages.length);
  const fileCount = useCountUp(files.length);

  useEffect(() => { applyTheme(theme); }, [theme]);

  /*
    Data tiruan saat dijalankan di browser lewat npm run dev.

    Tanpa ini, menggarap tampilan di browser mentok di layar masuk selamanya:
    tombol Masuk mengirim pesan ke host yang tidak ada, jadi tidak pernah ada
    yang membalas. Pola yang sama dipakai mockUnits() untuk panel admin.
  */
  useEffect(() => {
    if (isHosted) return;
    setState({ connected: true, unitId: 'PC-07', teacher: 'Bu Rina' });
    setStudent({ nim: '2311501234', displayName: 'Michael Putra' });
    setMessages([
      { fromAdmin: true, displayName: 'PC Asisten', text: 'Silakan buka modul minggu ke-5.',
        sentAt: new Date().toISOString(), toEveryone: true },
    ]);
    setFiles([
      { fileName: 'materi-minggu5.pptx', savedPath: 'C:\\Users\\lab\\Documents\\Retriva\\materi-minggu5.pptx',
        sizeBytes: 3_355_443, at: new Date().toISOString() },
    ]);
  }, []);

  useEffect(() => {
    if (!isHosted) return undefined;

    const offState = on('state', (m) => setState((prev) => ({ ...prev, ...m })));

    // Permintaan izin kendali. Penghitung mundurnya dari agent, bukan dihitung
    // ulang di sini — dua penghitung yang berjalan sendiri-sendiri akan
    // berbeda, dan yang terlihat mahasiswa bukan yang menentukan.
    const offConsent = on('remoteConsent', (m) => setConsent({
      issuedBy: m.issuedBy || 'Admin',
      allowInput: !!m.allowInput,
      until: Date.now() + (m.seconds || 20) * 1000,
    }));
    const offConsentClosed = on('remoteConsentClosed', () => setConsent(null));

    const offExam = on('exam', (m) => setExam(m.title ?? null));
    const offStudent = on('student', (m) => {
      setStudent(m.student ?? null);
      // Keluar mengembalikan hak masuk otomatis pada sesi berikutnya.
      if (!m.student) autoSignedIn.current = false;
      /*
        Keadaan "sedang menghubungkan" dibersihkan di sini, bukan di tempat
        tombolnya ditekan.

        Versi sebelumnya menyetelnya true saat Masuk dan tidak pernah
        mengembalikannya. Setelah keluar, layar masuk muncul lagi dengan nilai
        itu masih menyala, sehingga tombolnya terkunci selamanya menampilkan
        "Menghubungkan…". Membersihkannya di titik yang sama dengan datangnya
        jawaban membuat keadaan itu mustahil tertinggal — baik saat masuk
        berhasil maupun saat keluar.
      */
      setSigning(false);
    });
    const offHelp = on('help', (m) => setHelpRaised(Boolean(m.raised)));

    const offChat = on('chat', (m) => setMessages((prev) => [...prev, {
      fromAdmin: m.fromAdmin, displayName: m.displayName,
      text: m.text, sentAt: m.sentAt, toEveryone: m.toEveryone,
    }]));

    const offChatReset = on('chatCleared', () => { setMessages([]); setSeen(0); });

    /*
      Hasil pindaian folder saat halaman siap. Jenis pesan terpisah dari 'file'
      dengan sengaja: 'file' berarti sesuatu BARU SAJA tiba dan pantas disorot,
      sedangkan ini isi folder yang sudah lama ada. Memakai 'file' untuk keduanya
      akan membuat seluruh daftar berkilau setiap kali aplikasi dibuka.
    */
    const offLive = on('broadcast', (m) => {
      setLive(m.active
        ? { source: m.source, url: m.url, width: 16, height: 9, fullscreen: m.fullscreen }
        : null);

      /*
        Yang dibesarkan adalah TAMPILAN SIARANNYA, bukan jendela aplikasinya.

        Klien sudah punya mode layar penuh siaran — overlay cl-full yang biasanya
        dibuka mahasiswa dengan mengklik pratinjau. Untuk PC proyektor, mode itu
        dibuka sendiri: layarnya berisi gambar siaran saja, tanpa menu, tanpa
        pratinjau kecil di sudut.

        Ketika siaran berhenti, mode ini dilepas — kalau tidak, PC proyektor
        tertinggal menampilkan bidang kosong yang tidak dapat ditutup mahasiswa.
      */
      if (m.active && m.fullscreen) { setTab('home'); setLiveFull(true); setLiveAuto(true); }
      if (!m.active) { setLiveFull(false); setLiveAuto(false); }
      if (!m.active) setLiveFull(false);
    });

    const offLiveSize = on('broadcastSize', (m) => setLive((prev) =>
      prev ? { ...prev, width: m.width, height: m.height } : prev));

    const offInbox = on('inbox', (m) => setFiles(
      [...(m.files ?? [])].reverse().map((f) => ({
        fileName: f.fileName, savedPath: f.savedPath,
        sizeBytes: f.sizeBytes, at: f.at,
      })),
    ));

    const offFile = on('file', (m) => {
      setFiles((prev) => [
        { fileName: m.fileName, savedPath: m.savedPath, sizeBytes: m.sizeBytes, at: m.at },
        ...prev,
      ]);
      setFreshFile(m.savedPath);
      setTimeout(() => setFreshFile((c) => (c === m.savedPath ? null : c)), 2200);
    });

    send('ready');
    return () => { offState(); offStudent(); offHelp(); offChat(); offChatReset(); offFile(); offInbox(); offLive(); offLiveSize();
      offConsent(); offConsentClosed(); offExam(); };
  }, []);

  /*
    Gulung otomatis hanya bila pembaca memang sedang di bawah.

    Menggulung paksa saat mahasiswa sedang membaca pesan lama akan merenggut
    tempatnya membaca — gangguan kecil yang membuat aplikasi terasa tidak
    menghormati apa yang sedang dikerjakan orangnya.
  */
  useEffect(() => {
    const el = bodyRef.current;
    if (el && atBottom) el.scrollTop = el.scrollHeight;
  }, [messages.length, tab, atBottom]);

  function onThreadScroll(e) {
    const el = e.currentTarget;
    setAtBottom(el.scrollHeight - el.scrollTop - el.clientHeight < 48);
  }

  // Penanda belum dibaca padam saat tab Pesan dibuka, bukan saat pesannya
  // sekadar tiba. Penanda yang tidak pernah padam akan diabaikan dalam sehari.
  useEffect(() => {
    if (tab === 'chat') setSeen(messages.length);
  }, [tab, messages.length]);

  const unread = Math.max(0, messages.filter((m) => m.fromAdmin).length
    - messages.slice(0, seen).filter((m) => m.fromAdmin).length);

  function signIn() {
    if (!nim.trim() || !name.trim() || signing) return;
    setSigning(true);
    send('signIn', { nim: nim.trim(), displayName: name.trim() });
  }

  /*
    Masuk sebagai tamu: tanpa NIM, dan ditandai begitu di panel.

    Bukan jalan pintas untuk menghindari presensi. Asisten melihat unit ini
    bertanda tamu, jadi mahasiswa yang memakainya untuk menghindari pencatatan
    justru lebih kentara, bukan kurang.
  */
  function signInAsGuest() {
    if (signing) return;
    setSigning(true);
    send('signIn', {
      nim: '',
      displayName: name.trim() || state.windowsUser || 'Tamu',
      guest: true,
    });
  }

  /*
    Login tidak diwajibkan: masuk sendiri memakai nama pengguna Windows.

    Dijalankan sekali, saat aturannya diketahui DAN sambungan sudah ada.
    Menjalankannya sebelum aturan tiba berarti setiap klien sempat masuk tanpa
    identitas selama sepersekian detik pertama — dan yang sempat itu tercatat.
  */
  useEffect(() => {
    if (student || signing) return;
    if (state.requireLogin !== false) return;
    if (!state.connected) return;

    /*
        Sekali per sambungan, dijaga oleh ref dan bukan oleh keadaan.

        Efek ini pernah berputar tanpa henti: ia mengirim signIn, host membalas
        dengan "tidak ada siapa-siapa", syaratnya terpenuhi lagi, dan berulang
        secepat pipa sanggup membawanya. Penyebab sesungguhnya ada di host dan
        sudah diperbaiki — tetapi penjaga ini tetap dipasang, karena satu-satunya
        cara efek semacam ini gagal adalah dengan berputar, dan putarannya tidak
        memunculkan galat apa pun. Yang terlihat hanya layar yang tampak sibuk.

        Ref, bukan useState: menyetel keadaan akan memicu render yang menjalankan
        efek ini lagi, yaitu persis hal yang sedang dicegah.
    */
    if (autoSignedIn.current) return;
    autoSignedIn.current = true;

    setSigning(true);
    send('signIn', {
      nim: '',
      displayName: state.windowsUser || 'Pengguna',
      guest: true,
    });
  }, [student, signing, state.requireLogin, state.connected, state.windowsUser]);

  // Penjaga dilepas saat jalur terputus atau mahasiswa keluar, supaya masuk
  // otomatis dapat terjadi lagi pada sesi berikutnya.
  useEffect(() => {
    if (!state.connected || student) return;
    if (state.requireLogin !== false) autoSignedIn.current = false;
  }, [state.connected, state.requireLogin, student]);

  /*
    Penghitung mundur berhenti bersama permintaannya.

    Interval yang terus berjalan setelah dialognya hilang akan menyalakan ulang
    komponen ini sekali per detik selama aplikasi hidup — beban yang tidak
    terlihat di layar mana pun dan tidak akan pernah dicari orang.
  */
  useEffect(() => {
    if (!consent) return undefined;

    const tick = () => setConsentLeft(Math.max(0, Math.ceil((consent.until - Date.now()) / 1000)));
    tick();
    const id = setInterval(tick, 500);
    return () => clearInterval(id);
  }, [consent]);

  function answerConsent(granted) {
    send('remoteConsent', { granted });
    setConsent(null);
  }

  /*
    Penyaring daftar berkas yang SUDAH diterima — bukan pencarian di disk.

    Bedanya penting bagi yang memakainya. Mahasiswa yang mengetik nama berkas
    lalu tidak menemukannya perlu tahu bahwa artinya "asisten belum
    mengirimkannya", bukan "berkasnya tidak ada di komputer ini". Dua kesimpulan
    itu menuntun ke tindakan yang berbeda: yang pertama bertanya ke asisten,
    yang kedua mencari sendiri di Explorer.

    Karena itu labelnya berbunyi "Cari di daftar ini", dan keadaan kosongnya
    menyebutkan jumlah berkas yang ada.
  */
  const shownFiles = (() => {
    const q = fileQuery.trim().toLowerCase();
    if (!q) return files;
    return files.filter((f) => (f.fileName ?? '').toLowerCase().includes(q));
  })();

  function sendChat() {
    const text = draft.trim();
    if (!text) return;
    send('chatSend', { text });
    setDraft('');
  }

  function toggleHelp() {
    const next = !helpRaised;
    setHelpRaised(next);
    send('helpToggle', { raised: next });
  }

  function saveUnitName() {
    if (nameDraft === null) return;
    const name = nameDraft.trim();

    // Dikirim ke host, lalu draf dilepas. Keadaan berikutnya membawa nama yang
    // benar-benar tersimpan — jadi bila penyimpanannya gagal, kotaknya kembali
    // ke nilai lama alih-alih menampilkan nama yang sebenarnya tidak ada.
    send('setUnitName', { displayName: name });
    setNameDraft(null);
  }

  /* ================= layar masuk ================= */

  if (!student) {
    return (
      <div className={`cw${signing ? ' signing' : ''}`}>
        {/*
          Sisi kiri bukan panel gelap kosong melainkan peta laboratorium yang
          hidup. Ia memperlihatkan hal yang justru paling mahal dibangun di
          aplikasi ini — jaringan 40 mesin yang saling terhubung — kepada orang
          yang tidak akan pernah melihat kodenya.
        */}
        <aside className="cw-brand">
          <LabCanvas unitId={state.unitId} connected={state.connected} />

          <div className="cw-brand-content">
            <img className="cw-mark rv" style={{ '--d': '0ms' }} src={logo} alt="Retriva" />
            <h1 className="rv" style={{ '--d': '60ms' }}>Retriva</h1>
            <p className="cw-tag rv" style={{ '--d': '120ms' }}>
              Laboratorium Information Retrieval
            </p>

            <div className="cw-note rv" style={{ '--d': '200ms' }}>
              <ul>
                <li>Materi masuk langsung ke folder Dokumen</li>
                <li>Layar dosen tampil otomatis saat dibagikan</li>
                <li>Minta bantuan tanpa mengangkat tangan</li>
              </ul>
            </div>

            <div className="cw-foot rv" style={{ '--d': '280ms' }}>
              <span className={`live-dot${state.connected ? '' : ' off'}`} />
              {state.connected
                ? `${state.unitId ?? 'unit belum diketahui'} · terhubung`
                : 'menunggu sambungan ke laboratorium'}
            </div>
          </div>
        </aside>

        <main className="cw-form">
          <div className="cw-form-inner">
            <div className="cw-hi rv" style={{ '--d': '120ms' }}>{greeting()}</div>
            <h2 className="rv" style={{ '--d': '180ms' }}>Masuk untuk memulai praktikum</h2>
            <p className="cw-sub rv" style={{ '--d': '240ms' }}>
              Identitasmu dipakai untuk mencatat kehadiran dan kursi yang ditempati.
            </p>

            <div className="field rv" style={{ '--d': '300ms' }}>
              <label>NIM</label>
              <input value={nim} onChange={(e) => setNim(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && signIn()}
                placeholder="2311501234" disabled={signing} autoFocus />
            </div>

            <div className="field rv" style={{ '--d': '350ms', marginBottom: 18 }}>
              <label>Nama lengkap</label>
              <input value={name} onChange={(e) => setName(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && signIn()}
                placeholder="Sesuai daftar presensi" disabled={signing} />
            </div>

            <button className={`btn btn-primary cw-submit rv${signing ? ' busy' : ''}`}
              style={{ '--d': '400ms' }}
              onClick={signIn} disabled={!nim.trim() || !name.trim() || signing}>
              {signing ? (
                <><span className="spin" />Menghubungkan…</>
              ) : (
                <>Masuk<ArrowRight size={15} /></>
              )}
            </button>

            {/*
                Tombol tamu hanya muncul bila lab mengizinkannya.

                Ditampilkan lalu ditolak setelah ditekan adalah bentuk terburuk
                dari keduanya: mahasiswa mengira ia boleh, mencoba, dan gagal
                tanpa penjelasan yang dapat ia tindaklanjuti.
            */}
            {state.allowGuest && (
              <button className="btn cw-guest rv" style={{ '--d': '430ms' }}
                onClick={signInAsGuest} disabled={signing}>
                Masuk sebagai tamu
              </button>
            )}

            <div className="cw-help-note rv" style={{ '--d': '460ms' }}>
              Salah mengetik NIM? Beri tahu asisten setelah masuk — data dapat diperbaiki.
              {state.allowGuest && ' Masuk sebagai tamu tetap terlihat oleh asisten.'}
            </div>
          </div>
        </main>
      </div>
    );
  }

  /* ================= siaran layar penuh ================= */

  /*
    Layar penuh menggantikan seluruh isi, tetapi TIDAK menghalangi jalan keluar:
    tombol tutup dan Esc keduanya bekerja. Siaran yang tidak bisa dikecilkan
    akan membuat mahasiswa yang perlu melihat kodenya sendiri terkunci menonton.
  */
  if (live && liveFull) {
    return (
      <div className="cl-full" onKeyDown={(e) => e.key === 'Escape' && setLiveFull(false)} tabIndex={0}>
        <div className={`cl-full-bar${barShown ? '' : ' hidden'}`}>
          <span className="live-dot" />
          <b>Siaran dari {live.source}</b>
          <span className="num">{live.width}x{live.height}</span>
          <div className="sp" />
          <button className={`cl-chat-help${helpRaised ? ' on' : ''}`} onClick={toggleHelp}>
            <WarningDiamond size={14} weight={helpRaised ? 'fill' : 'regular'} />
            {helpRaised ? 'Asisten diberi tahu' : 'Minta bantuan'}
          </button>
          <button className="btn btn-sm" onClick={() => setLiveFull(false)}>
            <CornersIn size={14} />Keluar (Esc)
          </button>
        </div>
        <div className="cl-full-stage">
          <LiveFrame url={live.url} />
        </div>
      </div>
    );
  }

  /* ================= antarmuka utama ================= */

  /*
    Dialog izin kendali, ditampilkan di atas apa pun yang sedang terbuka.

    Sengaja TIDAK dapat ditutup dengan Esc atau klik di luar. Menutupnya secara
    tidak sengaja akan terbaca sebagai penolakan, dan asisten yang sedang
    membantu akan menyimpulkan mahasiswa itu menolak dibantu.

    Yang ditampilkan menyebut siapa yang meminta dan apakah ia akan memegang
    papan ketik. Perbedaan antara "melihat layar" dan "mengambil alih" adalah
    hal yang paling ingin diketahui orang yang sedang diminta izinnya.
  */
  const consentDialog = consent && (
    <div className="cl-consent">
      <div className="cl-consent-box">
        <div className="cl-consent-head">
          <Broadcast size={18} />
          <b>{consent.issuedBy} meminta akses ke layarmu</b>
        </div>

        <p>
          {consent.allowInput
            ? 'Ia akan melihat layarmu dan dapat menggerakkan mouse serta mengetik di PC ini.'
            : 'Ia hanya akan melihat layarmu, tanpa dapat mengetik atau menggerakkan mouse.'}
        </p>

        <div className="cl-consent-timer">
          Tidak dijawab dalam <b>{consentLeft}</b> detik dianggap menolak.
        </div>

        <div className="cl-consent-actions">
          <button className="btn" onClick={() => answerConsent(false)}>Tolak</button>
          <button className="btn btn-primary" onClick={() => answerConsent(true)}>Izinkan</button>
        </div>
      </div>
    </div>
  );

  return (
    <div className={`cl${exam ? ' exam' : ''}`}>
      {consentDialog}

      {/*
          Papan mode ujian: bilah tipis di paling atas, bukan dialog.

          Dialog akan ditutup dalam dua detik dan dilupakan; yang perlu diketahui
          mahasiswa harus tetap terlihat selama sesi berjalan. Bilah tipis
          memenuhi keduanya — hadir terus, tanpa menutupi pekerjaannya.

          Isinya menyebutkan APA yang dicatat, bukan hanya bahwa ada pencatatan.
          "Sedang diawasi" tanpa rincian membuat mahasiswa menduga yang
          lebih buruk daripada kenyataannya — dan dugaan itu wajar, karena
          tidak ada yang memberitahunya. Papan klip disebut apa adanya:
          peristiwanya dicatat, isinya tidak.
      */}
      {exam && (
        <div className="cl-exam" role="status">
          <WarningDiamond size={14} />
          <b>{exam}</b>
          <span>
            Aktivitas dicatat selama sesi: aplikasi yang dibuka, penyalinan
            (jumlah karakter saja, bukan isinya), dan flash disk yang dicolokkan.
          </span>
        </div>
      )}

      <nav className="cl-rail">
        <img className="rail-logo" src={logo} alt="Retriva" />

        {NAV.map(({ id, Icon, label }) => (
          <button key={id} className={`rail-btn${tab === id ? ' on' : ''}`}
            title={label} onClick={() => setTab(id)}>
            <Icon size={18} weight={tab === id ? 'fill' : 'regular'} />
            {id === 'chat' && unread > 0 && <span className="rail-dot" />}
          </button>
        ))}

        <div className="rail-sp" />

        <button className="rail-btn" title="Ganti tema"
          onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}>
          {theme === 'dark' ? <Sun size={17} /> : <Moon size={17} />}
        </button>
        <button className="rail-btn" title="Keluar" onClick={() => send('signOut')}>
          <SignOut size={17} />
        </button>
      </nav>

      <div className="cl-main">
        <header className="cl-bar">
          <h1>{TITLES[tab]}</h1>
          <span className="cl-chip">
            {/* Denyut ini selaras dengan heartbeat 5 detik milik agent, jadi
                yang berkedip bukan hiasan melainkan bukti jalurnya masih hidup. */}
            <span className={`live-dot${state.connected ? '' : ' off'}`} />
            {state.unitId ?? 'unit belum diketahui'}
          </span>
          <div className="sp" />
          {/* Tamu ditandai di layarnya sendiri, bukan hanya di panel asisten.
              Mahasiswa yang mengira dirinya tercatat presensi padahal masuk
              sebagai tamu tidak akan tahu sampai nilainya keluar. */}
          <span className="cl-me">
            {student.displayName}<span>{student.isGuest ? 'tamu' : student.nim}</span>
          </span>
        </header>

        {/* Pita tipis di tab selain beranda: siaran sedang berjalan dan bisa
            dibuka tanpa harus mencari-cari ke mana perginya. */}
        {live && tab !== 'home' && (
          <button className="cl-live-strip" onClick={() => setTab('home')}>
            <span className="live-dot" />
            Siaran dari {live.source} sedang berjalan
            <span className="cl-live-strip-go">buka<ArrowRight size={12} /></span>
          </button>
        )}

        <div className="cl-scroll" key={tab}>
          {tab === 'home' && (
            <div className="cl-home" key="home">
              <div className="cl-hero rv" style={{ '--d': '0ms' }}>
                {/*
                  Pita garis di belakang sapaan: puluhan garis tipis yang
                  berpilin dengan titik jepit bergerak. Kanvas, bukan SVG,
                  karena bentuknya dihitung ulang tiap frame — SVG akan berarti
                  puluhan elemen DOM yang atributnya ditulis ulang terus.
                */}
                <AmbientCanvas variant="ribbon" theme={theme} />

                <div className="cl-hi">{greeting()},</div>
                <h2>{firstName(student.displayName)}</h2>
                <p>
                  Kamu duduk di <b>{state.selfName || state.unitId || 'unit ini'}</b>
                  {state.teacher ? <> · pengajar <b>{state.teacher}</b></> : null}.
                  Aplikasi berjalan di latar; kamu bisa bekerja seperti biasa.
                </p>
              </div>

              {/*
                Menamai unit sendiri.

                Nama mesin Windows di laptop pribadi hampir selalu tidak berguna
                bagi asisten — "DESKTOP-4F9K2M1" tidak memberi tahu siapa pun
                siapa yang duduk di mana. Satu-satunya orang yang tahu jawabannya
                duduk di depan layar ini.

                Yang diketik di sini TIDAK menimpa penamaan asisten. Bila unit
                ini sudah punya nama dari panel, nama itulah yang tetap dipakai,
                dan yang ini hanya mengisi kekosongan.
              */}
              <div className="cl-name rv" style={{ '--d': '90ms' }}>
                <label htmlFor="cl-unit-name">Nama unit ini</label>
                <div className="cl-name-row">
                  <input
                    id="cl-unit-name"
                    value={nameDraft ?? state.selfName ?? ''}
                    maxLength={40}
                    placeholder={state.unitId ?? 'mis. Laptop Sinta'}
                    onChange={(e) => setNameDraft(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') saveUnitName();
                      if (e.key === 'Escape') setNameDraft(null);
                    }} />

                  {nameDraft !== null && nameDraft !== (state.selfName ?? '') && (
                    <>
                      <button className="btn btn-primary btn-sm" onClick={saveUnitName}>
                        Simpan
                      </button>
                      <button className="btn btn-quiet btn-sm" onClick={() => setNameDraft(null)}>
                        Batal
                      </button>
                    </>
                  )}
                </div>
                <span className="cl-name-hint">
                  Dipakai asisten untuk mengenali unitmu di panel. Kosongkan untuk
                  kembali memakai {state.unitId ?? 'nama bawaan'}.
                </span>
              </div>

              {/*
                Pita sesi. Angka yang bergerak tiap detik dan denyut yang selaras
                dengan heartbeat agent — keduanya data sungguhan, dan justru itu
                yang membuatnya terasa hidup alih-alih dihias.
              */}
              <div className="cl-strip rv" style={{ '--d': '60ms' }}>
                <div className="cl-strip-item">
                  <span>Sesi berjalan</span>
                  <b className="num">{elapsed}</b>
                </div>
                <div className="cl-strip-div" />
                <div className="cl-strip-item">
                  <span>Unit</span>
                  <b className="num">{state.selfName || state.unitId || '—'}</b>
                </div>
                <div className="cl-strip-div" />
                <div className="cl-strip-item">
                  <span>Sambungan</span>
                  <b className="cl-strip-live">
                    <span className={`live-dot${state.connected ? '' : ' off'}`} />
                    {state.connected ? 'aktif' : 'terputus'}
                  </b>
                </div>
              </div>

              {/*
                Minta bantuan adalah satu-satunya tindakan yang benar-benar perlu
                dilakukan mahasiswa di sini, jadi ia berdiri sendiri. Mahasiswa
                yang sedang bingung tidak akan mencari menu.
              */}
              <button className={`cl-action rv${helpRaised ? ' on' : ''}`}
                style={{ '--d': '120ms' }} onClick={toggleHelp}>
                <div className="cl-action-icon">
                  <WarningDiamond size={22} weight={helpRaised ? 'fill' : 'regular'} />
                </div>
                <div className="cl-action-text">
                  <b>{helpRaised ? 'Asisten sudah diberi tahu' : 'Minta bantuan asisten'}</b>
                  <span>
                    {helpRaised
                      ? 'Unitmu menyala di panel asisten. Tekan lagi bila sudah tidak perlu.'
                      : 'Tekan bila ada kendala. Asisten akan datang tanpa kamu perlu mengangkat tangan.'}
                  </span>
                </div>
                <ArrowRight size={16} className="cl-action-go" />
              </button>

              <div className="cl-cards rv" style={{ '--d': '180ms' }}>
                <button className="cl-card" onClick={() => setTab('chat')}>
                  <div className="cl-card-top">
                    <ChatTeardropText size={17} />
                    {unread > 0 && <span className="cl-card-badge">{unread} baru</span>}
                  </div>
                  <div className="cl-card-num num">{msgCount}</div>
                  <b>Pesan</b>
                  <span>
                    {messages.length === 0
                      ? 'Tanya apa saja ke asisten'
                      : messages[messages.length - 1].text}
                  </span>
                </button>

                <button className="cl-card" onClick={() => setTab('files')}>
                  <div className="cl-card-top">
                    <FolderOpen size={17} />
                    {files.length > 0 && (
                      <span className="cl-card-badge quiet">
                        {sizeLabel(files.reduce((n, f) => n + (f.sizeBytes ?? 0), 0))}
                      </span>
                    )}
                  </div>
                  <div className="cl-card-num num">{fileCount}</div>
                  <b>Berkas diterima</b>
                  <span>
                    {files.length === 0 ? 'Materi akan muncul di sini' : files[0].fileName}
                  </span>
                </button>
              </div>

              {/*
                Satu tempat, dua keadaan. Saat tidak ada siaran, orbit tata surya
                mengisinya sebagai keadaan kosong yang menjelaskan diri sendiri.
                Saat siaran berjalan, tempat yang sama berisi gambarnya —
                sehingga mahasiswa tahu ke mana harus melihat bahkan sebelum
                siarannya dimulai.
              */}
              {live ? (
                <div className="cl-live rv" style={{ '--d': '240ms' }}>
                  <div className="cl-live-bar">
                    <span className="live-dot" />
                    <b>Siaran dari {live.source}</b>
                    <span className="num">{live.width}x{live.height}</span>
                    <div className="sp" />
                    <button className="btn btn-sm" onClick={() => openLive()}>
                      <CornersOut size={13} />Layar penuh
                    </button>
                  </div>
                  <div className="cl-live-stage"
                    style={{ aspectRatio: `${live.width}/${live.height}` }}
                    onDoubleClick={() => openLive()}>
                    <LiveFrame url={live.url} />
                  </div>
                </div>
              ) : (
                <div className="cl-idle rv" style={{ '--d': '240ms' }}>
                  <AmbientCanvas variant="orbit" theme={theme} />
                  <div className="cl-idle-text">
                    <Broadcast size={16} />
                    <b>Menunggu siaran dari dosen</b>
                    <span>Layar yang dibagikan akan muncul di sini secara otomatis.</span>
                  </div>
                </div>
              )}
            </div>
          )}

          {tab === 'chat' && (
            <div className="pane cl-chat" key="chat">
              {/*
                Tombol bantuan diulang di sini, bukan hanya di beranda.

                Mahasiswa yang membuka menu pesan sudah berniat menghubungi
                asisten — memaksanya kembali ke beranda untuk menekan tombol
                yang maksudnya sama persis adalah langkah tambahan tanpa alasan.
              */}
              <div className="cl-chat-top">
                <button className={`cl-chat-help${helpRaised ? ' on' : ''}`} onClick={toggleHelp}>
                  <WarningDiamond size={14} weight={helpRaised ? 'fill' : 'regular'} />
                  {helpRaised ? 'Asisten sudah diberi tahu' : 'Minta bantuan asisten'}
                </button>
                <span className="cl-chat-note">
                  {helpRaised
                    ? 'Unitmu menyala di panel asisten.'
                    : 'Atau tulis langsung pertanyaanmu di bawah.'}
                </span>
              </div>

              {/* Topologi bintang yang berputar sangat lambat di belakang
                  percakapan. Opasitasnya rendah dengan sengaja: latar yang
                  bersaing dengan teks membuat orang berhenti membaca. */}
              <AmbientCanvas variant="topology" theme={theme} />

              <div className="thread-body" ref={bodyRef} onScroll={onThreadScroll}>
                {messages.length === 0 ? (
                  <div className="cl-blank">
                    <div className="cl-blank-art"><ChatTeardropText size={26} /></div>
                    <b>Belum ada percakapan</b>
                    <span>Tulis pertanyaanmu di bawah — asisten melihatnya langsung di panelnya.</span>
                  </div>
                ) : messages.map((m, i) => {
                  // Pemisah tanggal hanya muncul saat harinya berganti, bukan di
                  // setiap pesan. Yang perlu diketahui pembaca adalah batasnya.
                  const prev = messages[i - 1];
                  const newDay = !prev || dayLabel(prev.sentAt) !== dayLabel(m.sentAt);
                  return (
                    <div key={i} className="cl-msg-wrap">
                      {newDay && <div className="cl-day"><span>{dayLabel(m.sentAt)}</span></div>}
                      <div className={`cl-row ${m.fromAdmin ? 'them' : 'me'}`}>
                        {m.fromAdmin && (
                          <div className="cl-avatar">{initials(m.displayName)}</div>
                        )}
                        <div className={`msg ${m.fromAdmin ? 'them' : 'me'}`}>
                          {m.toEveryone && <div className="msg-tag">pengumuman kelas</div>}
                          <LinkedText text={m.text} />
                          <div className="msg-t">{m.displayName} · {time(m.sentAt)}</div>
                        </div>
                      </div>
                    </div>
                  );
                })}
              </div>

              {/* Muncul hanya bila pembaca sedang menggulung ke atas dan ada
                  yang belum terlihat di bawah. */}
              {!atBottom && messages.length > 0 && (
                <button className="cl-jump" onClick={() => {
                  setAtBottom(true);
                  const el = bodyRef.current;
                  if (el) el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
                }}>
                  Pesan terbaru <ArrowRight size={12} />
                </button>
              )}

              <div className="thread-input">
                <input value={draft} onChange={(e) => setDraft(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && sendChat()}
                  placeholder="Tulis pertanyaan untuk asisten" />
                <button className={`btn btn-primary btn-sm cl-send${draft.trim() ? ' ready' : ''}`}
                  onClick={sendChat} disabled={!draft.trim()}>
                  <PaperPlaneRight size={14} />Kirim
                </button>
              </div>
            </div>
          )}

          {tab === 'files' && (
            <div className="pane" key="files">
              <div className="sec">
                <h2>Berkas diterima</h2>
                <span className="hint">
                  {files.length} berkas
                  {files.length > 0 && ` · ${sizeLabel(files.reduce((n, f) => n + (f.sizeBytes ?? 0), 0))}`}
                </span>
                <div className="sp" />
                <button className="btn btn-quiet btn-sm" onClick={() => send('openFolder')}>
                  <FolderOpen size={13} />Buka folder
                </button>
              </div>

              {/* Kotak pencarian muncul hanya bila daftarnya cukup panjang untuk
                  perlu disaring. Di bawah itu, memindai dengan mata lebih cepat
                  daripada mengetik — dan kotak yang tidak berguna tetap menyita
                  perhatian setiap kali layar ini dibuka. */}
              {files.length > 4 && (
                <div className="cl-file-search">
                  <MagnifyingGlass size={14} />
                  <input value={fileQuery} onChange={(e) => setFileQuery(e.target.value)}
                    placeholder="Cari di daftar ini" />
                  {fileQuery && (
                    <button className="icon-btn" title="Hapus pencarian"
                      onClick={() => setFileQuery('')}><X size={13} /></button>
                  )}
                </div>
              )}

              {files.length > 0 && shownFiles.length === 0 ? (
                <div className="cl-blank">
                  <div className="cl-blank-art"><MagnifyingGlass size={26} /></div>
                  <b>Tidak ada yang cocok</b>
                  <span>
                    Tidak ada berkas bernama &ldquo;{fileQuery}&rdquo; di antara {files.length} berkas
                    yang sudah dikirim asisten. Yang dicari di sini hanya daftar ini,
                    bukan seluruh isi komputer — bila memang belum dikirim, tanyakan ke asisten.
                  </span>
                </div>
              ) : files.length === 0 ? (
                <div className="cl-blank">
                  <div className="cl-blank-art"><FolderOpen size={26} /></div>
                  <b>Belum ada berkas</b>
                  <span>
                    Materi yang dikirim asisten tersimpan otomatis di folder Dokumen,
                    dan diperiksa keutuhannya sebelum muncul di sini.
                  </span>
                </div>
              ) : shownFiles.map((f, i) => (
                <button
                  className={`cl-file rv${freshFile === f.savedPath ? ' fresh' : ''}`}
                  style={{ '--d': `${Math.min(i, 8) * 45}ms` }}
                  key={`${f.savedPath}-${i}`}
                  onClick={() => send('openFile', { path: f.savedPath })}>
                  <div className={`cl-file-ext t-${toneOf(f.fileName)}`}>
                    {(f.fileName.split('.').pop() ?? '?').slice(0, 4).toUpperCase()}
                  </div>
                  <div className="cl-file-text">
                    <b>{f.fileName}</b>
                    <span><Clock size={11} />{time(f.at)} · {sizeLabel(f.sizeBytes)}</span>
                  </div>
                  <ArrowSquareOut size={14} className="cl-file-go" />
                </button>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
