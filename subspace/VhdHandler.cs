using DiscUtils;
using DiscUtils.FileSystems;

namespace Subspace;

public class VhdHandler : IDisposable
{
    private readonly Stream _vhdxStream;
    private readonly VirtualDisk _disk;
    private readonly DiscFileSystem _fs;

    public string CurrentPath { get; private set; } = @"\";

    public VhdHandler(string vhdxPath)
    {
        FileSystemManager.RegisterFileSystems(typeof(DiscUtils.Ntfs.NtfsFileSystem).Assembly);
        FileSystemManager.RegisterFileSystems(typeof(DiscUtils.Fat.FatFileSystem).Assembly);

        _vhdxStream = File.Open(vhdxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _disk = CreateDisk(vhdxPath, _vhdxStream);
        _fs = OpenFileSystem(_disk);
    }

    private static VirtualDisk CreateDisk(string path, Stream stream)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".vhd")
            return new DiscUtils.Vhd.Disk(stream, DiscUtils.Streams.Ownership.None);
        return new DiscUtils.Vhdx.Disk(stream, DiscUtils.Streams.Ownership.None);
    }

    private static DiscFileSystem OpenFileSystem(VirtualDisk disk)
    {
        var volumes = VolumeManager.GetPhysicalVolumes(disk.Content);
        foreach (var volume in volumes)
        {
            var infos = FileSystemManager.DetectFileSystems(volume);
            foreach (var info in infos)
            {
                try
                {
                    return info.Open(volume);
                }
                catch
                {
                    // try next candidate
                }
            }
        }
        throw new InvalidOperationException("No supported filesystem found inside the VHD/X.");
    }

    public List<FileEntry> ListEntries()
    {
        var results = new List<FileEntry>();

        if (CurrentPath != @"\")
        {
            results.Add(new FileEntry("..", true));
        }

        foreach (var dir in _fs.GetDirectories(CurrentPath))
        {
            var name = Path.GetFileName(dir.TrimEnd('\\'));
            results.Add(new FileEntry(name, true));
        }

        foreach (var file in _fs.GetFiles(CurrentPath))
        {
            var name = Path.GetFileName(file);
            results.Add(new FileEntry(name, false));
        }

        return results;
    }

    public bool TryEnter(string name)
    {
        if (name == "..")
        {
            GoUp();
            return true;
        }
        var full = Combine(CurrentPath, name);
        if (_fs.DirectoryExists(full))
        {
            CurrentPath = full;
            return true;
        }
        return false;
    }

    public void GoUp()
    {
        if (CurrentPath == @"\") return;
        var parent = Path.GetDirectoryName(CurrentPath.TrimEnd('\\'));
        CurrentPath = string.IsNullOrEmpty(parent) ? @"\" : parent;
    }

    public Stream OpenFile(string name)
    {
        var full = Combine(CurrentPath, name);
        return _fs.OpenFile(full, FileMode.Open, FileAccess.Read);
    }

    private static string Combine(string path, string name)
    {
        if (path == @"\") return @"\" + name;
        return path.TrimEnd('\\') + "\\" + name;
    }

    public void Dispose()
    {
        _fs?.Dispose();
        _disk?.Dispose();
        _vhdxStream?.Dispose();
    }
}

public record FileEntry(string Name, bool IsDirectory);
