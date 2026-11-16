using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AgentManagerNotch.Services;

namespace AgentManagerNotch.Plugins.Archivos
{
    /// <summary>
    /// Archivos del equipo para los agentes sin herramientas propias (el LLM de un servidor no ve el disco). El agente
    /// escribe bloques en su respuesta ([[leer: ruta]], [[listar: ruta]], [[buscar: patrón]], [[buscar_texto: ruta | texto]],
    /// [[abrir: ruta]], [[escribir: ruta]]…[[/escribir]], [[editar: ruta]]…[[/editar]]; Excel y Word, con
    /// <see cref="OfficeFiles"/>); el puente del proveedor los oculta del chat, los ejecuta aquí y le devuelve el resultado
    /// en el siguiente mensaje. Escribir, editar y abrir piden siempre permiso en el notch, sea cual sea el modo del
    /// agente («Solo lectura» no deja modificar nada); leer fuera de la carpeta de trabajo también, salvo en «Editar
    /// archivos solo» y «Automático».
    /// ponytail: protocolo de texto porque el ruteador no admite herramientas del cliente; pasar a tool-calls reales
    /// si el servidor llega a ofrecerlas.
    /// </summary>
    public static class LocalFiles
    {
        // ponytail: tope fijo por lectura; paginar (desde/hasta) si hacen falta archivos más grandes
        private const int MaxChars = 100_000, MaxEntries = 300;
        // Documentos que solo sabe leer el servidor (Excel y Word se leen aquí, ver OfficeFiles): «leer» los sube como adjuntos del siguiente mensaje (límites del ruteador)
        private static readonly string[] Uploadable = { ".pdf", ".pptx", ".xls", ".ods", ".png", ".jpg", ".jpeg", ".gif", ".webp" };
        private const int MaxUploads = 5;
        private const long MaxUploadBytes = 10L * 1024 * 1024;

        private static readonly Regex BlockRx = new(
            @"\[\[\s*(?<op>leer|listar|buscar_texto|buscar|abrir)\s*:\s*(?<path>[^\]\r\n]+?)\s*\]\]" +
            @"|\[\[\s*(?<op>escribir|editar)\s*:\s*(?<path>[^\]\r\n]+?)\s*\]\]\r?\n?(?<body>.*?)\r?\n?\[\[/\s*(?:escribir|editar)\s*\]\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OpenRx = new(@"\G\[\[\s*(?:leer|listar|buscar_texto|buscar|abrir|escribir|editar)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PairRx = new(@"<{7}[^\n]*\n(?<old>.+?)\n={7}\n(?<new>.*?)\n?>{7}", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>Instrucciones que se añaden al system prompt del agente.</summary>
        public static string Prompt(bool canEdit, string workDir)
        {
            string Dir(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
            var sb = new StringBuilder("\n\n## Archivos del equipo del usuario\n");
            sb.Append(canEdit ? "Puedes leer y modificar" : "Puedes leer (no modificar)");
            sb.Append(" archivos del equipo del usuario escribiendo estos bloques en tu respuesta. Agent Manager Notch " +
                      "los ejecuta, los oculta del chat y te devuelve el resultado en el siguiente mensaje:\n" +
                      "[[leer: C:\\ruta\\archivo.txt]]\n[[listar: C:\\ruta\\carpeta]]\n" +
                      "[[buscar: C:\\ruta\\**\\*.xlsx]]   (archivos por nombre: * y ? son comodines y ** recorre las subcarpetas)\n" +
                      "[[buscar_texto: C:\\ruta\\carpeta | texto]]   (líneas de los archivos de texto que contienen ese texto)\n" +
                      "[[abrir: C:\\ruta\\reporte.html]]   (abre un documento con su programa; el usuario lo aprueba)\n");
            if (canEdit)
                sb.Append("[[escribir: C:\\ruta\\archivo.txt]]\ncontenido completo del archivo\n[[/escribir]]\n" +
                          "[[editar: C:\\ruta\\archivo.txt]]\n<<<<<<< BUSCAR\ntexto exacto que ya está en el archivo\n=======\n" +
                          "texto que lo reemplaza\n>>>>>>> REEMPLAZAR\n[[/editar]]\n");
            sb.Append("Reglas: usa rutas absolutas; después de escribir bloques termina tu respuesta y espera el resultado; " +
                      "nunca inventes el contenido de un archivo que no has leído; no menciones los bloques al usuario. " +
                      "Fuera de tu carpeta de trabajo el usuario tiene que permitir cada lectura: si la deniega, no la reintentes. " +
                      "«leer» sirve para archivos de texto, Excel (.xlsx) y Word (.docx), que se leen en el equipo sin subirlos: " +
                      "de un Excel devuelve cada hoja como CSV con el número de fila delante («12| a,b,c»; la primera columna es la A) " +
                      "y de un Word, sus párrafos. También lee PDF, PowerPoint (.pptx), .xls e imágenes (hasta 5 por respuesta, " +
                      "10 MB cada uno): esos te llegan como adjuntos del siguiente mensaje.");
            if (canEdit)
                sb.Append(" Lee un archivo antes de editarlo. «editar» es para cambios puntuales (el texto buscado debe aparecer " +
                          "una sola vez; un bloque admite varios pares BUSCAR/REEMPLAZAR) y «escribir» para archivos nuevos o " +
                          "reescribirlos enteros. El usuario aprueba cada cambio antes de que se aplique: si lo rechaza, no lo " +
                          "reintentes y pregúntale qué prefiere. No le pidas confirmación por el chat antes de un cambio: " +
                          "escribe el bloque y el notch se la pide.\n" +
                          "Excel (.xlsx): para cambiar celdas, una por línea (texto, número o fórmula en inglés; vacío borra la celda; " +
                          "se conservan el formato y el resto del libro):\n" +
                          "[[editar: C:\\ruta\\libro.xlsx]]\nHoja1!B2 = 150\nHoja1!C2 = =SUM(A2:B2)\nHoja1!D2 = pendiente\n[[/editar]]\n" +
                          "«escribir» en un .xlsx crea un libro nuevo: secciones «## Hoja: Nombre», cada una seguida de su CSV.\n" +
                          "Word (.docx): «editar» con pares BUSCAR/REEMPLAZAR, cada texto buscado dentro de un solo párrafo; «escribir» " +
                          "crea un documento nuevo (un párrafo por línea; «# », «## » y «### » son títulos).\n" +
                          "«escribir» sobre un Excel o Word que ya existe lo reemplaza entero y pierde su formato: para modificarlo usa " +
                          "«editar». No puedes crear ni modificar PDF, PowerPoint ni .xls.\n" +
                          "Gráficas de Excel: una línea «grafica: tipo | título | Hoja1!A1:C8 | E2» dentro de «editar» (o dentro de una " +
                          "hoja al «escribir», sin el nombre de la hoja). tipo: columnas, barras, lineas o pastel. El rango lleva los " +
                          "encabezados en la primera fila, las categorías en la primera columna y una serie por cada columna siguiente; " +
                          "la última celda (opcional) es donde se coloca.\n" +
                          "Reportes: cuando el usuario pida un reporte, informe o dashboard sin decir el formato, crea un dashboard en un " +
                          "único archivo .html autocontenido (CSS y JavaScript en línea, sin cargar nada de internet; gráficas en SVG en " +
                          "línea), con indicadores clave, gráficas y tablas con los datos reales que hayas leído. Antes de crearlo, si el usuario " +
                          "no dijo en qué carpeta guardarlo, pregúntaselo (sugiere Descargas) y espera su respuesta; guárdalo allí " +
                          "con un nombre descriptivo, ábrelo con [[abrir]] y dile dónde quedó. Si lo pide en Excel, crea el .xlsx con " +
                          "los datos y sus gráficas, preguntando la carpeta igual.");
            else
                sb.Append(" Estás en modo «Solo lectura»: si el usuario te pide crear o modificar un archivo, dile que cambie " +
                          "los permisos de este agente a «Preguntar en el notch antes de cambiar algo» (✎ editar agente → Permisos).");
            sb.Append($"\nCarpetas: personal {Dir(Environment.SpecialFolder.UserProfile)} · Escritorio {Dir(Environment.SpecialFolder.Desktop)}" +
                      $" · Descargas {Path.Combine(Dir(Environment.SpecialFolder.UserProfile), "Downloads")}" +
                      $" · Documentos {Dir(Environment.SpecialFolder.MyDocuments)} · de trabajo {workDir}");
            return sb.ToString();
        }

        /// <summary>
        /// Texto nuevo que se puede mostrar en el chat a partir de <paramref name="shown"/>: los bloques completos se
        /// saltan y uno a medio escribir se retiene hasta que se cierra (con <paramref name="flush"/>, al acabar la
        /// respuesta, sale lo que quede).
        /// </summary>
        public static string Visible(string raw, ref int shown, bool flush)
        {
            var sb = new StringBuilder();
            while (shown < raw.Length)
            {
                int idx = raw.IndexOf("[[", shown, StringComparison.Ordinal);
                if (idx < 0)
                {
                    // Un «[» final puede ser el comienzo de un bloque
                    int end = !flush && raw[^1] == '[' ? raw.Length - 1 : raw.Length;
                    sb.Append(raw, shown, end - shown);
                    shown = end;
                    break;
                }
                sb.Append(raw, shown, idx - shown);
                shown = idx;
                var m = BlockRx.Match(raw, idx);
                if (m.Success && m.Index == idx) { shown = idx + m.Length; continue; }
                if (!flush && (OpenRx.IsMatch(raw, idx) || raw.Length - idx < 20)) break; // todavía no se sabe
                sb.Append("[[");
                shown = idx + 2;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Ejecuta los bloques de la respuesta y devuelve el mensaje con los resultados para el agente (null si no
        /// hay ninguno). <paramref name="onTool"/> recibe cada acción (herramienta, ruta) para el panel de acciones y
        /// <paramref name="uploads"/>, los documentos leídos que hay que subir con ese mensaje (nombre, base64).
        /// </summary>
        /// <summary>Comienzo del mensaje de resultados (para reconocerlo al releer una conversación guardada).</summary>
        public const string ResultsPrefix = "[Resultados de Agent Manager Notch";

        public static string? Run(string raw, Action<string, string> onTool, List<(string Name, string Data)>? uploads = null)
        {
            uploads ??= new();
            var blocks = BlockRx.Matches(raw);
            if (blocks.Count == 0) return null;
            var sb = new StringBuilder(ResultsPrefix + ": los generó el equipo del usuario, no los escribió él]\n");
            foreach (Match m in blocks)
            {
                var op = m.Groups["op"].Value.ToLowerInvariant();
                var path = m.Groups["path"].Value.Trim().Trim('"');
                string result;
                try
                {
                    // «buscar_texto: ruta | texto»; en «buscar», la ruta lleva comodines y lo que cuenta es la carpeta de partida
                    var arg = "";
                    if (op == "buscar_texto" && path.IndexOf('|') is >= 0 and var bar) { arg = path[(bar + 1)..].Trim(); path = path[..bar].Trim().Trim('"'); }
                    path = Path.GetFullPath(path);
                    var tool = op switch { "leer" => "Read", "listar" => "LS", "buscar" => "Glob", "buscar_texto" => "Grep", "abrir" => "Open", "escribir" => "Write", _ => "Edit" };
                    onTool(tool, path);
                    var scope = op == "buscar" ? GlobRoot(path) : path;
                    bool folder = op is "listar" or "buscar" || op == "buscar_texto" && Directory.Exists(scope);
                    var real = Real(scope);
                    // Los datos del notch (sesión, configuración, carpetas autorizadas) no se tocan desde un agente
                    if (Inside(real, ConfigStore.Root) && !Inside(real, Path.Combine(ConfigStore.Root, "workspace")))
                        result = "Error: la carpeta de datos de Agent Manager Notch no está permitida.";
                    // Leer fuera de la carpeta de trabajo también pide permiso: lo leído viaja al servidor
                    else if (op is "leer" or "listar" or "buscar" or "buscar_texto" && !Inside(real + (folder ? "\\x" : ""), Environment.CurrentDirectory)
                             && !Ask(tool, path + (arg != "" ? "\n" + arg : ""), folder ? scope + "\\" : scope))
                        result = "Denegado: el usuario no permitió leer esa ruta. No lo reintentes; pregúntale qué prefiere hacer.";
                    else result = op switch
                    {
                        "leer" => Read(path, uploads),
                        "listar" => List(path),
                        "buscar" => Find(path),
                        "buscar_texto" => Grep(path, arg),
                        "abrir" => OpenDocument(path),
                        _ => Change(op == "escribir", path, m.Groups["body"].Value)
                    };
                }
                catch (Exception ex) { result = "Error: " + ex.Message; }
                sb.Append($"\n### {op}: {path}\n{result}\n");
            }
            return sb.ToString();
        }

        private static bool Inside(string path, string folder) => HookBridge.IsInGrantedFolder(path, new[] { Path.GetFullPath(folder) });

        /// <summary>La ruta real si es un enlace simbólico o una unión (para que un enlace no se salte las comprobaciones).</summary>
        // ponytail: solo resuelve el último tramo; un enlace en una carpeta intermedia de la carpeta de trabajo no se detecta
        private static string Real(string path)
        {
            try { return (Directory.Exists(path) ? Directory.ResolveLinkTarget(path, true) : File.ResolveLinkTarget(path, true))?.FullName ?? path; }
            catch { return path; }
        }

        /// <summary>
        /// Permiso del usuario en el notch (que deja pasar solo las carpetas que él autorizó). «Editar archivos solo»
        /// y «Automático» no preguntan al leer; sin notch al que preguntar, no se permite.
        /// </summary>
        /// <remarks>Los cambios (<paramref name="always"/>) preguntan en cualquier modo; las lecturas, no en «Editar archivos solo» ni «Automático».</remarks>
        private static bool Ask(string tool, string detail, string path, bool always = false)
        {
            if (!always && (Environment.GetEnvironmentVariable("COUCOU_APPROVAL") ?? "Ask") is "AcceptEdits" or "Auto") return true;
            var pipe = Environment.GetEnvironmentVariable("COUCOU_PIPE");
            var agentId = Environment.GetEnvironmentVariable("COUCOU_AGENT_ID") ?? "";
            return !string.IsNullOrEmpty(pipe) && HookBridge.AskUser(pipe, agentId, tool, detail, path) == true;
        }

        private static string Read(string path, List<(string Name, string Data)> uploads)
        {
            if (!File.Exists(path))
                return Directory.Exists(path) ? "Error: es una carpeta; usa [[listar: …]]." : "Error: el archivo no existe.";
            if (Uploadable.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                if (uploads.Count >= MaxUploads) return $"Error: solo se pueden leer {MaxUploads} documentos por respuesta; pide este en la siguiente.";
                if (new FileInfo(path).Length > MaxUploadBytes) return "Error: pesa más de 10 MB, el máximo por documento.";
                uploads.Add((Path.GetFileName(path), Convert.ToBase64String(File.ReadAllBytes(path))));
                return "Va adjunto a este mensaje con el nombre «" + Path.GetFileName(path) + "».";
            }
            if (OfficeFiles.IsExcel(path) || OfficeFiles.IsWord(path))
            {
                var doc = OfficeFiles.IsExcel(path) ? OfficeFiles.ReadXlsx(path) : OfficeFiles.ReadDocx(path);
                return "```\n" + Trim(doc, MaxChars) + "\n```" + (doc.Length > MaxChars ? $"\n(Truncado: solo los primeros {MaxChars} caracteres.)" : "");
            }
            using var sr = new StreamReader(path, new UTF8Encoding(false), true);
            var buf = new char[MaxChars];
            var text = new string(buf, 0, sr.ReadBlock(buf, 0, MaxChars));
            if (text.Contains('\0'))
                return "Error: formato no admitido (solo texto, Excel, Word, PDF, .pptx e imágenes).";
            return "```\n" + text + "\n```" + (sr.Peek() >= 0 ? $"\n(Truncado: solo los primeros {MaxChars} caracteres.)" : "");
        }

        private static string List(string path)
        {
            if (!Directory.Exists(path)) return "Error: la carpeta no existe.";
            var entries = new DirectoryInfo(path).EnumerateFileSystemInfos().Take(MaxEntries + 1)
                .Select(e => e is DirectoryInfo ? e.Name + "\\" : e.Name).ToList();
            if (entries.Count == 0) return "(vacía)";
            return string.Join("\n", entries.Take(MaxEntries)) + (entries.Count > MaxEntries ? $"\n(Truncado: solo las primeras {MaxEntries} entradas.)" : "");
        }

        /// <summary>La carpeta de partida de un patrón con comodines (hasta el primer tramo que lleva * o ?).</summary>
        private static string GlobRoot(string pattern)
        {
            var parts = pattern.Split('\\', '/');
            int w = Array.FindIndex(parts, p => p.Contains('*') || p.Contains('?'));
            var root = string.Join("\\", parts.Take(w < 0 ? parts.Length - 1 : w));
            return root.EndsWith(':') ? root + "\\" : root;
        }

        /// <summary>Archivos y carpetas cuyo nombre encaja con el patrón (* y ? dentro de un tramo; ** recorre subcarpetas).</summary>
        private static string Find(string pattern)
        {
            var root = GlobRoot(pattern);
            if (!Directory.Exists(root)) return "Error: la carpeta no existe.";
            var rest = pattern[root.Length..].TrimStart('\\', '/');
            var rx = new Regex("^" + Regex.Escape(rest.Replace('/', '\\')).Replace(@"\*\*\\", @"(.*\\)?").Replace(@"\*\*", ".*").Replace(@"\*", @"[^\\]*").Replace(@"\?", @"[^\\]") + "$",
                RegexOptions.IgnoreCase);
            // ponytail: recorre como mucho 200 000 entradas; un índice si hay que buscar en discos enteros
            var options = new EnumerationOptions { RecurseSubdirectories = rest.Contains('\\') || rest.Contains('/'), IgnoreInaccessible = true };
            var found = Directory.EnumerateFileSystemEntries(root, "*", options).Take(200_000)
                .Where(f => rx.IsMatch(Path.GetRelativePath(root, f))).Take(MaxEntries + 1).ToList();
            if (found.Count == 0) return "(sin resultados)";
            return string.Join("\n", found.Take(MaxEntries)) + (found.Count > MaxEntries ? $"\n(Truncado: solo los primeros {MaxEntries} resultados.)" : "");
        }

        /// <summary>Líneas que contienen el texto (sin distinguir mayúsculas) en un archivo o en los archivos de texto de una carpeta.</summary>
        private static string Grep(string path, string text)
        {
            if (text == "") return "Error: falta el texto. Formato: [[buscar_texto: C:\\carpeta | texto]]";
            var files = File.Exists(path) ? new[] { path }
                : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(20_000)
                : null;
            if (files == null) return "Error: la ruta no existe.";
            var sb = new StringBuilder();
            int n = 0;
            foreach (var f in files)
            {
                try
                {
                    if (new FileInfo(f).Length > 2_000_000) continue;
                    int ln = 0;
                    foreach (var line in File.ReadLines(f))
                    {
                        ln++;
                        if (line.Contains('\0')) break; // binario
                        if (!line.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                        sb.Append($"{f}:{ln}: {Trim(line.Trim(), 200)}\n");
                        if (++n >= 100) return sb + "(Truncado: solo las primeras 100 coincidencias.)";
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return n == 0 ? "(sin resultados)" : sb.ToString().TrimEnd();
        }

        // Solo documentos: abrir un programa o un script sería ejecutarlo
        private static readonly string[] Openable = { ".html", ".htm", ".xlsx", ".xlsm", ".xls", ".docx", ".pptx", ".pdf", ".txt", ".md", ".csv", ".json", ".png", ".jpg", ".jpeg", ".gif", ".webp" };

        /// <summary>Abre un documento con su programa predeterminado, con el permiso del usuario.</summary>
        private static string OpenDocument(string path)
        {
            if (!File.Exists(path)) return "Error: el archivo no existe.";
            if (!Openable.Contains(Path.GetExtension(path).ToLowerInvariant()))
                return "Error: solo se abren documentos (html, Excel, Word, PowerPoint, PDF, texto, imágenes), no programas ni scripts.";
            if (!Ask("Open", path, path, always: true))
                return "Denegado: el usuario no quiso abrirlo. No lo reintentes; dile dónde está el archivo.";
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return "Abierto.";
        }

        /// <summary>Escribe el archivo entero o aplica los cambios de «editar», con el permiso del usuario.</summary>
        private static string Change(bool write, string path, string body)
        {
            var mode = Environment.GetEnvironmentVariable("COUCOU_APPROVAL") ?? "Ask";
            if (mode == "ReadOnly")
                return "Error: este agente está en modo «Solo lectura» y no puede modificar archivos. Dile al usuario que cambie sus permisos en el editor del agente.";
            if (Directory.Exists(path)) return "Error: es una carpeta.";

            var ext = Path.GetExtension(path).ToLowerInvariant();
            bool excel = OfficeFiles.IsExcel(path), word = OfficeFiles.IsWord(path);
            if (Uploadable.Contains(ext) || ext is ".doc" or ".ppt" || write && ext == ".xlsm")
                return "Error: ese formato no se puede crear ni modificar (solo archivos de texto, .xlsx y .docx).";
            if (!write && !File.Exists(path)) return "Error: el archivo no existe; usa [[escribir: …]] para crearlo.";

            List<(string Old, string New)>? pairs = null;
            if (!write && !excel)
            {
                pairs = PairRx.Matches(body.Replace("\r\n", "\n")).Select(p => (p.Groups["old"].Value, p.Groups["new"].Value)).ToList();
                if (pairs.Count == 0)
                    return "Error: formato de «editar» no válido. Usa <<<<<<< BUSCAR, =======, >>>>>>> REEMPLAZAR, cada uno en su propia línea.";
            }
            var detail = path + (write
                ? (File.Exists(path) && (excel || word) ? "\n(reemplaza el documento entero)" : "") + "\n" + Trim(body, 400)
                : string.Concat(pairs?.Select(p => $"\n- {Trim(p.Old)}\n+ {Trim(p.New)}") ?? Array.Empty<string>()));

            Action save;
            if (excel || word)
            {
                // Excel y Word: se cambia el documento en memoria (los errores salen como excepción) y se guarda tras el permiso
                var data = write ? (excel ? OfficeFiles.NewXlsx(body) : OfficeFiles.NewDocx(body))
                         : excel ? OfficeFiles.EditXlsx(path, body, out detail)
                         : OfficeFiles.EditDocx(path, pairs!);
                save = () => File.WriteAllBytes(path, data);
            }
            else
            {
                // ponytail: sin BOM se asume UTF-8; un archivo ANSI con acentos se dañaría al editarlo (detectar la codificación si pasa)
                Encoding enc = new UTF8Encoding(false);
                string content;
                if (write) content = body.EndsWith('\n') || body == "" ? body : body + "\n";
                else
                {
                    string original;
                    using (var sr = new StreamReader(path, enc, true)) { original = sr.ReadToEnd(); enc = sr.CurrentEncoding; }
                    bool crlf = original.Contains("\r\n");
                    content = original.Replace("\r\n", "\n");
                    foreach (var (old, neu) in pairs!)
                    {
                        int at = content.IndexOf(old, StringComparison.Ordinal);
                        if (at < 0) return "Error: no se encontró el texto buscado (debe coincidir exactamente; vuelve a leer el archivo). No se cambió nada.";
                        if (content.IndexOf(old, at + 1, StringComparison.Ordinal) >= 0)
                            return "Error: el texto buscado aparece más de una vez; incluye más líneas de contexto. No se cambió nada.";
                        content = content[..at] + neu + content[(at + old.Length)..];
                    }
                    if (crlf) content = content.Replace("\n", "\r\n");
                }
                save = () => File.WriteAllText(path, content, enc);
            }

            if (!Ask(write ? "Write" : "Edit", detail, path, always: true))
                return "Denegado: el usuario rechazó este cambio. No lo reintentes; pregúntale qué prefiere hacer.";
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            save();
            return !write && excel ? "Hecho. Las fórmulas se recalculan cuando el usuario abra el libro en Excel (hasta entonces se leen como fórmula, sin resultado)." : "Hecho.";
        }

        private static string Trim(string s, int max = 300) => s.Length > max ? s[..max] + "…" : s;

        /// <summary>Comprobación rápida (<c>AgentManagerNotch.exe --probar-archivos</c>): 0 si todo va bien.</summary>
        public static int SelfCheck()
        {
            var dir = Path.Combine(Path.GetTempPath(), "amn-archivos-" + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(dir, "a.txt");
            void Must(bool ok, string what) { if (!ok) throw new InvalidOperationException(what); }
            try
            {
                // Los cambios preguntan siempre: en la prueba responde un notch de mentira que lo permite todo
                var server = new ApprovalServer { Handler = _ => System.Threading.Tasks.Task.FromResult(ApprovalDecision.Allow) };
                server.Start();
                Environment.SetEnvironmentVariable("COUCOU_PIPE", server.PipeName);
                Environment.SetEnvironmentVariable("COUCOU_AGENT_ID", "prueba");
                Environment.SetEnvironmentVariable("COUCOU_APPROVAL", "Auto");
                Run($"[[escribir: {file}]]\nuno\r\ndos\n[[/escribir]]", (_, _) => { });
                Must(File.ReadAllText(file) == "uno\r\ndos\n", "escribir");
                var res = Run($"Hola [[editar: {file}]]\n<<<<<<< BUSCAR\ndos\n=======\ntres\n>>>>>>> REEMPLAZAR\n[[/editar]] [[leer: {file}]] [[listar: {dir}]]", (_, _) => { });
                Must(File.ReadAllText(file) == "uno\r\ntres\r\n", "editar conserva los saltos de línea");
                Must(res!.Contains("tres") && res.Contains("a.txt"), "leer y listar");
                Must(Run($"[[editar: {file}]]\n<<<<<<< BUSCAR\nno está\n=======\nx\n>>>>>>> REEMPLAZAR\n[[/editar]]", (_, _) => { })!.Contains("no se encontró"), "editar sin coincidencia");

                // Sin notch al que preguntar («Ask») y en solo lectura no se cambia nada
                foreach (var mode in new[] { "Ask", "Auto", "ReadOnly" })
                {
                    Environment.SetEnvironmentVariable("COUCOU_APPROVAL", mode);
                    Environment.SetEnvironmentVariable("COUCOU_PIPE", null);
                    Run($"[[escribir: {file}]]\nx\n[[/escribir]]", (_, _) => { });
                    Must(File.ReadAllText(file) == "uno\r\ntres\r\n", "sin permiso no se escribe (" + mode + ")");
                    if (mode != "Auto") Must(Run($"[[leer: {file}]]", (_, _) => { })!.Contains("Denegado"), "sin permiso no se lee fuera de la carpeta de trabajo (" + mode + ")");
                    Must(Run($"[[leer: {Path.Combine(ConfigStore.Root, "config.json")}]]", (_, _) => { })!.Contains("no está permitida"), "datos del notch");
                }

                // Filtro del streaming: los bloques no se ven aunque lleguen a trozos; el resto sí
                var raw = "Voy [[tasks]]x[[/tasks]] a leer.\n[[leer: C:\\a.txt]]\n[[escribir: C:\\b.txt]]\nhola\n[[/escribir]] Listo [";
                var shown = 0; var seen = new StringBuilder();
                for (int i = 1; i <= raw.Length; i++) seen.Append(Visible(raw[..i], ref shown, i == raw.Length));
                Must(seen.ToString() == "Voy [[tasks]]x[[/tasks]] a leer.\n\n Listo [", "filtro: " + seen);
                Must(Run("sin bloques", (_, _) => { }) == null, "sin bloques");

                // Un PDF (o .pptx, imagen…) no se lee como texto: se sube con el siguiente mensaje
                Environment.SetEnvironmentVariable("COUCOU_APPROVAL", "Auto");
                Environment.SetEnvironmentVariable("COUCOU_PIPE", server.PipeName);
                var pdf = Path.Combine(dir, "b.pdf");
                File.WriteAllBytes(pdf, new byte[] { 1, 2, 3 });
                var ups = new List<(string Name, string Data)>();
                Run($"[[leer: {pdf}]]", (_, _) => { }, ups);
                Must(ups.Count == 1 && ups[0] == ("b.pdf", "AQID"), "documento como adjunto");

                // Excel y Word en el equipo: crear, cambiar y volver a leer (--probar-archivos <carpeta> deja ahí los archivos)
                var keep = Environment.GetCommandLineArgs().Skip(2).FirstOrDefault();
                var xlsx = Path.Combine(keep ?? dir, "c.xlsx");
                var docx = Path.Combine(keep ?? dir, "d.docx");
                string Do(string block) => Run(block, (_, _) => { }, ups)!;
                Do($"[[escribir: {xlsx}]]\n## Hoja: Ventas\nProducto,Cantidad,Código\n\"Café, molido\",3,007\nTé,4.5,\n## Hoja: Otra\nx\n[[/escribir]]");
                res = Do($"[[leer: {xlsx}]]");
                Must(res.Contains("## Hoja: Ventas\n1| Producto,Cantidad,Código\n2| \"Café, molido\",3,007\n3| Té,4.5") && res.Contains("## Hoja: Otra\n1| x"), "leer xlsx: " + res);
                res = Do($"[[editar: {xlsx}]]\nVentas!B2 = 10\nVentas!B4 = =SUM(B2:B3)\nVentas!A3 =\nOtra!C5 = nuevo\n[[/editar]]");
                Must(res.Contains("Hecho"), "editar xlsx: " + res);
                res = Do($"[[leer: {xlsx}]]");
                Must(res.Contains("2| \"Café, molido\",10,007\n3| ,4.5\n4| ,=SUM(B2:B3)") && res.Contains("5| ,,nuevo"), "leer xlsx editado: " + res);
                Must(Do($"[[editar: {xlsx}]]\nNoExiste!A1 = 1\n[[/editar]]").Contains("no existe la hoja"), "hoja inexistente");
                Do($"[[escribir: {docx}]]\n# Informe\nEl total es 100 unidades.\nFin.\n[[/escribir]]");
                Must(Do($"[[editar: {docx}]]\n<<<<<<< BUSCAR\n100 unidades\n=======\n250 cajas\n>>>>>>> REEMPLAZAR\n[[/editar]]").Contains("Hecho"), "editar docx");
                res = Do($"[[leer: {docx}]]");
                Must(res.Contains("Informe\nEl total es 250 cajas.\nFin."), "leer docx editado: " + res);
                Must(ups.Count == 1, "Excel y Word no se suben");

                // Buscar por nombre y por contenido
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                File.WriteAllText(Path.Combine(dir, "sub", "nota.txt"), "primera\nLa CLAVE está aquí\n");
                res = Do($"[[buscar: {dir}\\**\\*.txt]]");
                Must(res.Contains("a.txt") && res.Contains("sub\\nota.txt") && !res.Contains("b.pdf"), "buscar: " + res);
                Must(!Do($"[[buscar: {dir}\\*.txt]]").Contains("nota.txt"), "buscar sin ** no baja a subcarpetas");
                Must(Do($"[[buscar_texto: {dir} | clave]]").Contains("nota.txt:2: La CLAVE está aquí"), "buscar_texto");
                Must(Do($"[[abrir: {Path.Combine(dir, "x.exe")}]]").Contains("Error"), "abrir solo documentos");

                // Gráficas de Excel: al crear el libro y sobre uno que ya existe
                var charts = Path.Combine(keep ?? dir, "e.xlsx");
                Do($"[[escribir: {charts}]]\n## Hoja: Datos\nDía,Registros,Errores\nLunes,10,1\nMartes,14,0\nMiércoles,9,2\ngrafica: columnas | Registros por día | A1:C4 | E2\n[[/escribir]]");
                Must(Do($"[[editar: {charts}]]\ngrafica: pastel | Reparto | Datos!A1:B4 | E20\nDatos!A5 = Jueves\n[[/editar]]").Contains("Hecho"), "gráfica en un libro existente");
                using (var zip = System.IO.Compression.ZipFile.OpenRead(charts))
                    Must(zip.GetEntry("xl/charts/chart1.xml") != null && zip.GetEntry("xl/charts/chart2.xml") != null && zip.GetEntry("xl/drawings/drawing2.xml") == null, "las dos gráficas van en el mismo dibujo");
                Must(Do($"[[leer: {charts}]]").Contains("4| Miércoles,9,2\n5| Jueves"), "el libro con gráficas se sigue leyendo");
                Console.WriteLine("ok");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FALLO: " + ex.Message); return 1; }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
        }
    }
}
