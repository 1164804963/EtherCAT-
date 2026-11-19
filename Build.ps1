param([switch]$ManagedOnly, [string]$OutputDirectory = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    $output = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
    if (-not $ManagedOnly) {
        $dependencyDirectory = (Resolve-Path -LiteralPath 'native/deps').Path
        $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $dependencyDirectory 'zig-global'
        $env:ZIG_LOCAL_CACHE_DIR = Join-Path $dependencyDirectory 'zig-local'
        $zig = Join-Path $dependencyDirectory 'zig-x86_64-windows-0.15.2/zig.exe'
        $soem = Join-Path $dependencyDirectory 'soem-v2.0.1/soem-v2.0.1-Windows-x86_64'
        $npcap = Join-Path $dependencyDirectory 'npcap-sdk-1.17'
        $nativeSource = (Resolve-Path -LiteralPath 'native/bench_soem.c').Path
        $bridge = Join-Path $output 'EtherCatBench.Soem.dll'
        $upstream = Join-Path $dependencyDirectory 'soem-v2.0.1-source'
        $configPath = Join-Path $upstream 'ec_config.c'
        $hardwareHeader = Join-Path $upstream 'oshw.h'
        if ((Get-FileHash -LiteralPath $configPath).Hash -ne '9263B2FE708FADF4B8A8F0D46E9448C051A5A3394064BE590F9553DAE180285A' -or
            (Get-FileHash -LiteralPath $hardwareHeader).Hash -ne 'C6D43122FD83543D3F526051DF13668B8E0DBA7E59AD8AE4C2C4AE485929FBC3') {
            throw 'Unexpected SOEM v2.0.1 configuration source.'
        }
        $config = Get-Content -LiteralPath $configPath -Raw
        $config = $config.Replace('#include "oshw.h"', '#include "oshw.h"' + [Environment]::NewLine +
            'extern int tb_apply_online_pdo_sizes(ecx_contextt *, uint16, uint32 *, uint32 *);')
        $config = $config.Replace('if (context->slavelist[slave].mbx_proto & ECT_MBXPROT_COE) /* has CoE */',
            'if (tb_apply_online_pdo_sizes(context, slave, &Osize, &Isize)) { } else if (context->slavelist[slave].mbx_proto & ECT_MBXPROT_COE) /* has CoE */')
        $configUnit = Join-Path $upstream 'bench_ec_config.c'
        Set-Content -LiteralPath $configUnit -Value $config -Encoding utf8 -NoNewline
        $soemArchive = Join-Path $upstream 'libsoem-bench.a'
        Copy-Item -LiteralPath (Join-Path $soem 'lib/libsoem.a') -Destination $soemArchive -Force
        & $zig ar d $soemArchive ec_config.obj
        if ($LASTEXITCODE -ne 0) { throw "Could not replace the SOEM configuration unit ($LASTEXITCODE)." }

        & $zig cc -target x86_64-windows-gnu -O2 -shared "-I$soem/include" "-I$soem/include/soem" "-I$npcap/Include" $nativeSource $configUnit $soemArchive "$npcap/Lib/x64/wpcap.lib" "$npcap/Lib/x64/Packet.lib" -lws2_32 -lwinmm -o $bridge
        if ($LASTEXITCODE -ne 0) { throw "SOEM bridge build failed ($LASTEXITCODE)." }
    }

    $csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $executable = Join-Path $output 'EtherCatBench-Live.exe'
    & $csc /nologo /utf8output /langversion:5 /warn:4 /optimize+ /platform:x64 /target:winexe "/out:$executable" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll Program.cs BenchForm.cs BenchModel.cs BenchLive.cs HardwareForm.cs HardwareBus.cs
    if ($LASTEXITCODE -ne 0) { throw "Windows application build failed ($LASTEXITCODE)." }
    if ($output -ne $PSScriptRoot) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DemoESI.xml') -Destination $output
    }
} finally {
    Pop-Location
}
