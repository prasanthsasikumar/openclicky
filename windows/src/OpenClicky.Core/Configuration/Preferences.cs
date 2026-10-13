// The UserDefaults replacement (new on Windows): a JSON key/value file at
// %LOCALAPPDATA%\OpenClicky\preferences.json, with the Mac's key names ("dictation.engine",
// "openclicky.account.plan", …). Typed getters take the default the Mac's code used; a value of
// the wrong kind reads as missing. Thread-safe; every write lands atomically (temp file, then
// rename); `Changed` names the key that changed.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClicky.Core.Configuration;

public sealed class Preferences
{
    private static readonly Lazy<Preferences> SharedInstance = new(() => new Preferences(OpenClickyPaths.PreferencesFile));

    /// <summary>The app's preferences (UserDefaults.standard).</summary>
    public static Preferences Shared => SharedInstance.Value;

    private readonly object storeLock = new();
    private readonly Dictionary<string, JsonNode?> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Action>> observers = new(StringComparer.Ordinal);

    public string FilePath { get; }

    /// <summary>Raised after a key was set or removed, with the key; on the thread that changed it.</summary>
    public event Action<string>? Changed;

    public Preferences(string filePath)
    {
        FilePath = filePath;
        Load();
    }

    /// <summary>A store that is never written to disk (tests, previews).</summary>
    public static Preferences InMemory() => new(filePath: "");

    private void Load()
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject root) return;
            foreach (var (key, value) in root) values[key] = value?.DeepClone();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"preferences: {FilePath} does not read, starting empty: {exception.Message}");
        }
    }

    // MARK: reading

    public bool Contains(string key)
    {
        lock (storeLock) return values.ContainsKey(key);
    }

    /// <summary>The stored JSON value (UserDefaults.object(forKey:)), null when absent.</summary>
    public JsonNode? Object(string key)
    {
        lock (storeLock) return values.TryGetValue(key, out var value) ? value?.DeepClone() : null;
    }

    public IReadOnlyCollection<string> Keys
    {
        get { lock (storeLock) return values.Keys.ToList(); }
    }

    private JsonValue? Value(string key)
    {
        lock (storeLock) return values.TryGetValue(key, out var node) ? node as JsonValue : null;
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        var value = Value(key);
        if (value is null) return defaultValue;
        if (value.TryGetValue<bool>(out var boolean)) return boolean;
        if (value.TryGetValue<double>(out var number)) return number != 0;
        return defaultValue;
    }

    public bool? GetOptionalBool(string key)
    {
        var value = Value(key);
        return value is not null && value.TryGetValue<bool>(out var boolean) ? boolean : null;
    }

    public int GetInt(string key, int defaultValue = 0)
    {
        var value = Value(key);
        if (value is null) return defaultValue;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<double>(out var number) && number >= int.MinValue && number <= int.MaxValue) return (int)number;
        if (value.TryGetValue<bool>(out var boolean)) return boolean ? 1 : 0;
        return defaultValue;
    }

    public double GetDouble(string key, double defaultValue = 0)
    {
        var value = Value(key);
        if (value is null) return defaultValue;
        if (value.TryGetValue<double>(out var number)) return number;
        return defaultValue;
    }

    public double? GetOptionalDouble(string key) => Value(key) is { } value && value.TryGetValue<double>(out var number) ? number : null;

    public string? GetString(string key, string? defaultValue = null)
    {
        var value = Value(key);
        return value is not null && value.TryGetValue<string>(out var text) ? text : defaultValue;
    }

    public string[]? GetStringArray(string key)
    {
        JsonNode? node;
        lock (storeLock) node = values.TryGetValue(key, out var stored) ? stored : null;
        if (node is not JsonArray array) return null;
        var strings = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var text)) strings.Add(text);
        }
        return strings.ToArray();
    }

    /// <summary>An enum stored by its raw string (Swift's rawValue); missing or unknown → the default.</summary>
    public TEnum GetEnum<TEnum>(string key, TEnum defaultValue) where TEnum : struct, Enum =>
        RawValues.FromRawValue<TEnum>(GetString(key)) ?? defaultValue;

    /// <summary>Any JSON-serialisable value (a small record or list), null when absent or unreadable.</summary>
    public T? GetJson<T>(string key)
    {
        var node = Object(key);
        if (node is null) return default;
        try { return node.Deserialize<T>(); }
        catch (JsonException) { return default; }
    }

    // MARK: writing

    public void Set(string key, bool value) => SetNode(key, JsonValue.Create(value));
    public void Set(string key, int value) => SetNode(key, JsonValue.Create(value));
    public void Set(string key, double value) => SetNode(key, JsonValue.Create(value));

    /// <summary>A null string removes the key, as UserDefaults.set(nil, forKey:) does.</summary>
    public void Set(string key, string? value)
    {
        if (value is null) Remove(key);
        else SetNode(key, JsonValue.Create(value));
    }

    public void Set(string key, IEnumerable<string>? value)
    {
        if (value is null) { Remove(key); return; }
        SetNode(key, new JsonArray(value.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()));
    }

    public void SetEnum<TEnum>(string key, TEnum value) where TEnum : struct, Enum => Set(key, value.RawValue());

    public void SetJson<T>(string key, T value) => SetNode(key, JsonSerializer.SerializeToNode(value));

    public void Remove(string key)
    {
        bool removed;
        lock (storeLock)
        {
            removed = values.Remove(key);
            if (removed) SaveLocked();
        }
        if (removed) NotifyChanged(key);
    }

    private void SetNode(string key, JsonNode? node)
    {
        // Stored as it will read back from the file, so an int set here reads as a double too.
        node = node is null ? null : JsonNode.Parse(node.ToJsonString());
        lock (storeLock)
        {
            if (values.TryGetValue(key, out var existing) && JsonNode.DeepEquals(existing, node)) return;
            values[key] = node;
            SaveLocked();
        }
        NotifyChanged(key);
    }

    /// <summary>Calls `handler` whenever `key` changes; dispose the result to stop.</summary>
    public IDisposable Observe(string key, Action handler)
    {
        lock (storeLock)
        {
            if (!observers.TryGetValue(key, out var list)) observers[key] = list = [];
            list.Add(handler);
        }
        return new Observation(() =>
        {
            lock (storeLock)
            {
                if (observers.TryGetValue(key, out var list)) list.Remove(handler);
            }
        });
    }

    private void NotifyChanged(string key)
    {
        Action[] handlers;
        lock (storeLock) handlers = observers.TryGetValue(key, out var list) ? list.ToArray() : [];
        foreach (var handler in handlers) handler();
        Changed?.Invoke(key);
    }

    private void SaveLocked()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        var root = new JsonObject();
        foreach (var key in values.Keys.Order(StringComparer.Ordinal)) root[key] = values[key]?.DeepClone();
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        try
        {
            AtomicFile.WriteAllText(FilePath, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"preferences: could not write {FilePath}: {exception.Message}");
        }
    }

    private sealed class Observation(Action dispose) : IDisposable
    {
        private Action? disposeAction = dispose;
        public void Dispose() => Interlocked.Exchange(ref disposeAction, null)?.Invoke();
    }
}

/// <summary>Writes a file whole or not at all: a temp file beside it, then a replacing rename.</summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] data)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, data);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static void WriteAllText(string path, string text) =>
        WriteAllBytes(path, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
}
