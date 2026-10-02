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

Las líneas que empiezan por `- ` son las novedades que muestra la tarjeta de actualización.
