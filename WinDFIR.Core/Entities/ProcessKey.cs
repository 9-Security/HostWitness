namespace WinDFIR.Core.Entities;

/// <summary>
/// Global process identifier: (BootId, PID, CreateTime)
/// Ensures uniqueness across reboots and process reuse.
/// </summary>
public readonly record struct ProcessKey
{
    public ulong BootId { get; init; }
    public uint ProcessId { get; init; }
    public DateTime CreateTime { get; init; }

    public ProcessKey(ulong bootId, uint processId, DateTime createTime)
    {
        BootId = bootId;
        ProcessId = processId;
        CreateTime = createTime;
    }

    public override string ToString() => $"P:{BootId:X}:{ProcessId}:{CreateTime:O}";
}
