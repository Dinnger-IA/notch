# AGENTS.md

Guía para agentes (Claude Code, Codex, etc.) que trabajan en este repositorio. Detalle funcional en `README.md`.

## Ramas y repositorios

| Rama | Repositorio (remoto) | Qué va ahí |
|---|---|---|
| `main` | GitHub `Dinnger-IA/notch` (`notch`), **público** | El notch y la infraestructura de plugins. Nada de Agent Manager ni datos internos. |
| `agent-manager` | Azure DevOps `agent-manager-notch` (`origin`), **nunca a GitHub** | `main` + los plugins `Plugins/AgentManager/` y `Plugins/Voz/`. |

`main` siempre se descarga de GitHub (`git pull` en `main` trae `notch/main`) y `agent-manager` solo se sube a
Azure. Un hook local `pre-push` bloquea subir a GitHub cualquier rama que no sea `main`; `.\sincronizar-notch.ps1`
lo instala, configura los remotos y trae `main` a esta rama.

Depende de Agent Manager y por tanto va en `Plugins/AgentManager/` (solo en `agent-manager`):
- Inicio de sesión y token (`AuthService.cs`, `LoginView.*`: la puerta de acceso del notch).
- Instalación del MCP de Agent Manager en los CLI (`McpSetup.cs`).
- Proveedor LLM de Agent Manager (`AgentManagerProvider`, `LlmBridge.cs`, modo `--llm`).
- La primera configuración con Doky y la etapa *Agent Manager* del instalador (`InstallerStage.cs`, `AgentManagerCheck.cs`).

Además, por decisión del responsable, la **voz** (`Plugins/Voz/`, servidor TTS/STT interno) vive solo en
`agent-manager` aunque no depende de Agent Manager: no la lleves a `main` sin que lo pida.

### Identificar siempre qué es de Agent Manager

Antes de escribir código, clasifica **cada parte** del cambio: ¿necesita Agent Manager (su sesión o token, su
servidor, su LLM, su MCP) o funciona sin él? Dilo en el plan y en el mensaje del commit, y si mezcla las dos cosas,
sepáralas según el flujo de abajo. Ante la duda, pregunta al responsable antes de decidir la rama.

Lo de Agent Manager va **dentro de su plugin**, sin tocar los archivos comunes. Si el plugin necesita un punto de
extensión que no existe, añádelo de forma genérica a la API de plugins en `main` (sin nombrar a Agent Manager) y
úsalo después desde el plugin.

### Todo lo de Agent Manager va detrás de `UseAgentManager`

En `agent-manager`, la app también puede funcionar **sin** Agent Manager: se elige en el instalador y se guarda en
`config.json` como `Settings.UseAgentManager` (por defecto `true`; se activa después en ⚙ Configuración →
*Sesión*). Toda funcionalidad del plugin debe comprobarlo (`AgentManagerPlugin.Enabled`) y, con él en `false`,
comportarse como `main`: no se pide ni se restaura la sesión (la puerta de acceso queda abierta), no se instala el
MCP, no se crean agentes con su proveedor ni se ofrece en el editor, no se llama a `agent-manager.distelsa.net` y
nada de la interfaz da por hecho que hay sesión. Al revisar un cambio, comprueba también que funciona con la
bandera en `false`.

### Flujo

1. Cambios generales (y puntos de extensión nuevos): en `main`, commit y push a GitHub (`notch`).
2. Justo después, y siempre que `notch/main` traiga novedades, trae `main` a esta rama **por merge** (no rebase):
   ```powershell
   .\sincronizar-notch.ps1      # git pull en main + git merge main en agent-manager
   git push                     # agent-manager → Azure (origin)
   ```
3. Cambios de Agent Manager o de la voz: en `agent-manager` (dentro de su plugin), commit y push a Azure.
   **Nunca** se fusiona `agent-manager` en `main` ni se sube a GitHub.
4. Si por error un cambio general quedó en `agent-manager`, pásalo a `main` con `git cherry-pick <commit>` y
   vuelve a fusionar `main` en `agent-manager`.

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
  El notch solo avisa de una actualización cuando su rama tiene una versión mayor que la instalada.
- La versión se añade en `main` y llega a `agent-manager` con el merge; si un cambio solo de `agent-manager`
  merece versión, súbela en esta rama sin volver a usar ese número en `main`.
- La app de uso diario (`publish\`) se publica **siempre desde `agent-manager`**: una compilación de `main` no
  trae los plugins de Agent Manager ni de la voz.
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
- En `main` no escribas direcciones, IP, tokens ni datos internos: ese repositorio es público. Los datos internos
  solo pueden ir en los plugins de esta rama.
- Nunca registres ni muestres el token de Agent Manager (logs, mensajes de error, salida de comandos).
