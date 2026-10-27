using System.Text.Json.Serialization;

namespace Voxta.Modules.Aios.Spotify.Tools;

internal sealed record SpotifySearchItem(
    [property: JsonIgnore] string Uri,
    [property: JsonIgnore] string Type,
    string Name,
    string[]? Artists = null,
    string? Album = null,
    string? ReleaseDate = null,
    string? ReleaseDatePrecision = null,
    string? Owner = null,
    string? Description = null)
{
    public string Selection { get; init; } = "";
}

// Session-local bindings keep approval text tied to one exact URI. Access is serialized by the handler.
internal sealed class SpotifyToolSelections
{
    private const int Capacity = 200;
    private readonly Dictionary<string, SpotifySearchItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uris = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private long _nextNumber;

    public SpotifySearchItem Remember(SpotifySearchItem item)
    {
        if (_uris.TryGetValue(item.Uri, out var existing)) return _items[existing];

        var label = $"{item.Type}: {item.Name}";
        if (item.Artists is { Length: > 0 }) label += " — " + string.Join(", ", item.Artists);
        if (!string.IsNullOrWhiteSpace(item.Album)) label += " — " + item.Album;
        if (!string.IsNullOrWhiteSpace(item.Owner)) label += " — " + item.Owner;
        if (!string.IsNullOrWhiteSpace(item.ReleaseDate)) label += $" (released {item.ReleaseDate})";
        // Never reuse a label, even after eviction: a stale approval must not select another item.
        item = item with { Selection = $"{label} [{++_nextNumber}]" };
        if (_items.Count == Capacity)
        {
            var expired = _order.Dequeue();
            _uris.Remove(_items[expired].Uri);
            _items.Remove(expired);
        }
        _items.Add(item.Selection, item);
        _uris.Add(item.Uri, item.Selection);
        _order.Enqueue(item.Selection);
        return item;
    }

    public SpotifySearchItem? Resolve(string selection) => _items.GetValueOrDefault(selection);
}
