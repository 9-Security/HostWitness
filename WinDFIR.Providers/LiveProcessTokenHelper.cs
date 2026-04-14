using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WinDFIR.Providers;

/// <summary>
/// Best-effort token queries for live process enrichment. Never throws to callers.
/// </summary>
internal static class LiveProcessTokenHelper
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    internal static void TryAddTokenFields(int processId, IDictionary<string, object> fields)
    {
        try
        {
            var hProcess = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (hProcess == IntPtr.Zero)
                return;

            try
            {
                if (!OpenProcessToken(hProcess, TokenQuery, out var hToken))
                    return;

                try
                {
                    TryAddOwner(hToken, fields);
                    TryAddIntegrity(hToken, fields);
                    TryAddSessionId(hToken, fields);
                }
                finally
                {
                    CloseHandle(hToken);
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
        catch
        {
            // Access denied / race: omit token fields only.
        }
    }

    private static void TryAddOwner(IntPtr hToken, IDictionary<string, object> fields)
    {
        if (!GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenUser, IntPtr.Zero, 0, out var need) &&
            Marshal.GetLastWin32Error() != 122)
            return;

        var buf = Marshal.AllocHGlobal((int)need);
        try
        {
            if (!GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenUser, buf, need, out _))
                return;

            var tu = Marshal.PtrToStructure<TOKEN_USER>(buf);
            if (tu.User.Sid == IntPtr.Zero)
                return;

            var sid = SidToSecurityIdentifier(tu.User.Sid);
            if (sid == null)
                return;

            fields["OwnerSid"] = sid.Value;

            try
            {
                var account = sid.Translate(typeof(NTAccount));
                var name = account.Value;
                if (!string.IsNullOrWhiteSpace(name))
                    fields["UserName"] = name;
            }
            catch
            {
                // Leave UserName unchanged if translation fails.
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static SecurityIdentifier? SidToSecurityIdentifier(IntPtr pSid)
    {
        try
        {
            var len = GetLengthSid(pSid);
            if (len <= 0)
                return null;

            var bytes = new byte[len];
            Marshal.Copy(pSid, bytes, 0, len);
            return new SecurityIdentifier(bytes, 0);
        }
        catch
        {
            return null;
        }
    }

    private static void TryAddIntegrity(IntPtr hToken, IDictionary<string, object> fields)
    {
        if (!GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, IntPtr.Zero, 0, out var need) &&
            Marshal.GetLastWin32Error() != 122)
            return;

        var buf = Marshal.AllocHGlobal((int)need);
        try
        {
            if (!GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buf, need, out _))
                return;

            var til = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buf);
            if (til.Label.Sid == IntPtr.Zero)
                return;

            var sid = SidToSecurityIdentifier(til.Label.Sid);
            if (sid == null)
                return;

            if (!TryGetMandatoryLabelRid(sid, out var rid))
                return;

            fields["Integrity"] = MapIntegrityRid(rid);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static bool TryGetMandatoryLabelRid(SecurityIdentifier sid, out uint rid)
    {
        rid = 0;
        var v = sid.Value;
        if (!v.StartsWith("S-1-16-", StringComparison.OrdinalIgnoreCase))
            return false;

        var i = v.LastIndexOf('-');
        if (i < 0 || i >= v.Length - 1)
            return false;

        return uint.TryParse(v.AsSpan((i + 1)), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out rid);
    }

    private static string MapIntegrityRid(uint rid)
    {
        // S-1-16-* RIDs (SECURITY_MANDATORY_*)
        return rid switch
        {
            0x00000000 => "Untrusted",
            0x00001000 => "Low",
            0x00002000 => "Medium",
            0x00002100 => "MediumPlus",
            0x00003000 => "High",
            0x00004000 => "System",
            0x00005000 => "Protected",
            _ => $"Label({rid})"
        };
    }

    private static void TryAddSessionId(IntPtr hToken, IDictionary<string, object> fields)
    {
        var buf = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            if (!GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenSessionId, buf, sizeof(uint), out _))
                return;

            var session = (uint)Marshal.ReadInt32(buf);
            fields["SessionId"] = (int)session;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    #region Interop

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TOKEN_INFORMATION_CLASS tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int GetLengthSid(IntPtr sid);

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_USER
    {
        public SID_AND_ATTRIBUTES User;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenUser = 1,
        TokenSessionId = 12,
        TokenIntegrityLevel = 25
    }

    #endregion
}
