# como · hazme

Dos herramientas de línea de comandos para hablar con una IA sin salir del terminal.
Comparten configuración y conectores, pero tienen **filosofías opuestas a propósito**:

| | **`como`** | **`hazme`** |
|---|---|---|
| Qué es | Un oráculo: responde a tu pregunta | Un asistente: hace la tarea por ti |
| Ejecuta comandos | **Nunca. Imposible por construcción** | Sí, con supervisión |
| Lee tus archivos | **No** | Sí (`read_file`, `list_dir`) |
| Escribe archivos | **No** | Sí (`write_file`) |
| Puede pedirte datos | No | Sí (`ask_user`) |
| Ejemplo | `como comprimo esta carpeta en zip` | `hazme un zip de esta carpeta` |

Las releases son ejecutables nativos (NativeAOT): arrancan al instante y no necesitan .NET instalado.

---

## `como` — pregunta, no toca nada

```console
$ como cambio los permisos solo de los directorios para que no se puedan crear más archivos
Quita el permiso de escritura únicamente a los directorios de forma recursiva.

find . -type d -exec chmod a-w {} +
```

La respuesta llega en dos partes con colores distintos: primero un **resumen** y al final el **comando**.
Con la salida redirigida (`| sh`, `> archivo`) no hay colores ni adornos.

**`como` no lanza ni lee nada.** Solo envía tu pregunta al servicio de IA y muestra la respuesta. No tiene
código para crear procesos, ni para leer o escribir tus archivos, ni herramientas que ofrecer al modelo.
Está separado de `hazme` a nivel de proyecto: el binario de `como` ni siquiera contiene esas capacidades
(se puede comprobar auditando [`Como/`](Como) y [`Como.Core/`](Como.Core)).

> El nombre del programa forma parte de la frase: escribes `como hago esto` y `como` restaura la
> palabra inicial antes de enviarla al modelo. Igual con `hazme`.

## `hazme` — un asistente en la línea de comandos

```console
$ hazme un proyecto de consola dotnet en la carpeta demo y compílalo
Voy a crear el proyecto de consola.

$ dotnet new console -o demo
  ejecutando en 3s · Esc para abortar
...
Voy a compilarlo.

$ dotnet build demo
...
Listo: el proyecto está creado en ./demo y compila sin errores.
```

`hazme` funciona como los asistentes de código de escritorio, pero en la terminal: la IA propone un paso,
se ejecuta, ve el resultado (código de salida, stdout, stderr) y decide el siguiente, en bucle, hasta
completar la tarea.

### Seguridad sin interrupciones constantes

En lugar de preguntar "¿s/n?" antes de cada comando, `hazme` te deja el control en todo momento:

- **Cada comando se muestra antes de ejecutarse**, con un tiempo de gracia (3 s por defecto) en el que
  puedes pulsar **Esc** para abortar el trabajo entero.
- **Esc aborta en cualquier punto**: mientras la IA piensa, durante la cuenta atrás o al responderle.
- **Una vez lanzado, el comando es dueño del terminal.** Se interrumpe como cualquier otro: **Ctrl-C**
  (una segunda vez mata su árbol de procesos). La IA sabe que lo interrumpiste y te pregunta si continúa.
- **Puedes dialogar mientras trabaja**: escribe y pulsa Enter, y tu nota llega a la IA en el siguiente paso.
- **Límite de pasos** por trabajo (30 por defecto) para evitar bucles.

Herramientas que se ofrecen al modelo: `run_command`, `read_file`, `list_dir`, `write_file`, `ask_user`.

### Qué conviene tener presente

- `hazme` ejecuta comandos **con los permisos de tu usuario** y no hay sandbox. Léelos antes de que
  pase la cuenta atrás; el tiempo de gracia es una red de seguridad, no un sustituto de la atención.
- El contenido de los archivos que la IA lee y la salida de los comandos **se envían al servicio de IA**.
  No lo uses sobre información que no quieras compartir con él.
- **Usa modelos de primera línea.** Con modelos pequeños, gratuitos o locales `hazme` puede causar
  problemas serios: lee [Advertencias importantes](#advertencias-importantes).
- Si necesitas una garantía fuerte de que nada se ejecuta, usa `como`.

---

## Instalación

### 1. Descargar

En la página de [Releases](https://github.com/XWolfOverride/Como-y-Hazme/releases) hay binarios listos para usar:

| Sistema | Archivo |
|---|---|
| Windows x64 | `.zip` con `como.exe` y `hazme.exe` |
| Linux x86-64 | `.tar.gz` con `como` y `hazme` (compilado en Debian) |

Son ejecutables **nativos (NativeAOT)**: arrancan al instante y **no necesitan tener .NET instalado**.
Puedes instalar solo `como` si no quieres `hazme` en tu sistema.

Requisitos del binario de Linux:

- Una distribución con **glibc** (Debian, Ubuntu, Fedora, Arch, openSUSE...). **No** funciona en Alpine (musl).
- Una glibc igual o más reciente que la del sistema donde se compiló.
- **OpenSSL** instalado (`libssl`), algo que casi toda distribución ya trae. Si falta, instala `openssl`
  (Arch/Fedora) o `libssl3` (Debian/Ubuntu).

Para **macOS** todavía no hay binario: [compílalo tú](#compilar-desde-el-código-opcional), son dos comandos.

### 2. Ponerlos en el `PATH`

El `PATH` es la lista de carpetas donde tu terminal busca programas. Si copias `como` y `hazme` a una de
ellas, podrás escribir `como ...` desde cualquier carpeta.

#### Windows

Descomprime el `.zip` y, en PowerShell, desde la carpeta donde quedaron los `.exe`:

```powershell
mkdir $HOME\bin
copy como.exe, hazme.exe $HOME\bin

# Añadir la carpeta al PATH de tu usuario (no requiere administrador)
$p = [Environment]::GetEnvironmentVariable("Path", "User")
[Environment]::SetEnvironmentVariable("Path", "$p;$HOME\bin", "User")
```

**Cierra y vuelve a abrir la terminal** y comprueba con `Get-Command como`.
Funciona igual en PowerShell, `cmd` y Windows Terminal.

> Al ser ejecutables sin firmar, Windows SmartScreen puede mostrar un aviso la primera vez
> ("Windows protegió su PC"): *Más información → Ejecutar de todas formas*.

#### Linux

```bash
tar xzf como-linux-x64.tar.gz        # (usa el nombre real del archivo descargado)
mkdir -p ~/.local/bin
install -m 755 como hazme ~/.local/bin/
```

Comprueba con `command -v como`. Si no aparece, tu `PATH` aún no incluye `~/.local/bin`; añádela y reabre la terminal:

```bash
echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.bashrc     # bash
echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.zshrc      # zsh
fish_add_path ~/.local/bin                                   # fish
```

Para instalarlos **para todos los usuarios** del equipo: `sudo install -m 755 como hazme /usr/local/bin/`.

#### macOS

El shell por defecto es `zsh`:

```bash
mkdir -p ~/.local/bin
install -m 755 como hazme ~/.local/bin/
echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.zshrc
```

Reabre la terminal y comprueba con `command -v como`.

### 3. Comprobar y configurar

```console
como -connectors
```

Debe mostrar `claude`, `gemini` y `openai`. Después necesitas una API key de un servicio de IA,
por ejemplo [Anthropic](https://console.anthropic.com) o
[Google AI Studio](https://aistudio.google.com/apikey) (este último tiene un
[nivel gratuito](#opciones-gratuitas)), y guardarla como se explica en [Configuración](#configuración):

```console
como -set claude.api-key sk-ant-...
como listo los archivos más grandes de esta carpeta
```

### Actualizar y desinstalar

- **Actualizar**: descarga la nueva release y sobrescribe `como`/`hazme`. Tu configuración y tus claves se conservan.
- **Desinstalar**: borra los binarios. Si además quieres eliminar tu configuración y las API keys guardadas,
  borra la carpeta `~/.como` (en Windows, `%USERPROFILE%\.como`).

### Consejos

- **Entrecomilla la pregunta si lleva `?` o `*`**: el shell puede interpretarlos como comodines. En zsh,
  un `alias como='noglob como'` en `~/.zshrc` evita el error *"no matches found"*.
- **No encadenes `como` directamente a un intérprete** (`como ... | sh`) sin leer antes el comando: es una
  IA y puede equivocarse. Si quieres que se ejecute con supervisión, para eso está `hazme`.
- **API key por variable de entorno**: si prefieres no guardarla en disco, define `ANTHROPIC_API_KEY` o
  `GEMINI_API_KEY` en su lugar.
- **Otro conector puntual**: `como -connector gemini hago un zip de esta carpeta` no cambia tu conector por defecto.
- **Sin colores**: `-no-color` en una llamada, `como -set no-color true` para siempre, o la variable de entorno estándar `NO_COLOR`.

---

## Compilar desde el código (opcional)

No hace falta para usar el programa: sirve si quieres la última versión, usarlo en **macOS**, en otra
arquitectura (ARM, Raspberry Pi...) o simplemente auditar lo que ejecutas.

### Compilación sencilla (recomendada)

Solo necesitas **git** y el **SDK de .NET 10**. Copia los comandos de tu sistema en una terminal:

| Sistema | Instalar git y el SDK |
|---|---|
| Windows | `winget install Git.Git` y `winget install Microsoft.DotNet.SDK.10` |
| macOS | `brew install git` y `brew install --cask dotnet-sdk` ([Homebrew](https://brew.sh)) |
| Debian / Ubuntu | `sudo apt install -y git curl` y el SDK con `curl -sSL https://dot.net/v1/dotnet-install.sh \| bash -s -- --channel 10.0` (o `sudo apt install dotnet-sdk-10.0` si tu versión lo incluye) |
| Arch | `sudo pacman -S --needed git dotnet-sdk` |

Tras instalar, **cierra y reabre la terminal** (con el instalador de .NET en Linux, añade antes
`export DOTNET_ROOT="$HOME/.dotnet" PATH="$PATH:$HOME/.dotnet"`). Después:

```bash
git clone https://github.com/XWolfOverride/Como-y-Hazme
cd Como-y-Hazme

dotnet publish Como/Como.csproj   -c Release -p:PublishAot=false -o salida
dotnet publish Hazme/Hazme.csproj -c Release -p:PublishAot=false -o salida
```

(En Windows usa `\` en las rutas, o deja `/`, que también funciona.)

El resultado es la carpeta `salida` con `como` y `hazme` (`.exe` en Windows) junto a sus `.dll`. **Mantén
la carpeta completa junta**: no basta con copiar un único archivo. Para usarlos, mueve la carpeta a un
sitio fijo y añade **la carpeta** al `PATH` (mismas instrucciones de la sección anterior, cambiando
`~/.local/bin` por la ruta de la carpeta):

```bash
mv salida ~/.local/share/como-hazme
echo 'export PATH="$HOME/.local/share/como-hazme:$PATH"' >> ~/.bashrc   # o ~/.zshrc
```

```powershell
move salida $HOME\como-hazme      # y añade $HOME\como-hazme al PATH como en la sección de Windows
```

Esta versión **necesita .NET instalado** para ejecutarse (el SDK ya lo incluye) y arranca algo más lenta
que la nativa. Si quieres una carpeta que no dependa de .NET, añade `--self-contained -r <RID>` a los
comandos (más grande, incluye el runtime).

> Un **RID** es el "apellido" de tu sistema: `win-x64`, `win-arm64`, `osx-arm64` (Mac con chip Apple),
> `osx-x64` (Mac Intel), `linux-x64`, `linux-arm64`. En Linux, `uname -m` te lo dice:
> `x86_64` → `linux-x64`, `aarch64` → `linux-arm64`.

### Añadido: binario nativo con NativeAOT

Es lo que se publica en las releases: **un solo archivo**, arranque instantáneo y sin depender de .NET.
Requiere además un **compilador nativo**. Instálalo y compila:

| Sistema | Compilador nativo |
|---|---|
| Windows | `winget install Microsoft.VisualStudio.2022.BuildTools --override "--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"` (descarga grande) |
| macOS | `xcode-select --install` |
| Debian / Ubuntu | `sudo apt install -y clang zlib1g-dev` |
| Arch | `sudo pacman -S --needed base-devel clang zlib` |

```bash
# Sustituye <RID> por el de tu sistema (ver arriba), p. ej. linux-x64
dotnet publish Como/Como.csproj   -c Release -r <RID>
dotnet publish Hazme/Hazme.csproj -c Release -r <RID>
```

Los binarios quedan en `Como/bin/Release/net10.0/<RID>/publish/` y `Hazme/bin/Release/net10.0/<RID>/publish/`.
Cópialos a tu `PATH` como en la sección [Instalación](#2-ponerlos-en-el-path).

Notas importantes:

- **Compila en el mismo sistema operativo para el que quieres el binario**: NativeAOT no genera binarios
  de Linux desde Windows, ni al revés.
- **Si vas a distribuir el binario de Linux**, compílalo en el sistema más antiguo que quieras soportar:
  un binario enlazado con una glibc reciente no arranca en distribuciones con una glibc más antigua.
- En Windows, si ves **`vswhere.exe no se reconoce`**, abre *Developer PowerShell for VS 2022*
  (Inicio → escríbelo) y repite el `dotnet publish`.

#### SteamOS (Steam Deck)

SteamOS está basado en Arch, pero su sistema de archivos es de **solo lectura** y las actualizaciones
borran los paquetes instalados con `pacman`. En *Modo escritorio*, abre **Konsole**:

```bash
sudo steamos-readonly disable
sudo pacman-key --init
sudo pacman-key --populate archlinux
sudo pacman -S --needed git base-devel clang zlib
sudo steamos-readonly enable

# SDK de .NET en tu carpeta personal (no se pierde con las actualizaciones)
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export DOTNET_ROOT="$HOME/.dotnet" PATH="$PATH:$HOME/.dotnet"
```

y sigue con los `git clone` y `dotnet publish` de arriba. Los binarios generados en tu carpeta personal
sobreviven a las actualizaciones; solo tendrías que repetir la instalación de paquetes para volver a
compilar tras una actualización grande. Para **usar** el programa en una Steam Deck basta con el binario
de Linux de las releases. (Esta sección no está verificada en un dispositivo real: si algo falla, abre un *issue*.)

## Configuración

```console
como -set claude.api-key sk-ant-...
como -get                      # lista todas las claves y sus valores
como -get claude.model         # solo el valor, sin cabeceras
como -set connector gemini     # conector por defecto
```

| Opción | Efecto |
|---|---|
| `-get` / `-get <clave>` | Lista las claves, o imprime solo el valor de una |
| `-set <clave> <valor>` | Guarda un valor |
| `-connectors` | Lista los conectores implementados |
| `-models` | Lista los modelos del conector |
| `-connector <nombre>` | Usa otro conector **solo en esta llamada** (en cualquier posición) |
| `-no-color` | Sin colores **solo en esta llamada** (en cualquier posición) |

Claves globales: `connector`, `no-color`. Cada conector añade las suyas con su nombre por delante:
`<conector>.api-key`, `<conector>.model` y `<conector>.base-url` (más `<conector>.max-tokens` en `claude`
y `gemini`). Las API keys también se leen de variables de entorno si no hay valor guardado
(ver tabla de conectores).

Claves que añade `hazme` (globales, sin prefijo): `grace-seconds`, `max-steps`, `shell`.

El archivo de configuración es **compartido** entre las herramientas y cada una solo muestra y valida las
claves que conoce. Las claves desconocidas para una herramienta **se conservan intactas** al guardar: si
cambias algo con `como`, no se pierde nada de `hazme` (ni de futuras herramientas), y `como -get` no lista
lo que no es suyo.

### Dónde y cómo se guarda

La configuración vive en `~/.como/config` (en Windows, `%USERPROFILE%\.como\config`), **cifrada** con
AES-256-GCM. La clave no está en el código: se deriva de un salt aleatorio por instalación más el usuario
y su carpeta personal, así que no hay secretos embebidos y el coste es despreciable. En Linux y macOS el
archivo queda con permisos `600`.

Es una protección razonable contra lecturas casuales y volcados accidentales (backups, repositorios de
dotfiles, capturas), **no** contra alguien que ya tenga acceso a tu cuenta.

### Conectores

Un **conector** es el adaptador que habla con un servicio de IA concreto. Se elige con la clave
`connector` (o `-connector <id>` en una sola llamada).

| Conector | Id | Servicio | Modelo por defecto | Variable de entorno |
|---|---|---|---|---|
| Anthropic Claude | `claude` | Anthropic | `claude-sonnet-5-5` | `ANTHROPIC_API_KEY` |
| Google Gemini | `gemini` | Google AI Studio | `gemini-3.8-flash` | `GEMINI_API_KEY` |
| OpenAI | `openai` | OpenAI **y servicios compatibles** (Grok, Groq, OpenRouter...) | `gpt-4.1-mini` | `OPENAI_API_KEY` |

Los modelos por defecto pueden quedarse antiguos: `como -models` lista los disponibles y
`como -set <conector>.model <modelo>` cambia el que se usa.

Ante errores transitorios (429, 5xx, saturación del servicio, fallos de red) se reintenta
automáticamente con *exponential backoff* y jitter.

#### El conector `openai` sirve para muchos servicios

El protocolo de OpenAI (*Chat Completions*) se ha convertido en un estándar de facto: no tiene un nombre
genérico oficial, y se le suele llamar **"API compatible con OpenAI"** (*OpenAI-compatible*). Lo
implementan muchísimos servicios, así que con el conector `openai` y cambiando la `base-url` puedes
hablar con ellos:

| Servicio | `base-url` |
|---|---|
| OpenAI (por defecto) | `https://api.openai.com/v1` |
| xAI (Grok) | `https://api.x.ai/v1` |
| Groq | `https://api.groq.com/openai/v1` |
| OpenRouter | `https://openrouter.ai/api/v1` |
| Mistral | `https://api.mistral.ai/v1` |
| Ollama (local) | `http://localhost:11434/v1` |
| LM Studio (local) | `http://localhost:1234/v1` |

```console
como -set openai.base-url https://api.groq.com/openai/v1
como -set openai.api-key gsk_...
como -set openai.model llama-3.3-70b-versatile
como -set connector openai
```

- **Grok** (xAI) **no es** Groq (el servicio de inferencia rápida): son dos servicios distintos, ambos
  accesibles con este conector.
- La `base-url` **incluye la versión** (`/v1`).
- Los servicios locales no piden clave, pero el conector exige una: pon cualquier texto.
- El conector `openai` apunta a **un servicio a la vez**: para cambiar de uno a otro, cambia `openai.base-url`.
- Las URL y nombres de modelo de terceros las mantiene cada servicio: confírmalas en su documentación.
  No están todos probados con esta herramienta; los informes son bienvenidos.

#### Opciones gratuitas

Si dar de alta una API key de pago te echa para atrás, estas opciones tienen **nivel gratuito con
límites de uso** y no piden tarjeta:

- **Google Gemini** (conector `gemini`): inicia sesión en [AI Studio](https://aistudio.google.com/apikey) y crea la key.
- **Groq**, **OpenRouter** (solo modelos `:free`) y **Mistral**, mediante el conector `openai`.

Los límites exactos y las condiciones **cambian con frecuencia**: consúltalos en el panel de cada servicio.
Antes de usar un nivel gratuito, lee las [advertencias](#advertencias-importantes).

## Advertencias importantes

> [!CAUTION]
> ### Modelos pequeños con `hazme`: riesgo real
>
> `hazme` da a la IA la capacidad de **ejecutar comandos y escribir archivos con tus permisos**. La
> seguridad de ese bucle depende, sobre todo, de la calidad del modelo. Los modelos **pequeños, gratuitos
> o locales de pocos parámetros** se equivocan mucho más y pueden causar **problemas graves**:
>
> - **Inventan** rutas, opciones de comandos o archivos que no existen.
> - **Malinterpretan** la tarea y hacen algo distinto (o más amplio) de lo que pediste.
> - **Ignoran instrucciones** de cautela: pueden lanzar operaciones destructivas sin necesidad
>   (`rm -r`, sobrescribir archivos, `chmod`/`chown` recursivos) o con un alcance equivocado.
> - **Entran en bucles** o repiten comandos que fallan, y su recuperación ante errores es pobre.
> - Generan **llamadas a herramientas mal formadas** o razonan mal sobre la salida de los comandos.
>
> Esc y el tiempo de gracia **no deshacen** lo que ya se ejecutó. Recomendaciones:
>
> - Usa un **modelo de primera línea** con `hazme`. Si dudas del modelo, no uses `hazme`.
> - Con un modelo pequeño o gratuito, usa **`como`**: tú decides qué se ejecuta y cuándo.
> - Si aun así quieres probar `hazme` con un modelo pequeño, hazlo en una **carpeta desechable, una
>   máquina virtual o un contenedor**, con tareas inocuas y copias de seguridad, y sube el tiempo de gracia
>   (`como -set grace-seconds 10`) para leer cada comando.
> - Que un modelo funcione bien en un par de tareas no demuestra que sea fiable.

- **Privacidad.** En los niveles gratuitos suele haber menos garantías sobre el uso de tus datos que en
  los de pago, y es habitual que se usen para mejorar los modelos. Con `hazme` se envían contenidos de
  archivos y salidas de comandos; no lo uses sobre información sensible. Un servicio local (Ollama,
  LM Studio) mantiene los datos en tu equipo, a costa de tener modelos más pequeños (ver aviso anterior).
- **Límites y estabilidad.** Los niveles gratuitos tienen cuotas bajas y pueden cambiarlas o retirarlas
  sin previo aviso. Verás errores 429 o de saturación; la herramienta reintenta con espera creciente, pero
  si se agota la cuota, el trabajo falla.
- **Coste.** En servicios de pago, `hazme` hace varias llamadas por tarea y reenvía el historial en cada
  paso, por lo que gasta bastantes más tokens que `como`. El límite `max-steps` (30 por defecto) acota el
  gasto máximo por trabajo.
- **Las respuestas pueden ser incorrectas.** Aunque `como` no ejecute nada, **lee el comando antes de
  ejecutarlo**, sobre todo si modifica, mueve o borra datos, o si usa `sudo`.

## Estructura

```
Como.Core/   Librería compartida: conectores (solo preguntar), configuración cifrada, consola
Como/        Ejecutable "como"  → solo depende de Como.Core
Hazme/       Ejecutable "hazme" → Como.Core + bucle de agente, herramientas y ejecución de comandos
```

La frontera es deliberada: todo lo que da "miedo" (procesos, archivos, herramientas) está únicamente en `Hazme/`.

### Añadir un conector

1. Implementar `IAiConnector` en `Como.Core/Connectors` y registrarlo en `ConnectorRegistry`. Si el
   servicio habla el protocolo compatible con OpenAI no hace falta conector nuevo: se usa `openai` con su
   `base-url` (o, para un id propio, se hereda de `OpenAiCompatibleConnector`).
2. Para que `hazme` pueda usarlo, añadir su cliente de herramientas en `Hazme/Chat` y enlazarlo en
   `ChatClients.For` (los compatibles con OpenAI reutilizan `OpenAiChat`). Hasta entonces el conector
   funciona en `como` pero no en `hazme`.

## Notas

- Desarrollado y probado en Windows. El código es multiplataforma (Linux/macOS) y se publica con
  NativeAOT, pero conviene verificarlo en cada sistema; los informes de fallos son bienvenidos.
- En bash/zsh, un `?` o `*` sin comillas en la frase puede expandirse como *glob*.
  Si hace falta, entrecomilla la pregunta.

## Licencia

[MIT](LICENSE)
