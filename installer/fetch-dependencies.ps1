<#
.SYNOPSIS
    Pobiera z publicznego NuGet (nuget.org) biblioteki Tekla Open API i inne
    zaleznosci wymagane przez RadiusDimensionMover.exe, i kopiuje je do
    folderu docelowego (obok .exe).

.DESCRIPTION
    Ten skrypt NIE zawiera ani nie rozprowadza samodzielnie zadnego kodu
    Trimble/Tekla - pobiera dokladnie te same publiczne pakiety NuGet, ktore
    kazdy deweloper Tekla Open API pobralby sam poleceniem "dotnet restore"
    albo z poziomu Visual Studio. Wersje ponizej sa dokladnie takie same,
    jak uzyte przy budowaniu tego programu (zweryfikowane rozmiarami plikow
    .dll w wyjsciu builda).

.PARAMETER TargetDir
    Folder, do ktorego maja trafic pobrane pliki .dll (folder instalacji
    RadiusDimensionMover.exe).
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$TargetDir
)

$ErrorActionPreference = "Stop"

$packages = @(
    @{ Id = "mono.cecil";                              Version = "0.11.5";  Lib = "net40" }
    @{ Id = "system.buffers";                           Version = "4.5.1";   Lib = "net461" }
    @{ Id = "system.memory";                            Version = "4.5.5";   Lib = "net461" }
    @{ Id = "system.numerics.vectors";                  Version = "4.5.0";   Lib = "net46" }
    @{ Id = "system.runtime.compilerservices.unsafe";   Version = "6.0.0";   Lib = "net461" }
    @{ Id = "tekla.common.geometry";                    Version = "4.6.4";   Lib = "net" }
    @{ Id = "tekla.structures";                         Version = "2025.0.0"; Lib = "net48" }
    @{ Id = "tekla.structures.datatype";                Version = "2025.0.0"; Lib = "net48" }
    @{ Id = "tekla.structures.drawing";                 Version = "2025.0.0"; Lib = "net48" }
    @{ Id = "tekla.structures.model";                   Version = "2025.0.0"; Lib = "net48" }
    @{ Id = "tekla.structures.plugins";                 Version = "2025.0.0"; Lib = "net48" }
    @{ Id = "tekla.technology.scripting.plugins";       Version = "5.5.6";   Lib = "netstandard2.0" }
    @{ Id = "tekla.technology.serialization";           Version = "4.3.7";   Lib = "netstandard2.0" }
    @{ Id = "trimble.remoting";                         Version = "4.0.8";   Lib = "netstandard2.0" }
)

if (-not (Test-Path $TargetDir)) {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
}

$tempDir = Join-Path $env:TEMP ("RadiusDimensionMover_deps_" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $failed = @()

    foreach ($pkg in $packages) {
        $id = $pkg.Id
        $version = $pkg.Version
        $lib = $pkg.Lib

        Write-Host "Pobieranie $id $version..."

        try {
            $url = "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg"
            $nupkgPath = Join-Path $tempDir "$id.$version.nupkg"
            $zipPath = Join-Path $tempDir "$id.$version.zip"
            $extractPath = Join-Path $tempDir "$id.$version"

            Invoke-WebRequest -Uri $url -OutFile $nupkgPath -UseBasicParsing
            Copy-Item $nupkgPath $zipPath
            Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

            $libPath = Join-Path $extractPath "lib\$lib"
            if (Test-Path $libPath) {
                Copy-Item -Path (Join-Path $libPath "*.dll") -Destination $TargetDir -Force
            }
            else {
                throw "Brak folderu lib\$lib w pobranym pakiecie."
            }
        }
        catch {
            Write-Warning ("Nie udalo sie pobrac/rozpakowac " + $id + " " + $version + ": " + $_.Exception.Message)
            $failed += $id
        }
    }

    if ($failed.Count -gt 0) {
        Write-Warning "Nie udalo sie pobrac nastepujacych pakietow: $($failed -join ', ')"
        Write-Warning "Program moze nie dzialac poprawnie. Sprawdz polaczenie internetowe i uruchom instalator ponownie,"
        Write-Warning "albo zbuduj program z kodu zrodlowego (patrz README) na komputerze z internetem."
        exit 1
    }

    Write-Host "Wszystkie wymagane biblioteki zostaly pobrane pomyslnie."
    exit 0
}
finally {
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
