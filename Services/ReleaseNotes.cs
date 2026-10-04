using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AgentManagerNotch.Services
{
    /// <summary>Una versión de la carpeta <c>versiones\</c>: su archivo X.Y.Z.md y, si tiene cambios visuales, X.Y.Z.gif.</summary>
    public sealed record ReleaseNote(Version Version, string Date, string Body, byte[]? Gif)
    {
        public string Name => Version.ToString(3);
        /// <summary>Es la versión que está corriendo.</summary>
        public bool IsCurrent => Version == App.Current.Updates.LocalVersion;
    }

    /// <summary>
    /// Notas de todas las versiones, incrustadas en el ensamblado al compilar (ver AgentManagerNotch.csproj): la
    /// ventana de notas las muestra sin red ni repositorio.
    /// </summary>
    public static class ReleaseNotes
    {
        private const string Prefix = "versiones/";
        private static IReadOnlyList<ReleaseNote>? _all;

        /// <summary>De la más nueva a la más antigua.</summary>
        public static IReadOnlyList<ReleaseNote> All => _all ??= Load();

        private static IReadOnlyList<ReleaseNote> Load()
        {
            var asm = typeof(ReleaseNotes).Assembly;
            var names = asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix)).ToHashSet();
            var list = new List<ReleaseNote>();
            foreach (var name in names.Where(n => n.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            {
                var stem = name[Prefix.Length..^3];
                if (!Version.TryParse(stem, out var v)) continue; // README.md y similares
                try
                {
                    string text;
                    using (var r = new StreamReader(asm.GetManifestResourceStream(name)!, Encoding.UTF8)) text = r.ReadToEnd();
                    byte[]? gif = null;
                    if (names.Contains($"{Prefix}{stem}.gif"))
                    {
                        using var s = asm.GetManifestResourceStream($"{Prefix}{stem}.gif")!;
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        gif = ms.ToArray();
                    }
                    var (date, body) = Parse(text);
                    list.Add(new ReleaseNote(new Version(v.Major, v.Minor, Math.Max(v.Build, 0)), date, body, gif));
                }
                catch (Exception ex) { Log.Error($"Notas de la versión {stem}", ex); }
            }
            return list.OrderByDescending(n => n.Version).ToList();
        }

        /// <summary>«# 0.3.0 · 2026-10-15» → fecha; el resto es el cuerpo en Markdown.</summary>
        private static (string, string) Parse(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            var date = "";
            if (lines.Count > 0 && lines[0].StartsWith("# "))
            {
                var dot = lines[0].IndexOf('·');
                if (dot >= 0) date = lines[0][(dot + 1)..].Trim();
                lines.RemoveAt(0);
            }
            return (date, string.Join("\n", lines).Trim());
        }
    }
}
