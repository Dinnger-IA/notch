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
reproduce encima de las novedades. Se graba con `grabar-gif.ps1` (sin dependencias):

```powershell
# Una ventana por su título, durante 6 segundos
.\versiones\grabar-gif.ps1 -Ventana "Notas de la versión" -Segundos 6 -Salida versiones\0.3.0.gif

# Una zona de la pantalla (x, y, ancho, alto en píxeles), p. ej. el notch abierto arriba al centro
.\versiones\grabar-gif.ps1 -Region 1100,0,1240,420 -Segundos 6 -Salida versiones\0.3.0.gif

# Haciendo algo mientras graba (clics, teclas, comandos del notch...)
.\versiones\grabar-gif.ps1 -Ventana "Notas de la versión" -Salida versiones\0.3.0.gif -Accion {
    Start-Sleep 1; & .\publish\AgentManagerNotch.exe --release-notes 0.2.0; Start-Sleep 2
}
```

Opciones: `-Fps` (10), `-AnchoMax` (760 px; se reduce si la captura es más ancha). Los fotogramas iguales se
funden en uno, así que las pausas apenas ocupan. Intenta que el GIF quede por debajo de ~1,5 MB.

Para grabar la tarjeta de actualización sin publicar nada, arranca el notch con
`AGENT_MANAGER_NOTCH_SIMULAR_VERSION=9.9.9`: al minuto «encuentra» esa versión y `--show` abre la tarjeta.
