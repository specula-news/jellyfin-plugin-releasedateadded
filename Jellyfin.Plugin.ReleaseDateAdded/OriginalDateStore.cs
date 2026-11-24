using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Sparar Jellyfins ursprungliga "Date added" innan pluginet ändrar det,
/// så att allt kan återställas med uppgiften "Återställ ursprungliga 'Date added'".
/// Filen ligger bredvid pluginets inställningsfil (plugins/configurations/).
/// </summary>
public sealed class OriginalDateStore
{
    private const string FileName = "Jellyfin.Plugin.ReleaseDateAdded.originaldates.json";
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly ILogger _logger;
    private Dictionary<Guid, Entry>? _entries;
    private string? _path;
    private bool _dirty;

    public OriginalDateStore(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Datum före och efter pluginets ändring.</summary>
    public sealed class Entry
    {
        /// <summary>Jellyfins eget datum (filskapning/skanning/NFO).</summary>
        public DateTime Original { get; set; }

        /// <summary>Datumet pluginet satte.</summary>
        public DateTime Applied { get; set; }
    }

    /// <summary>Noterar en ändring. Om objektet redan har vårt datum behålls det ursprungliga.</summary>
    public void Record(Guid itemId, DateTime oldValue, DateTime newValue)
    {
        lock (_lock)
        {
            var entries = Load();
            if (entries.TryGetValue(itemId, out var existing) && (existing.Applied - oldValue).Duration() < Tolerance)
            {
                // Vi justerar vårt eget tidigare värde – originalet är oförändrat.
                existing.Applied = newValue;
            }
            else
            {
                entries[itemId] = new Entry { Original = oldValue, Applied = newValue };
            }

            _dirty = true;
        }
    }

    /// <summary>Kopia av alla poster.</summary>
    public List<KeyValuePair<Guid, Entry>> Snapshot()
    {
        lock (_lock)
        {
            return new List<KeyValuePair<Guid, Entry>>(Load());
        }
    }

    /// <summary>Tar bort en post.</summary>
    public void Remove(Guid itemId)
    {
        lock (_lock)
        {
            if (Load().Remove(itemId))
            {
                _dirty = true;
            }
        }
    }

    /// <summary>Skriver till disk om något ändrats.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty || _entries is null)
            {
                return;
            }

            try
            {
                var path = GetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
                File.Move(tmp, path, true);
                _dirty = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Air Date Fixer: kunde inte spara backup av ursprungliga datum.");
            }
        }
    }

    private Dictionary<Guid, Entry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = new Dictionary<Guid, Entry>();
        try
        {
            var path = GetPath();
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<Guid, Entry>>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    _entries = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Air Date Fixer: kunde inte läsa backup av ursprungliga datum.");
        }

        return _entries;
    }

    private string GetPath()
    {
        if (_path is not null)
        {
            return _path;
        }

        var configFile = Plugin.Instance?.ConfigurationFilePath;
        var folder = string.IsNullOrEmpty(configFile) ? Path.GetTempPath() : Path.GetDirectoryName(configFile)!;
        _path = Path.Combine(folder, FileName);
        return _path;
    }
}
