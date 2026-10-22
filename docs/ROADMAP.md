# Roadmap

Ideas y trabajo pendiente, sin compromiso de fechas. Lo ya hecho vive en el código y en el [README](../README.md);
el contexto técnico, en [AGENTS.md](../AGENTS.md).

## OpenAI: API Responses (`/v1/responses`)

**Estado:** implementada (`openai.api` = `chat` | `responses` | `auto`, por defecto `chat`); **pendiente de validar contra
el servicio real**. Se probó solo con un servidor simulado.

**Por qué:** `gpt-6.1-sol` exige razonamiento y no admite herramientas en Chat Completions (con `reasoning_effort=none` da
400), y los modelos codex (`gpt-5.3-codex`) responden 404 en `/v1/chat/completions`. Sin Responses no se pueden usar con hazme.

**Cómo está hecha** (`Hazme/Chat/OpenAiResponsesChat.cs`, `OpenAiAutoChat.cs`)
- Sin estado en el servidor: `store:false` e `include:["reasoning.encrypted_content"]`; el razonamiento viaja cifrado en
  nuestras peticiones. Los elementos del turno se guardan saneados (sin `id` ni `status`) en `RawContentJson` y se reenvían tal cual.
- Herramientas con `strict:false` (en Responses las funciones son estrictas por defecto y las nuestras tienen campos opcionales).
- `auto`: Chat Completions y, si el servicio dice que ese modelo solo funciona con `/v1/responses`, cambia el resto de la
  sesión (solo antes de la primera respuesta del asistente, porque el formato del historial difiere).
- `reasoning` solo se envía con `openai.reasoning-effort`; si el servicio rechaza el razonamiento cifrado, se reintenta sin él.
- No se envía `max_output_tokens` (los tokens de razonamiento cuentan y cada modelo tiene su tope).

**Por validar con la API real**
- Que reenviar el razonamiento sin `id` es aceptado con `store:false` (se basa en cómo lo hacen otros clientes sin estado).
- Los nombres de los eventos de streaming usados (`response.output_text.delta`, `response.output_item.done`,
  `response.completed`/`incomplete`/`failed`, `error`) y `incomplete_details.reason`.
- Modelos sin razonamiento (`gpt-4.1-mini`) con `include`: debería reintentar sin él, pero no está comprobado.

**Pendiente**
- `como` (el oráculo) sigue usando solo Chat Completions: con modelos que exigen Responses falla igual.
- `auto` por URL: usar Responses por defecto solo si la `base-url` es la de OpenAI.
- `store:false` también en la ruta de Chat Completions, solo hacia `api.openai.com`.

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
