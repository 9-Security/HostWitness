using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace WinDFIR.Providers;

/// <summary>
/// Best-effort Authenticode inspection without URL fetches: WinVerifyTrust uses
/// <c>WTD_CACHE_ONLY_URL_RETRIEVAL</c> so chain URL retrieval is cache-only; revocation checks are disabled.
/// Embedded publisher read uses <c>CryptQueryObject</c> on the local file only. Never throws to callers.
/// </summary>
internal static class LiveProcessAuthenticodeHelper
{
    /// <summary>WTD_CACHE_ONLY_URL_RETRIEVAL — only the local cache is used for URL-based chain data.</summary>
    private const uint WtdCacheOnlyUrlRetrieval = 0x00000800;
    private static readonly ConcurrentDictionary<string, (string Status, string Publisher)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private const int CertNameSimpleDisplayType = 4;
    private const int CertQueryObjectFile = 1;
    private const uint CertQueryContentPkcs7SignedEmbed = 0x00000200;
    private const uint CertQueryFormatBinary = 0x00000002;

    internal static void TryAddAuthenticodeFields(string? imagePath, IDictionary<string, object> fields)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return;

        try
        {
            if (Cache.TryGetValue(imagePath, out var cached))
            {
                fields["AuthenticodeStatus"] = cached.Status;
                if (!string.IsNullOrEmpty(cached.Publisher))
                    fields["AuthenticodePublisher"] = cached.Publisher;
                return;
            }

            var hr = WinVerifyTrustFile(imagePath, out var status);
            fields["AuthenticodeStatus"] = status;

            string publisher = string.Empty;
            if (hr == 0 && TryGetPublisherFromEmbeddedCert(imagePath, out var pub) &&
                !string.IsNullOrWhiteSpace(pub))
            {
                publisher = pub;
                fields["AuthenticodePublisher"] = publisher;
            }

            Cache[imagePath] = (status, publisher);
        }
        catch
        {
            if (!fields.ContainsKey("AuthenticodeStatus"))
                fields["AuthenticodeStatus"] = "Unknown";
        }
    }

    /// <summary>HRESULT from WinVerifyTrust mapped to a short analyst-facing label.</summary>
    private static int WinVerifyTrustFile(string path, out string status)
    {
        status = "Unknown";
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            pPolicyCallbackData = IntPtr.Zero,
            pSIPClientData = IntPtr.Zero,
            dwUIChoice = 2, // WTD_UI_NONE
            fdwRevocationChecks = 0, // WTD_REVOKE_NONE
            dwUnionChoice = 1, // WTD_CHOICE_FILE
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>()),
            dwStateAction = 0,
            hWVTStateData = IntPtr.Zero,
            pwszURLReference = IntPtr.Zero,
            dwProvFlags = WtdCacheOnlyUrlRetrieval,
            dwUIContext = 0
        };

        try
        {
            Marshal.StructureToPtr(fileInfo, data.pFile, false);
            var hr = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            status = MapWinVerifyHr(hr);
            return hr;
        }
        finally
        {
            if (data.pFile != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(data.pFile);
                Marshal.FreeHGlobal(data.pFile);
            }
        }
    }

    private static string MapWinVerifyHr(int hr)
    {
        if (hr == 0)
            return "Verified";

        const int trustNoSignature = unchecked((int)0x800B0100);
        const int trustSubjectFormUnknown = unchecked((int)0x800B0003);
        const int trustSubjectNotTrusted = unchecked((int)0x800B0004);
        const int nteBadSig = unchecked((int)0x80090006);
        const int trustExplicitDistrust = unchecked((int)0x800B0111);

        return hr switch
        {
            trustNoSignature => "NotSigned",
            trustSubjectFormUnknown => "UnknownFormat",
            trustSubjectNotTrusted => "NotTrusted",
            nteBadSig => "BadSignature",
            trustExplicitDistrust => "ExplicitDistrust",
            _ => $"CheckFailed(0x{hr:X8})"
        };
    }

    private static bool TryGetPublisherFromEmbeddedCert(string path, out string publisher)
    {
        publisher = string.Empty;
        IntPtr hStore = IntPtr.Zero;
        IntPtr hMsg = IntPtr.Zero;
        IntPtr pvContext = IntPtr.Zero;

        try
        {
            if (!CryptQueryObject(
                    CertQueryObjectFile,
                    path,
                    CertQueryContentPkcs7SignedEmbed,
                    CertQueryFormatBinary,
                    0,
                    out _,
                    out _,
                    out _,
                    out hStore,
                    out hMsg,
                    out pvContext))
            {
                return false;
            }

            if (pvContext == IntPtr.Zero)
                return false;

            var size = CertGetNameString(pvContext, CertNameSimpleDisplayType, 0, IntPtr.Zero, null, 0);
            if (size <= 1)
                return false;

            var sb = new StringBuilder(size);
            if (CertGetNameString(pvContext, CertNameSimpleDisplayType, 0, IntPtr.Zero, sb, size) <= 1)
                return false;

            publisher = sb.ToString().Trim();
            return !string.IsNullOrEmpty(publisher);
        }
        finally
        {
            if (pvContext != IntPtr.Zero)
                CertFreeCertificateContext(pvContext);
            if (hMsg != IntPtr.Zero)
                CryptMsgClose(hMsg);
            if (hStore != IntPtr.Zero)
                CertCloseStore(hStore, 0);
        }
    }

    #region WinTrust / crypt32

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptQueryObject(
        int dwObjectType,
        string pvObject,
        uint dwExpectedContentTypeFlags,
        uint dwExpectedFormatTypeFlags,
        uint dwFlags,
        out uint pdwEncoding,
        out uint pdwContentType,
        out uint pdwFormatType,
        out IntPtr phCertStore,
        out IntPtr phMsg,
        out IntPtr ppvContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CertCloseStore(IntPtr hCertStore, uint dwFlags);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptMsgClose(IntPtr hCryptMsg);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CertFreeCertificateContext(IntPtr pCertContext);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CertGetNameString(
        IntPtr pCertContext,
        int dwType,
        int dwFlags,
        IntPtr pvTypePara,
        StringBuilder? pszNameString,
        int cchNameString);

    #endregion
}
