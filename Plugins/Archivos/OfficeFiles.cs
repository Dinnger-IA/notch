using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentManagerNotch.Plugins.Archivos
{
    /// <summary>
    /// Excel (.xlsx/.xlsm) y Word (.docx) en el equipo, sin subirlos al servidor ni dependencias: son un ZIP con XML.
    /// Leer devuelve texto (cada hoja como CSV con el número de fila; los párrafos del documento), editar cambia
    /// celdas o texto conservando el resto del archivo (formato, fórmulas, imágenes) y escribir crea uno nuevo sin
    /// formato. Los errores para el agente salen como <see cref="InvalidOperationException"/>.
    /// Las gráficas de Excel (columnas, barras, líneas, pastel) se añaden a partir de un rango de la hoja.
    /// ponytail: lo mínimo de cada formato. Excel: sin insertar/borrar filas ni hojas, sin formato de celdas, fechas
    /// con el sistema 1900. Word: sin tablas nuevas ni estilos; encabezados y pies no se leen. Pasar a Open XML SDK
    /// si hace falta más.
    /// </summary>
    public static class OfficeFiles
    {
        private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";
        private const long MaxBytes = 50L * 1024 * 1024;

        public static bool IsExcel(string path) => Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm";
        public static bool IsWord(string path) => Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- paquete (ZIP + XML)
        /// <summary>El archivo en memoria (se puede leer aunque esté abierto en Excel o Word).</summary>
        private static MemoryStream Open(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxBytes) throw new InvalidOperationException("el archivo pesa más de 50 MB.");
            var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        private static XDocument Load(ZipArchive z, string part)
        {
            var entry = z.GetEntry(part) ?? throw new InvalidOperationException($"el archivo no es válido (falta {part}).");
            using var s = entry.Open();
            return XDocument.Load(s, LoadOptions.PreserveWhitespace); // tal cual: los espacios cuentan en Word
        }

        private static XDocument? TryLoad(ZipArchive z, string part) => z.GetEntry(part) == null ? null : Load(z, part);

        private static void Save(ZipArchive z, string part, XDocument doc)
        {
            using var s = (z.GetEntry(part) ?? z.CreateEntry(part)).Open();
            s.SetLength(0);
            doc.Save(s, SaveOptions.DisableFormatting);
        }

        private static XElement Rels(params (string Id, string Type, string Target)[] rels) => new(PR + "Relationships",
            rels.Select(r => new XElement(PR + "Relationship", new XAttribute("Id", r.Id), new XAttribute("Type", R.NamespaceName + "/" + r.Type), new XAttribute("Target", r.Target))));

        /// <summary>Un paquete nuevo: la primera parte es el documento principal; las de tipo vacío son relaciones.</summary>
        private static byte[] Package(params (string Part, string Type, XElement Root)[] parts)
        {
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Update, true))
            {
                Save(z, "[Content_Types].xml", new XDocument(new XElement(CT + "Types",
                    new XElement(CT + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                    new XElement(CT + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                    parts.Where(p => p.Type != "").Select(p => new XElement(CT + "Override", new XAttribute("PartName", "/" + p.Part),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument." + p.Type + "+xml"))))));
                Save(z, "_rels/.rels", new XDocument(Rels(("rId1", "officeDocument", parts[0].Part))));
                foreach (var p in parts) Save(z, p.Part, new XDocument(p.Root));
            }
            return ms.ToArray();
        }

        // ---------------------------------------------------------------- Excel
        private static int Col(string letters) => letters.Aggregate(0, (n, ch) => n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1));
        private static string ColName(int n) { var s = ""; for (; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s; return s; }
        private static string Letters(string cellRef) => new(cellRef.TakeWhile(char.IsLetter).ToArray());

        /// <summary>Las hojas del libro, en orden, con la parte del ZIP donde está cada una.</summary>
        private static List<(string Name, string Part)> Sheets(ZipArchive z)
        {
            var rels = Load(z, "xl/_rels/workbook.xml.rels").Root!.Elements(PR + "Relationship")
                .ToDictionary(r => (string?)r.Attribute("Id") ?? "", r => (string?)r.Attribute("Target") ?? "");
            var list = new List<(string, string)>();
            foreach (var sh in Load(z, "xl/workbook.xml").Descendants(S + "sheet"))
                if (rels.TryGetValue((string?)sh.Attribute(R + "id") ?? "", out var target))
                    list.Add(((string?)sh.Attribute("name") ?? "", target.StartsWith('/') ? target[1..] : "xl/" + target));
            if (list.Count == 0) throw new InvalidOperationException("el libro no tiene hojas.");
            return list;
        }

        private static List<string> SharedStrings(ZipArchive z) =>
            TryLoad(z, "xl/sharedStrings.xml")?.Root!.Elements(S + "si")
                .Select(si => string.Concat(si.Elements(S + "t").Concat(si.Elements(S + "r").Elements(S + "t")).Select(t => t.Value))).ToList() ?? new();

        /// <summary>Índices de estilo de celda (atributo <c>s</c>) cuyo formato de número es de fecha u hora.</summary>
        private static HashSet<int> DateStyles(ZipArchive z)
        {
            var set = new HashSet<int>();
            if (TryLoad(z, "xl/styles.xml")?.Root is not { } root) return set;
            var custom = root.Elements(S + "numFmts").Elements(S + "numFmt")
                .ToDictionary(f => (int?)f.Attribute("numFmtId") ?? -1, f => (string?)f.Attribute("formatCode") ?? "");
            int i = 0;
            foreach (var xf in root.Elements(S + "cellXfs").Elements(S + "xf"))
            {
                int id = (int?)xf.Attribute("numFmtId") ?? 0;
                // Formatos de fecha integrados, o uno propio con d/y/h/s o «mm» fuera de comillas y corchetes
                if (id is >= 14 and <= 22 or >= 45 and <= 47
                    || custom.TryGetValue(id, out var code) && Regex.IsMatch(Regex.Replace(code, @"""[^""]*""|\[[^\]]*\]|\\.", ""), "[dyhs]|mm", RegexOptions.IgnoreCase))
                    set.Add(i);
                i++;
            }
            return set;
        }

        private static bool IsDate(XElement c, HashSet<int> dates) => dates.Contains((int?)c.Attribute("s") ?? -1);

        private static string Value(XElement c, List<string> shared, HashSet<int> dates)
        {
            var v = (string?)c.Element(S + "v") ?? "";
            // Una fórmula sin valor guardado: aún no la ha calculado Excel (ver Recalculate)
            if (v == "" && c.Element(S + "f") is { } f) return "=" + (f.Value != "" ? f.Value : "…");
            switch ((string?)c.Attribute("t"))
            {
                case "s": return int.TryParse(v, out var i) && i >= 0 && i < shared.Count ? shared[i] : "";
                case "inlineStr": return string.Concat(c.Descendants(S + "t").Select(t => t.Value));
                case "b": return v == "1" ? "TRUE" : "FALSE";
                case "str": case "e": return v;
            }
            if (v != "" && IsDate(c, dates) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                try { return DateTime.FromOADate(d).ToString(d < 1 ? "HH:mm:ss" : d % 1 == 0 ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss"); }
                catch (ArgumentException) { }
            return v;
        }

        private static readonly Regex NumberRx = new(@"^-?(0|[1-9]\d{0,14})(\.\d+)?$", RegexOptions.Compiled);

        /// <summary>
        /// Pone el valor en la celda conservando su estilo: «=…» es una fórmula, un número va como número (no los
        /// códigos con ceros delante ni los de más de 15 cifras) y lo demás como texto; vacío la borra.
        /// </summary>
        private static void SetValue(XElement c, string val, bool dateStyle)
        {
            c.RemoveNodes();
            c.Attribute("t")?.Remove();
            if (val == "") return;
            if (val.StartsWith('=') && val.Length > 1) c.Add(new XElement(S + "f", val[1..]));
            else if (NumberRx.IsMatch(val)) c.Add(new XElement(S + "v", val));
            else if (dateStyle && DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                c.Add(new XElement(S + "v", dt.ToOADate().ToString(CultureInfo.InvariantCulture)));
            else
            {
                c.SetAttributeValue("t", "inlineStr");
                c.Add(new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), val)));
            }
        }

        /// <summary>La celda (creando fila y celda en su sitio si no existían).</summary>
        private static XElement Cell(XDocument sheet, string col, int rowN)
        {
            var data = sheet.Root!.Element(S + "sheetData") ?? throw new InvalidOperationException("la hoja no es válida.");
            var row = data.Elements(S + "row").FirstOrDefault(r => (int?)r.Attribute("r") == rowN);
            if (row == null)
            {
                row = new XElement(S + "row", new XAttribute("r", rowN));
                if (data.Elements(S + "row").FirstOrDefault(r => (int?)r.Attribute("r") > rowN) is { } nextRow) nextRow.AddBeforeSelf(row);
                else data.Add(row);
            }
            var cref = col + rowN;
            var c = row.Elements(S + "c").FirstOrDefault(x => (string?)x.Attribute("r") == cref);
            if (c == null)
            {
                c = new XElement(S + "c", new XAttribute("r", cref));
                int ci = Col(col);
                if (row.Elements(S + "c").FirstOrDefault(x => Col(Letters((string?)x.Attribute("r") ?? "")) > ci) is { } next) next.AddBeforeSelf(c);
                else row.Add(c);
            }
            return c;
        }

        private static string CsvField(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        /// <summary>Cada hoja como CSV, con el número de fila delante («12| a,b,c»; la primera columna es la A).</summary>
        public static string ReadXlsx(string path)
        {
            using var z = new ZipArchive(Open(path), ZipArchiveMode.Read);
            var shared = SharedStrings(z);
            var dates = DateStyles(z);
            var sb = new StringBuilder();
            foreach (var (name, part) in Sheets(z))
            {
                sb.Append("## Hoja: ").Append(name).Append('\n');
                int rowN = 0;
                foreach (var row in Load(z, part).Descendants(S + "sheetData").Elements(S + "row"))
                {
                    rowN = (int?)row.Attribute("r") ?? rowN + 1;
                    var cells = new List<string>();
                    foreach (var c in row.Elements(S + "c"))
                    {
                        int ci = (string?)c.Attribute("r") is { } r ? Col(Letters(r)) : cells.Count + 1;
                        if (ci > 16384) continue;
                        while (cells.Count < ci - 1) cells.Add("");
                        cells.Add(Value(c, shared, dates));
                    }
                    if (cells.Any(x => x != "")) sb.Append(rowN).Append("| ").Append(string.Join(",", cells.Select(CsvField))).Append('\n');
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        private static readonly Regex CellRx = new(@"^\s*(?:(?<sheet>[^!=]+)!)?\$?(?<col>[A-Za-z]{1,3})\$?(?<row>[1-9]\d{0,6})\s*=\s?(?<val>.*)$", RegexOptions.Compiled);

        /// <summary>Cambia celdas («Hoja1!B2 = valor», una por línea) y devuelve el libro; <paramref name="detail"/> es el resumen para el permiso.</summary>
        public static byte[] EditXlsx(string path, string body, out string detail)
        {
            var ms = Open(path);
            var sb = new StringBuilder(path);
            using (var z = new ZipArchive(ms, ZipArchiveMode.Update, true))
            {
                var sheets = Sheets(z);
                var shared = SharedStrings(z);
                var dates = DateStyles(z);
                var docs = new Dictionary<string, XDocument>();
                var charts = new List<((string Name, string Part) Sheet, Match Line)>();
                foreach (var line in body.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim() != ""))
                {
                    var chart = ChartRx.Match(line);
                    var m = chart.Success ? chart : CellRx.Match(line);
                    if (!m.Success)
                        throw new InvalidOperationException($"línea no válida «{line}». Formato: Hoja1!B2 = valor (una celda por línea) o grafica: tipo | título | Hoja1!A1:C8 | E2. No se cambió nada.");
                    var name = m.Groups["sheet"].Value.Trim().Trim('\'');
                    var sheet = name == "" ? sheets[0] : sheets.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (sheet.Part == null)
                        throw new InvalidOperationException($"no existe la hoja «{name}». Hojas: {string.Join(", ", sheets.Select(s => s.Name))}. No se cambió nada.");
                    if (chart.Success) { charts.Add((sheet, chart)); continue; }
                    if (!docs.TryGetValue(sheet.Part, out var doc)) docs[sheet.Part] = doc = Load(z, sheet.Part);
                    var col = m.Groups["col"].Value.ToUpperInvariant();
                    var c = Cell(doc, col, int.Parse(m.Groups["row"].Value));
                    var val = m.Groups["val"].Value.Trim();
                    sb.Append($"\n{sheet.Name}!{col}{m.Groups["row"].Value}: {Short(Value(c, shared, dates))} → {Short(val)}");
                    // ponytail: si la celda era la base de una fórmula compartida, las que dependían de ella la pierden
                    SetValue(c, val, IsDate(c, dates));
                }
                if (docs.Count == 0 && charts.Count == 0) throw new InvalidOperationException("no hay celdas que cambiar. Formato: Hoja1!B2 = valor (una celda por línea).");
                foreach (var (part, doc) in docs) Save(z, part, doc);
                // Las gráficas, después de guardar las celdas (también tocan la hoja)
                foreach (var (sheet, line) in charts) sb.Append('\n').Append(AddChart(z, sheet, line));
                Recalculate(z, sheets);
            }
            detail = sb.ToString();
            return ms.ToArray();
        }

        /// <summary>
        /// Excel recalcula todo al abrir (los valores guardados de las fórmulas ya no valen: se quitan) y se quita la cadena de
        /// cálculo, que puede apuntar a celdas que dejaron de tener fórmula (Excel daría el libro por dañado).
        /// </summary>
        private static void Recalculate(ZipArchive z, List<(string Name, string Part)> sheets)
        {
            // Sin los valores guardados, al volver a leer el libro se ve la fórmula y no un resultado viejo
            foreach (var (_, part) in sheets)
            {
                var doc = Load(z, part);
                var formulas = doc.Descendants(S + "c").Where(c => c.Element(S + "f") != null && c.Element(S + "v") != null).ToList();
                if (formulas.Count == 0) continue;
                foreach (var c in formulas) { c.Element(S + "v")!.Remove(); c.Attribute("t")?.Remove(); }
                Save(z, part, doc);
            }
            if (z.GetEntry("xl/calcChain.xml") is { } chain)
            {
                chain.Delete();
                foreach (var (part, attr) in new[] { ("[Content_Types].xml", "PartName"), ("xl/_rels/workbook.xml.rels", "Target") })
                {
                    var d = Load(z, part);
                    d.Root!.Elements().Where(e => ((string?)e.Attribute(attr) ?? "").EndsWith("calcChain.xml", StringComparison.OrdinalIgnoreCase)).Remove();
                    Save(z, part, d);
                }
            }
            var wb = Load(z, "xl/workbook.xml");
            var pr = wb.Root!.Element(S + "calcPr");
            if (pr == null)
            {
                pr = new XElement(S + "calcPr");
                // El esquema fija el orden: calcPr va después de estos
                new[] { "definedNames", "externalReferences", "functionGroups", "sheets" }.Select(n => wb.Root.Element(S + n)).First(e => e != null)!.AddAfterSelf(pr);
            }
            pr.SetAttributeValue("fullCalcOnLoad", "1");
            Save(z, "xl/workbook.xml", wb);
        }

        // ---------------------------------------------------------------- gráficas de Excel
        private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace XDR = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";

        // grafica: tipo | título | [Hoja!]A1:C8 [| E2]
        private static readonly Regex ChartRx = new(
            @"^\s*gr[aá]fic[ao]\s*:\s*(?<type>[^|]+?)\s*\|\s*(?<title>[^|]*?)\s*\|\s*(?:(?<sheet>[^!|]+)!)?\$?(?<c1>[A-Za-z]{1,3})\$?(?<r1>\d+):\$?(?<c2>[A-Za-z]{1,3})\$?(?<r2>\d+)\s*(?:\|\s*(?<ac>[A-Za-z]{1,3})(?<ar>\d+)\s*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string RelsOf(string part) { int i = part.LastIndexOf('/') + 1; return part[..i] + "_rels/" + part[i..] + ".rels"; }

        /// <summary>La parte a la que apunta una relación (ruta relativa a <paramref name="part"/> o absoluta).</summary>
        private static string Resolve(string part, string target)
        {
            if (target.StartsWith('/')) return target[1..];
            var segs = part.Split('/').SkipLast(1).ToList();
            foreach (var s in target.Split('/'))
                if (s == "..") { if (segs.Count > 0) segs.RemoveAt(segs.Count - 1); }
                else if (s != ".") segs.Add(s);
            return string.Join("/", segs);
        }

        /// <summary>Añade una relación a la parte y devuelve su id.</summary>
        private static string AddRel(ZipArchive z, string part, string type, string target)
        {
            var doc = TryLoad(z, RelsOf(part)) ?? new XDocument(Rels());
            var ids = doc.Root!.Elements().Select(e => (string?)e.Attribute("Id")).ToHashSet();
            var id = Enumerable.Range(1, int.MaxValue).Select(i => "rId" + i).First(i => !ids.Contains(i));
            doc.Root.Add(Rels((id, type, target)).Elements());
            Save(z, RelsOf(part), doc);
            return id;
        }

        /// <summary>Una parte nueva con el primer número libre («xl/charts/chart{0}.xml»), declarada en los tipos de contenido.</summary>
        private static string NewPart(ZipArchive z, string pattern, string contentType)
        {
            var part = Enumerable.Range(1, int.MaxValue).Select(i => string.Format(pattern, i)).First(p => z.GetEntry(p) == null);
            var types = Load(z, "[Content_Types].xml");
            types.Root!.Add(new XElement(CT + "Override", new XAttribute("PartName", "/" + part),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument." + contentType + "+xml")));
            Save(z, "[Content_Types].xml", types);
            return part;
        }

        private static XElement Val(string name, object value) => new(C + name, new XAttribute("val", value));

        /// <summary>
        /// Añade una gráfica a la hoja a partir de un rango: encabezados en la primera fila, categorías en la primera
        /// columna y una serie por cada columna siguiente (en «pastel», solo la primera). Devuelve su descripción.
        /// </summary>
        private static string AddChart(ZipArchive z, (string Name, string Part) sheet, Match m)
        {
            int c1 = Col(m.Groups["c1"].Value), c2 = Col(m.Groups["c2"].Value), r1 = int.Parse(m.Groups["r1"].Value), r2 = int.Parse(m.Groups["r2"].Value);
            if (c1 > c2) (c1, c2) = (c2, c1);
            if (r1 > r2) (r1, r2) = (r2, r1);
            if (c1 == c2 || r1 == r2)
                throw new InvalidOperationException("el rango de una gráfica necesita al menos dos columnas (categorías y valores) y dos filas (encabezados y datos). No se cambió nada.");
            var type = m.Groups["type"].Value.Trim().ToLowerInvariant();
            var kind = type.StartsWith("col") ? "col" : type.StartsWith("bar") ? "bar" : type.StartsWith("lin") || type.StartsWith("lín") ? "line"
                     : type.StartsWith("pas") || type.StartsWith("cir") || type.StartsWith("pie") ? "pie"
                     : throw new InvalidOperationException($"tipo de gráfica «{type}» no admitido: usa columnas, barras, lineas o pastel. No se cambió nada.");
            var title = m.Groups["title"].Value.Trim();

            var q = "'" + sheet.Name.Replace("'", "''") + "'!";
            string Ref(int col, int from, int to) => $"{q}${ColName(col)}${from}" + (to != from ? $":${ColName(col)}${to}" : "");
            var series = Enumerable.Range(c1 + 1, kind == "pie" ? 1 : c2 - c1).Select((col, i) => new XElement(C + "ser",
                Val("idx", i), Val("order", i),
                new XElement(C + "tx", new XElement(C + "strRef", new XElement(C + "f", Ref(col, r1, r1)))),
                new XElement(C + "cat", new XElement(C + "strRef", new XElement(C + "f", Ref(c1, r1 + 1, r2)))),
                new XElement(C + "val", new XElement(C + "numRef", new XElement(C + "f", Ref(col, r1 + 1, r2))))));
            XElement Axis(string name, int id, int cross, string pos, bool grid) => new(C + name,
                Val("axId", id), new XElement(C + "scaling", Val("orientation", "minMax")), Val("delete", 0), Val("axPos", pos),
                grid ? new XElement(C + "majorGridlines") : null, Val("crossAx", cross));
            var plot = new XElement(C + "plotArea", new XElement(C + "layout"));
            if (kind == "pie") plot.Add(new XElement(C + "pieChart", Val("varyColors", 1), series));
            else
            {
                plot.Add(kind == "line"
                    ? new XElement(C + "lineChart", Val("grouping", "standard"), Val("varyColors", 0), series, Val("marker", 1), Val("axId", 1), Val("axId", 2))
                    : new XElement(C + "barChart", Val("barDir", kind), Val("grouping", "clustered"), Val("varyColors", 0), series, Val("axId", 1), Val("axId", 2)));
                plot.Add(Axis("catAx", 1, 2, kind == "bar" ? "l" : "b", false), Axis("valAx", 2, 1, kind == "bar" ? "b" : "l", true));
            }
            var chartPart = NewPart(z, "xl/charts/chart{0}.xml", "drawingml.chart");
            Save(z, chartPart, new XDocument(new XElement(C + "chartSpace", new XAttribute(XNamespace.Xmlns + "c", C), new XAttribute(XNamespace.Xmlns + "a", A),
                new XElement(C + "chart",
                    title == "" ? null : new XElement(C + "title",
                        new XElement(C + "tx", new XElement(C + "rich", new XElement(A + "bodyPr"), new XElement(A + "p", new XElement(A + "r", new XElement(A + "t", title))))),
                        Val("overlay", 0)),
                    Val("autoTitleDeleted", title == "" ? 1 : 0), plot,
                    new XElement(C + "legend", Val("legendPos", "r")), Val("plotVisOnly", 1)))));

            // El dibujo de la hoja (donde van ancladas sus gráficas): el que ya tenga o uno nuevo
            var sdoc = Load(z, sheet.Part);
            string drawingPart;
            XDocument drawing;
            if (sdoc.Root!.Element(S + "drawing") is { } existing)
            {
                var rel = Load(z, RelsOf(sheet.Part)).Root!.Elements().First(r => (string?)r.Attribute("Id") == (string?)existing.Attribute(R + "id"));
                drawingPart = Resolve(sheet.Part, (string?)rel.Attribute("Target") ?? "");
                drawing = Load(z, drawingPart);
            }
            else
            {
                drawingPart = NewPart(z, "xl/drawings/drawing{0}.xml", "drawing");
                drawing = new XDocument(new XElement(XDR + "wsDr", new XAttribute(XNamespace.Xmlns + "xdr", XDR), new XAttribute(XNamespace.Xmlns + "a", A)));
                var el = new XElement(S + "drawing", new XAttribute(R + "id", AddRel(z, sheet.Part, "drawing", "/" + drawingPart)));
                // El esquema fija el orden: «drawing» va antes de estos
                if (sdoc.Root.Elements().FirstOrDefault(e => e.Name.LocalName is "legacyDrawing" or "legacyDrawingHF" or "drawingHF" or "picture"
                        or "oleObjects" or "controls" or "webPublishItems" or "tableParts" or "extLst") is { } later) later.AddBeforeSelf(el);
                else sdoc.Root.Add(el);
                Save(z, sheet.Part, sdoc);
            }

            // Anclada en la celda indicada (o dos columnas a la derecha de los datos), de 8 columnas por 16 filas
            int col = m.Groups["ac"].Success ? Col(m.Groups["ac"].Value) - 1 : c2 + 1, row = m.Groups["ar"].Success ? Math.Max(1, int.Parse(m.Groups["ar"].Value)) - 1 : r1 - 1;
            XElement Corner(string name, int c, int r) => new(XDR + name, new XElement(XDR + "col", c), new XElement(XDR + "colOff", 0), new XElement(XDR + "row", r), new XElement(XDR + "rowOff", 0));
            int id = drawing.Descendants(XDR + "cNvPr").Select(e => (int?)e.Attribute("id") ?? 0).DefaultIfEmpty(1).Max() + 1;
            drawing.Root!.Add(new XElement(XDR + "twoCellAnchor", Corner("from", col, row), Corner("to", col + 8, row + 16),
                new XElement(XDR + "graphicFrame", new XAttribute("macro", ""),
                    new XElement(XDR + "nvGraphicFramePr", new XElement(XDR + "cNvPr", new XAttribute("id", id), new XAttribute("name", "Gráfica " + id)), new XElement(XDR + "cNvGraphicFramePr")),
                    new XElement(XDR + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0))),
                    new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", C.NamespaceName),
                        new XElement(C + "chart", new XAttribute(XNamespace.Xmlns + "c", C), new XAttribute(XNamespace.Xmlns + "r", R),
                            new XAttribute(R + "id", AddRel(z, drawingPart, "chart", "/" + chartPart)))))),
                new XElement(XDR + "clientData")));
            Save(z, drawingPart, drawing);
            return $"gráfica de {type}{(title != "" ? $" «{title}»" : "")} con {sheet.Name}!{ColName(c1)}{r1}:{ColName(c2)}{r2}";
        }

        private static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false, any = false;
            void EndRow() { row.Add(sb.ToString()); sb.Clear(); rows.Add(row); row = new(); any = false; }
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c != '"') sb.Append(c);
                    else if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else if (c == '"' && sb.Length == 0) { quoted = true; any = true; }
                else if (c == ',') { row.Add(sb.ToString()); sb.Clear(); any = true; }
                else if (c == '\n') EndRow();
                else if (c != '\r') { sb.Append(c); any = true; }
            }
            if (any || row.Count > 0) EndRow();
            return rows;
        }

        /// <summary>Un libro nuevo: secciones «## Hoja: Nombre» seguidas de su CSV (sin secciones, una sola hoja).</summary>
        public static byte[] NewXlsx(string body)
        {
            body = body.Replace("\r\n", "\n");
            var sheets = Regex.Matches(body, @"^##\s*Hoja:[ \t]*(?<n>[^\n]*)\n?(?<c>.*?)(?=^##\s*Hoja:|\z)", RegexOptions.Multiline | RegexOptions.Singleline)
                .Select(m => (Name: m.Groups["n"].Value.Trim(), Csv: m.Groups["c"].Value)).ToList();
            if (sheets.Count == 0) sheets.Add(("Hoja1", body));

            var parts = new List<(string, string, XElement)>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sheetList = new XElement(S + "sheets");
            var rels = new List<(string, string, string)>();
            var charts = new List<(int Sheet, Match Line)>();
            for (int i = 1; i <= sheets.Count; i++)
            {
                // Nombre de hoja válido para Excel: sin []:*?/\, hasta 31 caracteres y sin repetir
                var name = Regex.Replace(sheets[i - 1].Name, @"[\[\]:*?/\\]", " ").Trim();
                if (name.Length > 31) name = name[..31];
                if (name == "" || !names.Add(name)) names.Add(name = "Hoja" + i);
                var data = new XElement(S + "sheetData");
                int r = 0;
                // Las líneas «grafica: …» de la hoja no son datos: se añaden al final
                var lines = sheets[i - 1].Csv.Split('\n');
                charts.AddRange(lines.Select(l => ChartRx.Match(l)).Where(m => m.Success).Select(m => (i - 1, m)));
                foreach (var row in ParseCsv(string.Join("\n", lines.Where(l => !ChartRx.IsMatch(l)))))
                {
                    r++;
                    var cells = row.Select((val, ci) => (val: val.Trim(), ci)).Where(x => x.val != "").Select(x =>
                    {
                        var c = new XElement(S + "c", new XAttribute("r", ColName(x.ci + 1) + r));
                        SetValue(c, x.val, false);
                        return c;
                    }).ToList();
                    if (cells.Count > 0) data.Add(new XElement(S + "row", new XAttribute("r", r), cells));
                }
                sheetList.Add(new XElement(S + "sheet", new XAttribute("name", name), new XAttribute("sheetId", i), new XAttribute(R + "id", "rId" + i)));
                rels.Add(("rId" + i, "worksheet", $"worksheets/sheet{i}.xml"));
                parts.Add(($"xl/worksheets/sheet{i}.xml", "spreadsheetml.worksheet", new XElement(S + "worksheet", new XAttribute(XNamespace.Xmlns + "r", R), data)));
            }
            parts.Insert(0, ("xl/workbook.xml", "spreadsheetml.sheet.main", new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", R), sheetList)));
            parts.Add(("xl/_rels/workbook.xml.rels", "", Rels(rels.ToArray())));
            var book = Package(parts.ToArray());
            if (charts.Count == 0) return book;
            var ms = new MemoryStream();
            ms.Write(book);
            using (var z = new ZipArchive(ms, ZipArchiveMode.Update, true))
            {
                var made = Sheets(z);
                foreach (var (sheet, line) in charts) AddChart(z, made[sheet], line);
            }
            return ms.ToArray();
        }

        // ---------------------------------------------------------------- Word
        /// <summary>Los párrafos del documento (también los de las tablas), uno por línea; los títulos, con «#».</summary>
        public static string ReadDocx(string path)
        {
            using var z = new ZipArchive(Open(path), ZipArchiveMode.Read);
            var sb = new StringBuilder();
            foreach (var p in Load(z, "word/document.xml").Descendants(W + "p"))
            {
                var style = (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";
                if (Regex.Match(style, @"^(?:Heading|T[ií]?tulo)(\d)$", RegexOptions.IgnoreCase) is { Success: true } h)
                    sb.Append('#', h.Groups[1].Value[0] - '0').Append(' ');
                foreach (var e in p.Descendants())
                    if (e.Name == W + "t") sb.Append(e.Value);
                    else if (e.Name == W + "tab") sb.Append('\t');
                    else if (e.Name == W + "br") sb.Append(' ');
                sb.Append('\n');
            }
            return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
        }

        /// <summary>
        /// Reemplaza texto dentro de un párrafo (cada texto buscado debe aparecer una sola vez en el documento). El
        /// formato de lo que queda fuera del texto reemplazado se conserva; el texto nuevo toma el del comienzo.
        /// </summary>
        public static byte[] EditDocx(string path, IEnumerable<(string Old, string New)> pairs)
        {
            var ms = Open(path);
            using (var z = new ZipArchive(ms, ZipArchiveMode.Update, true))
            {
                var doc = Load(z, "word/document.xml");
                foreach (var (old, neu) in pairs)
                {
                    if (old.Contains('\n')) throw new InvalidOperationException("en Word cada texto buscado debe estar dentro de un solo párrafo (sin saltos de línea). No se cambió nada.");
                    var paras = doc.Descendants(W + "p").Select(p => p.Descendants(W + "t").ToList())
                        .Select(ts => (Ts: ts, Text: string.Concat(ts.Select(t => t.Value)))).Where(p => p.Text.Contains(old, StringComparison.Ordinal)).ToList();
                    if (paras.Count == 0) throw new InvalidOperationException($"no se encontró «{Short(old)}» (debe coincidir exactamente y estar en un solo párrafo; vuelve a leer el documento). No se cambió nada.");
                    int at = paras[0].Text.IndexOf(old, StringComparison.Ordinal), end = at + old.Length;
                    if (paras.Count > 1 || paras[0].Text.IndexOf(old, at + 1, StringComparison.Ordinal) >= 0)
                        throw new InvalidOperationException($"«{Short(old)}» aparece más de una vez; incluye más texto del párrafo. No se cambió nada.");
                    // El párrafo está partido en tramos (w:t) con formato propio: solo se tocan los que pisa el texto buscado
                    int pos = 0;
                    bool placed = false;
                    foreach (var t in paras[0].Ts)
                    {
                        int a = pos, b = pos + t.Value.Length;
                        pos = b;
                        if (b <= at || a >= end) continue;
                        t.Value = t.Value[..Math.Max(0, at - a)] + (placed ? "" : neu) + (end < b ? t.Value[(end - a)..] : "");
                        t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
                        placed = true;
                    }
                }
                Save(z, "word/document.xml", doc);
            }
            return ms.ToArray();
        }

        /// <summary>Un documento nuevo: un párrafo por línea; «# », «## » y «### » son títulos.</summary>
        public static byte[] NewDocx(string body)
        {
            var paragraphs = body.Replace("\r\n", "\n").Split('\n').Select(line =>
            {
                var h = Regex.Match(line, @"^(#{1,3})\s+(.*)$");
                var run = new XElement(W + "r");
                if (h.Success) // negrita y tamaño en medios puntos: 18, 15 y 13 pt
                    run.Add(new XElement(W + "rPr", new XElement(W + "b"), new XElement(W + "sz", new XAttribute(W + "val", new[] { 36, 30, 26 }[h.Groups[1].Length - 1]))));
                run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), h.Success ? h.Groups[2].Value : line));
                return new XElement(W + "p", run);
            });
            return Package(("word/document.xml", "wordprocessingml.document.main",
                new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), new XElement(W + "body", paragraphs))));
        }

        private static string Short(string s) => s.Length > 80 ? s[..80] + "…" : s;
    }
}
