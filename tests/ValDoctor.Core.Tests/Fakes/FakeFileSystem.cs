using ValDoctor.Core.Hosts;

namespace ValDoctor.Core.Tests.Fakes;

/// <summary>In-memory file system with fault injection. Tests never touch a real hosts file.</summary>
public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileAttributes> _attributes = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Operations { get; } = [];

    /// <summary>Called with the operation name and path before each operation; throw from it to inject failures.</summary>
    public Action<string, string>? BeforeOperation { get; set; }

    /// <summary>When set, Replace writes these bytes instead of the source (simulates an interfering program).</summary>
    public byte[]? CorruptReplaceWith { get; set; }

    public void Seed(string path, byte[] content, FileAttributes attributes = FileAttributes.Normal)
    {
        _files[path] = content;
        _attributes[path] = attributes;
    }

    public byte[] Get(string path) => _files[path];

    public bool Has(string path) => _files.ContainsKey(path);

    public void SetRaw(string path, byte[] content) => _files[path] = content;

    /// <summary>Removes a file without going through the hooks (simulates another component deleting it).</summary>
    public void Remove(string path) => _files.Remove(path);

    public bool FileExists(string path)
    {
        Hook("exists", path);
        return _files.ContainsKey(path);
    }

    public byte[] ReadAllBytes(string path)
    {
        Hook("read", path);
        return _files.TryGetValue(path, out var data) ? [.. data] : throw new FileNotFoundException("not found", path);
    }

    public FileAttributes GetAttributes(string path)
    {
        Hook("attributes", path);
        return _attributes.TryGetValue(path, out var a) ? a : FileAttributes.Normal;
    }

    public void WriteAllBytesDurable(string path, byte[] data)
    {
        Hook("write", path);
        _files[path] = [.. data];
    }

    public void Replace(string source, string destination)
    {
        Hook("replace", destination);
        if (!_files.TryGetValue(source, out var data))
        {
            throw new FileNotFoundException("source missing", source);
        }

        _files[destination] = CorruptReplaceWith ?? data;
        _files.Remove(source);
    }

    public void Move(string source, string destination)
    {
        Hook("move", destination);
        if (_files.ContainsKey(destination))
        {
            throw new IOException("exists");
        }

        _files[destination] = _files[source];
        _files.Remove(source);
    }

    public void Delete(string path)
    {
        Hook("delete", path);
        _files.Remove(path);
    }

    private void Hook(string op, string path)
    {
        Operations.Add($"{op}:{path[(path.LastIndexOfAny(['\\', '/']) + 1)..]}");
        BeforeOperation?.Invoke(op, path);
    }
}

public static class IoErrors
{
    public static IOException SharingViolation() => new("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));
}
