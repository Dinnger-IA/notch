using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using AgentManagerNotch.Models;

namespace AgentManagerNotch.Views
{
    /// <summary>
    /// Ventana sobre el historial de una sesión: solo los últimos <see cref="Page"/> mensajes que cumplen el filtro.
    /// Pintar el historial entero (con Markdown) al cambiar de pestaña trababa el notch; los anteriores se cargan de
    /// <see cref="Page"/> en <see cref="Page"/> al desplazarse hacia arriba (<see cref="LoadOlder"/>).
    /// Hay que llamar a <see cref="Detach"/> al dejar de usarla.
    /// </summary>
    public sealed class MessageWindow : ObservableCollection<ChatMessage>
    {
        public const int Page = 10;

        private readonly ObservableCollection<ChatMessage> _source;
        private readonly Func<ChatMessage, bool> _filter;
        private int _limit = Page;

        public MessageWindow(ObservableCollection<ChatMessage> source, Func<ChatMessage, bool> filter)
        {
            _source = source;
            _filter = filter;
            Rebuild();
            _source.CollectionChanged += Source_CollectionChanged;
        }

        public void Detach() => _source.CollectionChanged -= Source_CollectionChanged;

        /// <summary>Añade arriba los <see cref="Page"/> anteriores. Devuelve false si ya no quedan.</summary>
        public bool LoadOlder()
        {
            var older = Count == 0 ? Array.Empty<ChatMessage>()
                : _source.TakeWhile(m => m != this[0]).Where(_filter).TakeLast(Page).ToArray();
            if (older.Length == 0) return false;
            for (int i = 0; i < older.Length; i++) Insert(i, older[i]);
            _limit = Count;
            return true;
        }

        private void Rebuild()
        {
            Clear();
            foreach (var m in _source.Where(_filter).TakeLast(_limit)) Add(m);
        }

        private void Source_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add when e.NewItems != null && e.NewStartingIndex >= _source.Count - e.NewItems.Count:
                    foreach (ChatMessage m in e.NewItems)
                    {
                        if (!_filter(m)) continue;
                        Add(m);
                        // Sin anteriores cargados se queda en la última página; si el usuario está leyendo más arriba,
                        // la ventana crece para no mover lo que tiene delante
                        if (_limit == Page && Count > _limit) RemoveAt(0);
                        else _limit = Math.Max(_limit, Count);
                    }
                    break;
                case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                    foreach (ChatMessage m in e.OldItems) Remove(m);
                    break;
                default:
                    _limit = Page;
                    Rebuild();
                    break;
            }
        }
    }
}
