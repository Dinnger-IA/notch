using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Archivos adjuntos en el chat (arrastrados sobre el notch o con el clip). El mensaje lleva un bloque
    /// «Archivos:» con la ruta de cada uno: a los CLI les basta, porque leen el disco. Un proveedor que no puede leer
    /// el disco (p. ej. uno que manda el mensaje a un servidor) usa <see cref="SplitForUpload"/> para sacar los
    /// archivos y subirlos aparte.
    /// </summary>
    public static class ChatAttachments
    {
        private const string Header = "Archivos:";

        /// <summary>
        /// Rutas que el usuario adjuntó en esta ejecución (arrastre o clip). Solo estas se suben a un servidor: un
        /// bloque «Archivos:» pegado o escrito por un agente no basta para mandar archivos del disco.
        /// </summary>
        private static readonly HashSet<string> Attached = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>El bloque que se añade al mensaje (y registra esos archivos como adjuntados por el usuario).</summary>
        public static string Block(IEnumerable<string> files)
        {
            var list = files.ToList();
            lock (Attached) foreach (var f in list) Attached.Add(f);
            return Header + "\n" + string.Join("\n", list.Select(f => "- " + f));
        }

        /// <summary>
        /// Separa los archivos adjuntos del texto del mensaje para mostrarlos como chips. No toca el disco (se llama
        /// al pintar cada mensaje): un bloque cuenta si todas sus líneas son rutas absolutas locales.
        /// </summary>
        public static string Split(string text, out List<string> files) => Split(text, out files, IsLocalPath);

        /// <summary>
        /// Como <see cref="Split"/>, pero solo saca los bloques que el usuario adjuntó en esta ejecución y que siguen
        /// en el disco: los que se van a subir. Los demás se quedan en el texto.
        /// </summary>
        public static string SplitForUpload(string text, out List<string> files) =>
            Split(text, out files, f => IsAttached(f) && File.Exists(f));

        private static bool IsAttached(string f) { lock (Attached) return Attached.Contains(f); }

        // Rutas como C:\… (no UNC \\servidor\…: resolverlas puede colgar la interfaz o mandar credenciales de red)
        private static bool IsLocalPath(string f) =>
            f.Length > 3 && char.IsLetter(f[0]) && f[1] == ':' && f[2] is '\\' or '/';

        private static string Split(string text, out List<string> files, Func<string, bool> isFile)
        {
            var found = new List<string>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var kept = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() != Header) { kept.Add(lines[i]); continue; }
                int j = i + 1;
                var block = new List<string>();
                while (j < lines.Length && lines[j].StartsWith("- ")) block.Add(lines[j++][2..].Trim());
                // Un «Archivos:» escrito a mano (sin rutas reales) se queda en el texto
                if (block.Count == 0 || !block.All(isFile)) { kept.Add(lines[i]); continue; }
                found.AddRange(block.Where(f => !found.Contains(f, StringComparer.OrdinalIgnoreCase)));
                i = j - 1;
            }
            files = found;
            return string.Join("\n", kept).Trim();
        }
    }
}
