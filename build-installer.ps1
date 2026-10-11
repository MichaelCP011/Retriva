<#
    PENTING: BERKAS INI HARUS DISIMPAN DENGAN BOM UTF-8.

    Windows PowerShell 5.1 — yang masih bawaan Windows 11 — membaca .ps1 tanpa
    BOM sebagai Windows-1252. Em dash "—" (E2 80 94) lalu terbaca sebagai tiga
    karakter, dan byte terakhirnya 0x94 adalah tanda kutip penutup di CP1252.
    Kutip itu MENUTUP string yang sedang dibaca parser, sehingga sisa barisnya
    menjadi token liar dengan galat yang menunjuk ke kata yang tidak ada
    hubungannya dengan sebabnya.
#>

<#
    Membangun installer Retriva: dari sumber sampai dua berkas .exe mandiri.

    DUA TAHAP, DAN URUTANNYA WAJIB.

    Muatan installer adalah hasil publish proyek lain, jadi ia harus ada
    SEBELUM Retriva.Setup dibangun — resource tidak dapat ditanam ke assembly
    yang sudah dikompilasi. Urutannya: publish → bungkus jadi ZIP → bangun
    installer yang menelan ZIP itu.

    GAGAL KERAS, TIDAK PERNAH SEPARUH JADI.

    Installer yang terbentuk dari publish yang gagal punya nama yang benar,
    ukuran yang wajar, dan berjalan sampai selesai — lalu unitnya tidak
    berfungsi karena ada berkas yang tidak ikut. Kegagalan itu ditemukan di lab,
    bukan di sini.
#>

param(
    # Hanya membangun salah satu peran. Berguna saat menggarap satu sisi saja.
    [ValidateSet('lab', 'panel', 'both')]
    [string]$Role = 'both',

    # Melewati publish. Menghemat sekitar satu menit, dan berbahaya bila kode
    # C#-nya juga berubah — karena itu bukan bawaan.
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$publish = Join-Path $PSScriptRoot 'publish'
$payload = Join-Path $PSScriptRoot 'src\Retriva.Setup\payload'
<#
    Installer TIDAK boleh mendarat di dist\.

    dist\ adalah keluaran Vite — ui/vite.config.js menulis ke sana, dan
    Retriva.Admin.csproj menyalin SELURUH isinya ke wwwroot lewat
    Content Include="..\..\dist\**\*".

    Menaruh RetrivaSetup.exe di situ berarti installer ikut terbungkus ke dalam
    panel pada build berikutnya, lalu installer berikutnya ikut membungkus
    installer sebelumnya — berkasnya membesar berlipat tiap kali dibangun, dan
    tidak ada satu pun galat yang menyebutkannya.
#>
$dist    = Join-Path $PSScriptRoot 'release'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "   $text" -ForegroundColor DarkGray }

function Publish-Project($project, $target) {
    $out = Join-Path $publish $target

    <#
        Folder dikosongkan, bukan ditimpa.

        dotnet publish tidak menghapus berkas yang sudah tidak lagi dihasilkan.
        Sebuah DLL dari build sebelumnya akan ikut terbungkus dan dipasang ke
        empat puluh PC — dan konflik versinya muncul sebagai galat yang tidak
        menyebut nama berkas itu sama sekali.
    #>
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    Step "Publish $project"

    dotnet publish "src\$project\$project.csproj" `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        --output $out `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish gagal untuk $project (exit $LASTEXITCODE)" }

    $count = (Get-ChildItem $out -Recurse -File).Count
    if ($count -lt 5) { throw "publish\$target hanya berisi $count berkas, tidak wajar" }

    Ok "$count berkas"
}

function Require-File($path) {
    if (-not (Test-Path $path)) { throw "berkas wajib tidak ada: $path" }
    Ok $path
}

# --------------------------------------------------------------------------
Step 'Membangun antarmuka React'
Push-Location ui
try {
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build gagal (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}
Ok 'ui/dist siap'

$buildLab   = $Role -in @('lab', 'both')
$buildPanel = $Role -in @('panel', 'both')

if (-not $SkipPublish) {
    if ($buildLab) {
        Publish-Project 'Retriva.Agent'  'agent'
        Publish-Project 'Retriva.Client' 'client'
    }
    if ($buildPanel) {
        Publish-Project 'Retriva.Admin'  'admin'
    }
} else {
    Step 'Publish dilewati (-SkipPublish)'
}

# --------------------------------------------------------------------------
Step 'Membungkus muatan'

if (-not (Test-Path $payload)) { New-Item -ItemType Directory -Path $payload | Out-Null }

<#
    Muatan lama DIHAPUS lebih dulu, selalu.

    ZIP yang tertinggal dari build sebelumnya akan ikut tertanam ke installer
    baru bila peran itu tidak dibangun kali ini — dan installernya memasang
    versi lama tanpa satu pun tanda. Nomor versinya pun tetap yang baru.
#>
Get-ChildItem $payload -Filter *.zip -ErrorAction SilentlyContinue | Remove-Item -Force

if ($buildLab) {
    Require-File "publish\agent\RetrivaAgent.exe"
    Require-File "publish\client\RetrivaClient.exe"

    # Client diletakkan di subfolder Client\, persis seperti yang dicari
    # Installer.cs saat menyusun jalur autostart.
    $staging = Join-Path $env:TEMP "retriva-lab-$(Get-Random)"
    New-Item -ItemType Directory -Path $staging | Out-Null
    try {
        Copy-Item "$publish\agent\*"  $staging -Recurse -Force
        Copy-Item "$publish\client"   (Join-Path $staging 'Client') -Recurse -Force
        Compress-Archive -Path "$staging\*" -DestinationPath (Join-Path $payload 'lab.zip') -Force
    } finally {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
    Ok ("lab.zip  {0:N1} MB" -f ((Get-Item (Join-Path $payload 'lab.zip')).Length / 1MB))
}

if ($buildPanel) {
    Require-File "publish\admin\Retriva.exe"
    Compress-Archive -Path "$publish\admin\*" -DestinationPath (Join-Path $payload 'panel.zip') -Force
    Ok ("panel.zip {0:N1} MB" -f ((Get-Item (Join-Path $payload 'panel.zip')).Length / 1MB))
}

# --------------------------------------------------------------------------
Step 'Membangun installer'

<#
    Retriva.Setup dibangun SETELAH muatannya ada.

    Bila urutannya terbalik, MSBuild tidak mengeluh sama sekali: Condition
    Exists di csproj hanya melewati resource yang belum ada, dan hasilnya
    installer tanpa muatan yang tampak sehat sampai tombol Pasang ditekan.
#>
$setupOut = Join-Path $publish 'setup'
if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }

dotnet publish "src\Retriva.Setup\Retriva.Setup.csproj" `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $setupOut `
    --nologo `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true

if ($LASTEXITCODE -ne 0) { throw "dotnet publish gagal untuk Retriva.Setup (exit $LASTEXITCODE)" }

# --------------------------------------------------------------------------
Step 'Memeriksa hasil'

if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

$built = Join-Path $setupOut 'RetrivaSetup.exe'
Require-File $built

<#
    Muatan diperiksa BENAR-BENAR tertanam, bukan diandaikan.

    Ukuran exe dibandingkan dengan ukuran ZIP yang seharusnya ada di dalamnya.
    Installer yang jauh lebih kecil daripada muatannya berarti resource-nya
    tidak ikut — dan itu keadaan yang tidak menghasilkan galat build apa pun.
#>
$expected = (Get-ChildItem $payload -Filter *.zip | Measure-Object Length -Sum).Sum
$actual   = (Get-Item $built).Length

if ($actual -lt $expected) {
    throw "RetrivaSetup.exe ($([math]::Round($actual/1MB,1)) MB) lebih kecil daripada muatannya " +
          "($([math]::Round($expected/1MB,1)) MB) - resource tidak ikut tertanam"
}

$final = Join-Path $dist 'RetrivaSetup.exe'
Copy-Item $built $final -Force

Write-Host ("`nSelesai: {0} ({1:N1} MB)" -f $final, ($actual / 1MB)) -ForegroundColor Green
Write-Host @"

PC pertama, dengan wizard:

    .\release\RetrivaSetup.exe

PC mahasiswa berikutnya, senyap. Ketiga nilai ada di layar Konfigurasi
pada panel Superadmin:

    .\release\RetrivaSetup.exe --silent --role lab ``
        --host 192.168.1.252 --port 8443 ``
        --secret <kode pendaftaran agent> ``
        --thumbprint <sidik jari sertifikat> ``
        --log C:\retriva-install.log

Memeriksa PC yang sudah terpasang, tanpa mengubah apa pun:

    & "$env:ProgramFiles\Retriva\Lab\RetrivaSetup.exe" --check --host 192.168.1.252

Mencopot:

    & "$env:ProgramFiles\Retriva\Lab\RetrivaSetup.exe" --uninstall

"@ -ForegroundColor DarkGray
