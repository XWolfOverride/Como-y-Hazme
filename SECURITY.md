# Política de seguridad

## Modelo de amenazas, en breve

- **`como`** envía tu pregunta al proveedor de IA y muestra la respuesta. No ejecuta comandos, no lee ni
  escribe archivos del usuario. Un fallo que permita lo contrario es una vulnerabilidad.
- **`hazme`** ejecuta comandos y lee/escribe archivos **por diseño**, con los permisos del usuario y sin
  sandbox. Esto es comportamiento esperado, no una vulnerabilidad. Se mitiga con la visibilidad previa de cada
  acción, el tiempo de gracia con Esc y el límite de pasos; el usuario sigue siendo responsable de revisar.
- **Configuración**: `~/.como/config` está cifrada para evitar lecturas casuales; no protege frente a un
  atacante con acceso a la cuenta del usuario.

## Qué se considera una vulnerabilidad

- Que `como` (o `Como.Core`) pueda lanzar procesos o tocar archivos del usuario.
- Que `hazme` ejecute algo **sin haberlo mostrado** o ignore el aborto con Esc / el límite de pasos.
- Fugas de credenciales (API keys en logs, salida o mensajes de error).
- Debilidades en el cifrado o en los permisos del archivo de configuración.

## Cómo reportar

Abre un *security advisory* privado en el repositorio (pestaña **Security → Report a vulnerability**).
Por favor, no publiques detalles en un issue público hasta que haya una corrección.
