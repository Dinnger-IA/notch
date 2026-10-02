using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgentManagerNotch.Controls
{
    /// <summary>
    /// Reproduce un GIF animado (WPF solo muestra el primer fotograma). Compone los fotogramas una vez, respetando
    /// posición, retardo y forma de borrado de cada uno, y los va cambiando con un temporizador mientras está visible.
    /// </summary>
    public sealed class GifPlayer : Image
    {
        private readonly DispatcherTimer _timer = new();
        private List<(BitmapSource frame, TimeSpan delay)> _frames = new();
        private int _index;

        public GifPlayer()
        {
            Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            _timer.Tick += (_, _) => Next();
            IsVisibleChanged += (_, _) => Sync();
            Unloaded += (_, _) => _timer.Stop();
            Loaded += (_, _) => Sync();
        }

        /// <summary>Bytes del GIF, o null para vaciar.</summary>
        public void Load(byte[]? gif)
        {
            _timer.Stop();
            _frames = new();
            _index = 0;
            Source = null;
            if (gif == null) return;
            try { _frames = Decode(gif); }
            catch (Exception ex) { Services.Log.Error("GIF de las notas de la versión", ex); }
            if (_frames.Count > 0) Source = _frames[0].frame;
            Sync();
        }

        private void Sync()
        {
            if (_frames.Count > 1 && IsVisible && IsLoaded)
            {
                _timer.Interval = _frames[_index].delay;
                _timer.Start();
            }
            else _timer.Stop();
        }

        private void Next()
        {
            _index = (_index + 1) % _frames.Count;
            Source = _frames[_index].frame;
            _timer.Interval = _frames[_index].delay;
        }

        private static List<(BitmapSource, TimeSpan)> Decode(byte[] gif)
        {
            var decoder = new GifBitmapDecoder(new MemoryStream(gif), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var first = decoder.Frames[0];
            int w = Meta(decoder.Metadata, "/logscrdesc/Width", first.PixelWidth);
            int h = Meta(decoder.Metadata, "/logscrdesc/Height", first.PixelHeight);
            var result = new List<(BitmapSource, TimeSpan)>();
            BitmapSource? canvas = null; // lo que queda en pantalla para el siguiente fotograma
            foreach (var f in decoder.Frames)
            {
                var m = f.Metadata as BitmapMetadata;
                int left = Meta(m, "/imgdesc/Left", 0), top = Meta(m, "/imgdesc/Top", 0);
                int delay = Meta(m, "/grctlext/Delay", 10), disposal = Meta(m, "/grctlext/Disposal", 0);
                var rect = new Rect(left, top, f.PixelWidth, f.PixelHeight);

                var full = Render(w, h, dc =>
                {
                    if (canvas != null) dc.DrawImage(canvas, new Rect(0, 0, w, h));
                    dc.DrawImage(f, rect);
                });
                result.Add((full, TimeSpan.FromMilliseconds(Math.Max(delay, 2) * 10)));

                canvas = disposal switch
                {
                    2 => Render(w, h, dc => // borra la zona del fotograma al fondo (transparente)
                    {
                        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, w, h)), new RectangleGeometry(rect)));
                        dc.DrawImage(full, new Rect(0, 0, w, h));
                        dc.Pop();
                    }),
                    3 => canvas, // vuelve a lo que había antes
                    _ => full
                };
            }
            return result;
        }

        private static BitmapSource Render(int w, int h, Action<DrawingContext> draw)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) draw(dc);
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        private static int Meta(ImageMetadata? m, string query, int fallback)
        {
            try { return m is BitmapMetadata bm && bm.GetQuery(query) is { } v ? Convert.ToInt32(v) : fallback; }
            catch { return fallback; }
        }
    }
}
