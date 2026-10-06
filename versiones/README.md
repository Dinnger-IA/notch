# Versiones

Un archivo por versión, con el nombre `X.Y.Z.md` (p. ej. `0.3.0.md`). La versión de la app es la **mayor** de
esta carpeta: se toma al compilar, y el notch la compara con la de su rama en el repositorio para avisar de que
hay una versión nueva. Un commit sin archivo de versión nuevo no avisa a nadie.

Formato:

```markdown
# 0.3.0 · 2026-10-15

- Novedad o corrección, en una línea, tal como la verá el usuario.
- Otra más.
```

Las líneas que empiezan por `- ` son las novedades que muestra la tarjeta de actualización (sin formato); la
ventana *Notas de la versión* muestra el archivo entero con Markdown.

Cada vez que haya cambios nuevos se crea su versión (o se amplía la más alta si todavía no se ha subido).

## GIF de los cambios visuales

Si la versión cambia algo que se ve, añade `X.Y.Z.gif` junto a su `.md`: la ventana *Notas de la versión* lo
reproduce encima de las novedades. Se graba con el propio ejecutable, que monta la vista en una ventana fuera de
la pantalla y la dibuja (no captura la pantalla ni toca el notch abierto ni los datos del usuario):

```powershell
dotnet build -c Release -o $env:TEMP\amn-gif
& $env:TEMP\amn-gif\AgentManagerNotch.exe --grabar-gif actualizacion=0.2.0 versiones\0.2.0.gif
& $env:TEMP\amn-gif\AgentManagerNotch.exe --grabar-gif notas=0.3.0 versiones\0.3.0.gif
```

| Vista | Qué graba |
|---|---|
| `actualizacion[=X.Y.Z]` | La tarjeta «Versión X.Y.Z disponible» con las novedades de esa versión. |
| `notas[=X.Y.Z]` | La ventana de notas, recorriendo las pestañas desde esa versión (reproduce los GIF que ya haya). |
| `tabla` | Una respuesta del chat con tablas Markdown. |
| `instalador` | La ventana del instalador cambiando sus opciones (no instala nada). |
| `terminal` | El aviso de una sesión de Claude Code de la terminal y esa sesión en una pestaña de codi-claude. |
| `aviso-panel` | El aviso de Claude Code de la terminal flotando sobre el panel abierto. |
| `acciones` | Las 5 últimas acciones de un agente de código: la nueva entra arriba, las demás bajan difuminándose y con scroll se ven las anteriores. |
| `editor-color` | El editor de agentes eligiendo colores de la fila de muestras. |
| `sesion-ocupada` | La sesión de Codex abierta en otro programa: el error y su botón para seguir en una sesión nueva. |
| `git` | El panel de git de un workspace: se preparan cambios, el personaje escribe el commit, commit y push. |

Para un cambio visual que no cubra ninguna vista, añade la suya en `Views/GifDemos.cs`: crea el control o la
ventana con datos de ejemplo, muéstrala con `ShowOffscreenAsync` y grábala con `GifWriter.RecordAsync`, haciendo
en el guion lo que enseñe el cambio. Los fotogramas iguales se funden y cada uno solo guarda la zona que cambió;
intenta que el GIF quede por debajo de ~1,5 MB. Compila antes de grabar la vista `notas` para que incluya los GIF
nuevos, y revisa el resultado antes de subirlo.
