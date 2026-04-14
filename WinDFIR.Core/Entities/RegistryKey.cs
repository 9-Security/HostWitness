namespace WinDFIR.Core.Entities;

/// <summary>
/// Global registry key identifier: Normalized Registry Path
/// </summary>
public readonly record struct RegistryKey
{
    public string NormalizedPath { get; init; }

    public RegistryKey(string normalizedPath)
    {
        NormalizedPath = normalizedPath ?? throw new ArgumentNullException(nameof(normalizedPath));
    }

    public override string ToString() => $"R:{NormalizedPath}";
}
