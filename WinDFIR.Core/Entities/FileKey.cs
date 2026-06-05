namespace WinDFIR.Core.Entities;

/// <summary>
/// Global file identifier: (VolumeSerial, FileId) or Path+Hash
/// </summary>
public readonly record struct FileKey
{
    public string? VolumeSerial { get; init; }
    public ulong? FileId { get; init; }
    public string? Path { get; init; }
    public string? Hash { get; init; }

    public FileKey(string? volumeSerial, ulong? fileId, string? path, string? hash)
    {
        VolumeSerial = volumeSerial;
        FileId = fileId;
        Path = path;
        Hash = hash;
    }

    public override string ToString()
    {
        if (VolumeSerial != null && FileId.HasValue)
            return $"F:{VolumeSerial}:{FileId}";
        if (!string.IsNullOrEmpty(Path))
            return $"F:{Path}{(string.IsNullOrEmpty(Hash) ? "" : $":{Hash[..Math.Min(8, Hash.Length)]}")}";
        return "F:Unknown";
    }
}
