<#
    Menjalankan Retriva lengkap untuk pengembangan di satu mesin.

    EMPAT PROSES, TIGA JENDELA YANG DILIHAT ORANG:

      1. Superadmin — PC Asisten. Membuat sertifikat, membuka port 8443,
                      memegang basis data, menyetujui panel lain.
      2. Agent      — layanan di PC mahasiswa. Tanpa jendela.
      3. Client     — jendela mahasiswa, menyambung ke pipa milik Agent.
      4. Dosen      — panel admin kedua, wewenang terbatas, harus disetujui.

    Urutannya penting: Agent tidak menemukan Superadmin bila dijalankan lebih
    dulu, dan Client tidak menemukan pipa bila Agent belum siap.

    PERANNYA DITETAPKAN SKRIP INI, TIDAK DITANYAKAN.

    Superadmin dan Dosen adalah aplikasi yang sama dengan folder data berbeda,
    dan yang membedakan perannya hanya satu baris di panel.json. Skrip ini
    menuliskannya sebelum menjalankan keduanya, sehingga tidak mungkin salah
    satunya terbuka dengan peran yang keliru karena jawaban dialog dari sesi
    kemarin masih tersimpan.

    Yang TIDAK dilewati: pendaftaran. Panel Dosen di sini benar-benar menyambung
    lewat TLS ke 127.0.0.1:8443, benar-benar menghitung bukti HMAC atas sidik
    jari yang dilihatnya, dan benar-benar menunggu disetujui. Jalur pintas untuk
    pengembangan akan menjadi jalur yang paling sering dijalankan sekaligus
    paling jarang diuji — sedangkan yang dipakai dosen sungguhan adalah cabang
    yang lain.

    Pemakaian:
        .\run-dev.ps1                 Superadmin + Agent + Client + Dosen
        .\run-dev.ps1 -NoDosen        tanpa panel Dosen
        .\run-dev.ps1 -Fake 39        tambah 39 unit tiruan sehingga grid terisi 40
        .\run-dev.ps1 -UiDev          UI lewat dev server Vite (hot reload)
        .\run-dev.ps1 -SkipBuild      lewati build, lebih cepat saat mengulang
        .\run-dev.ps1 -ResetDosen     buang tautan panel Dosen, daftar dari nol
        .\run-dev.ps1 -ResetAll       buang SEMUA: basis data, sertifikat, identitas
#>

param(
    [int]$Fake = 0,
    [switch]$UiDev,
    [switch]$SkipBuild,
    [switch]$NoDosen,
    [switch]$ResetDosen,
    [switch]$ResetAll
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

<#
    Folder data terpisah untuk panel Dosen.

    Tanpa ini, dua instance Retriva.Admin di satu mesin berbagi panel.json yang
    sama — dan yang kedua akan membaca "IsSuperadmin = true" milik yang pertama,
    lalu mencoba membuka port 8443 yang sudah dipakai. Gejalanya panel kedua
    yang gagal start dengan galat soket, yang sama sekali tidak menyebut
    penyebab sesungguhnya.
#>
$dosenData = Join-Path $env:LOCALAPPDATA 'Retriva-dev-dosen'
$superData = Join-Path $env:ProgramData 'Retriva'

# Letak berkas log ketiga komponen. Harus sama persis dengan yang dipakai
# Program.cs dan AgentWorker — kalau berbeda, jendela log akan mengekor berkas
# yang tidak pernah ditulis siapa pun, dan diamnya terlihat seperti aplikasi
# yang tidak mencatat apa-apa.
<#
    Letak log MENGIKUTI RetrivaPaths.LogFile, dan itu berubah.

    Sebelumnya keempatnya ada di $env:TEMP. Nilai itu berbeda untuk tiap akun
    Windows, jadi log agent yang berjalan sebagai LocalSystem sebenarnya mendarat
    di C:\Windows\Temp — bukan di folder yang diekor skrip ini. Sekarang semuanya
    di ProgramData\Retriva\logs.

    Kalau jalur di sini tidak ikut berubah, jendela log akan mengekor berkas
    kosong yang tidak pernah ditulis siapa pun. Diamnya terlihat persis seperti
    komponen yang tidak berjalan — kegagalan yang membaca dirinya sendiri sebagai
    kegagalan yang lain.

    Nama komponennya harus sama persis dengan argumen LogFile() di kode.
#>
$logDir = Join-Path $env:ProgramData 'Retriva\logs'

$consoleLog = Join-Path $logDir 'retriva-koordinator.log'
$agentLog   = Join-Path $logDir 'retriva-agent.log'
$clientLog  = Join-Path $logDir 'retriva-klien.log'

<#
    Log panel Dosen ada di FOLDER DATANYA SENDIRI, bukan di $logDir.

    LogDir diturunkan dari DataDir, dan skrip ini sengaja memberi panel Dosen
    folder data terpisah supaya ia tidak membaca panel.json milik Superadmin.
    Konsekuensinya lognya ikut pindah — dan itu justru yang diinginkan: dua
    panel di satu mesin tidak lagi saling menimpa berkas log.

    Baris ini harus diturunkan dari $dosenData, bukan ditulis ulang, supaya
    keduanya tidak dapat berbeda.
#>
$panelLog = Join-Path $dosenData 'logs\retriva-panel.log'

function Write-Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Note($text) { Write-Host "    $text" -ForegroundColor DarkGray }

<#
    Menetapkan peran sebuah instance dengan menulis panel.json.

    ALASANNYA, DAN INI YANG SEBENARNYA MENGGIGIT.

    Jawaban dialog penyiapan tersimpan permanen di panel.json, dan aplikasinya
    tidak menanyakan lagi setelah terjawab sekali. Kalau folder data utama
    pernah dijawab "bergabung ke lab", maka SETIAP kali Retriva dijalankan dari
    situ ia terbuka sebagai panel Dosen — termasuk saat yang dibutuhkan adalah
    Superadmin. Tidak ada Superadmin yang berjalan, jadi tidak ada yang bisa
    membuka pendaftaran, jadi panel Dosen itu tidak akan pernah tertaut.

    Skrip pengembangan tidak boleh bergantung pada jawaban sesi kemarin. Ia
    menuliskan peran yang dimaksudnya, lalu menjalankan.

    DeviceId dan DeviceToken dipertahankan bila sudah ada — membuangnya berarti
    panel mendaftar sebagai perangkat baru setiap kali skrip dijalankan, dan
    daftar persetujuan di layar Superadmin akan dipenuhi hantu.
#>
function Set-PanelRole($dir, [bool]$isSuperadmin, $displayName) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir 'panel.json'

    $panel = if (Test-Path $file) {
        try { Get-Content $file -Raw | ConvertFrom-Json } catch { $null }
    } else { $null }

    if (-not $panel) { $panel = [pscustomobject]@{} }

    foreach ($pair in @{
        IsSuperadmin = $isSuperadmin
        Configured   = $true
        DisplayName  = $displayName
    }.GetEnumerator()) {
        $panel | Add-Member -NotePropertyName $pair.Key -NotePropertyValue $pair.Value -Force
    }

    $panel | ConvertTo-Json -Depth 5 | Set-Content $file -Encoding UTF8

    # Dibaca kembali dan dicetak. Menulis lalu memercayai bahwa tulisannya
    # terbaca adalah persis asumsi yang membuat dua jendela panel admin terbuka
    # tanpa satu pun galat: berkasnya ada, isinya terlihat benar sepintas, dan
    # aplikasinya membaca sesuatu yang lain.
    $check = Get-Content $file -Raw | ConvertFrom-Json
    Write-Note "  panel.json -> IsSuperadmin=$($check.IsSuperadmin) DisplayName='$($check.DisplayName)'"
}

<#
    Menunggu koordinator benar-benar mendengarkan.

    Sebelumnya skrip ini hanya tidur delapan detik lalu menjalankan agent. Kalau
    koordinatornya gagal start — misalnya karena perannya terbaca salah dan
    Kestrel tidak pernah dinyalakan — agent tetap dijalankan, gagal menyambung,
    dan mencetak "The SSL connection could not be established".

    Pesan itu menyesatkan ke arah sertifikat, padahal yang terjadi adalah tidak
    ada yang mendengarkan sama sekali. Berjam-jam bisa habis memeriksa sidik
    jari yang sebenarnya tidak bermasalah.
#>
function Wait-Coordinator([int]$port, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $probe = New-Object System.Net.Sockets.TcpClient
        try {
            $probe.Connect('127.0.0.1', $port)
            if ($probe.Connected) { $probe.Close(); return $true }
        } catch { }
        finally { $probe.Dispose() }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Start-Pane($title, $command) {
    Start-Process powershell -ArgumentList @(
        '-NoExit', '-Command',
        "`$host.UI.RawUI.WindowTitle = '$title'; Set-Location '$root'; $command"
    ) | Out-Null
}

# ---------------------------------------------------------------- bersihkan
<#
    Proses lama dihentikan sebelum build.

    Windows mengunci berkas exe selama prosesnya hidup, jadi build gagal dengan
    MSB3021 bila sesi sebelumnya masih terbuka. Pesannya panjang dan didahului
    sepuluh percobaan ulang, sehingga penyebab sesungguhnya — "aplikasinya masih
    jalan" — terkubur di antara puluhan baris peringatan dan mudah disalahartikan
    sebagai kesalahan kode.
#>
function Stop-Retriva {
    $names = @('RetrivaClient', 'Retriva', 'RetrivaAgent', 'Retriva.FakeAgent')
    $found = Get-Process -Name $names -ErrorAction SilentlyContinue
    if (-not $found) { return }

    Write-Step 'Menghentikan sesi Retriva yang masih berjalan'
    foreach ($p in $found) {
        Write-Note "$($p.ProcessName) (PID $($p.Id))"
        try { $p.CloseMainWindow() | Out-Null } catch { }
    }

    # Diberi waktu menutup diri dengan rapi supaya pemblokir input dilepas dan
    # pipa ditutup, baru dipaksa bila membandel.
    Start-Sleep -Milliseconds 1200
    Get-Process -Name $names -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
}

Stop-Retriva

<#
    -ResetAll membuang SELURUH keadaan: basis data, sertifikat, identitas kedua
    panel. Semuanya dibangun ulang dari nol pada jalankan berikutnya.

    Ada karena keadaan setengah jadi jauh lebih membingungkan daripada tidak
    ada keadaan sama sekali — baris panel lama di basis data, token yang tidak
    lagi cocok dengan panel.json, sertifikat yang sudah tidak disematkan siapa
    pun. Semuanya menghasilkan gejala yang tampak seperti kesalahan kode.

    Agent akan mendaftar ulang sendiri karena kodenya juga baru.
#>
if ($ResetAll) {
    Write-Step 'Membuang SELURUH keadaan Retriva'
    foreach ($dir in @($superData, $dosenData)) {
        if (Test-Path $dir) {
            Write-Note $dir
            Remove-Item $dir -Recurse -Force
        }
    }
}

if ($ResetDosen -and (Test-Path $dosenData)) {
    Write-Step 'Membuang identitas panel Dosen'
    Write-Note $dosenData
    Remove-Item $dosenData -Recurse -Force
    Write-Note 'Panel Dosen akan meminta kode pendaftaran baru.'
}

# ---------------------------------------------------------------- peran
Write-Step 'Menetapkan peran tiap instance'
Set-PanelRole $superData $true  'PC Asisten'
Write-Note "Superadmin -> $superData"

if (-not $NoDosen) {
    Set-PanelRole $dosenData $false 'Laptop Dosen'
    Write-Note "Dosen      -> $dosenData"
}

# ---------------------------------------------------------------- build
Push-Location "$root\ui"
if (-not (Test-Path 'node_modules')) {
    Write-Step 'Memasang dependensi UI'
    npm install
}
if (-not $SkipBuild -and -not $UiDev) {
    Write-Step 'Membangun UI React'
    npm run build
}
Pop-Location

if (-not $SkipBuild) {
    Write-Step 'Membangun solusi .NET'
    dotnet build "$root\Retriva.sln"
}

# ---------------------------------------------------------------- 0. dev server UI
if ($UiDev) {
    Write-Step '0/3 Menjalankan dev server Vite'
    Write-Note 'Perubahan pada berkas .jsx langsung terlihat tanpa build ulang.'
    Start-Pane 'Retriva — Vite' 'Set-Location ui; npm run dev'

    Write-Note 'Menunggu Vite siap di port 5173...'
    Start-Sleep -Seconds 6
}

# ---------------------------------------------------------------- 1. Superadmin
Write-Step '1/4 Menjalankan panel Superadmin'
Write-Note 'Membuat sertifikat, kode pendaftaran agent, dan basis data bila belum ada.'

<#
    SETIAP VARIABEL DISETEL EKSPLISIT, TIDAK ADA YANG DIWARISKAN.

    Start-Process mewariskan seluruh variabel lingkungan dari jendela induk.
    Kalau RETRIVA_DATA_DIR masih tertinggal di sesi ini — misalnya karena
    pernah diketik manual untuk mencoba panel Dosen — maka panel yang
    dimaksudkan sebagai Superadmin akan membaca panel.json milik Dosen, dan
    terbuka sebagai panel Dosen kedua.

    Gejalanya persis seperti skrip yang rusak: dua jendela panel admin yang
    sama-sama belum tertaut, dan tidak ada Superadmin sama sekali. Tidak ada
    galat, karena tidak ada yang gagal — aplikasinya menuruti variabel yang
    memang ada.

    Peringatan yang sama sudah ditulis untuk RETRIVA_UI_URL di versi
    sebelumnya, tetapi hanya diterapkan pada satu variabel. Sekarang keduanya.
#>
$superEnv = @"
`$env:RETRIVA_DATA_DIR = '$superData'
"@

if ($UiDev) {
    $adminCmd = @"
$superEnv
`$env:RETRIVA_UI_URL = 'http://localhost:5173'
dotnet run --project src/Retriva.Admin
"@
    Write-Note 'UI dimuat dari dev server Vite.'
} else {
    $adminCmd = @"
$superEnv
Remove-Item Env:RETRIVA_UI_URL -ErrorAction SilentlyContinue
dotnet run --project src/Retriva.Admin
"@
    Write-Note 'UI dimuat dari hasil build di wwwroot.'
}
Start-Pane 'Retriva — SUPERADMIN' $adminCmd

Write-Note 'Menunggu koordinator mendengarkan di port 8443...'

if (-not (Wait-Coordinator 8443 45)) {
    Write-Host ''
    Write-Host 'KOORDINATOR TIDAK PERNAH MEMBUKA PORT 8443.' -ForegroundColor Red
    Write-Host ''
    Write-Host 'Artinya panel Superadmin tidak berjalan sebagai penyelenggara lab.'
    Write-Host 'Yang perlu dilihat, di jendela "Retriva - SUPERADMIN":'
    Write-Host '  - baris pertama harus berbunyi   Retriva - SUPERADMIN - data: ...'
    Write-Host '  - kalau berbunyi PANEL ADMIN, panel.json terbaca sebagai bukan Superadmin'
    Write-Host '  - kalau ada dialog penyiapan terbuka, jawab lalu jalankan skrip ini lagi'
    Write-Host ''
    Write-Host 'Agent dan Client TIDAK dijalankan — keduanya hanya akan gagal dan'
    Write-Host 'menghasilkan galat SSL yang menyesatkan.' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host 'Membersihkan seluruh keadaan:  .\run-dev.ps1 -ResetAll' -ForegroundColor Yellow
    return
}

Write-Note 'Koordinator siap.'

<#
    Jendela log koordinator.

    ADA KARENA RETRIVA.ADMIN ADALAH WinExe.

    Windows tidak memberi konsol kepada aplikasi ber-subsystem GUI, jadi setiap
    baris log koordinator — sertifikat, pendaftaran panel, relay siaran —
    mengalir ke tempat yang tidak ada. Selama berjam-jam penelusuran, satu-
    satunya log yang tersedia adalah milik agent: sisi yang paling jarang
    menjadi penyebab masalahnya.

    Sekarang koordinator menulis ke berkas, dan jendela ini mengekornya supaya
    lognya muncul di tempat yang memang sudah dilihat orang sejak awal.
#>
Write-Step 'Membuka jendela log koordinator'
Write-Note $consoleLog

# Berkas disentuh lebih dulu bila belum ada. Get-Content -Wait pada berkas yang
# belum lahir langsung berhenti dengan galat, dan jendelanya lalu diam
# selamanya — terlihat persis seperti koordinator yang tidak mencatat apa pun.
if (-not (Test-Path $consoleLog)) { New-Item -ItemType File -Path $consoleLog -Force | Out-Null }

$tailCmd = @"
Write-Host 'Log koordinator: $consoleLog' -ForegroundColor Cyan
Write-Host ''
Get-Content -LiteralPath '$consoleLog' -Wait -Tail 500
"@
Start-Pane 'Retriva — LOG KOORDINATOR' $tailCmd

<#
    Jendela log klien.

    Sama seperti koordinator, RetrivaClient adalah WinExe tanpa konsol — tetapi
    ia sudah menulis ke berkas sejak lama. Yang tidak pernah ada hanyalah cara
    membacanya, sehingga baris "frame pertama diterima" yang justru paling
    menentukan tidak pernah sampai ke mata siapa pun.
#>
Write-Step 'Membuka jendela log klien'
Write-Note $clientLog

if (-not (Test-Path $clientLog)) { New-Item -ItemType File -Path $clientLog -Force | Out-Null }

$clientTail = @"
Write-Host 'Log klien: $clientLog' -ForegroundColor Cyan
Write-Host ''
Get-Content -LiteralPath '$clientLog' -Wait -Tail 500
"@
Start-Pane 'Retriva — LOG KLIEN' $clientTail

# ---------------------------------------------------------------- 2. Agent
Write-Step '2/4 Menjalankan Retriva.Agent sebagai PC-01'
Write-Note 'RETRIVA_POWER=simulate -> perintah daya hanya dicatat, tidak dieksekusi.'
Write-Note 'Tanpa ini, tombol Matikan akan benar-benar mematikan komputermu.'

# Agent memakai folder data Superadmin: di situlah console.thumbprint dan kode
# pendaftaran agent ditulis. Diwariskan dari sesi yang kebetulan menunjuk folder
# Dosen, agent tidak menemukan sidik jari dan menolak setiap sambungan.
$agentCmd = @"
`$env:RETRIVA_DATA_DIR = '$superData'
`$env:RETRIVA_CONSOLE = '127.0.0.1'
`$env:RETRIVA_UNIT    = 'PC-01'
`$env:RETRIVA_POWER   = 'simulate'
`$log = Join-Path `$env:TEMP 'retriva-agent.log'
Write-Host "Log agent: `$log" -ForegroundColor DarkGray
dotnet run --project src/Retriva.Agent 2>&1 | Tee-Object -FilePath `$log
"@
Start-Pane 'Retriva — Agent PC-01' $agentCmd

Write-Note 'Menunggu agent membuka pipa sesi...'
Start-Sleep -Seconds 8

# ---------------------------------------------------------------- 3. Client
Write-Step '3/4 Menjalankan Retriva.Client'
Write-Note 'RETRIVA_DEV_RESCUE=1 -> Ctrl+Shift+Alt+U melepas kunci layar.'
Write-Note 'Hanya untuk pengujian di satu mesin; tidak pernah aktif di lab.'

$clientCmd = @"
`$env:RETRIVA_DATA_DIR = '$superData'
`$env:RETRIVA_DEV_RESCUE = '1'
dotnet run --project src/Retriva.Client
"@
Start-Pane 'Retriva — Client' $clientCmd

# ---------------------------------------------------------------- 4. panel Dosen
if (-not $NoDosen) {
    Write-Step '4/4 Menjalankan panel Dosen'
    Write-Note "Folder data: $dosenData"
    Write-Note 'Terbuka dalam keadaan belum tertaut — itu memang yang diuji.'

    $uiLine = if ($UiDev) {
        "`$env:RETRIVA_UI_URL = 'http://localhost:5173'"
    } else {
        'Remove-Item Env:RETRIVA_UI_URL -ErrorAction SilentlyContinue'
    }

    <#
        --no-build, bukan dotnet run biasa.

        Panel ini memakai project yang SAMA dengan Superadmin yang sudah
        berjalan, dan Windows mengunci Retriva.exe selama prosesnya hidup.
        Tanpa --no-build, MSBuild mencoba menulis ulang berkas yang terkunci,
        gagal dengan MSB3021 setelah sepuluh percobaan, dan pesan sesungguhnya
        terkubur di antara puluhan baris peringatan.

        Solusi sudah dibangun beberapa langkah di atas, jadi tidak ada yang
        hilang. Dengan -SkipBuild dan folder bin kosong, ini akan mengeluh
        dengan jelas — dan itu memang keluhan yang benar.
    #>
    $dosenCmd = @"
`$env:RETRIVA_DATA_DIR = '$dosenData'
$uiLine
dotnet run --no-build --project src/Retriva.Admin
"@
    Start-Pane 'Retriva — PANEL DOSEN' $dosenCmd
}

# ---------------------------------------------------------------- unit tiruan
if ($Fake -gt 0) {
    Write-Step "Menjalankan $Fake unit tiruan"
    Write-Note 'Unit tiruan mulai dari PC-02 agar tidak bertabrakan dengan agent asli.'
    Start-Pane 'Retriva — Simulator' `
        "dotnet run --project src/Retriva.FakeAgent -- --from 2 --to $($Fake + 1)"
}

Write-Host ''
Write-Host 'Semua komponen dijalankan di jendela terpisah.' -ForegroundColor Green

if (-not $NoDosen) {
    Write-Host ''
    Write-Host 'Jendela yang terbuka:' -ForegroundColor Yellow
    Write-Host '  "Retriva - SUPERADMIN"    lencana hijau, semua menu ada'
    Write-Host '  "Retriva - PANEL DOSEN"   lencana kuning "Belum tertaut ke lab"'
    Write-Host '  "Retriva - Client"        jendela mahasiswa'
    Write-Host ''
    Write-Host 'Menautkan panel Dosen:' -ForegroundColor Yellow
    Write-Host '  1. SUPERADMIN -> menu "Panel admin" -> Buka pendaftaran'
    Write-Host '     Kode 12 karakter muncul besar, hidup 10 menit.'
    Write-Host '  2. PANEL DOSEN -> menu "Panel admin" (atau klik lencana kuningnya)'
    Write-Host '     Alamat: 127.0.0.1   Kode: yang tadi   Peran: Dosen'
    Write-Host '     Tekan Tautkan.'
    Write-Host '  3. SUPERADMIN -> baris "Laptop Dosen" muncul di Menunggu persetujuan'
    Write-Host '     Tekan tombol Dosen atau Asisten.'
    Write-Host '  4. PANEL DOSEN -> lencana berubah hijau. Selesai, permanen.'
    Write-Host ''
    Write-Host '  Kedua panel menampilkan sidik jari sertifikat. Nilainya harus sama.'
    Write-Host '  Mengulang dari nol:  .\run-dev.ps1 -ResetDosen -SkipBuild'
}

Write-Host ''
Write-Host 'Log:' -ForegroundColor Yellow
Write-Host "  koordinator : $consoleLog   <- jendela LOG KOORDINATOR"
Write-Host "  panel dosen : $panelLog"
Write-Host "  agent       : $env:TEMP\retriva-agent.log"
Write-Host ''
Write-Host 'Menguji kunci layar:' -ForegroundColor Yellow
Write-Host '  1. Di jendela Admin, klik unit PC-01 pada grid'
Write-Host '  2. Tekan tombol "Kunci layar"'
Write-Host '  3. Layarmu tertutup overlay — itu memang yang diuji'
Write-Host ''
Write-Host 'Cara membuka kunci saat menguji di satu mesin:' -ForegroundColor Yellow
Write-Host '  a. Ctrl+Shift+Alt+U     <- paling mudah, langsung dari overlay'
Write-Host '  b. Ctrl+Alt+Del -> Task Manager -> akhiri proses RetrivaClient'
Write-Host ''
Write-Host 'Catatan: tombol "Buka kunci" di panel admin TIDAK dapat dipakai saat'
Write-Host 'menguji di satu mesin, karena overlay menutupi jendela admin juga.'
Write-Host 'Di lab sungguhan itu bukan masalah — admin ada di komputer terpisah.'
Write-Host ''
