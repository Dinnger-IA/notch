# AGENTS.md

Guía para agentes (Claude Code, Codex, etc.) que trabajan en este repositorio. Detalle funcional en `README.md`.

## Ramas

Todo el trabajo va en `main`: commit con su versión (ver abajo) y push.

## Plugins

Lo que no es del notch en sí (integraciones con un servicio concreto, inicio de sesión, voz…) va en un **plugin**
(`Plugins/<Nombre>/`, ver *Plugins* en `README.md`), no mezclado en los archivos comunes. Si un plugin necesita
un punto de extensión que no existe, añádelo a la API (`Plugins/INotchPlugin.cs`, `Plugins/NotchHost.cs`) de
forma genérica, sin nombrar al plugin, y comprueba que sin plugins todo sigue igual.

## Compilar, publicar y reiniciar

- .NET 10 + WPF, sin dependencias externas (ni NuGet ni npm). Comprueba con `dotnet build -c Release`.
- La app que se usa a diario corre desde `publish\AgentManagerNotch.exe`. Para desplegar un cambio:
  `.\publish.ps1` (detiene la instancia en ejecución) y luego `Start-Process .\publish\AgentManagerNotch.exe`.
- Registro: `%APPDATA%\AgentManagerNotch\agent-manager-notch.log`. Datos del usuario (agentes, historial,
  configuración) en esa misma carpeta: haz una copia antes de borrar o restablecer nada.

## Versiones

- La versión es la mayor de `versiones/` (un archivo `X.Y.Z.md` por versión; formato en `versiones/README.md`).
  El notch solo avisa de una actualización cuando el repositorio tiene una versión mayor que la instalada.
- **Cada vez que haya cambios nuevos, crea la versión que los recoge**: `versiones/X.Y.Z.md` con las novedades en
  líneas `- …` (en español, pensadas para el usuario; admiten Markdown). Si la versión más alta todavía no se ha
  subido (`git log origin/<rama> -- versiones/`), añade las novedades a ese archivo en vez de crear otro. Sube el
  menor (Z) para correcciones y el intermedio (Y) para funciones nuevas.
- **Si el cambio es visual** (algo que se ve distinto en el notch o en una ventana), graba un GIF que lo enseñe y
  guárdalo como `versiones/X.Y.Z.gif` con `AgentManagerNotch.exe --grabar-gif` (ver `versiones/README.md`;
  dibuja la vista fuera de la pantalla, así que no graba la pantalla del usuario). Se ve en la
  ventana *Notas de la versión*, donde cada versión tiene su pestaña en la barra lateral.

## Convenciones

- Interfaz, comentarios, mensajes de commit y documentación **en español**.
- Commits con mensaje descriptivo (título + cuerpo con el porqué) y la línea `Co-Authored-By` del agente.
- Actualiza `README.md` cuando cambie el comportamiento visible.
- No escribas en el código ni en la documentación direcciones, IP, tokens ni datos internos: el repositorio es público.
