# como · hazme

Dos herramientas de línea de comandos para hablar con una IA sin salir de la terminal.
Comparten configuración y conectores, pero tienen **filosofías opuestas a propósito**:

| | **`como`** | **`hazme`** |
|---|---|---|
| Qué es | Un oráculo: responde a tu pregunta | Un asistente: hace la tarea por ti |
| Ejecuta comandos | **Nunca. Imposible por construcción** | Sí, con supervisión |
| Lee / escribe archivos | **No** | Sí |
| Ejemplo | `como comprimo esta carpeta en zip` | `hazme un zip de esta carpeta` |

Ejecutables nativos (NativeAOT): arrancan al instante y no necesitan .NET.

## `como` — pregunta, no toca nada

```console
$ como cambio los permisos solo de los directorios para que no se puedan crear más archivos
Quita el permiso de escritura únicamente a los directorios de forma recursiva.

find . -type d -exec chmod a-w {} +
```

Resumen primero, comando al final, cada uno con su color. **No lanza ni lee nada**: solo envía tu pregunta
al servicio de IA y muestra la respuesta. Ni siquiera contiene el código para hacerlo (puedes comprobarlo
en [`Como/`](Como) y [`Como.Core/`](Como.Core)).

> El nombre del programa forma parte de la frase: `como hago esto` envía "como hago esto". Igual con `hazme`.

## `hazme` — un asistente en la línea de comandos

```console
$ hazme un proyecto de consola dotnet en la carpeta demo y compílalo
Voy a crear el proyecto de consola.

$ dotnet new console -o demo
  ejecutando en 3s · Esc para abortar
...
Listo: el proyecto está creado en ./demo y compila sin errores.
```

La IA propone un paso, se ejecuta, ve el resultado y decide el siguiente, hasta completar la tarea.
Herramientas internas: lanzar comandos, listar en árbol, leer, comprobar si existe, escribir y proponer
cambios con **diff**.

**Control sin interrupciones constantes:**

- Cada comando se **muestra antes de ejecutarse**, con unos segundos de gracia. **Esc** aborta el trabajo en cualquier momento.
- **Ctrl-C** interrumpe el comando en curso (dos veces, lo mata); la IA lo sabe y pregunta si continúa.
- Escribe + Enter para **dar una nota** a la IA mientras trabaja.
- Cambios **críticos** (sistema, `~/.ssh`, `.bashrc`, configuraciones) se muestran como **diff** para
  **A**plicar, **D**escartar o dar **I**nstrucciones. `-confirm` lo exige para todo.

## Instalación

1. Descarga los binarios de [Releases](https://github.com/XWolfOverride/Como-y-Hazme/releases)
   (Windows x64 y Linux x86-64).
2. Cópialos a una carpeta de tu `PATH`:

   ```powershell
   # Windows (PowerShell, sin administrador); después reabre la terminal
   mkdir $HOME\bin; copy como.exe, hazme.exe $HOME\bin
   $p = [Environment]::GetEnvironmentVariable("Path","User"); [Environment]::SetEnvironmentVariable("Path","$p;$HOME\bin","User")
   ```

   ```bash
   # Linux / macOS (para todos los usuarios: sudo install -m 755 como hazme /usr/local/bin/)
   mkdir -p ~/.local/bin && install -m 755 como hazme ~/.local/bin/
   ```

3. Comprueba con `como -connectors`.

Linux: distribución con glibc (no Alpine) y OpenSSL. **¿Prefieres compilarlo?** Un solo comando, tras
instalar git, .NET 10 y un compilador nativo ([prerrequisitos](docs/BUILD.md)):

```bash
curl -fsSL https://raw.githubusercontent.com/XWolfOverride/Como-y-Hazme/main/build.sh | bash      # Debian/Ubuntu
```
```powershell
irm https://raw.githubusercontent.com/XWolfOverride/Como-y-Hazme/main/build.ps1 | iex            # Windows
```

Más opciones (macOS, sin compilador nativo, Arch, SteamOS) en **[docs/BUILD.md](docs/BUILD.md)**.

## Configuración en 4 pasos

Ejemplo con **Gemini**, que tiene nivel gratuito y no pide tarjeta:

```console
# 1. Crea una API key en https://aistudio.google.com/apikey y guárdala
como -set gemini.api-key AIza...

# 2. Usa Gemini por defecto
como -set connector gemini

# 3. Comprueba que la clave funciona (lista los modelos disponibles)
como -models

# 4. Pregunta
como listo los archivos más grandes de esta carpeta
```

La configuración se guarda **cifrada** en `~/.como/config` y la comparten `como` y `hazme`.
Para no dejar la clave en el historial, usa la variable de entorno del conector (`GEMINI_API_KEY`...).

## Referencia

| Opción | Efecto |
|---|---|
| `-get` / `-get <clave>` | Lista las claves, o imprime solo el valor de una |
| `-set <clave> <valor>` | Guarda un valor |
| `-connectors` / `-models` | Lista los conectores / los modelos del conector |
| `-connector <id>` | Usa otro conector **solo en esta llamada** |
| `-no-color` | Sin colores en esta llamada (o `set no-color true`; también respeta `NO_COLOR`) |
| `-confirm` | Solo `hazme`: confirma con diff toda escritura de archivos |

Las opciones pueden ir en cualquier posición. Claves: `connector`, `no-color`, `<conector>.api-key`,
`<conector>.model`, `<conector>.base-url`; y de `hazme`: `grace-seconds` (3), `max-steps` (30), `shell`,
`confirm-changes` (false). Tu configuración conserva las claves que cada herramienta no conoce.

### Conectores

| Conector | Id | Modelo por defecto | Variable de entorno |
|---|---|---|---|
| Anthropic Claude | `claude` | `claude-sonnet-5-5` | `ANTHROPIC_API_KEY` |
| Google Gemini | `gemini` | `gemini-3.8-flash` | `GEMINI_API_KEY` |
| OpenAI **y compatibles** | `openai` | `gpt-4.1-mini` | `OPENAI_API_KEY` |

El conector `openai` habla el protocolo **"compatible con OpenAI"**, que implementan Grok (xAI), Groq,
OpenRouter, Mistral, Ollama, LM Studio... cambiando `openai.base-url`. Ejemplos, opciones gratuitas y
solución de problemas en **[docs/CONFIG.md](docs/CONFIG.md)**.

## Advertencias

> [!CAUTION]
> **Modelos pequeños con `hazme`: riesgo real.** `hazme` ejecuta comandos y escribe archivos con tus
> permisos. Los modelos **pequeños, gratuitos o locales** inventan rutas, malinterpretan la tarea, ignoran
> la cautela y pueden lanzar operaciones destructivas. Esc y el tiempo de gracia **no deshacen** lo ya
> ejecutado. Usa un modelo de primera línea con `hazme`; con uno pequeño, usa `como`, o prueba en una
> carpeta desechable o una máquina virtual.

- `hazme` **no tiene sandbox** y envía al servicio de IA el contenido de los archivos que lee y la salida de los comandos.
- Los niveles **gratuitos** suelen dar menos garantías de privacidad y tienen límites bajos.
- **Lee siempre el comando** de `como` antes de ejecutarlo, sobre todo con `sudo` o si borra datos.

Vulnerabilidades: [SECURITY.md](SECURITY.md).

## Estructura

```
Como.Core/   Conectores (solo preguntar), configuración cifrada, consola
Como/        Ejecutable "como"  → solo depende de Como.Core
Hazme/       Ejecutable "hazme" → Como.Core + agente, herramientas y ejecución de comandos
```

Todo lo que ejecuta o toca archivos está únicamente en `Hazme/`. Para añadir un conector: implementar
`IAiConnector` en `Como.Core/Connectors`, registrarlo en `ConnectorRegistry` y, si `hazme` ha de usarlo,
su cliente de herramientas en `Hazme/Chat` (los compatibles con OpenAI no necesitan nada: usan `openai`).

Desarrollado y probado en Windows; el código es multiplataforma y los informes de fallos en Linux/macOS
son bienvenidos.

## Licencia

[MIT](LICENSE)
