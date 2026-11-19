$ErrorActionPreference = 'Stop'
$deps = (New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot 'native/deps')).FullName

function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Sha256) {
    if (!(Test-Path -LiteralPath $Path)) {
        $partial = "$Path.download"
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $partial -TimeoutSec 180
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Sha256) {
                throw "SHA-256 mismatch for $Url"
            }
            Move-Item -LiteralPath $partial -Destination $Path
        } finally {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial }
        }
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "SHA-256 mismatch for $Path"
    }
}

function Get-Sdk([string]$Url, [string]$File, [string]$Sha256, [string]$Destination, [string]$RequiredFile) {
    $archive = Join-Path $deps $File
    Get-VerifiedFile $Url $archive $Sha256
    $folder = Join-Path $deps $Destination
    if (!(Test-Path -LiteralPath (Join-Path $folder $RequiredFile) -PathType Leaf)) {
        Expand-Archive -LiteralPath $archive -DestinationPath $folder -Force
    }
    if (!(Test-Path -LiteralPath (Join-Path $folder $RequiredFile) -PathType Leaf)) {
        throw "Required SDK file missing after extraction: $RequiredFile"
    }
}

Get-Sdk 'https://ziglang.org/download/0.15.2/zig-x86_64-windows-0.15.2.zip' 'zig-x86_64-windows-0.15.2.zip' `
    '3A0ED1E8799A2F8CE2A6E6290A9FF22E6906F8227865911FB7DDEDC3CC14CB0C' '.' 'zig-x86_64-windows-0.15.2/zig.exe'
Get-Sdk 'https://github.com/OpenEtherCATsociety/SOEM/releases/download/v2.0.1/soem-v2.0.1-Windows-x86_64.zip' 'soem-v2.0.1-Windows-x86_64.zip' `
    'BB7672CC2873E1F880932CC4E8E0EBF4D238D5A5B09F54A37BA8889EEFD55671' 'soem-v2.0.1' 'soem-v2.0.1-Windows-x86_64/lib/libsoem.a'
Get-Sdk 'https://npcap.com/dist/npcap-sdk-1.17.zip' 'npcap-sdk-1.17.zip' `
    'AB7E1A6CB8D07427E0071EC11BE380BD9D2BB19AE6AB3F5EDE0698C301638258' 'npcap-sdk-1.17' 'Lib/x64/wpcap.lib'

$source = (New-Item -ItemType Directory -Force -Path (Join-Path $deps 'soem-v2.0.1-source')).FullName
Get-VerifiedFile 'https://raw.githubusercontent.com/OpenEtherCATsociety/SOEM/v2.0.1/src/ec_config.c' (Join-Path $source 'ec_config.c') `
    '9263B2FE708FADF4B8A8F0D46E9448C051A5A3394064BE590F9553DAE180285A'
Get-VerifiedFile 'https://raw.githubusercontent.com/OpenEtherCATsociety/SOEM/v2.0.1/oshw/win32/oshw.h' (Join-Path $source 'oshw.h') `
    'C6D43122FD83543D3F526051DF13668B8E0DBA7E59AD8AE4C2C4AE485929FBC3'
