namespace RiotFix.Core.Hosts;

/// <summary>File operations used for the hosts file. Abstracted so tests never touch the real hosts file.</summary>
public interface IFileSystem
{
    bool FileExists(string path);

    byte[] ReadAllBytes(string path);

    FileAttributes GetAttributes(string path);

    /// <summary>Creates or overwrites a file and flushes it to disk.</summary>
    void WriteAllBytesDurable(string path, byte[] data);

    /// <summary>Atomically replaces <paramref name="destination"/> with <paramref name="source"/> (keeps destination ACLs on Windows).</summary>
    void Replace(string source, string destination);

    void Move(string source, string destination);

    void Delete(string path);
}

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public void WriteAllBytesDurable(string path, byte[] data)
    {
        // Never write through a symbolic link / junction placed at the target path.
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"Refusing to write through a reparse point: {Path.GetFileName(path)}");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Refusing to write into a directory that is a reparse point.");
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    public void Replace(string source, string destination) => File.Replace(source, destination, destinationBackupFileName: null, ignoreMetadataErrors: false);

    public void Move(string source, string destination) => File.Move(source, destination, overwrite: false);

    public void Delete(string path) => File.Delete(path);
}
