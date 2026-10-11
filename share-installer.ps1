<#
    PENTING: BERKAS INI HARUS DISIMPAN DENGAN BOM UTF-8.
    Windows PowerShell 5.1 membaca .ps1 tanpa BOM sebagai Windows-1252, dan
    setiap em dash akan menutup string yang sedang dibaca parser. Lihat catatan
    di build-installer.ps1.
#>

<#
    Menyiapkan pemasangan Retriva untuk beberapa PC sekaligus.

    DIJALANKAN DI PC KOORDINATOR, sekali. Sesudah itu tiap PC mahasiswa dipasang
    dengan satu baris perintah yang dicetak skrip ini — tanpa flash disk, tanpa
    menyalin apa pun, tanpa mengetik alamat maupun sidik jari.

    YANG DIPECAHKAN BUKAN WAKTU BUILD.

    dotnet build dan build-installer.ps1 memakan sekitar satu menit. Yang
    memakan waktu adalah memindahkan berkas seratus megabyte ke setiap PC, lalu
    mengetik alamat, kode pendaftaran, dan sidik jari sertifikat di masing-masing
    — tiga nilai panjang yang salah satu karakternya saja sudah cukup untuk
    membuat unit tidak pernah muncul di dashboard.

    Skrip ini menghapus keduanya: berkasnya dibagikan lewat jaringan yang sudah
    ada, dan ketiga nilai dibaca langsung dari konfigurasi koordinator.
#>

param(
    # Folder yang dibagikan. Bawaannya release\ hasil build-installer.ps1.
    [string]$Path = (Join-Path $PSScriptRoot 'release'),

    [string]$ShareName = 'retriva',

    # Membatalkan pembagian. Dipakai setelah seluruh PC selesai dipasang —
    # share yang dibiarkan terbuka adalah folder yang dapat dibaca siapa pun
    # di jaringan lab, dan di dalamnya ada installer beserta muatannya.
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "   $text" -ForegroundColor DarkGray }
function Warn($text) { Write-Host "   $text" -ForegroundColor Yellow }

# --------------------------------------------------------------------------
if ($Remove) {
    Step 'Membatalkan pembagian'
    if (Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue) {
        Remove-SmbShare -Name $ShareName -Force
        Ok "share '$ShareName' dihapus"
    } else {
        Ok "share '$ShareName' memang tidak ada"
    }
    return
}

<#
    Hak administrator diperiksa DI AWAL.

    New-SmbShare menuntutnya, dan penolakannya muncul di tengah skrip sebagai
    pengecualian tentang akses — bukan sebagai kalimat yang menyuruh orangnya
    membuka PowerShell sebagai administrator.
#>
$elevated = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $elevated) {
    throw "Jalankan PowerShell sebagai Administrator. Membagikan folder menuntut hak itu."
}

# --------------------------------------------------------------------------
Step 'Memeriksa installer'

if (-not (Test-Path $Path)) {
    throw "Folder $Path belum ada. Jalankan .\build-installer.ps1 lebih dulu."
}

$setup = Join-Path $Path 'RetrivaSetup.exe'
if (-not (Test-Path $setup)) {
    throw "RetrivaSetup.exe tidak ada di $Path. Jalankan .\build-installer.ps1 lebih dulu."
}

$built = (Get-Item $setup).LastWriteTime
Ok ("RetrivaSetup.exe  {0:N1} MB  dibangun {1:dd MMM HH:mm}" -f ((Get-Item $setup).Length / 1MB), $built)

<#
    Umur installer disebutkan, bukan diandaikan baru.

    Menjalankan skrip ini setelah menyunting kode tetapi lupa membangun ulang
    akan membagikan versi lama — dan seluruh PC memasang versi itu tanpa satu
    pun tanda. Tanggalnya di layar membuat kelalaian itu terlihat sebelum
    empat puluh mesin ikut salah.
#>
if ((Get-Date) - $built -gt [TimeSpan]::FromHours(2)) {
    Warn "Installer dibangun lebih dari dua jam lalu. Pastikan ini memang versi yang dimaksud."
}

# --------------------------------------------------------------------------
Step 'Membaca konfigurasi koordinator'

<#
    Kode pendaftaran dan sidik jari DIBACA, bukan diminta diketik.

    Keduanya panjang, tidak bermakna, dan satu karakter yang tertukar
    menghasilkan unit yang terpasang sempurna lalu ditolak koordinator
    selamanya. Membacanya dari sumbernya menghapus seluruh kelas kesalahan itu.
#>
$dataDir = Join-Path $env:ProgramData 'Retriva'
$consoleJson = Join-Path $dataDir 'console.json'

if (-not (Test-Path $consoleJson)) {
    throw @"
$consoleJson tidak ada.

Panel Superadmin belum pernah dijalankan di mesin ini, jadi kode pendaftaran
dan sertifikatnya belum dibuat. Buka Retriva Panel Admin sekali, lalu jalankan
skrip ini lagi.
"@
}

$console = Get-Content $consoleJson -Raw | ConvertFrom-Json
$secret = $console.agentEnrollmentSecret
$port = if ($console.listenPort) { $console.listenPort } else { 8443 }

if ([string]::IsNullOrWhiteSpace($secret)) {
    throw "console.json tidak memuat agentEnrollmentSecret. Buka panel sekali supaya kodenya dibuat."
}

$thumbFile = Join-Path $dataDir 'console.thumbprint'
if (-not (Test-Path $thumbFile)) {
    throw "console.thumbprint tidak ada. Sertifikat koordinator belum dibuat."
}
$thumbprint = (Get-Content $thumbFile -Raw).Trim()

Ok "lab      $($console.labName)"
Ok "port     $port"
Ok "kode     $($secret.Substring(0, [Math]::Min(8, $secret.Length)))… ($($secret.Length) karakter)"
Ok "sidik    $($thumbprint.Substring(0, 16))…"

# --------------------------------------------------------------------------
Step 'Membagikan folder'

if (Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue) {
    Remove-SmbShare -Name $ShareName -Force
    Ok 'share lama dihapus'
}

<#
    HANYA BACA, untuk Everyone.

    Klien cukup membaca installer; tidak ada satu pun alasan mereka boleh
    menulis ke folder ini. Share yang dapat ditulis di jaringan lab adalah
    tempat berkas asing mendarat, dan berkas asing di folder yang dijalankan
    empat puluh PC adalah masalah yang tidak perlu dibuat.

    Everyone dipakai karena lab ini workgroup, bukan domain: tidak ada akun
    bersama yang dapat diberi izin. Yang membatasi adalah jaringannya sendiri.
#>
New-SmbShare -Name $ShareName -Path $Path -ReadAccess 'Everyone' -Description 'Retriva installer' | Out-Null
Ok "\\$env:COMPUTERNAME\$ShareName  (hanya baca)"

# --------------------------------------------------------------------------
Step 'Alamat yang dapat dipakai PC lain'

<#
    Loopback dan APIPA dibuang.

    127.0.0.1 di PC mahasiswa berarti mesin itu sendiri. 169.254.x adalah alamat
    yang diberikan Windows JUSTRU KETIKA jaringannya gagal — DHCP tidak menjawab
    — dan tidak akan pernah dapat dijangkau mesin lain. Menampilkan keduanya di
    daftar "alamat untuk PC lain" hanya mengundang kesalahan yang sama.
#>
$addresses = Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' } |
    Select-Object -ExpandProperty IPAddress

foreach ($ip in $addresses) { Ok $ip }

if ($addresses.Count -eq 0) {
    throw "Tidak ada alamat IPv4 yang dapat dijangkau mesin lain. Periksa kabel jaringan."
}

# --------------------------------------------------------------------------
Step 'Perintah untuk tiap PC mahasiswa'

Write-Host @"

Jalankan di tiap PC mahasiswa, sebagai Administrator. Ganti <ALAMAT> dengan
alamat di atas yang SATU JARINGAN dengan PC itu:

"@ -ForegroundColor DarkGray

foreach ($ip in $addresses) {
    Write-Host "  # lewat $ip" -ForegroundColor DarkGray
    Write-Host "  \\$ip\$ShareName\RetrivaSetup.exe --silent --role lab ``" -ForegroundColor White
    Write-Host "      --host $ip --port $port ``" -ForegroundColor White
    Write-Host "      --secret $secret ``" -ForegroundColor White
    Write-Host "      --thumbprint $thumbprint ``" -ForegroundColor White
    Write-Host "      --log C:\retriva-install.log" -ForegroundColor White
    Write-Host ""
}

Write-Host @"
Catatan:

  · Aman dijalankan ulang di PC yang sudah terpasang. Layanan dihentikan,
    berkas ditimpa, dan token perangkat dipertahankan — unit tidak kehilangan
    nama maupun riwayatnya.

  · Berhasil bila keluar dengan kode 0. Periksa dengan:  echo `$LASTEXITCODE

  · Bila gagal, alasannya ada di C:\retriva-install.log di PC itu.

  · Setelah semua terpasang, tutup share:
        .\share-installer.ps1 -Remove

"@ -ForegroundColor DarkGray
