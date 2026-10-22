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
  ejecutando en 5s · Esc para abortar
...
Listo: el proyecto está creado en ./demo y compila sin errores.
```

Por defecto sigue un flujo prudente: **analiza** (solo lectura) → te muestra un **plan de acción** →
tú **apruebas, modificas o cancelas** → **ejecuta**. Después la **conversación sigue abierta**: puedes
pedirle más cosas y reutiliza lo que ya analizó (Enter o Esc para salir). Los planes son cortos: si el
trabajo es grande, se divide en **fases** que apruebas una a una. `-no-plan` lo desactiva.
Herramientas internas: lanzar comandos, listar en árbol, leer, comprobar si existe, escribir y proponer
cambios con **diff**.

**Control sin interrupciones constantes:**

- Nada que modifique el sistema se ejecuta **hasta que apruebas el plan**.
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
curl -fsSL https://raw.githubusercontent.com/XWolfOverride/Como-y-Hazme/master/build.sh | bash      # Debian/Ubuntu
```
```powershell
irm https://raw.githubusercontent.com/XWolfOverride/Como-y-Hazme/master/build.ps1 | iex            # Windows
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
| `-no-plan` | Solo `hazme`: sin plan previo, la IA actúa directamente (o `set plan false`) |
| `-less-safe` | Solo `hazme`: **modo menos seguro** en esta llamada, lee archivos privados sin preguntar (o `set less-safe true`) |
| `-jobs` / `-resume [id] [texto]` / `-discard <id\|all>` | Solo `hazme`: lista, retoma o borra trabajos interrumpidos (ver abajo) |
| `-no-persist` | Solo `hazme`: no guarda el diario del trabajo en esta llamada (o `set persist-jobs false`) |
| `-y` | Solo `hazme`, **desatendido** (scripts): no pide confirmaciones de plan ni de diffs, **mantiene los tiempos de gracia** de los comandos y termina al acabar. No lee archivos privados |

Las opciones pueden ir en cualquier posición. Claves: `connector`, `no-color`, `<conector>.api-key`,
`<conector>.model`, `<conector>.base-url`, `openai.reasoning-effort`, `openai.api` (chat, responses o auto); y de `hazme`: `plan` (true), `less-safe` (false), `grace-seconds` (5), `max-steps` (30),
`shell`, `confirm-changes` (false), `persist-jobs` (true), `job-retention-hours` (4). Tu configuración conserva las claves que cada herramienta no conoce.

### Retomar un trabajo interrumpido

Si un trabajo de `hazme` se corta (Esc, error de red, cierre de la terminal), `hazme -resume` lo retoma. Solo se
guarda un **diario cifrado y breve** (tarea, conclusiones de la IA, decisiones, comandos y cambios hechos, último
plan), **nunca** el contenido de archivos ni la salida de comandos: la IA vuelve a leer lo que necesite y presenta
de nuevo el plan. El diario se **borra al terminar** el trabajo y caduca solo a las 4 h (`job-retention-hours`).
`persist-jobs false` o `-no-persist` lo desactivan.

### Trabajar en varios servidores a la vez (`hazme-agent` / `-peer`)

Un solo `hazme` (un solo contexto) puede ejecutar comandos también en otras máquinas, con los mismos controles
(comando visible, tiempo de gracia, plan, Esc). En la otra máquina corre `hazme-agent`, un **ejecutor temporal** y
independiente (otro binario, sin IA ni claves, que **no se instala con `como` y `hazme`**: cópialo solo a los servidores
donde lo quieras, p. ej. con `scp`; los scripts de compilación lo generan junto a los otros dos):

```console
# En B (por ssh, p. ej.):                    # En A:
$ hazme-agent                               $ ssh -L 7700:127.0.0.1:7700 usuario@B     # túnel (o -listen en la misma red)
  Token: K7QM-2XPD-9TRB-...                  $ hazme -peer web2:127.0.0.1:7700 migra X de web2 a este servidor
                                               Token de «web2» › ****
```

- El agente muestra un **token aleatorio de un solo uso** (140 bits): no se guarda ni viaja; el tráfico va **cifrado**.
- **Una sola conexión** (el puerto se cierra al autenticarse). **3 tokens incorrectos y el agente se cierra.**
- Es **efímero**: se cierra a los 10 min sin conexión, a los 30 min sin órdenes o a las 2 h en total (`-wait`, `-idle`, `-ttl`).
  Si se pierde la conexión no se reconecta: arranca otro agente.
- Escucha solo en `127.0.0.1` salvo que pases `-listen`. En la otra máquina ves cada comando que se ejecuta;
  Ctrl-C la corta. Los comandos no reciben entrada de teclado allí. Los permisos son los del usuario que lo arranca.
- La IA conoce las máquinas conectadas, debe indicar en cuál actúa y preguntar si no está claro. Por ahora los
  archivos remotos se tocan con `run_command` (cat, tee...).
- Sin terminal, el token puede pasarse en `HAZME_PEER_TOKEN_<ID>`.

### Instrucciones permanentes

Lo que escribas en `~/.como/AGENTS.md` y/o `~/AGENTS.md` se envía a la IA en cada trabajo de `hazme` (preferencias,
convenciones, cosas a evitar). No puede anular las reglas de seguridad.

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
> **MODELOS PEQUEÑOS CON `hazme`: RIESGO REAL DE PÉRDIDA DE DATOS.** `hazme` ejecuta comandos y escribe
> archivos con tus permisos. Los modelos **pequeños, gratuitos o locales** inventan rutas, malinterpretan la
> tarea, ignoran la cautela y pueden **borrar o sobrescribir lo que no debían**. Esc y el tiempo de gracia
> **no deshacen** lo ya ejecutado.
>
> - Usa un modelo de primera línea con `hazme`. Con uno pequeño, usa **`como`**.
> - Si aun así lo pruebas, hazlo en una **carpeta desechable o una máquina virtual**, con copias de seguridad.
> - `hazme` te lo recuerda: pide **aceptar los riesgos** la primera vez y **avisa en cada ejecución** si el
>   modelo parece pequeño.

- `hazme` **no tiene sandbox** y envía al servicio de IA el contenido de los archivos que lee y la salida de los comandos.
  Se le indica que evite datos privados (claves, `.env`, credenciales...) y **pide tu permiso** antes de leer
  archivos de ese tipo, pero no es infalible: no lo uses con información sensible.
- **Modo menos seguro** (`hazme -set less-safe true` o `-less-safe` en una llamada): la IA lee archivos
  privados **sin pedir permiso**. Es **decisión y responsabilidad tuya**; pensado para una IA local de
  confianza. Avisa en rojo al activarlo y en cada ejecución.
- Los niveles **gratuitos** suelen dar menos garantías de privacidad y tienen límites bajos.
- **Lee siempre el comando** de `como` antes de ejecutarlo, sobre todo con `sudo` o si borra datos.

Vulnerabilidades: [SECURITY.md](SECURITY.md).

## Estructura

```
Como.Core/    Conectores (solo preguntar), configuración cifrada, consola
Como/         Ejecutable "como"  → solo depende de Como.Core
Hazme/        Ejecutable "hazme" → Como.Core + agente de IA, herramientas, ejecución de comandos y cliente de peers
Hazme.Agent/  Ejecutable "hazme-agent" → solo el ejecutor remoto temporal (sin IA); comparte con hazme el canal cifrado
```

Todo lo que ejecuta o toca archivos está únicamente en `Hazme/` y `Hazme.Agent/`. Para añadir un conector: implementar
`IAiConnector` en `Como.Core/Connectors`, registrarlo en `ConnectorRegistry` y, si `hazme` ha de usarlo,
su cliente de herramientas en `Hazme/Chat` (los compatibles con OpenAI no necesitan nada: usan `openai`).

Desarrollado y probado en Windows; el código es multiplataforma y los informes de fallos en Linux/macOS
son bienvenidos.

## Licencia

[MIT](LICENSE)
