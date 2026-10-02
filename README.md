# Agent Manager Notch

Una réplica nativa para Windows de [coucou](https://louis-cfm.github.io/coucou/): pequeños personajes "mochi"
que viven en el borde superior de la pantalla, con los que **conversas**, que **te avisan cuando terminan**,
te piden **permiso** antes de tocar algo y te mandan **recordatorios**.

Cada personaje es un **agente** con su nombre, su color, su *system prompt* y el **CLI que usa por debajo**:
Claude Code, Codex, Gemini CLI o cualquier comando que imprima texto (ollama, aider, scripts…).

- **100 % nativo**: WPF sobre .NET 10 con el tema Fluent de Windows 11, icono en la bandeja del sistema,
  notificaciones de Windows, named pipes y atajo global. **Sin dependencias externas** (ni NuGet ni npm).
- Todo lo que instala o compila se queda en esta carpeta (`nuget.config` apunta a `.nuget/` local).
  Tus datos (agentes, historial, recordatorios) viven en `%APPDATA%\AgentManagerNotch`.

## Arrancar

```powershell
cd D:\Proyectos\JAVASCRIPT\agent-manager-notch
dotnet run                 # desarrollo
.\publish.ps1              # genera .\publish\AgentManagerNotch.exe
.\publish.ps1 -SelfContained   # un solo .exe que no necesita .NET instalado
```

Requisitos: Windows 10/11, SDK de .NET 10 para compilar, y el CLI que quieras usar (`claude`, `codex`,
`gemini`…) en el PATH. Los shims `.cmd` de npm se resuelven a `node script.js` para no pasar por `cmd.exe`.

## Inicio de sesión

Antes de ver los agentes hay que entrar con la cuenta de **Agent Manager**. Sin sesión, la píldora muestra un
candado y el panel solo la tarjeta *Iniciar sesión* (los agentes programados y los recordatorios siguen
funcionando en segundo plano, pero no se muestran avisos).

1. *Iniciar sesión* abre en el navegador `https://agent-manager.distelsa.net/external/token?client=notch`.
2. Agent Manager redirige a `http://localhost:5454/api/callback?token=…&state=…`. Ese puerto solo escucha
   mientras se espera el login (máx. 5 min) y se rechaza cualquier `state` que no haya generado el notch.
3. El token se valida con `GET /api/auth/me` y se guarda cifrado con DPAPI en
   `%APPDATA%\AgentManagerNotch\auth.bin` (solo lo puede leer tu usuario de Windows).

Al arrancar se revalida el token; si caducó, se vuelve a pedir el login (sin conexión se confía en el guardado).
⚙ → *Cerrar sesión* lo invalida en Agent Manager y lo borra.

**MCP de Agent Manager en los CLI.** Cada vez que se valida la sesión se revisan los CLI instalados
(Claude Code, Codex, Gemini CLI). Si alguno no tiene un MCP llamado `agent-manager` (o que apunte a
`https://agent-manager.distelsa.net/mcp`) en su configuración de usuario (`~/.claude.json`,
`~/.codex/config.toml`, `~/.gemini/settings.json`), se instala con su propio comando
(`claude mcp add --transport http --scope user …`, `codex mcp add … --url …`, `gemini mcp add …`) usando
`https://agent-manager.distelsa.net/mcp?t=<token>`. Si ya existe con esa URL pero con otro token (cada inicio de sesión
revoca el anterior), se reemplaza por el de la sesión actual; las entradas sin `?t=` (OAuth) no se tocan. El notch avisa si instaló algo
o si falló, y el resultado queda en el registro (sin el token).

**Primera configuración.** La primera vez (sin `config.json`) se crean los agentes según los CLI instalados:

| ¿Hay algún CLI? | Agentes |
|---|---|
| Sí (el primero de Claude Code → Codex → Gemini) | **Codi** (código, con ese CLI, principal), **Doky** (chat, LLM de Agent Manager) y **Nube** (chat, con ese CLI) |
| No | **Doky** y **Nube**, los dos con el LLM de Agent Manager |

## Tipos de agente

| Tipo | Qué hace | Al abrirlo |
|---|---|---|
| **Código** | Chat con **pestañas de workspace**: cada pestaña es una carpeta donde se abre el CLI, con su propia conversación. | Chat estilo coucou + pestañas + lista de tareas |
| **Chat** | Conversacional, sin carpeta de proyecto. | Chat estilo coucou + lista de tareas |
| **Programado** | No interactivo: ejecuta una **instrucción** según un **horario** y te avisa con el resultado. | Su estado en la tarjeta grande de la vista general |

### Píldora y barra superior

- **Píldora** (modo pequeño), en dos lados: a la izquierda el **principal**; a la derecha **todos los demás
  agrupados** en un bloque 2×2 del mismo alto (máximo 4 visibles; si hay más, un indicador «+N»).
- Al abrir el notch siempre hay una **barra superior**: 🏠 *Inicio* a la izquierda y, a la derecha, 📌 fijar,
  📅 **tareas programadas** y ⚙ **configuración** (ajustes del agente, nuevos agentes y opciones de Agent Manager Notch:
  sonidos, avisos, abrir al pasar el ratón, avisos de Claude Code, iniciar con Windows, salir).
- **Cierre del panel** (⚙ → *Accesibilidad*): por defecto **solo se cierra al hacer clic fuera** del recuadro
  (o con Esc / Ctrl+Alt+Espacio); sacar el ratón no lo cierra. Alternativa: *Cerrar al sacar el ratón*.
  Ahí también está *Abrir al pasar el ratón*. Con 📌 fijado, o con un menú o diálogo abierto, nunca se cierra.
- **Tareas programadas**: los recordatorios y tareas (también los que crean los agentes, p. ej. «recuérdame…»)
  con su hora, agente, tipo y repetición, que se pueden borrar; y los agentes programados con su próxima
  ejecución. Pulsar un elemento abre su agente.

### Vista general (al pasar el ratón o con el primer clic)

Igual que coucou: **una tarjeta grande a la izquierda** con el agente enfocado y **el resto en chips pequeños
a la derecha**.

- Tarjeta grande: personaje, nombre, CLI, chip de estado (*Error*, *Permiso*, *Trabajando*, *En pausa*),
  contador y botón ↗. Debajo, hasta tres líneas de detalle:
  - **Código**: el último estado de cada workspace (`▶ demo-api · Leer server.js`, `✓ demo-blog · <resultado>`,
    `✗ … · falló`). El contador es **pestañas activas / total**.
  - **Chat**: el resultado de la última tarea y lo que hace ahora (el contador son sus pasos, p. ej. `2/5`).
  - **Programado**: su **horario**, **qué hará en la próxima ejecución** (hora + instrucción) y cuándo fue la última,
    con botones *Ejecutar ahora* y *Pausar*.
- Chips: insignia roja si su última tarea falló, naranja si piden permiso, borde ámbar si están trabajando.
- **Pulsar un chip** lo pasa a la tarjeta grande y **queda como principal** (se recuerda al cerrar y sale en grande en la píldora); el que estaba ahí baja a los chips.
- **Pulsar la tarjeta grande** (o ↗) abre el chat. En un **programado**, la tarjeta se **expande** con más
  detalle: último resultado completo, instrucción, próximas 3 ejecuciones, última ejecución y permisos.
  El chip *+ Nuevo* crea agentes, workspaces o recordatorios.

### Chat (interactivos y código)

- El chat va dentro de una **tarjeta** (como las de la vista general) bajo la barra superior. Estilo coucou:
  nombre del agente + nueva conversación (o pestañas de workspace en código), píldora de contexto,
  tus mensajes en burbuja a la derecha, las respuestas como texto suelto, fondo oscuro con halo violeta y el
  personaje asomando abajo a la izquierda. Entrada «Continuar…».
- **Código**: en lugar de «+» aparecen las **pestañas de workspace** (carpeta + estado: ▶ activa, ✓ terminó,
  ✗ falló, ! permiso) y el botón **Nuevo workspace**. La carpeta de una pestaña **no se puede cambiar**: se cierra
  (×) y se abre otra. Arrastrar una carpeta sobre el notch abre un workspace nuevo. Desde un script:
  `AgentManagerNotch.exe --code D:\mi\proyecto` (si ya hay una pestaña para esa carpeta, la abre).
- El **personaje** está arriba a la izquierda, a la altura de las pestañas, y debajo su **lista de tareas**
  (hasta media altura, siempre mostrando lo más reciente, con desplazamiento y lo que no cabe difuminado), mínima: un listado con iconos (✓ hecha, ! fallida, × detenida,
  ▶ en curso, ○ pendiente) y los pasos de la tarea en curso. Cada mensaje es una tarea; si escribes mientras
  trabaja, se **encola**. Los pasos salen de un bloque `[[tasks]]` que Agent Manager Notch pide al agente (se oculta del chat)
  o de las listas nativas del CLI (`todo_list` de Codex, `TodoWrite`, `write_todos`).

### Ejemplo: agente del clima

Un programado que cada 12 horas (06:00 y 18:00) consulta el clima con `WebFetch` a wttr.in y te avisa:
tipo *Programado*, permisos *Solo lectura*, argumentos extra `--allowedTools WebFetch,WebSearch` (para que pueda
usar la web sin pedir permiso) e instrucción del estilo «Consulta el clima actual y de las próximas 12 horas para
Ciudad de Guatemala… resúmelo en 2 líneas con una recomendación».

### Principal y horarios

- **Agente principal**: cualquiera (pulsando su chip, clic derecho → *Establecer como principal*, o la casilla del editor). Es el
  que aparece en la tarjeta grande al abrir y en grande en la píldora. Por defecto, el agente de código.
  Cualquier agente se puede **eliminar** (clic derecho → *Eliminar*, o el botón *Eliminar agente* de sus ajustes), siempre con confirmación.
- **Horario de un programado**: *cada N horas* (mínimo 1 h) dentro de una franja, o *a horas fijas* (separadas
  al menos 1 h), en los **días** que elijas. Si el PC estuvo apagado, recupera una ejecución perdida de las
  últimas 12 h. Como nadie puede aprobar permisos, usa *Solo lectura* o *Automático*.

## Cómo se usa

| Acción | Cómo |
|---|---|
| Ver los agentes | pasar el ratón por la píldora, clic en ella, o **Ctrl + Alt + Espacio** |
| Abrir un agente | clic en su chip (pasa a la tarjeta grande) y clic en la tarjeta para el chat; clic derecho = menú |
| Crear | chip **+ Nuevo** → workspace de código / chat / programado; o arrastra una carpeta |
| Enviar | **Enter** (Shift+Enter = salto de línea); si está ocupado, se encola. Botón rojo = detener |
| Adjuntar archivos | arrastrarlos sobre el notch (el personaje se convierte en caja 📦) o con el clip |
| Fijar el panel abierto | chincheta; **Esc** lo cierra |
| Permisos | tarjeta naranja: *Permitir*, *Permitir siempre* (en la sesión) o *Denegar* |
| Recordatorios | botón ⏰, comando `/recordar 10m texto`, o pídeselo al agente en lenguaje natural |
| Tareas programadas | `/tarea 9:00 revisa mis PRs`: el agente ejecutará la instrucción a esa hora |

Comandos del chat: `/nuevo`, `/recordar <cuándo> <texto>`, `/tarea <cuándo> <instrucción>`, `/ayuda`.
Formatos de tiempo: `10m`, `1h30m`, `2 horas`, `15:30`, `mañana 9:00`, `2026-10-02 09:00`.

Los agentes también crean recordatorios solos: Agent Manager Notch les indica que escriban
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
| **Agent Manager** | `AgentManagerNotch.exe --llm` → `POST /api/notch/notch-agent` (ruteador de Agent Manager: `{"messages":[…], "stream": true}` → streaming SSE, o `data.response` si responde JSON entero) con `Authorization: Bearer <token>` de la sesión; cada petición queda en tu historial de chat de Agent Manager | se reenvía el historial reciente (30 mensajes) | mensaje `system` | — (solo texto) |

Modos de permiso por agente: *Preguntar* (recomendado), *Editar solo / preguntar comandos*,
*Solo lectura* y *Automático*.

### Cómo funcionan las aprobaciones con Claude Code

Agent Manager Notch lanza `claude` con `--settings %APPDATA%\AgentManagerNotch\claude-hook-settings.json`, que registra
`AgentManagerNotch.exe --hook` como hook `PreToolUse`. Ese mismo ejecutable, en modo hook, lee la petición por stdin,
la envía por un *named pipe* a la instancia abierta y espera tu decisión. Las herramientas de solo lectura
(Read, Grep, Glob…) pasan sin preguntar. Si Agent Manager Notch no está abierto, el hook sale al instante y **nunca
bloquea** a Claude Code.

## Notificaciones desde fuera (scripts, builds, otras apps)

```powershell
AgentManagerNotch.exe --notify "Build terminado" "npm run build OK" --agent Codi   # aviso en el notch + toast
AgentManagerNotch.exe --ask Nube "Resume mi día"                                   # le habla a un agente
AgentManagerNotch.exe --code D:\proyecto                                         # agente de código para esa carpeta
AgentManagerNotch.exe --show | --new-agent | --reminders
```

Ejemplo: `npm run build; AgentManagerNotch.exe --notify "Build" "Terminó con código $LASTEXITCODE"`.

### Avisos de tus sesiones normales de Claude Code

En el menú de la bandeja → **"Avisarme de mis sesiones de Claude Code"**. Añade hooks `Stop` y
`Notification` a `~/.claude/settings.json` (guarda antes una copia en `settings.json.coucou-backup`) para
que el notch te avise cuando una sesión que abriste en la terminal termina o necesita permiso.
Desmarcarlo los quita. Es opcional y no se activa solo.

## Estructura

```
App.cs                     Main, arranque, coordinación de notificaciones/recordatorios/canal de control
Controls/MochiView.cs      el personaje y todas sus animaciones
Providers/Providers.cs     Claude Code, Codex, Gemini, Personalizado → eventos normalizados
Services/AgentEntry.cs     agente en la interfaz: pestañas, estado agregado y líneas de detalle
Services/AgentSession.cs   una conversación (agente o pestaña de workspace): cola de tareas, pasos [[tasks]], proceso del CLI, aprobaciones, TimeParser
Services/Scheduler.cs      ejecuta los agentes programados según su horario
Services/AuthService.cs    inicio de sesión con Agent Manager (callback en localhost:5454, token cifrado con DPAPI)
Services/McpSetup.cs       instala el MCP de Agent Manager en los CLI que no lo tengan
Services/LlmBridge.cs      modo --llm: el LLM de Agent Manager como un CLI más (stdin mensajes, stdout eventos)
Services/Approvals.cs      servidor de named pipe + modo --hook
Services/ControlChannel.cs --notify / --ask / --claude-event e integración opcional con ~/.claude
Services/Notifications.cs  bandeja del sistema, toasts, sonidos, inicio con Windows, servicio de recordatorios
Services/CliResolver.cs    búsqueda en el PATH y resolución de shims de npm
Views/NotchWindow.*        el notch: píldora, aviso, vista general (tarjeta + chips) y chat con pestañas
Views/AgentEditorWindow.*  crear/editar agentes con vista previa animada
Views/RemindersWindow.*    recordatorios y tareas programadas
Views/Markdown.cs          Markdown ligero en las burbujas (seleccionable, tablas, bloques de código con copiar)
```

## Notas

- El panel no roba el foco al aparecer por hover; solo al hacer clic o usar el atajo.
- Gemini CLI se implementó según su formato `stream-json` documentado, pero no estaba instalado en este
  equipo, así que no se probó de punta a punta.
- Registro de errores: `%APPDATA%\AgentManagerNotch\coucou.log`.
