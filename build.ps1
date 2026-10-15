# como / hazme - autocompilación NativeAOT para Windows (PowerShell 5.1 o superior).
#
#   irm https://raw.githubusercontent.com/XWolfOverride/Como-y-Hazme/main/build.ps1 | iex
#
# Qué hace:
#   1. Comprueba que están instaladas las herramientas necesarias (NO instala nada).
#   2. Descarga (o actualiza) la última versión del código con git.
#   3. Compila `como` y `hazme` con NativeAOT.
#   4. Deja los .exe en la raíz del proyecto y te explica cómo instalarlos.
#
# Variable opcional:
#   $env:COMO_REPO_URL   URL del repositorio (por defecto, el oficial; útil para forks)
#
# Nota: todo va dentro de una función y se sale con `return`, para que al ejecutarlo con
# `| iex` un fallo no cierre tu ventana de PowerShell.

function Invoke-ComoBuild {
    $ErrorActionPreference = 'Stop'

    $repoUrl = if ($env:COMO_REPO_URL) { $env:COMO_REPO_URL } else { 'https://github.com/XWolfOverride/Como-y-Hazme' }
    $dirName = 'Como-y-Hazme'

    function Ok($m)   { Write-Host "[OK] " -ForegroundColor Green -NoNewline; Write-Host $m }
    function Bad($m)  { Write-Host "[X]  " -ForegroundColor Red -NoNewline; Write-Host $m }
    function Info($m) { Write-Host ">>   " -ForegroundColor Cyan -NoNewline; Write-Host $m }

    Write-Host ""
    Write-Host "como - hazme: autocompilacion" -ForegroundColor White
    Write-Host ""

    # ---------------------------------------------------------- arquitectura
    $arch = $env:PROCESSOR_ARCHITEW6432
    if (-not $arch) { $arch = $env:PROCESSOR_ARCHITECTURE }
    switch ($arch) {
        'AMD64' { $rid = 'win-x64' }
        'ARM64' { $rid = 'win-arm64' }
        default { Write-Host "Arquitectura no soportada por el script: $arch" -ForegroundColor Red; return }
    }
    Info "Sistema detectado: $rid"
    Write-Host ""

    # ---------------------------------------------------------- prerrequisitos
    $missing = @()
    $hints = @()

    if (Get-Command git -ErrorAction SilentlyContinue) {
        Ok ("git (" + ((git --version) -replace 'git version ', '') + ")")
    } else {
        Bad 'git'; $missing += 'git'; $hints += 'winget install Git.Git'
    }

    $dotnetOk = $false
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $ver = (& dotnet --version 2>$null)
        $major = 0
        if ($ver -and [int]::TryParse(($ver -split '\.')[0], [ref]$major) -and $major -ge 10) {
            Ok "SDK de .NET ($ver)"; $dotnetOk = $true
        } else {
            Bad "SDK de .NET: version $ver, se necesita 10 o superior"
        }
    } else {
        Bad 'SDK de .NET 10'
    }
    if (-not $dotnetOk) { $missing += 'SDK de .NET 10'; $hints += 'winget install Microsoft.DotNet.SDK.10' }

    # Compilador nativo: Build Tools / Visual Studio con la carga de trabajo de C++.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $hasCpp = $false
    if (Test-Path $vswhere) {
        $vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
        if ($vsPath) { $hasCpp = $true }
    }
    if ($hasCpp) {
        Ok 'Compilador de C++ (Visual Studio Build Tools)'
        # ILCompiler invoca "vswhere.exe" por nombre: nos aseguramos de que esté en el PATH de este proceso.
        $vsDir = Split-Path $vswhere
        if (($env:PATH -split ';') -notcontains $vsDir) { $env:PATH = "$env:PATH;$vsDir" }
    } else {
        Bad 'Compilador de C++ (Visual Studio Build Tools con "Desktop development with C++")'
        $missing += 'Compilador de C++'
        $hints += 'winget install Microsoft.VisualStudio.2022.BuildTools --override "--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"'
    }

    if ($missing.Count -gt 0) {
        Write-Host ""
        Write-Host ("Faltan herramientas necesarias: " + ($missing -join ', ')) -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Instalalas con (este script no instala nada por ti):"
        Write-Host ""
        foreach ($h in $hints) { Write-Host "    $h" }
        Write-Host ""
        Write-Host "Cierra y vuelve a abrir PowerShell tras instalarlas y repite este comando."
        Write-Host "Detalles en el README del proyecto."
        return
    }

    # ---------------------------------------------------------- codigo fuente
    Write-Host ""
    $here = (Get-Location).Path
    if ((Test-Path (Join-Path $here 'Como.slnx')) -and (Test-Path (Join-Path $here '.git'))) {
        $root = $here
        Info "Usando el repositorio actual: $root"
        git -C $root pull --ff-only
        if ($LASTEXITCODE -ne 0) { Write-Host "No se pudo actualizar (hay cambios locales?). Resuelvelo y repite." -ForegroundColor Red; return }
    } elseif (Test-Path (Join-Path $here "$dirName\.git")) {
        $root = Join-Path $here $dirName
        Info "Actualizando $root"
        git -C $root pull --ff-only
        if ($LASTEXITCODE -ne 0) { Write-Host "No se pudo actualizar $root (hay cambios locales?). Resuelvelo o borralo y repite." -ForegroundColor Red; return }
    } else {
        $root = Join-Path $here $dirName
        Info "Descargando $repoUrl"
        git clone --depth 1 $repoUrl $root
        if ($LASTEXITCODE -ne 0) { Write-Host "No se pudo clonar el repositorio." -ForegroundColor Red; return }
    }

    # ---------------------------------------------------------- compilacion
    $env:DOTNET_NOLOGO = '1'
    $pub = Join-Path $root 'publish'
    if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }

    foreach ($p in @(@{ Project = 'Como'; Exe = 'como' }, @{ Project = 'Hazme'; Exe = 'hazme' })) {
        $proj = Join-Path $root "$($p.Project)\$($p.Project).csproj"
        $out = Join-Path $pub $p.Exe
        Info "Compilando $($p.Exe) (NativeAOT, $rid)... puede tardar un par de minutos"
        dotnet publish $proj -c Release -r $rid -o $out -nologo -v q
        if ($LASTEXITCODE -ne 0) { Write-Host "Fallo la compilacion de $($p.Exe)." -ForegroundColor Red; return }
        $built = Join-Path $out "$($p.Exe).exe"
        if (-not (Test-Path $built)) { Write-Host "No se encontro $($p.Exe).exe tras compilar." -ForegroundColor Red; return }
        Copy-Item $built (Join-Path $root "$($p.Exe).exe") -Force
        Ok "$($p.Exe).exe -> $(Join-Path $root "$($p.Exe).exe")"
    }
    Remove-Item $pub -Recurse -Force

    # ---------------------------------------------------------- final
    Write-Host ""
    Write-Host "Listo. " -ForegroundColor Green -NoNewline
    Write-Host "Los programas estan en: $root"
    foreach ($e in 'como.exe', 'hazme.exe') {
        $f = Get-Item (Join-Path $root $e)
        Write-Host ("    {0,8:N1} MB  {1}" -f ($f.Length / 1MB), $e)
    }
    Write-Host ""
    Write-Host "Para instalarlos en tu PATH (sin administrador), ejecuta desde esa carpeta:" -ForegroundColor White
    Write-Host ""
    Write-Host "    cd `"$root`""
    Write-Host '    mkdir $HOME\bin -Force | Out-Null'
    Write-Host '    copy como.exe, hazme.exe $HOME\bin'
    Write-Host '    $p = [Environment]::GetEnvironmentVariable("Path", "User")'
    Write-Host '    [Environment]::SetEnvironmentVariable("Path", "$p;$HOME\bin", "User")'
    Write-Host ""
    Write-Host "Cierra y vuelve a abrir la terminal y comprueba con:  como -connectors"
    Write-Host "Despues configura tu API key, por ejemplo:  como -set claude.api-key sk-ant-..."
    Write-Host ""
}

Invoke-ComoBuild
