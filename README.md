# como · hazme

Dos herramientas de línea de comandos para hablar con una IA sin salir de la terminal.
Comparten configuración y proveedores, pero tienen **filosofías opuestas a propósito**:

| | **`como`** | **`hazme`** |
|---|---|---|
| Qué es | Un oráculo: responde a tu pregunta | Un asistente: hace la tarea por ti |
| Ejecuta comandos | **Nunca. Imposible por construcción** | Sí, con supervisión |
| Lee tus archivos | **No** | Sí (`read_file`, `list_dir`) |
| Escribe archivos | **No** | Sí (`write_file`) |
| Puede pedirte datos | No | Sí (`ask_user`) |
| Ejemplo | `como comprimo esta carpeta en zip` | `hazme un zip de esta carpeta` |

Ambos son ejecutables nativos (NativeAOT): arrancan al instante y no necesitan runtime instalado.

---

## `como` — pregunta, no toca nada

```console
$ como cambio los permisos solo de los directorios para que no se puedan crear más archivos
Quita el permiso de escritura únicamente a los directorios de forma recursiva.

find . -type d -exec chmod a-w {} +
```

La respuesta llega en dos partes con colores distintos: primero un **resumen** y al final el **comando**.
Con la salida redirigida (`| sh`, `> archivo`) no hay colores ni adornos.

**`como` no lanza ni lee nada.** Solo envía tu pregunta al proveedor y muestra la respuesta. No tiene
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
- El contenido de los archivos que la IA lee y la salida de los comandos **se envían al proveedor**.
  No lo uses sobre información que no quieras compartir con él.
- Si necesitas una garantía fuerte de que nada se ejecuta, usa `como`.

---

## Instalación

Requisitos: [.NET SDK 10](https://dotnet.microsoft.com/download). Para NativeAOT se necesita además la
cadena nativa de la plataforma (clang y zlib en Linux, *Desktop development with C++* en Windows, Xcode
Command Line Tools en macOS).

```console
git clone <este-repositorio>
cd Como

# Elige tu RID: linux-x64, linux-arm64, osx-arm64, osx-x64, win-x64...
dotnet publish Como/Como.csproj   -c Release -r linux-x64
dotnet publish Hazme/Hazme.csproj -c Release -r linux-x64
```

Copia `como` y/o `hazme` (carpeta `bin/Release/net10.0/<rid>/publish/`) a un directorio de tu `PATH`.
Puedes instalar solo `como` si no quieres `hazme` en tu sistema.

## Configuración

```console
como -set claude.api-key sk-ant-...
como -get                      # lista todas las claves y sus valores
como -get claude.model         # solo el valor, sin cabeceras
como -set provider gemini      # proveedor por defecto
```

| Opción | Efecto |
|---|---|
| `-get` / `-get <clave>` | Lista las claves, o imprime solo el valor de una |
| `-set <clave> <valor>` | Guarda un valor |
| `-providers` | Lista los proveedores implementados |
| `-models` | Lista los modelos del proveedor |
| `-provider <nombre>` | Usa otro proveedor **solo en esta llamada** (en cualquier posición) |
| `-no-color` | Sin colores **solo en esta llamada** (en cualquier posición) |

Claves globales: `provider`, `no-color`. Cada proveedor añade las suyas (`<proveedor>.api-key`,
`.model`, `.max-tokens`, `.base-url`). Las API keys también se leen de `ANTHROPIC_API_KEY` y
`GEMINI_API_KEY` si no hay valor guardado.

Claves propias de `hazme`: `hazme.grace-seconds`, `hazme.max-steps`, `hazme.shell`.

### Dónde y cómo se guarda

La configuración vive en `~/.como/config` (en Windows, `%USERPROFILE%\.como\config`), **cifrada** con
AES-256-GCM. La clave no está en el código: se deriva de un salt aleatorio por instalación más el usuario
y su carpeta personal, así que no hay secretos embebidos y el coste es despreciable. En Linux y macOS el
archivo queda con permisos `600`.

Es una protección razonable contra lecturas casuales y volcados accidentales (backups, repositorios de
dotfiles, capturas), **no** contra alguien que ya tenga acceso a tu cuenta.

### Proveedores

| Proveedor | Id | Modelo por defecto |
|---|---|---|
| Anthropic Claude | `claude` | `claude-sonnet-5-5` |
| Google Gemini | `gemini` | `gemini-3.8-flash` |

Ante errores transitorios (429, 5xx, saturación del servicio, fallos de red) se reintenta
automáticamente con *exponential backoff* y jitter.

## Estructura

```
Como.Core/   Librería compartida: proveedores (solo preguntar), configuración cifrada, consola
Como/        Ejecutable "como"  → solo depende de Como.Core
Hazme/       Ejecutable "hazme" → Como.Core + bucle de agente, herramientas y ejecución de comandos
```

La frontera es deliberada: todo lo que da "miedo" (procesos, archivos, herramientas) está únicamente en `Hazme/`.

### Añadir un proveedor

1. Implementar `IAiProvider` en `Como.Core/Providers` y registrarlo en `ProviderRegistry`.
2. Para que `hazme` pueda usarlo, añadir su cliente de herramientas en `Hazme/Chat` y enlazarlo en
   `ChatClients.For`. Hasta entonces el proveedor funciona en `como` pero no en `hazme`.

## Notas

- Desarrollado y probado en Windows. El código es multiplataforma (Linux/macOS) y se publica con
  NativeAOT, pero conviene verificarlo en cada sistema; los informes de fallos son bienvenidos.
- En bash/zsh, un `?` o `*` sin comillas en la frase puede expandirse como *glob*.
  Si hace falta, entrecomilla la pregunta.

## Licencia

[MIT](LICENSE)
