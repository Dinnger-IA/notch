# AGENTS.md

Guía para agentes (Claude Code, Codex, etc.) que trabajan en este repositorio. Detalle funcional en `README.md`.

## Ramas

| Rama | Qué va ahí |
|---|---|
| `main` | Todo el diseño y la funcionalidad propios del notch que **no dependen de Agent Manager**: interfaz, animaciones, personajes, chat, Markdown, editor de agentes, workspaces, recordatorios, CLIs (Claude Code, Codex, Gemini, personalizado), correcciones generales. |
| `agent-manager` | Solo lo que **depende de Agent Manager** (`agent-manager.distelsa.net`). Debe estar **siempre actualizada con `main`**. |

Depende de Agent Manager y por tanto va en `agent-manager`:
- Inicio de sesión y token: `Services/AuthService.cs`, la vista de login y la puerta de acceso en `Views/NotchWindow.*`.
- Instalación del MCP de Agent Manager en los CLI: `Services/McpSetup.cs`.
- Proveedor LLM de Agent Manager (`ProviderKind.AgentManager`, `AgentManagerProvider`, `Services/LlmBridge.cs`, modo `--llm`).
- La primera configuración con Doky (usa el proveedor de Agent Manager).
- La opción *Usar Agent Manager* del instalador y su comprobación (`Services/AgentManagerCheck.cs`).

Además, por decisión del responsable, la **voz** (`Services/VoiceService.cs`, servidor TTS/STT interno) vive solo en
`agent-manager` aunque no depende de Agent Manager: no la lleves a `main` sin que lo pida.

### Identificar siempre qué es de Agent Manager

Antes de escribir código, clasifica **cada parte** del cambio: ¿necesita Agent Manager (su sesión o token, su
servidor, su LLM, su MCP) o funciona sin él? Dilo en el plan y en el mensaje del commit, y si mezcla las dos cosas,
sepáralas según el flujo de abajo. Ante la duda, pregunta al responsable antes de decidir la rama.

### Todo lo de Agent Manager va detrás de la bandera `UseAgentManager`

En `agent-manager`, la app también puede funcionar **sin** Agent Manager: se elige en el instalador y se guarda en
`config.json` como `Settings.UseAgentManager` (por defecto `true`; se activa después en ⚙ Configuración →
*Sesión*). Por eso **toda funcionalidad que dependa de Agent Manager debe comprobar esa bandera** y, con ella en
`false`, comportarse como `main`:

- No se pide ni se restaura la sesión, no se muestra la puerta de inicio de sesión ni se instala el MCP.
- No se crean agentes con el proveedor de Agent Manager ni se ofrece ese proveedor en el editor.
- No se hace ninguna llamada a `agent-manager.distelsa.net`.
- Nada de la interfaz da por hecho que hay sesión (textos, tarjetas, botones): o se oculta o explica que no se usa.

Puntos donde ya se comprueba (sigue el mismo patrón al añadir algo nuevo): `Authed` en `Views/NotchWindow.xaml.cs`,
el arranque de la sesión y el MCP en `App.cs`, `ConfigStore.DefaultAgents(useAgentManager)`, la lista de
proveedores de `Views/AgentEditorWindow.xaml.cs` y la tarjeta de sesión de la configuración. Al revisar un cambio de
`agent-manager`, comprueba también que funciona con la bandera en `false`.

### Flujo

1. Antes de empezar, decide a qué rama pertenece el cambio con la regla de arriba. Si un cambio mezcla las dos
   cosas, sepáralo: la parte general en `main` y la que usa Agent Manager en `agent-manager`.
2. Cambios generales: commit en `main` y push.
3. Justo después, actualiza `agent-manager` con `main` **por merge** (no rebase: la rama ya está publicada):
   ```powershell
   git checkout agent-manager
   git merge main
   git push
   ```
4. Cambios de Agent Manager: commit en `agent-manager` y push. **Nunca** se fusiona `agent-manager` en `main`
   salvo que el responsable del proyecto lo pida explícitamente.
5. Si por error un cambio general quedó en `agent-manager`, pásalo a `main` con `git cherry-pick <commit>` y
   vuelve a fusionar `main` en `agent-manager`.

## Compilar, publicar y reiniciar

- .NET 10 + WPF, sin dependencias externas (ni NuGet ni npm). Comprueba con `dotnet build -c Release`.
- La app que se usa a diario corre desde `publish\AgentManagerNotch.exe`. Para desplegar un cambio:
  `.\publish.ps1` (detiene la instancia en ejecución) y luego `Start-Process .\publish\AgentManagerNotch.exe`.
- Registro: `%APPDATA%\AgentManagerNotch\agent-manager-notch.log`. Datos del usuario (agentes, historial,
  sesión cifrada) en esa misma carpeta: haz una copia antes de borrar o restablecer nada.

## Versiones

- La versión es la mayor de `versiones/` (un archivo `X.Y.Z.md` por versión; formato en `versiones/README.md`).
  El notch solo avisa de una actualización cuando su rama tiene una versión mayor que la instalada.
- **Cada vez que haya cambios nuevos, crea la versión que los recoge**: `versiones/X.Y.Z.md` con las novedades en
  líneas `- …` (en español, pensadas para el usuario; admiten Markdown). Si la versión más alta todavía no se ha
  subido (`git log origin/<rama> -- versiones/`), añade las novedades a ese archivo en vez de crear otro. Sube el
  menor (Z) para correcciones y el intermedio (Y) para funciones nuevas.
- **Si el cambio es visual** (algo que se ve distinto en el notch o en una ventana), graba un GIF que lo enseñe y
  guárdalo como `versiones/X.Y.Z.gif` con `AgentManagerNotch.exe --grabar-gif` (ver `versiones/README.md`;
  dibuja la vista fuera de la pantalla, así que no graba la pantalla del usuario). Se ve en la
  ventana *Notas de la versión*, donde cada versión tiene su pestaña en la barra lateral.
- La versión se añade en `main` y llega a `agent-manager` con el merge; si un cambio solo de `agent-manager`
  merece versión, súbela en esa rama sin volver a usar ese número en `main`.
- La app de uso diario (`publish\`) se publica **siempre desde `agent-manager`**: una compilación de `main` no
  conoce el proveedor de Agent Manager y no puede leer la configuración del usuario.

## Convenciones

- Interfaz, comentarios, mensajes de commit y documentación **en español**.
- Commits con mensaje descriptivo (título + cuerpo con el porqué) y la línea `Co-Authored-By` del agente.
- Actualiza `README.md` cuando cambie el comportamiento visible.
- Nunca registres ni muestres el token de Agent Manager (logs, mensajes de error, salida de comandos).
