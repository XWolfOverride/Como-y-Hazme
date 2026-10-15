# Configuración avanzada

Ver también la [configuración rápida](../README.md#configuración-en-4-pasos) del README.

## Otros servicios

| Servicio | Pasos |
|---|---|
| **Anthropic Claude** | Key en [console.anthropic.com](https://console.anthropic.com) → `como -set claude.api-key sk-ant-...` → `como -set connector claude` |
| **OpenAI** | `como -set openai.api-key sk-...` → `como -set connector openai` |
| **Compatibles con OpenAI** | Mismo conector `openai` cambiando `openai.base-url` (abajo) |

## El conector `openai` y los servicios compatibles

El protocolo de OpenAI (*Chat Completions*) es un estándar de facto, sin nombre genérico oficial: se le
llama **"API compatible con OpenAI"**. Con el conector `openai` y su `base-url` hablas con muchos servicios:

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

- La `base-url` **incluye la versión** (`/v1`). Apunta a **un servicio a la vez**.
- Los servicios locales no piden clave, pero el conector exige una: pon cualquier texto.
- **Grok** (xAI) **no es** Groq: son servicios distintos, ambos accesibles con este conector.
- URL y modelos de terceros los mantiene cada servicio: confírmalos en su documentación; no están todos probados.

## Opciones gratuitas

Con **nivel gratuito, límites de uso y sin tarjeta**: **Google Gemini** (conector `gemini`, key en
[AI Studio](https://aistudio.google.com/apikey)) y, vía `openai`, **Groq**, **OpenRouter** (modelos `:free`)
y **Mistral**. Los límites y condiciones **cambian con frecuencia**: consúltalos en el panel de cada
servicio. Lee antes las [advertencias](../README.md#advertencias): privacidad más laxa y modelos pequeños
poco fiables para `hazme`.

## Dónde y cómo se guarda

`~/.como/config` (Windows: `%USERPROFILE%\.como\config`), **cifrado** con AES-256-GCM. La clave se deriva
de un salt aleatorio por instalación más el usuario y su carpeta personal: sin secretos embebidos y con
coste despreciable. En Linux/macOS, permisos `600`. Protege de lecturas casuales y volcados accidentales,
**no** de alguien con acceso a tu cuenta.

## Problemas frecuentes

| Qué ves | Qué hacer |
|---|---|
| `command not found` / *"no se reconoce"* | El programa no está en el `PATH`: revisa la instalación y **reabre la terminal** |
| `Falta la clave 'gemini.api-key'` | Guarda la API key: `como -set gemini.api-key <clave>` |
| `Error 401` | Clave incorrecta, caducada o incompleta: genera otra |
| `Error 404` / *model not found* | Modelo inexistente: `como -models` y `como -set <conector>.model <modelo>` |
| `Error 429` | Límite de uso (típico en gratuitos). Se reintenta solo; si persiste, espera o cambia de conector |
| `Error 503` / *high demand* | Servicio saturado: se reintenta con espera creciente; prueba más tarde |
| `No se pudo conectar con el servicio de IA` | Sin red, proxy o `base-url` incorrecta: `como -get <conector>.base-url` |
| `Conector desconocido` | Los válidos salen en `como -connectors` |
| `No se puede descifrar la configuración` | El archivo es de otro usuario/equipo: bórralo y reconfigura |
| Caracteres raros en vez de colores | Usa `-no-color` o `como -set no-color true` |

## Trucos

- **`?` o `*` en la pregunta**: el shell puede expandirlos; entrecomilla. En zsh, `alias como='noglob como'`.
- **No encadenes `como ... | sh`** sin leer el comando: es una IA y puede equivocarse. Para ejecutar con supervisión, `hazme`.
- **Desinstalar**: borra los binarios y, si quieres eliminar tus claves, la carpeta `~/.como`.
