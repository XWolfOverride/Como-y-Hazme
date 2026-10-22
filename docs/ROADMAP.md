# Roadmap

Ideas y trabajo pendiente, sin compromiso de fechas. Lo ya hecho vive en el código y en el [README](../README.md);
el contexto técnico, en [AGENTS.md](../AGENTS.md).

## OpenAI: API Responses (`/v1/responses`)

**Estado:** evaluado, decidido no hacerlo todavía.

**Por qué interesa**
- Los modelos de razonamiento como `gpt-6.1-sol` no admiten herramientas con razonamiento en `/v1/chat/completions`.
  Hoy hazme reintenta con `reasoning_effort=none` (o `openai.reasoning-effort`), así que **razona menos justo donde
  más importa**: decidir qué comando es de solo lectura, qué es destructivo y qué plan proponer.
- Responses permite razonamiento y herramientas a la vez. OpenAI indica mejor resultado con modelos de razonamiento
  (cifra suya, ~3 % en SWE-bench) y menos tokens y más caché al reutilizar el razonamiento entre llamadas a herramientas.
- **Privacidad:** con `store:false` e `include:["reasoning.encrypted_content"]` el servicio no retiene la conversación y el
  razonamiento viaja cifrado en nuestras propias peticiones (modo sin estado / retención cero).
- Las barreras reales (plan, tiempo de gracia, diffs, permisos) siguen siendo las de hazme: razonar mejor reduce errores,
  no sustituye esas barreras.

**Diseño previsto**
- Cliente nuevo `OpenAiResponsesChat` (`IChatClient`) junto al de Chat Completions. Responses no admite mensajes
  `role:"tool"`: el historial pasa a elementos `message`, `reasoning`, `function_call` y `function_call_output`.
- Encaja con la arquitectura actual: `ChatMessage.RawContentJson` guardaría los elementos de salida del turno (incluido el
  razonamiento cifrado) y se reenviarían tal cual, como ya se hace con Claude y Gemini.
- Mantener: aviso de salida cortada (`incomplete`), validación de argumentos inválidos (`ToolCall.Create`) y diagnósticos a la IA.
- Clave nueva `openai.api` = `auto` | `chat` | `responses`. En `auto`, Responses solo si la `base-url` es la de OpenAI;
  Groq, Ollama, Mistral y demás compatibles siguen con Chat Completions (no todos implementan Responses).
- Enviar `reasoning` solo si hay `openai.reasoning-effort`, y `include` de razonamiento cifrado solo cuando proceda,
  con reintento sin él (modelos sin razonamiento como `gpt-4.1-mini` podrían rechazarlo: **por verificar**).
- Tamaño estimado: 250-300 líneas.

**Riesgos / por verificar**
- Comprobar en la documentación oficial los nombres exactos de los eventos de streaming (texto, argumentos de función,
  finalización) antes de escribir el código.
- Solo se puede probar con un servidor simulado; hace falta una prueba real contra OpenAI con una tarea inocua.
- El razonamiento se factura como salida y tarda más; `openai.reasoning-effort` permite acotarlo.

**Mejora rápida independiente:** enviar `store:false` en la ruta actual de Chat Completions, solo a `api.openai.com`
(los servicios compatibles podrían rechazar el campo).

## Varios servidores: `hazme-agent` y `-peer`

La fase 1 está hecha (ejecución de comandos en peers). Pendiente:

- **Herramientas de archivos en peers:** `read_file`, `list_dir`, `stat_path`, `write_file` (con `append`) y `propose_change`
  con `host`. Hoy devuelven un error que remite a `run_command`.
- **Reglas de rutas por host:** privadas y sensibles evaluadas con el `$HOME` y el SO del peer (hoy se usan las locales).
- **Diario y `-resume` con peers:** anotar el host en cada entrada y reconectar al retomar.
- **`hazme-agent -confirm`:** pedir aprobación en la máquina remota para cada orden (colaboración entre dos personas).
- Opcional: transporte por ssh (stdio) además de TCP, para no abrir ningún puerto.
- Una revisión externa del canal cifrado (`PeerCrypto.cs`) antes de recomendarlo fuera de entornos propios.

## Errores y coste

- Pasar a la IA también los fallos de red o de API de los conectores (hoy solo se muestran al usuario y se reintentan).
- Revisar el uso real de caché de prompt con Claude (`cache_control` ya se envía) para reducir el gasto por paso.
- Evaluar el comportamiento de Haiku 5.5 frente a Gemini 3.8 Flash en tareas reales de hazme (hoy hay solo estimaciones).

## Menores

- Aceptar mayúsculas en `-set openai.reasoning-effort` (hoy exige minúsculas).
- Bloqueo contra retomar el mismo trabajo desde dos terminales a la vez.
- Que `-resume` ofrezca elegir entre varios trabajos cuando hay más de uno reciente.
