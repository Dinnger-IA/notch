using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Archivos adjuntos en el chat (arrastrados sobre el notch o con el clip). El mensaje lleva un bloque
    /// «Archivos:» con la ruta de cada uno: a los CLI les basta, porque leen el disco. Un proveedor que no puede leer
    /// el disco (p. ej. uno que manda el mensaje a un servidor) usa <see cref="Split"/> para sacar los archivos y
    /// subirlos aparte.
    /// </summary>
    public static class ChatAttachments
    {
        private const string Header = "Archivos:";

        /// <summary>El bloque que se añade al mensaje.</summary>
        public static string Block(IEnumerable<string> files) =>
            Header + "\n" + string.Join("\n", files.Select(f => "- " + f));

        /// <summary>
        /// Separa los archivos adjuntos (los que existen en el disco) del texto del mensaje. Devuelve el texto sin
        /// los bloques «Archivos:» que solo listaban archivos existentes.
        /// </summary>
        public static string Split(string text, out List<string> files)
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
                if (block.Count == 0 || !block.All(File.Exists)) { kept.Add(lines[i]); continue; }
                found.AddRange(block.Where(f => !found.Contains(f, StringComparer.OrdinalIgnoreCase)));
                i = j - 1;
            }
            files = found;
            return string.Join("\n", kept).Trim();
        }
    }
}
