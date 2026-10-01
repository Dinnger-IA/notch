# Coucou para Windows

Una réplica nativa para Windows de [coucou](https://louis-cfm.github.io/coucou/): pequeños personajes "mochi"
que viven en el borde superior de la pantalla, con los que **conversas**, que **te avisan cuando terminan**,
te piden **permiso** antes de tocar algo y te mandan **recordatorios**.

Cada personaje es un **agente** con su nombre, su color, su *system prompt* y el **CLI que usa por debajo**:
Claude Code, Codex, Gemini CLI o cualquier comando que imprima texto (ollama, aider, scripts…).

- **100 % nativo**: WPF sobre .NET 10 con el tema Fluent de Windows 11, icono en la bandeja del sistema,
  notificaciones de Windows, named pipes y atajo global. **Sin dependencias externas** (ni NuGet ni npm).
- Todo lo que instala o compila se queda en esta carpeta (`nuget.config` apunta a `.nuget/` local).
  Tus datos (agentes, historial, recordatorios) viven en `%APPDATA%\CoucouWin`.

## Arrancar

```powershell
cd D:\Proyectos\JAVASCRIPT\CoucouWin
dotnet run                 # desarrollo
.\publish.ps1              # genera .\publish\CoucouWin.exe
.\publish.ps1 -SelfContained   # un solo .exe que no necesita .NET instalado
```

Requisitos: Windows 10/11, SDK de .NET 10 para compilar, y el CLI que quieras usar (`claude`, `codex`,
`gemini`…) en el PATH. Los shims `.cmd` de npm se resuelven a `node script.js` para no pasar por `cmd.exe`.

## Tipos de agente

| Tipo | Qué hace | En el panel |
|---|---|---|
| **Código** | Conversacional y atado a **una carpeta fija**: ahí se abren sus CLI. Puedes tener varios, uno por proyecto. | Chat + barra de tareas + carpeta en la línea de estado |
| **Chat** | Conversacional, sin carpeta de proyecto. | Chat + barra de tareas |
| **Programado** | No interactivo: ejecuta una **instrucción** según un **horario** y te avisa con el resultado. | Tarjeta con instrucción, horario, próxima y última ejecución + historial de ejecuciones |

- **Agente principal**: cualquiera puede serlo (clic derecho → *Establecer como principal*, o la casilla en el editor).
  Se ve grande en el notch; el resto se ve pequeño y **agrupado por tipo** (Código · Chat · Programados).
  Al pasar el ratón sobre un grupo se despliega y muestra cada agente completo, con el **nombre de su carpeta**
  (código) o su **próxima ejecución** (programados). Por defecto el principal es un agente de código.
- Cualquier agente se puede **eliminar**, incluido el principal (otro pasa a serlo).
- **Carpeta de un agente de código**: arrastra la carpeta sobre el notch o pulsa *Seleccionar carpeta*.
  **No se puede cambiar**: para trabajar en otra carpeta, cierra el agente (✕) y abre otro.
  Soltar una carpeta sobre el notch crea un agente de código nuevo para ella; el botón **+** del grupo *Código*
  también. Desde un script: `CoucouWin.exe --code D:\mi\proyecto` (si ya hay uno para esa carpeta, lo abre).
- **Barra lateral de tareas** (chat y código):
  - **Ahora**: la tarea en curso y sus pasos (✓ hecho, ▶ en curso, ○ pendiente).
  - **Pendientes**: si escribes mientras el agente trabaja, el mensaje se **encola** y se ejecuta al terminar.
    Puedes añadir tareas desde la barra o quitarlas. Si detienes una tarea, la cola espera a *Reanudar cola*.
  - **Completadas**: historial con estado y hora.

  Los pasos salen del propio agente: Coucou le pide que publique su plan como un bloque
  `[[tasks]] - [x] hecho / - [~] en curso / - [ ] pendiente [[/tasks]]`, que se oculta del chat y se dibuja en la
  barra. Funciona con cualquier CLI. Además se leen las listas nativas cuando existen (`todo_list` de Codex,
  `TodoWrite` de Claude Code, `write_todos` de Gemini).
- **Horario de un programado**: *cada N horas* (mínimo 1 h) dentro de una franja (p. ej. de 09:00 a 18:00),
  o *a horas fijas* (separadas al menos 1 h), en los **días** que elijas. Puede pausarse, ejecutarse a mano
  (*Ejecutar ahora*) y opcionalmente recordar las ejecuciones anteriores. Si el PC estuvo apagado, recupera
  una ejecución perdida de las últimas 12 h. Como nadie puede aprobar permisos, usa *Solo lectura* o *Automático*.

## Cómo se usa

| Acción | Cómo |
|---|---|
| Abrir el panel | pasar el ratón por la píldora, clic en ella, o **Ctrl + Alt + Espacio** |
| Cambiar de agente | clic en su personaje (clic derecho: editar, principal, nueva conversación, abrir carpeta, eliminar) |
| Crear un agente | botón **+** → código / chat / programado (con plantillas en el editor) |
| Enviar | **Enter** (Shift+Enter = salto de línea); si está ocupado, va a *Pendientes*. Botón rojo = detener |
| Adjuntar archivos | arrastrarlos sobre el notch (el personaje se convierte en caja 📦) o con el clip |
| Fijar el panel abierto | chincheta; **Esc** lo cierra |
| Permisos | tarjeta naranja: *Permitir*, *Permitir siempre* (en la sesión) o *Denegar* |
| Recordatorios | botón ⏰, comando `/recordar 10m texto`, o pídeselo al agente en lenguaje natural |
| Tareas programadas | `/tarea 9:00 revisa mis PRs`: el agente ejecutará la instrucción a esa hora |

Comandos del chat: `/nuevo`, `/recordar <cuándo> <texto>`, `/tarea <cuándo> <instrucción>`, `/ayuda`.
Formatos de tiempo: `10m`, `1h30m`, `2 horas`, `15:30`, `mañana 9:00`, `2026-10-02 09:00`.

Los agentes también crean recordatorios solos: Coucou les indica que escriban
`[[reminder: 10m | texto]]` o `[[task: 2026-10-02T09:00 | instrucción]]`; la etiqueta se elimina del chat y
queda programada (con repetición diaria, semanal, de lunes a viernes, etc. desde la ventana ⏰).

## Personajes y animaciones

Dibujados a mano en tiempo real (`Controls/MochiView.cs`, 60 fps):

- **Reposo**: respira, parpadea (a veces dos veces), sigue el cursor con los ojos proyectados sobre la
  esfera y, si dejas el ratón quieto, mira a su alrededor.
- **Pensando**: mira hacia arriba con una burbuja de puntos. **Trabajando**: da saltitos y mira de lado a
  lado con un spinner. **Esperando permiso**: ojos grandes, se sacude y muestra "!".
- **¡Listo!**: salto con *squash & stretch*, ojos felices ^ ^ y confeti. **Error**: cejas tristes.
- **Recordatorio**: vibra como un despertador. **Durmiendo**: tras 2 min sin actividad, con "z z z".
- **Interacción**: un clic lo aplasta y pone cara de molestia `> <`; 5 clics seguidos lo **marean**
  (ojos en espiral y estrellitas). Al arrastrar archivos se mete en una **caja**.

En el editor de agentes puedes previsualizar todos los estados.

## CLIs soportados

| CLI | Invocación | Conversación | System prompt | Permisos |
|---|---|---|---|---|
| **Claude Code** | `claude -p --output-format stream-json --verbose --include-partial-messages` | `--resume <session>` | `--append-system-prompt` | hook `PreToolUse` → named pipe → tarjeta en el notch |
| **Codex** | `codex exec --json … -` (prompt por stdin) | `codex exec resume <thread>` | `-c developer_instructions="…"` | sandbox `read-only` / `workspace-write` / bypass |
| **Gemini CLI** | `gemini -p … --output-format stream-json` | `--resume` | se antepone al primer mensaje | `--approval-mode auto_edit` / `--yolo` |
| **Personalizado** | tu comando, con `{prompt}` `{system}` `{model}` | se reenvía el historial reciente | incluido en el prompt | — |

Modos de permiso por agente: *Preguntar* (recomendado), *Editar solo / preguntar comandos*,
*Solo lectura* y *Automático*.

### Cómo funcionan las aprobaciones con Claude Code

Coucou lanza `claude` con `--settings %APPDATA%\CoucouWin\claude-hook-settings.json`, que registra
`CoucouWin.exe --hook` como hook `PreToolUse`. Ese mismo ejecutable, en modo hook, lee la petición por stdin,
la envía por un *named pipe* a la instancia abierta y espera tu decisión. Las herramientas de solo lectura
(Read, Grep, Glob…) pasan sin preguntar. Si Coucou no está abierto, el hook sale al instante y **nunca
bloquea** a Claude Code.

## Notificaciones desde fuera (scripts, builds, otras apps)

```powershell
CoucouWin.exe --notify "Build terminado" "npm run build OK" --agent Codi   # aviso en el notch + toast
CoucouWin.exe --ask Nube "Resume mi día"                                   # le habla a un agente
CoucouWin.exe --code D:\proyecto                                         # agente de código para esa carpeta
CoucouWin.exe --show | --new-agent | --reminders
```

Ejemplo: `npm run build; CoucouWin.exe --notify "Build" "Terminó con código $LASTEXITCODE"`.

### Avisos de tus sesiones normales de Claude Code

En el menú de la bandeja → **"Avisarme de mis sesiones de Claude Code"**. Añade hooks `Stop` y
`Notification` a `~/.claude/settings.json` (guarda antes una copia en `settings.json.coucou-backup`) para
que el notch te avise cuando una sesión que abriste en la terminal termina o necesita permiso.
Desmarcarlo los quita. Es opcional y no se activa solo.

## Estructura

```
App.cs                     Main, arranque, coordinación de notificaciones/recordatorios/canal de control
Controls/MochiView.cs      el personaje y todas sus animaciones
Controls/AgentGroupView.cs grupos de agentes secundarios (compactos / desplegados al pasar el ratón)
Providers/Providers.cs     Claude Code, Codex, Gemini, Personalizado → eventos normalizados
Services/AgentSession.cs   conversación, cola de tareas, pasos [[tasks]], proceso del CLI, aprobaciones, TimeParser
Services/Scheduler.cs      ejecuta los agentes programados según su horario
Services/Approvals.cs      servidor de named pipe + modo --hook
Services/ControlChannel.cs --notify / --ask / --claude-event e integración opcional con ~/.claude
Services/Notifications.cs  bandeja del sistema, toasts, sonidos, inicio con Windows, servicio de recordatorios
Services/CliResolver.cs    búsqueda en el PATH y resolución de shims de npm
Views/NotchWindow.*        el notch: píldora, aviso y panel de chat
Views/AgentEditorWindow.*  crear/editar agentes con vista previa animada
Views/RemindersWindow.*    recordatorios y tareas programadas
Views/Markdown.cs          Markdown ligero en las burbujas
```

## Notas

- El panel no roba el foco al aparecer por hover; solo al hacer clic o usar el atajo.
- Gemini CLI se implementó según su formato `stream-json` documentado, pero no estaba instalado en este
  equipo, así que no se probó de punta a punta.
- Registro de errores: `%APPDATA%\CoucouWin\coucou.log`.
