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
