namespace WinDFIR.Core.Entities;

/// <summary>
/// Global host identifier: Machine SID / Hostname
/// </summary>
public readonly record struct HostKey
{
    public string MachineSid { get; init; }
    public string Hostname { get; init; }

    public HostKey(string machineSid, string hostname)
    {
        MachineSid = machineSid;
        Hostname = hostname;
    }

    public override string ToString() => $"H:{Hostname}({MachineSid})";
}
