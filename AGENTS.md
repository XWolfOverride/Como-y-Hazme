# AGENTS.md — contexto rápido

Resumen para agentes de IA: léelo antes de explorar el código. Detalle de usuario en [README.md](README.md),
[docs/CONFIG.md](docs/CONFIG.md) y [docs/BUILD.md](docs/BUILD.md). Idioma del proyecto (código, comentarios,
textos de UI, commits): **español**.

## Qué es

Dos CLIs .NET 10 (NativeAOT, sin dependencias) para hablar con una IA desde la terminal. Comparten conectores y
configuración, pero con **filosofías opuestas a propósito**:

- **`como`** — oráculo: solo envía la pregunta y muestra la respuesta. **No ejecuta, no lee ni escribe nada.**
  Invariante de diseño: el código para hacerlo no debe existir en `Como/` ni `Como.Core/`.
- **`hazme`** — agente: la IA usa herramientas (comandos, archivos) con supervisión del usuario.

El nombre del programa es parte de la frase: `como hago X` envía "como hago X"; `hazme X` envía "hazme X".

## Estructura

```
Como.slnx            Solución
Directory.Build.props  net10.0, nullable, AOT-compatible, sin reflexión JSON (usar source generators)
Como.Core/           Biblioteca común (SOLO preguntar)
  CliCommon.cs         Opciones globales (-no-color, -connector) y -get/-set/-connectors/-models
  ConsoleUi.cs         Salida con colores (respeta NO_COLOR / -no-color)
  ResponseRenderer.cs  Pinta la respuesta de `como` (resumen + comando)
  Connectors/          IAiConnector + Claude, Gemini, OpenAiCompatible (+HttpUtil), ConnectorRegistry
  Settings/            SettingsStore (~/.como/config cifrado, compartido), EncryptedFile (AES-GCM) y KnownKeys (catálogo de claves)
Como/Program.cs     Ejecutable `como`: solo depende de Como.Core
Hazme/               Ejecutable `hazme` (todo lo que ejecuta o toca archivos vive SOLO aquí)
  Program.cs           Parseo de flags, aceptación de riesgos (risk-ack), aviso de modelo pequeño, arranque
  JobRunner.cs         Bucle agente: herramientas, plan previo, diffs, tiempo de gracia, system prompt
  Interaction.cs       Teclado: Esc aborta, texto+Enter = nota a la IA, ReadChoice/ReadLine
  ShellRunner.cs       Lanza comandos (bash/sh, PowerShell en Windows), Ctrl-C en dos fases
  FileTools.cs         Resolver rutas, listar árbol, detectar binarios/rutas privadas y sensibles
  TextDiff.cs          Diff de líneas para propose_change
  JobStore.cs          Diario cifrado para retomar trabajos: JobRecord, JobFiles (~/.como/jobs/*.job), JobLog
  HazmeKeys.cs         Claves propias de hazme (plan, less-safe, grace-seconds, max-steps, shell, confirm-changes, risk-ack, persist-jobs, job-retention-hours)
  Chat/                IChatClient con tool use por conector: ClaudeChat, GeminiChat, OpenAiChat
docs/  .github/workflows/build.yml  build.sh  build.ps1  SECURITY.md
```

## Flujo de `hazme`

1. `Program.cs` quita sus flags (`-confirm -no-plan -y -less-safe`), delega el resto en `CliCommon`
   (`-get/-set/-connectors/-models`) y lo que quede es la tarea.
2. **Sin tarea y con terminal interactiva**, pide la primera tarea con un prompt `›` (Enter/Esc vacío sale).
   Sin terminal imprime la ayuda y devuelve 1. `-h` muestra la ayuda.
3. `JobRunner.RunAsync`: turno de la IA → herramientas → resultados → … hasta que no pida más herramientas,
   se agoten `max-steps` o el usuario aborte. Después la conversación sigue abierta (`›`) salvo con `-y`.
4. Flujo por defecto: **analizar (solo lectura) → `present_plan` (máx. 7 pasos, si no, fases) → aprobar
   [A]probar/[M]odificar/[C]ancelar → ejecutar**. Hasta aprobar, `IsMutating` bloquea escrituras y comandos
   no marcados `read_only`.
5. Herramientas: `run_command`, `present_plan`, `read_file`, `list_dir`, `stat_path`, `write_file`,
   `propose_change` (diff: [A]plicar/[D]escartar/[I]nstrucciones), `ask_user`.
6. Seguridad: tiempo de gracia visible antes de ejecutar; archivos privados piden permiso (salvo `less-safe`,
   denegados en `-y`); rutas sensibles siempre con diff; `-confirm` fuerza diff en toda escritura.

## Trabajos retomables (JobStore.cs)

- `JobLog` guarda tras cada anotación un **diario cifrado** (`~/.como/jobs/<id>.job`, AES-GCM vía `EncryptedFile`):
  tarea, notas de la IA, decisiones del usuario, comandos y cambios hechos, y el último plan. **Nunca** contenido de
  archivos ni salida de comandos. No se guarda el historial de chat (por eso es independiente del conector).
- La IA rellena el campo opcional `journal` que llevan todas las herramientas (ver `JobRunner.Tools` y el system prompt).
- Se **conserva** si se corta (Esc, error, proceso muerto) y se **borra** al terminar: conversación cerrada, `-y`
  completado o `[P]arar` en el límite de pasos. Caduca a las `job-retention-hours` (4) y se purga al arrancar.
- `hazme -jobs | -resume [id] [texto] | -discard <id|all>`; sin argumentos ofrece retomar el último.
  Al retomar la IA recibe el diario como primer mensaje y **ningún plan queda aprobado**: debe verificar y replanificar.
- Se desactiva con `-no-persist` o `persist-jobs false`.

## Instrucciones de usuario

`FileTools.LoadUserInstructions` añade al system prompt el contenido de `~/.como/AGENTS.md` y `~/AGENTS.md`
(máx. 8000 caracteres). No pueden anular las reglas de seguridad. Son solo del usuario; no se lee el AGENTS.md del proyecto.

## Convenciones y cuidados

- **No rompas la separación** `como` (solo preguntar) / `hazme` (actuar). Nada de herramientas en `Como.Core`.
- **NativeAOT**: sin reflexión. JSON con `JsonDocument`/source generators; los conectores se registran a mano.
- Los textos de la IA que gobiernan el comportamiento de `hazme` están en `JobRunner.BuildSystemPrompt` y en las
  descripciones de `Tools` (en inglés); la UI y los mensajes al usuario, en español.
- Las claves de configuración se declaran con `ConfigKey` (valor guardado → variable de entorno → defecto).
  Una herramienta conserva las claves que no conoce al guardar.
- Estilo: `internal sealed`, constructores primarios, `///` en español, `ConsoleUi` para toda salida de color.
- No hay tests automáticos; CI solo compila (`dotnet build Como.slnx -c Release` en Linux/Windows/macOS).

## Cómo añadir cosas

- **Conector nuevo**: implementar `IAiConnector` en `Como.Core/Connectors`, añadirlo a `ConnectorRegistry.All` y,
  para `hazme`, su `IChatClient` en `Hazme/Chat` + caso en `ChatClients.For`. Los compatibles con OpenAI usan `openai`
  cambiando `openai.base-url`.
- **Clave de hazme nueva**: constante + `ConfigKey` en `HazmeKeys.All`; documentarla en `Usage` (Program.cs),
  README y docs/CONFIG.md.
- **Herramienta nueva de hazme**: `ToolSpec` en `JobRunner.Tools`, caso en `ExecuteAsync`, decidir si es mutante en
  `IsMutating`, y mencionarla en el system prompt si hace falta.

## Compilar y probar

```
dotnet build Como.slnx -c Release
dotnet run --project Hazme -- <tarea>          # o Como
dotnet publish Hazme -c Release -r win-x64     # NativeAOT: requiere compilador nativo (ver docs/BUILD.md)
```

Rama de trabajo: `master`; rama principal para PRs: `main`.
