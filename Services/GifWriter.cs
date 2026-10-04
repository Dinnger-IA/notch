using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Graba en GIF un elemento de la interfaz dibujándolo con RenderTargetBitmap (no captura la pantalla: da igual
    /// lo que haya encima o que la ventana esté fuera de ella). Cada fotograma solo codifica la zona que cambió.
    /// </summary>
    public sealed class GifWriter
    {
        private readonly FrameworkElement _target;
        private readonly Brush _background;
        private readonly double _scale;
        private readonly List<(byte[] block, long at)> _parts = new(); // at: ms desde el inicio
        private readonly Stopwatch _clock = new();
        private int[]? _prev;
        private int _w, _h;

        public GifWriter(FrameworkElement target, Brush background, int maxWidth = 800)
        {
            _target = target;
            _background = background;
            _scale = Math.Min(1.25, maxWidth / Math.Max(1, target.ActualWidth));
        }

        /// <summary>Graba mientras corre <paramref name="script"/> y guarda el GIF (en bucle) en <paramref name="path"/>.</summary>
        public async Task<int> RecordAsync(string path, Func<Task> script, int fps = 12)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / fps) };
            timer.Tick += (_, _) => Capture();
            _clock.Start();
            Capture();
            timer.Start();
            try { await script(); }
            finally { timer.Stop(); }
            Capture();
            Save(path, _clock.ElapsedMilliseconds + 150); // pausa antes de repetir
            return _parts.Count;
        }

        private void Capture()
        {
            int w = (int)Math.Round(_target.ActualWidth * _scale), h = (int)Math.Round(_target.ActualHeight * _scale);
            if (w <= 0 || h <= 0) return;
            if (_prev != null && (w != _w || h != _h)) return; // el tamaño no debe cambiar durante la grabación
            _w = w; _h = h;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var rect = new Rect(0, 0, _target.ActualWidth, _target.ActualHeight);
                dc.PushTransform(new ScaleTransform(_scale, _scale));
                dc.DrawRectangle(_background, null, rect);
                dc.DrawRectangle(new VisualBrush(_target) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, rect);
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var px = new int[w * h];
            bmp.CopyPixels(px, w * 4, 0);

            var box = _prev == null ? new Int32Rect(0, 0, w, h) : Changed(_prev, px, w, h);
            if (box.IsEmpty) return; // igual que el anterior: ese se queda más tiempo
            _parts.Add((ImageBlock(Encode(new CroppedBitmap(bmp, box)), box.X, box.Y), _clock.ElapsedMilliseconds));
            _prev = px;
        }

        private static Int32Rect Changed(int[] a, int[] b, int w, int h)
        {
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0, i = y * w; x < w; x++, i++)
                    if (a[i] != b[i])
                    {
                        if (x < x0) x0 = x;
                        if (x > x1) x1 = x;
                        if (y < y0) y0 = y;
                        y1 = y;
                    }
            return x1 < 0 ? Int32Rect.Empty : new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }

        private static byte[] Encode(BitmapSource src)
        {
            var enc = new GifBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        /// <summary>Cada fotograma dura hasta que empieza el siguiente (tiempo real, aunque se pierdan ticks).</summary>
        private void Save(string path, long endAt)
        {
            using var ms = new MemoryStream();
            void W(params byte[] b) => ms.Write(b, 0, b.Length);
            W(Encoding.ASCII.GetBytes("GIF89a"));
            W(U16(_w)); W(U16(_h)); W(0x70, 0, 0); // sin tabla global: cada fotograma lleva la suya
            W(0x21, 0xFF, 0x0B); W(Encoding.ASCII.GetBytes("NETSCAPE2.0")); W(3, 1, 0, 0, 0); // repetir siempre
            for (int i = 0; i < _parts.Count; i++)
            {
                var next = i + 1 < _parts.Count ? _parts[i + 1].at : endAt;
                var d = (int)Math.Clamp((next - _parts[i].at) / 10, 2, 65535); // centésimas
                W(0x21, 0xF9, 4, 0x04); W(U16(d)); W(0, 0); // retardo; el fotograma se queda debajo del siguiente
                W(_parts[i].block);
            }
            W(0x3B);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, path, true);
        }

        /// <summary>Descriptor (en la posición indicada) + paleta como tabla local + datos de un GIF de un fotograma.</summary>
        private static byte[] ImageBlock(byte[] g, int left, int top)
        {
            int packed = g[10], p = 13;
            byte[]? gct = null;
            if ((packed & 0x80) != 0) { int n = 3 << ((packed & 7) + 1); gct = g[p..(p + n)]; p += n; }
            while (p < g.Length)
            {
                if (g[p] == 0x21) { p += 2; while (g[p] != 0) p += g[p] + 1; p++; continue; } // extensiones
                if (g[p] != 0x2C) break;
                var desc = g[p..(p + 10)];
                desc[1] = (byte)left; desc[2] = (byte)(left >> 8);
                desc[3] = (byte)top; desc[4] = (byte)(top >> 8);
                p += 10;
                byte[] table;
                if ((desc[9] & 0x80) != 0) { int n = 3 << ((desc[9] & 7) + 1); table = g[p..(p + n)]; p += n; }
                else { table = gct ?? throw new InvalidDataException("GIF sin paleta"); desc[9] = (byte)(0x80 | (packed & 7)); }
                int start = p++;
                while (g[p] != 0) p += g[p] + 1;
                p++;
                return [.. desc, .. table, .. g[start..p]];
            }
            throw new InvalidDataException("GIF sin imagen");
        }

        private static byte[] U16(int v) => [(byte)v, (byte)(v >> 8)];
    }
}
