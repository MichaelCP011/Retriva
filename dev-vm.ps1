<#
    PENTING: BERKAS INI HARUS DISIMPAN DENGAN BOM UTF-8.
    Windows PowerShell 5.1 membaca .ps1 tanpa BOM sebagai Windows-1252, dan
    setiap em dash akan menutup string yang sedang dibaca parser.

    ================================================================
    Satu perintah untuk menguji perubahan di PC mahasiswa tiruan.
    ================================================================

    DIJALANKAN DI PC SUPERADMIN, tempat kode ditulis dan koordinator berjalan.

    YANG DIPECAHKAN: SIKLUS SETENGAH JAM UNTUK PERUBAHAN SEPULUH DETIK.

    Alur sebelumnya untuk memeriksa satu perbaikan di sisi mahasiswa:

        build → salin installer ke PC lain → matikan jaringan lab →
        sambung internet → unduh → sambung ulang jaringan lab →
        copot pemasangan lama → pasang → uji → ulangi

    Sebagian besar langkah itu tidak menguji apa pun. Ia hanya memindahkan
    berkas, dan memindahkan berkas adalah masalah yang sudah dipecahkan sejak
    lama oleh jaringan yang kabelnya sudah terpasang di ruangan itu.

    Alur sekarang:

        .\dev-vm.ps1

    Build, bagikan lewat jaringan lokal, kembalikan VM ke snapshot bersih,
    pasang di dalamnya tanpa dialog, selesai. Tidak ada internet, tidak ada
    flash disk, tidak ada berjalan ke PC lain.

    MENGAPA VM, BUKAN PC KEDUA.

    Bukan karena VM lebih mirip PC mahasiswa — justru sebaliknya, ia kurang
    mirip. Alasannya SNAPSHOT.

    Pemasangan yang gagal separuh jalan meninggalkan mesin dalam keadaan yang
    tidak sama dengan sebelum maupun sesudah: layanan terdaftar tapi tidak
    jalan, token perangkat tertulis tapi tidak dikenali koordinator, aturan
    firewall separuh. Di PC fisik, memulihkannya berarti mencopot pemasangan
    dan berharap uninstall-nya benar — yang berarti menguji uninstall setiap
    kali hendak menguji hal lain. Di VM, ia dikembalikan dalam lima detik dan
    keadaannya identik dengan pemasangan pertama, setiap kali.

    Itu juga berarti bug "hanya muncul pada pemasangan bersih" akhirnya dapat
    direproduksi dengan andal.

    YANG TETAP MENUNTUT PC FISIK: Wake-on-LAN (VM tidak punya NIC yang benar-
    benar tidur), dan pengukuran kecepatan tautan. Sisanya — siaran, kendali
    jarak jauh, mode ujian, berbagi berkas, pemasangan — berjalan sungguhan di
    sini, lewat switch yang sama dengan lab.

    Pemakaian:
        .\dev-vm.ps1                    build, bagikan, kembalikan snapshot, pasang
        .\dev-vm.ps1 -SkipBuild         lewati build, hanya pasang ulang
        .\dev-vm.ps1 -NoRevert          jangan kembalikan snapshot (uji pembaruan di atas versi lama)
        .\dev-vm.ps1 -ShareOnly         hanya bagikan dan cetak perintahnya, jangan sentuh VM
        .\dev-vm.ps1 -Vm 'nama VM'      pilih VM lain
        .\dev-vm.ps1 -Snapshot 'bersih' pilih snapshot lain
#>

param(
    [string]$Vm = 'Retriva-Mahasiswa',
    [string]$Snapshot = 'bersih',
    [string]$ShareName = 'retriva',
    [string]$GuestUser = 'lab',
    [string]$GuestPassword = 'lab',
    [switch]$SkipBuild,
    [switch]$NoRevert,
    [switch]$ShareOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "   $t" -ForegroundColor DarkGray }
function Warn($t) { Write-Host "   $t" -ForegroundColor Yellow }
function Bad($t)  { Write-Host "   $t" -ForegroundColor Red }

# ---------------------------------------------------------------- elevasi
<#
    Hak administrator diperiksa DI AWAL, bukan saat pertama dibutuhkan.

    New-SmbShare menuntutnya, dan penolakannya muncul di tengah skrip sebagai
    pengecualian tentang akses — setelah build berjalan satu menit. Memeriksanya
    lebih dulu mengubah kegagalan itu dari satu menit terbuang menjadi satu
    kalimat.
#>
$elevated = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $elevated) {
    throw "Jalankan PowerShell sebagai Administrator. Membagikan folder menuntut hak itu."
}

# ---------------------------------------------------------------- VBoxManage
<#
    VBoxManage dicari, bukan diasumsikan ada di PATH.

    Pemasang VirtualBox tidak selalu menambahkan foldernya ke PATH, dan
    "VBoxManage tidak dikenali sebagai perintah" adalah pesan yang menyesatkan:
    ia terbaca seperti VirtualBox belum terpasang, padahal ia terpasang dan
    hanya tidak terdaftar.
#>
function Find-VBoxManage {
    $cmd = Get-Command VBoxManage.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        (Join-Path $env:VBOX_MSI_INSTALL_PATH 'VBoxManage.exe'),
        'C:\Program Files\Oracle\VirtualBox\VBoxManage.exe',
        'C:\Program Files (x86)\Oracle\VirtualBox\VBoxManage.exe'
    )

    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    return $null
}

$vbox = if ($ShareOnly) { $null } else { Find-VBoxManage }

if (-not $ShareOnly -and -not $vbox) {
    Warn 'VBoxManage tidak ditemukan. VirtualBox mungkin belum terpasang.'
    Warn 'Skrip tetap berjalan sampai pembagian berkas, lalu mencetak perintahnya.'
    Warn 'Jalankan perintah itu di dalam VM secara manual.'
    $ShareOnly = $true
}

# ---------------------------------------------------------------- build
if (-not $SkipBuild) {
    Step 'Membangun UI dan installer'

    Push-Location $root
    try {
        & "$root\build-installer.ps1"
        if ($LASTEXITCODE -ne 0) { throw "build-installer.ps1 gagal (exit $LASTEXITCODE)" }
    } finally {
        Pop-Location
    }
} else {
    Ok 'Build dilewati.'
}

$release = Join-Path $root 'release'
$setupExe = Join-Path $release 'RetrivaSetup.exe'

if (-not (Test-Path $setupExe)) {
    throw "RetrivaSetup.exe tidak ada di $release. Jalankan tanpa -SkipBuild."
}

$size = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Ok "RetrivaSetup.exe  $size MB  $((Get-Item $setupExe).LastWriteTime.ToString('HH:mm:ss'))"

# ---------------------------------------------------------------- rahasia koordinator
<#
    Alamat, kode pendaftaran, dan sidik jari DIBACA DARI KONFIGURASI, tidak
    diketik.

    Ketiganya panjang, dan salah satu karakter saja sudah cukup membuat unit
    tidak pernah muncul di dashboard — tanpa galat, karena dari sudut pandang
    koordinator memang tidak ada yang mencoba mendaftar. Mengetiknya ulang tiap
    kali menguji adalah undangan bagi kesalahan yang paling sulit dilihat.
#>
Step 'Membaca konfigurasi koordinator'

$dataDir = Join-Path $env:ProgramData 'Retriva'
$consoleJson = Join-Path $dataDir 'console.json'
$thumbFile = Join-Path $dataDir 'console.thumbprint'

if (-not (Test-Path $consoleJson)) {
    throw "console.json tidak ada di $dataDir. Buka panel Superadmin sekali supaya dibuat."
}
if (-not (Test-Path $thumbFile)) {
    throw "console.thumbprint tidak ada. Sertifikat koordinator belum dibuat."
}

$console = Get-Content $consoleJson -Raw | ConvertFrom-Json
$secret = $console.agentEnrollmentSecret
$port = if ($console.listenPort) { $console.listenPort } else { 8443 }
$thumbprint = (Get-Content $thumbFile -Raw).Trim()

if ([string]::IsNullOrWhiteSpace($secret)) {
    throw "console.json tidak memuat agentEnrollmentSecret. Buka panel sekali supaya kodenya dibuat."
}

Ok "port     $port"
Ok "kode     $($secret.Substring(0, [Math]::Min(6, $secret.Length)))…"
Ok "sidik    $($thumbprint.Substring(0, 16))…"

# ---------------------------------------------------------------- alamat host
<#
    Alamat yang dipakai adalah yang SATU SUBNET dengan lab, bukan yang pertama
    ditemukan.

    Mesin ini punya beberapa: adapter host-only VirtualBox (192.168.56.1),
    kemungkinan VPN, dan kartu lab yang sebenarnya. VM ber-jaringan bridged
    berada di subnet lab, jadi hanya alamat lab yang dapat dijangkaunya.

    Menebak salah di sini menghasilkan gejala yang sudah pernah menghabiskan
    waktu berjam-jam: pemasangan berhasil, layanan berjalan, dan unitnya tidak
    pernah muncul — karena ia menghubungi alamat yang benar-benar ada tetapi
    tidak dapat dicapai dari tempatnya berada.
#>
Step 'Menentukan alamat koordinator'

$addresses = Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object {
        $_.IPAddress -notlike '127.*' -and
        $_.IPAddress -notlike '169.254.*' -and
        $_.PrefixOrigin -ne 'WellKnown'
    } |
    Sort-Object -Property @{ Expression = {
        # Adapter host-only VirtualBox diturunkan prioritasnya. Ia berfungsi
        # untuk VM host-only, tetapi bukan yang dipakai VM bridged maupun PC
        # lab sungguhan — dan keduanya yang lebih sering diuji.
        if ($_.IPAddress -like '192.168.56.*') { 2 } else { 1 }
    }}

if (-not $addresses) { throw 'Tidak ada alamat IPv4 yang dapat dipakai di mesin ini.' }

$hostIp = $addresses[0].IPAddress

Ok "dipakai  $hostIp"
if ($addresses.Count -gt 1) {
    Ok "lainnya  $(($addresses | Select-Object -Skip 1 | ForEach-Object { $_.IPAddress }) -join ', ')"
}

# ---------------------------------------------------------------- bagikan
Step 'Membagikan folder release lewat jaringan'

if (Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue) {
    Remove-SmbShare -Name $ShareName -Force | Out-Null
}

<#
    Dibagikan HANYA-BACA kepada Everyone.

    Folder ini berisi installer, bukan data. Memberi hak tulis berarti mesin
    mana pun di jaringan lab dapat mengganti RetrivaSetup.exe dengan berkas
    lain — dan berkas itulah yang kemudian dipasang sebagai administrator di
    setiap PC mahasiswa.
#>
New-SmbShare -Name $ShareName -Path $release -ReadAccess 'Everyone' | Out-Null
Ok "\\$hostIp\$ShareName  (hanya-baca)"

$installCmd = "\\$hostIp\$ShareName\RetrivaSetup.exe --silent --role lab " +
              "--host $hostIp --port $port --secret $secret --thumbprint $thumbprint " +
              "--log C:\retriva-install.log"

if ($ShareOnly) {
    Step 'Jalankan perintah ini DI DALAM VM (PowerShell sebagai Administrator)'
    Write-Host ''
    Write-Host "  $installCmd" -ForegroundColor White
    Write-Host ''
    Ok 'Setelah selesai, buka RetrivaClient dari Start Menu di dalam VM.'
    return
}

# ---------------------------------------------------------------- VM
Step "Menyiapkan VM '$Vm'"

$vmList = & $vbox list vms
if ($vmList -notmatch [regex]::Escape("`"$Vm`"")) {
    Bad "VM bernama '$Vm' tidak ada. Yang tersedia:"
    & $vbox list vms | ForEach-Object { Write-Host "     $_" -ForegroundColor DarkGray }
    Write-Host ''
    Warn "Buat VM-nya dulu (lihat docs\vm-lab.md), atau sebut namanya: -Vm 'nama'"
    Warn "Untuk sementara, pakai -ShareOnly lalu pasang manual."
    return
}

# Dimatikan lebih dulu. Snapshot tidak dapat dikembalikan selagi VM berjalan,
# dan penolakannya berbunyi tentang keadaan mesin — bukan tentang apa yang
# harus dilakukan.
$running = (& $vbox list runningvms) -match [regex]::Escape("`"$Vm`"")
if ($running) {
    Ok 'Mematikan VM yang sedang berjalan...'
    & $vbox controlvm $Vm poweroff 2>$null | Out-Null
    Start-Sleep -Seconds 3
}

if (-not $NoRevert) {
    $snapshots = & $vbox snapshot $Vm list 2>$null

    if ($LASTEXITCODE -ne 0 -or $snapshots -notmatch [regex]::Escape($Snapshot)) {
        Warn "Snapshot '$Snapshot' tidak ada — VM tidak dikembalikan."
        Warn "Buat sekali setelah VM bersih siap:"
        Warn "  VBoxManage snapshot `"$Vm`" take `"$Snapshot`""
    } else {
        Ok "Mengembalikan ke snapshot '$Snapshot'..."
        & $vbox snapshot $Vm restore $Snapshot | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Pengembalian snapshot gagal.' }
    }
}

Ok 'Menjalankan VM...'
& $vbox startvm $Vm --type gui | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'VM tidak dapat dijalankan.' }

# ---------------------------------------------------------------- pasang di dalam VM
<#
    Menunggu Guest Additions, bukan tidur sekian detik.

    Waktu boot berbeda-beda: setelah pengembalian snapshot ia cepat, setelah
    pembaruan Windows ia bisa menit-menitan. Angka tetap yang cukup untuk satu
    keadaan akan gagal pada keadaan lain, dan kegagalannya muncul sebagai
    perintah yang ditolak karena VM belum siap — yang terbaca seperti masalah
    kredensial.
#>
Step 'Menunggu VM siap'

$deadline = (Get-Date).AddMinutes(3)
$ready = $false

while ((Get-Date) -lt $deadline) {
    $info = & $vbox guestproperty get $Vm '/VirtualBox/GuestInfo/OS/LoggedInUsers' 2>$null
    if ($info -match 'Value:\s*[1-9]') { $ready = $true; break }
    Start-Sleep -Seconds 3
}

if (-not $ready) {
    Warn 'VM belum melaporkan pengguna yang login dalam 3 menit.'
    Warn 'Kemungkinan Guest Additions belum terpasang, atau login belum selesai.'
    Write-Host ''
    Write-Host 'Jalankan ini DI DALAM VM (PowerShell sebagai Administrator):' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "  $installCmd" -ForegroundColor White
    Write-Host ''
    return
}

Ok 'VM siap.'

Step 'Memasang Retriva di dalam VM'

# Beri Windows waktu menyelesaikan startup sebelum perintah pertama. Layanan
# yang masih dimuat membuat sc dan netsh gagal dengan cara yang tidak konsisten.
Start-Sleep -Seconds 8

$guestArgs = @(
    'guestcontrol', $Vm, 'run',
    '--username', $GuestUser,
    '--password', $GuestPassword,
    '--exe', 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe',
    '--wait-stdout', '--wait-stderr',
    '--', 'powershell', '-NoProfile', '-Command', $installCmd
)

& $vbox @guestArgs
$installExit = $LASTEXITCODE

Write-Host ''
if ($installExit -eq 0) {
    Write-Host '  PEMASANGAN SELESAI.' -ForegroundColor Green
    Write-Host ''
    Ok 'Unit akan muncul di dashboard Superadmin dalam beberapa detik.'
    Ok 'RetrivaClient berjalan otomatis saat pengguna login di VM.'
} else {
    Bad "Pemasangan di dalam VM gagal (exit $installExit)."
    Write-Host ''
    Warn 'Log pemasangan ada di C:\retriva-install.log DI DALAM VM.'
    Warn 'Bila guest control yang bermasalah, jalankan manual di dalam VM:'
    Write-Host ''
    Write-Host "  $installCmd" -ForegroundColor White
}

Write-Host ''
Write-Host 'Loop berikutnya:  .\dev-vm.ps1' -ForegroundColor Cyan
Write-Host 'Tanpa build   :  .\dev-vm.ps1 -SkipBuild' -ForegroundColor DarkGray
Write-Host 'Batalkan share:  .\share-installer.ps1 -Remove' -ForegroundColor DarkGray
