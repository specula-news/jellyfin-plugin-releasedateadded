using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseDateAdded;

/// <summary>
/// Stores Jellyfin's original "Date added" before the plugin changes it,
/// so everything can be undone with the "restore original 'Date added'" task.
/// The file lives next to the plugin's settings file (plugins/configurations/).
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

    /// <summary>Date before and after the plugin's change.</summary>
    public sealed class Entry
    {
        /// <summary>Jellyfin's own date (file creation/scan/NFO).</summary>
        public DateTime Original { get; set; }

        /// <summary>The date the plugin set.</summary>
        public DateTime Applied { get; set; }
    }

    /// <summary>Records a change. If the item already has our date, the original is kept.</summary>
    public void Record(Guid itemId, DateTime oldValue, DateTime newValue)
    {
        lock (_lock)
        {
            var entries = Load();
            if (entries.TryGetValue(itemId, out var existing) && (existing.Applied - oldValue).Duration() < Tolerance)
            {
                // We are adjusting our own earlier value – the original is unchanged.
                existing.Applied = newValue;
            }
            else
            {
                entries[itemId] = new Entry { Original = oldValue, Applied = newValue };
            }

            _dirty = true;
        }
    }

    /// <summary>Copy of all entries.</summary>
    public List<KeyValuePair<Guid, Entry>> Snapshot()
    {
        lock (_lock)
        {
            return new List<KeyValuePair<Guid, Entry>>(Load());
        }
    }

    /// <summary>Removes an entry.</summary>
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

    /// <summary>Writes to disk if anything changed.</summary>
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
                _logger.LogError(ex, "{Prefix} could not save the backup of original dates.", Plugin.LogPrefix);
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
            _logger.LogError(ex, "{Prefix} could not read the backup of original dates.", Plugin.LogPrefix);
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
