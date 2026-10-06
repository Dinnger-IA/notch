using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Menciones <c>@workspace</c> en el chat: citan otro proyecto abierto en el notch para que el agente lo use como
    /// contexto. Al enviar, el CLI recibe acceso a esa carpeta (Claude Code <c>--add-dir</c>, Gemini
    /// <c>--include-directories</c>; Codex ya puede leer fuera de su carpeta) y el mensaje lleva delante la ruta de cada
    /// workspace citado.
    /// </summary>
    public static class WorkspaceMentions
    {
        public record WorkspaceRef(string Name, string Folder);

        /// <summary>Las sesiones del notch (la pone App al arrancar).</summary>
        public static Func<IEnumerable<AgentSession>>? Sessions { get; set; }

        /// <summary>
        /// Workspaces que se pueden citar: las carpetas de las pestañas de los agentes de código, sin repetir y sin
        /// <paramref name="exclude"/> (la carpeta de la pestaña actual). Si dos carpetas se llaman igual, se distinguen
        /// con la carpeta que las contiene («cliente/api»).
        /// </summary>
        public static List<WorkspaceRef> List(string? exclude = null)
        {
            var folders = (Sessions?.Invoke() ?? Enumerable.Empty<AgentSession>())
                .Where(s => s.HasFolder && Directory.Exists(s.WorkDir))
                .Select(s => Normalize(s.WorkDir))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => exclude == null || !string.Equals(f, Normalize(exclude), StringComparison.OrdinalIgnoreCase))
                .ToList();
            var names = folders.ToLookup(Leaf, StringComparer.OrdinalIgnoreCase);
            return folders
                .Select(f => new WorkspaceRef(names[Leaf(f)].Count() > 1 ? $"{Leaf(Path.GetDirectoryName(f) ?? "")}/{Leaf(f)}" : Leaf(f), f))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Workspaces citados en <paramref name="text"/> con <c>@nombre</c>.</summary>
        public static List<WorkspaceRef> Resolve(string text, string? exclude = null)
        {
            if (!text.Contains('@')) return new();
            // Los nombres más largos primero, para que «@api-v2» no cuente también como «@api»
            var found = new List<WorkspaceRef>();
            var rest = text;
            foreach (var r in List(exclude).OrderByDescending(r => r.Name.Length))
            {
                var re = new Regex($@"(?<![\w@])@{Regex.Escape(r.Name)}(?![\w\-./])", RegexOptions.IgnoreCase);
                if (!re.IsMatch(rest)) continue;
                found.Add(r);
                rest = re.Replace(rest, " ");
            }
            return found;
        }

        /// <summary>El mensaje para el CLI: la lista de workspaces citados con su ruta y, debajo, lo que escribió el usuario.</summary>
        public static string Augment(string prompt, IReadOnlyCollection<WorkspaceRef> refs)
        {
            if (refs.Count == 0) return prompt;
            var sb = new StringBuilder();
            sb.AppendLine("[Workspaces citados: el usuario hace referencia a estos otros proyectos con @. Lee sus archivos " +
                          "para tener el contexto que necesites; no los modifiques salvo que te lo pida expresamente.]");
            foreach (var r in refs) sb.AppendLine($"- @{r.Name}: {r.Folder}");
            sb.AppendLine();
            sb.Append(prompt);
            return sb.ToString();
        }

        private static string Normalize(string folder) => Path.GetFullPath(folder).TrimEnd('\\', '/');

        private static string Leaf(string folder)
        {
            var n = Path.GetFileName(folder.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(n) ? folder : n;
        }
    }
}
