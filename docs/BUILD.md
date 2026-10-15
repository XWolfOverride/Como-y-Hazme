# Compilar `como` y `hazme`

No hace falta para usar el programa (hay [binarios en Releases](https://github.com/XWolfOverride/Como-y-Hazme/releases)):
sirve para tener la última versión, usarlo en **macOS**, en ARM o auditar lo que ejecutas.

| Opción | Necesitas | Resultado |
|---|---|---|
| **A. Sencilla** (recomendada) | git + SDK de .NET 10 | Carpeta con `.dll`; necesita .NET para ejecutarse |
| **B. Script** | A + compilador nativo | Binarios nativos con **un solo comando** (Windows, Debian/Ubuntu) |
| **C. NativeAOT a mano** | A + compilador nativo | Binarios nativos; cualquier sistema, incluido macOS |

**RID** (el "apellido" de tu sistema): `win-x64`, `win-arm64`, `osx-arm64` (Mac con chip Apple), `osx-x64`,
`linux-x64`, `linux-arm64`. En Linux, `uname -m`: `x86_64` → `linux-x64`, `aarch64` → `linux-arm64`.

## Instalar las herramientas

| Sistema | git + SDK de .NET 10 | Compilador nativo (B y C) |
|---|---|---|
| Windows | `winget install Git.Git` y `winget install Microsoft.DotNet.SDK.10` | `winget install Microsoft.VisualStudio.2022.BuildTools --override "--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"` |
| macOS | `brew install git` y `brew install --cask dotnet-sdk` | `xcode-select --install` |
| Debian / Ubuntu | `sudo apt install -y git curl` + SDK (abajo) | `sudo apt install -y clang zlib1g-dev build-essential` |
| Arch | `sudo pacman -S --needed git dotnet-sdk` | `sudo pacman -S --needed base-devel clang zlib` |

SDK en Debian/Ubuntu (instalador oficial, sin administrador; o `sudo apt install dotnet-sdk-10.0` si tu versión lo incluye):

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export DOTNET_ROOT="$HOME/.dotnet" PATH="$PATH:$HOME/.dotnet"   # añádelo a ~/.bashrc para que sea permanente
```

Tras instalar, **reabre la terminal**.

## A. Sencilla

```bash
git clone https://github.com/XWolfOverride/Como-y-Hazme && cd Como-y-Hazme
dotnet publish Como/Como.csproj   -c Release -p:PublishAot=false -o salida
dotnet publish Hazme/Hazme.csproj -c Release -p:PublishAot=false -o salida
```

La carpeta `salida` contiene `como` y `hazme` (`.exe` en Windows) con sus `.dll`: **mantenla completa** y
añade la **carpeta** al `PATH` (no basta con copiar un archivo). Necesita .NET instalado; con
`--self-contained -r <RID>` no lo necesita (pesa más).

## B. Script de autocompilación

Comprueba que tienes las herramientas (**no instala nada**), descarga la última versión, compila con
NativeAOT y deja los binarios en `Como-y-Hazme/`, indicando cómo instalarlos. Vuelve a ejecutarlo para actualizar.

```bash
curl -fsSL https://raw.githubusercontent.com/XWolfOverride/Como-y-Hazme/main/build.sh | bash     # Debian/Ubuntu
wget -qO- https://raw.githubusercontent.com/XWolfOverride/Como-y-Hazme/main/build.sh | bash      # (con wget)
```
```powershell
irm https://raw.githubusercontent.com/XWolfOverride/Como-y-Hazme/main/build.ps1 | iex           # Windows
```

> Ejecutar un script descargado requiere confiar en él: léelo antes en [`build.sh`](../build.sh) /
> [`build.ps1`](../build.ps1) o descárgalo (`curl -fsSLO <url>`) y ejecútalo tú.

## C. NativeAOT a mano

```bash
dotnet publish Como/Como.csproj   -c Release -r <RID>
dotnet publish Hazme/Hazme.csproj -c Release -r <RID>
```

Los binarios quedan en `Como/bin/Release/net10.0/<RID>/publish/` y `Hazme/bin/Release/net10.0/<RID>/publish/`.

- **Compila en el sistema operativo de destino**: NativeAOT no genera binarios de Linux desde Windows.
- **Para distribuir en Linux**, compila en el sistema más antiguo que quieras soportar (glibc).
- **Windows**, error `vswhere.exe no se reconoce`: usa *Developer PowerShell for VS 2022*.

## Instalar los binarios

```bash
mkdir -p ~/.local/bin && install -m 755 como hazme ~/.local/bin/    # usuario (añade ~/.local/bin al PATH si hace falta)
sudo install -m 755 como hazme /usr/local/bin/                      # todos los usuarios
```

En Windows, copia los `.exe` a `$HOME\bin` y añádela al `PATH` (ver [README](../README.md#instalación)).

## SteamOS (Steam Deck)

Basado en Arch, pero con el sistema de archivos de **solo lectura**; las actualizaciones borran los paquetes
de `pacman`. En *Modo escritorio* (Konsole):

```bash
sudo steamos-readonly disable
sudo pacman-key --init && sudo pacman-key --populate archlinux
sudo pacman -S --needed git base-devel clang zlib
sudo steamos-readonly enable
# SDK de .NET en tu carpeta personal (sobrevive a las actualizaciones): ver "SDK en Debian/Ubuntu"
```

Y sigue con B o C. Para **usar** el programa en una Steam Deck basta con el binario de Linux de las
releases. *(No verificado en un dispositivo real: si algo falla, abre un issue.)*
