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

## Convenciones

- Interfaz, comentarios, mensajes de commit y documentación **en español**.
- Commits con mensaje descriptivo (título + cuerpo con el porqué) y la línea `Co-Authored-By` del agente.
- Actualiza `README.md` cuando cambie el comportamiento visible.
- Nunca registres ni muestres el token de Agent Manager (logs, mensajes de error, salida de comandos).
