namespace WinDFIR.Core.Entities;

/// <summary>
/// Global user identifier: Windows SID
/// </summary>
public readonly record struct UserKey
{
    public string Sid { get; init; }

    public UserKey(string sid)
    {
        Sid = sid ?? throw new ArgumentNullException(nameof(sid));
    }

    public override string ToString() => $"U:{Sid}";
}
