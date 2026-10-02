# Graba un GIF animado de una ventana (o de una zona de la pantalla) para las notas de una versión.
# Sin dependencias: captura con System.Drawing y codifica con el codificador GIF de Windows.
#
#   .\versiones\grabar-gif.ps1 -Ventana "Notas de la versión" -Segundos 6 -Salida versiones\0.3.0.gif
#   .\versiones\grabar-gif.ps1 -Region 100,0,900,400 -Salida versiones\0.3.0.gif
#   .\versiones\grabar-gif.ps1 -Ventana "Notas de la versión" -Salida versiones\0.3.0.gif -Accion { ...clics, teclas... }
#
# -Accion se ejecuta mientras se graba (útil para enseñar un cambio paso a paso); sin ella se graban -Segundos.
param(
    [string]$Ventana,
    [int[]]$Region,
    [double]$Segundos = 6,
    [int]$Fps = 10,
    [int]$AnchoMax = 760,
    [scriptblock]$Accion,
    [Parameter(Mandatory = $true)][string]$Salida
)
$ErrorActionPreference = "Stop"

Add-Type -ReferencedAssemblies System.Drawing, PresentationCore, WindowsBase, System.Xaml -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;

public static class GifRecorder
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static readonly List<Bitmap> Frames = new List<Bitmap>();
    static Thread worker;
    static volatile bool running;

    public static void DpiAware() { SetProcessDPIAware(); }

    /// Rectángulo visible (sin la sombra) de la ventana con ese título exacto.
    public static int[] WindowRect(string title)
    {
        IntPtr h = FindWindow(null, title);
        if (h == IntPtr.Zero) throw new Exception("No hay ninguna ventana con el título '" + title + "'.");
        SetForegroundWindow(h);
        RECT r;
        DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))); // DWMWA_EXTENDED_FRAME_BOUNDS
        return new[] { r.L, r.T, r.R - r.L, r.B - r.T };
    }

    public static void Start(int x, int y, int w, int h, int fps, int maxWidth)
    {
        Frames.Clear();
        running = true;
        double scale = Math.Min(1.0, (double)maxWidth / w);
        int ow = (int)Math.Round(w * scale), oh = (int)Math.Round(h * scale);
        worker = new Thread(() =>
        {
            var interval = TimeSpan.FromMilliseconds(1000.0 / fps);
            var next = DateTime.UtcNow;
            while (running)
            {
                using (var shot = new Bitmap(w, h))
                {
                    using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
                    var frame = new Bitmap(ow, oh);
                    using (var g = Graphics.FromImage(frame))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(shot, 0, 0, ow, oh);
                    }
                    lock (Frames) Frames.Add(frame);
                }
                next += interval;
                var wait = next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            }
        });
        worker.IsBackground = true;
        worker.Start();
    }

    /// Detiene la captura y guarda el GIF (en bucle; los fotogramas repetidos se funden en uno más largo).
    public static int Stop(string path, int fps)
    {
        running = false;
        worker.Join();
        int delay = (int)Math.Round(100.0 / fps); // centésimas
        var parts = new List<byte[]>();
        var delays = new List<int>();
        int w = 0, h = 0;
        byte[] prev = null;
        foreach (var bmp in Frames)
        {
            w = bmp.Width; h = bmp.Height;
            byte[] png;
            using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); png = ms.ToArray(); }
            var src = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var enc = new GifBitmapEncoder();
            enc.Frames.Add(src);
            byte[] gif;
            using (var ms = new MemoryStream()) { enc.Save(ms); gif = ms.ToArray(); }
            var img = ImageBlock(gif);
            bmp.Dispose();
            if (prev != null && Same(prev, img)) { delays[delays.Count - 1] += delay; continue; }
            parts.Add(img); delays.Add(delay); prev = img;
        }
        if (parts.Count > 0) delays[delays.Count - 1] += 150; // pausa al final antes de repetir

        using (var f = new FileStream(path, FileMode.Create))
        {
            Write(f, System.Text.Encoding.ASCII.GetBytes("GIF89a"));
            Write(f, U16(w)); Write(f, U16(h)); Write(f, new byte[] { 0x70, 0, 0 }); // sin tabla global
            Write(f, new byte[] { 0x21, 0xFF, 0x0B });
            Write(f, System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            Write(f, new byte[] { 3, 1, 0, 0, 0 }); // repetir siempre
            for (int i = 0; i < parts.Count; i++)
            {
                Write(f, new byte[] { 0x21, 0xF9, 4, 0x04 }); Write(f, U16(delays[i])); Write(f, new byte[] { 0, 0 });
                Write(f, parts[i]);
            }
            f.WriteByte(0x3B);
        }
        Frames.Clear();
        return parts.Count;
    }

    /// Descriptor + paleta (como tabla local) + datos de la imagen de un GIF de un solo fotograma.
    static byte[] ImageBlock(byte[] g)
    {
        int p = 6;
        int packed = g[p + 4];
        p += 7;
        byte[] gct = null;
        if ((packed & 0x80) != 0) { int n = 3 << ((packed & 7) + 1); gct = Sub(g, p, n); p += n; }
        int gctBits = packed & 7;
        while (p < g.Length)
        {
            byte b = g[p];
            if (b == 0x21) { p += 2; while (g[p] != 0) p += g[p] + 1; p++; }
            else if (b == 0x2C)
            {
                var outp = new MemoryStream();
                var desc = Sub(g, p, 10);
                int ip = desc[9];
                p += 10;
                byte[] table;
                if ((ip & 0x80) != 0) { int n = 3 << ((ip & 7) + 1); table = Sub(g, p, n); p += n; }
                else { table = gct; desc[9] = (byte)(0x80 | gctBits); }
                Write(outp, desc); Write(outp, table);
                int start = p; p++; // tamaño mínimo de código LZW
                while (g[p] != 0) p += g[p] + 1;
                p++;
                Write(outp, Sub(g, start, p - start));
                return outp.ToArray();
            }
            else break;
        }
        throw new Exception("GIF sin imagen");
    }

    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    static byte[] Sub(byte[] a, int i, int n) { var r = new byte[n]; Array.Copy(a, i, r, 0, n); return r; }
    static byte[] U16(int v) { return new[] { (byte)(v & 0xFF), (byte)(v >> 8) }; }
    static void Write(Stream s, byte[] b) { s.Write(b, 0, b.Length); }
}
"@

[GifRecorder]::DpiAware()
if ($Ventana) { $r = [GifRecorder]::WindowRect($Ventana); Start-Sleep -Milliseconds 400 }
elseif ($Region -and $Region.Count -eq 4) { $r = $Region }
else { throw "Indica -Ventana 'Título' o -Region x,y,ancho,alto" }

$out = [IO.Path]::GetFullPath((Join-Path (Get-Location) $Salida))
[GifRecorder]::Start($r[0], $r[1], $r[2], $r[3], $Fps, $AnchoMax)
try {
    if ($Accion) { & $Accion } else { Start-Sleep -Milliseconds ([int]($Segundos * 1000)) }
}
finally {
    $n = [GifRecorder]::Stop($out, $Fps)
}
$kb = [math]::Round((Get-Item $out).Length / 1KB)
Write-Host "Listo: $out ($n fotogramas, $kb KB)" -ForegroundColor Green
