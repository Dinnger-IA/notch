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

## Instalador

```powershell
.\crear-instalador.ps1     # genera .\instalador\AgentManagerNotch-Setup-X.Y.Z.exe
```

El instalador es el propio programa como un único `.exe` autónomo (no necesita .NET instalado): abierto con ese
nombre (o con `--instalar`) muestra la ventana de instalación. Instala por usuario, sin permisos de administrador,
en `%LOCALAPPDATA%\Programs\Agent Manager Notch`, con acceso en el menú Inicio y opciones para **iniciar con
Windows**, crear un acceso en el escritorio y abrirlo al terminar. Si ya estaba instalado, lo actualiza (cierra el
notch abierto y conserva agentes, conversaciones y sesión).

Va **por etapas**: *Herramientas* (muestra si Claude Code y Codex están instalados, con un botón **Instalar** al lado
de los que falten, y la opción de conectar sus avisos de la terminal) → *Opciones* → *Instalación* (cada paso con su
resultado; el notch se abre al final). Claude Code se instala con su instalador oficial de Windows y Codex con npm
(antes Node LTS con winget si falta), en `Services/CliSetup.cs`. Los avisos apuntan al programa instalado: hooks de
Claude Code y la opción `notify` de Codex (si la usaba otra app, se encadena: ver más abajo).

Se desinstala desde *Configuración de Windows → Aplicaciones instaladas* (o `AgentManagerNotch.exe --desinstalar`),
con la opción de borrar también los datos. Para instalar desde un script: `AgentManagerNotch-Setup-X.Y.Z.exe
--silencioso [--sin-inicio-windows] [--escritorio] [--abrir] [--instalar-cli] [--sin-avisos]`, y `--desinstalar
--silencioso [--borrar-datos]`.
No se comprime a propósito: comprimido ocupa la mitad, pero tarda ~0,9 s en cada arranque (Claude Code lo lanza en
cada herramienta) frente a ~0,2 s.

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
  📅 **tareas programadas** y ⚙ **configuración**.
- **Configuración** (⚙): una vista dentro del notch con tarjetas agrupadas: *Agente* (ajustes del agente abierto,
  hacer principal), *Crear* (chat, programado, workspace), *Notificaciones* (sonidos, avisar al terminar, avisos de
  Claude Code), *Panel · accesibilidad*, *Sistema* (iniciar con Windows, carpeta de datos, salir) y *Versión*.
- **Cierre del panel** (⚙ → *Panel · accesibilidad*): por defecto **solo se cierra al hacer clic fuera** del recuadro
  (o con Esc / Ctrl+Alt+Espacio); sacar el ratón no lo cierra. Alternativa: *Cerrar al sacar el ratón*.
  Ahí también está *Abrir al pasar el ratón*. Con 📌 fijado, o con un menú o diálogo abierto, nunca se cierra.
  Fuera del recuadro visible el notch deja pasar los clics a lo que hay debajo (también sobre su sombra).
- **Ocultar el notch** (⚙ → *Panel · accesibilidad*): plegado, el notch es solo una línea gris arriba al centro;
  crece un poco al acercar el ratón y se abre con un clic (pasar el ratón no lo abre). Con esta opción los avisos
  de los agentes (terminó, necesita permiso, recordatorios…) llegan como notificaciones de Windows.
- **Actualizaciones** (⚙ → *Versión*): la versión de la app es la mayor de la carpeta [`versiones/`](versiones/)
  (un archivo `X.Y.Z.md` por versión con sus novedades) y se fija al compilar, junto con la rama. Cada 2 horas el
  notch lee esa carpeta en su rama del repositorio (`git fetch`, o un clon mínimo temporal si la copia no está
  dentro del repositorio); solo si hay una **versión mayor** aparece un **agente de actualización** (personaje
  violeta con «!») en la píldora y, al abrir el notch, una tarjeta con la versión nueva, sus novedades y
  *Actualizar ahora* o *Más tarde* (pospone hasta la siguiente versión); también avisa con una notificación. Los
  commits sin versión nueva no avisan. *Actualizar* hace `git pull --ff-only` + `publish.ps1` en la carpeta del
  repositorio y abre la copia recién publicada (`publish\AgentManagerNotch.exe`, y «Iniciar con Windows» pasa a
  apuntar a ella), solo si el repositorio está en esa rama y sin cambios locales; si no, explica el motivo.
  Registro en `%APPDATA%\AgentManagerNotch\update.log`.
- **Notas de la versión** (⚙ → *Versión* → *Notas de la versión*, menú de la bandeja o `--release-notes [X.Y.Z]`):
  ventana con una pestaña por versión en la barra lateral (la instalada marcada) y, a la derecha, sus novedades y,
  si la versión cambió algo visual, un GIF que lo enseña (`versiones/X.Y.Z.gif`). Las notas van dentro del
  ejecutable, así que no necesita red. Tras actualizar, el notch abre solo las notas de la versión nueva.
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
  nombre del agente + nueva conversación (o pestañas de workspace en código), píldora de contexto
  (no en los agentes de código: su carpeta ya se ve en la pestaña),
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
- En los agentes de **código**, bajo el personaje se ven las **5 últimas acciones** (leer, editar, ejecutar…),
  la más reciente **arriba** y las demás cada vez más difuminadas; las anteriores, con **scroll** (al pasar el ratón
  o desplazarse se ven todas nítidas). La nueva entra deslizándose y las otras bajan a su sitio
  (`Views/RecentActionList.cs`). Se cargan **10** de entrada y otras 10 cada vez que llegas al final (scroll infinito).
- Para que cambiar de pestaña no trabe el notch, la conversación (y el panel de acciones de los demás agentes) solo
  pinta los **10 últimos mensajes**; al desplazarte hacia arriba se cargan los 10 anteriores sin perder lo que
  estabas leyendo (`Views/MessageWindow.cs`).
- **Git** en los agentes de código: si la carpeta del workspace es un repositorio, a la derecha de las pestañas se
  ve la rama y el número de cambios. Al pulsarlo, el panel de git tapa la conversación y muestra los cambios en
  *preparados* y *sin preparar* (**+** / **−** por archivo o para todos; doble clic abre el archivo), la rama
  (cambiar a otra o crear una nueva), los commits por subir (↑) o por bajar (↓), el mensaje del commit, **Commit**
  (si no hay nada preparado, confirma todo) y **Push** (o **Publicar**, que la sube con `-u` al remoto). El estado se
  refresca solo cada pocos segundos y al terminar cada turno del agente. Con el panel abierto, **pulsa el
  personaje** y el agente escribe el mensaje del commit: se lanza su CLI en solo lectura y sin sesión con el diff
  (lo preparado o, si no hay nada, todo) y los últimos commits para seguir su estilo; pulsarlo otra vez lo cancela.
  Usa el `git` del PATH; las credenciales las pide el administrador de credenciales de Git
  (`Services/GitService.cs`, `Views/NotchWindow.Git.cs`).

### Ejemplo: agente del clima

Un programado que cada 12 horas (06:00 y 18:00) consulta el clima con `WebFetch` a wttr.in y te avisa:
tipo *Programado*, permisos *Solo lectura*, argumentos extra `--allowedTools WebFetch,WebSearch` (para que pueda
usar la web sin pedir permiso) e instrucción del estilo «Consulta el clima actual y de las próximas 12 horas para
Ciudad de Guatemala… resúmelo en 2 líneas con una recomendación».

### Principal y horarios

- **Ajustes de un agente**: arriba del editor están todos los agentes; pulsar uno lo abre en el mismo editor para
  cambiar sus atributos (si hay cambios sin guardar, pregunta si guardarlos).
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
AgentManagerNotch.exe --show | --new-agent | --reminders | --release-notes [X.Y.Z]
```

Ejemplo: `npm run build; AgentManagerNotch.exe --notify "Build" "Terminó con código $LASTEXITCODE"`.

### Avisos de tus sesiones normales de Claude Code

En el menú de la bandeja → **"Avisarme de mis sesiones de Claude Code"**. Añade hooks `Stop`, `Notification` y
`UserPromptSubmit` a `~/.claude/settings.json` (guarda antes una copia en `settings.json.coucou-backup`) para
que el notch te avise cuando una sesión que abriste en la terminal termina o necesita permiso. Con el notch plegado
el aviso sale en la píldora; con el panel abierto, como una tarjeta flotante bajo la barra superior (lo mismo para
Codex y `--notify`). Pulsarla abre la sesión.
Desmarcarlo los quita. Es opcional y no se activa solo (si ya estaba activado, se añade solo el evento nuevo).

Cada sesión de la terminal se **asocia a una pestaña** de un agente de código que use Claude Code: el que ya tenga
esa carpeta, el principal, el que se llame Codi… o el primero; si no hay ninguno se crea **codi-claude**. La pestaña
muestra la sesión leída de su transcripción (`transcript_path` del hook, `Services/ClaudeTranscript.cs`): lo que
pediste, las acciones y las respuestas, sin razonamientos ni salidas de herramientas. Lo que escribas ahí continúa
esa misma sesión (`claude --resume <id>`) con las aprobaciones del notch. El aviso y la notificación abren esa
pestaña. El aviso de inactividad de Claude Code («Claude is waiting for your input», que llega un rato después de
terminar) se ignora: ya avisó el `Stop`. `UserPromptSubmit` (una vez por mensaje, no por herramienta) hace que aparezca en cuanto empiezas. Las
sesiones que lanza el propio notch no cuentan (llevan `COUCOU_AGENT_ID`).

**Codex** avisa con su opción `notify` (~/.codex/config.toml → `AgentManagerNotch.exe --codex-event <json>` al
terminar cada turno). Su JSON no trae la transcripción, así que la pestaña (de un agente de código de Codex, o de
**codi-codex**) va sumando lo pedido y la respuesta de cada turno, y guarda el `thread-id` para continuar la sesión.
Codex solo admite un `notify`: si ya lo usa otra aplicación (p. ej. la app de Codex), el notch lo toma, guarda el
anterior en `%APPDATA%\AgentManagerNotch\codex-notify-anterior.txt` y le **reenvía cada aviso**, así que esa app
sigue funcionando; al quitarlo se restaura. Se activa en el instalador o en ⚙ Configuración → *Avisos de mis
sesiones de Codex*. Gemini no tiene un mecanismo equivalente.

## Estructura

```
App.cs                     Main, arranque, coordinación de notificaciones/recordatorios/canal de control
Controls/MochiView.cs      el personaje y todas sus animaciones
Providers/Providers.cs     Claude Code, Codex, Gemini, Personalizado → eventos normalizados
Services/AgentEntry.cs     agente en la interfaz: pestañas, estado agregado y líneas de detalle
Services/AgentSession.cs   una conversación (agente o pestaña de workspace): cola de tareas, pasos [[tasks]], proceso del CLI, aprobaciones, TimeParser
Services/Scheduler.cs      ejecuta los agentes programados según su horario
Services/UpdateService.cs  busca versiones nuevas (carpeta versiones/ de la rama, cada 2 h) y actualiza
Services/Approvals.cs      servidor de named pipe + modo --hook
Services/ControlChannel.cs --notify / --ask / --claude-event e integración opcional con ~/.claude
Services/Notifications.cs  bandeja del sistema, toasts, sonidos, inicio con Windows, servicio de recordatorios
Services/CliResolver.cs    búsqueda en el PATH y resolución de shims de npm
Services/CliSetup.cs       instalador: detecta e instala Claude Code y Codex y conecta sus avisos (CodexNotify)
Services/ClaudeTranscript.cs lee la sesión de Claude Code de la terminal (JSONL) para mostrarla en una pestaña
Services/Installer.cs      instalar/desinstalar por usuario (accesos, registro, inicio con Windows)
Views/InstallerWindow.*    ventana del instalador
Views/NotchWindow.*        el notch: píldora, aviso, vista general (tarjeta + chips) y chat con pestañas
Views/AgentEditorWindow.*  crear/editar agentes con vista previa animada
Views/RemindersWindow.*    recordatorios y tareas programadas
Views/ReleaseNotesWindow.* notas de la versión: una pestaña por versión, con su GIF (Controls/GifPlayer.cs)
Services/ReleaseNotes.cs   lee las notas y GIF de versiones/ incrustados en el ejecutable
Services/GifWriter.cs      graba en GIF un elemento de la interfaz (RenderTargetBitmap, sin capturar la pantalla)
Views/GifDemos.cs          vistas de --grabar-gif para los GIF de las versiones
Views/UpdateCard.*         tarjeta de versión nueva (en el notch y en los GIF)
Views/Markdown.cs          Markdown ligero en las burbujas (seleccionable, tablas, bloques de código con copiar)
```

## Notas

- El panel no roba el foco al aparecer por hover; solo al hacer clic o usar el atajo.
- Gemini CLI se implementó según su formato `stream-json` documentado, pero no estaba instalado en este
  equipo, así que no se probó de punta a punta.
- Registro de errores: `%APPDATA%\AgentManagerNotch\coucou.log`.
