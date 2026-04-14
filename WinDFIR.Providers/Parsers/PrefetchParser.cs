using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using WinDFIR.Core.Normalization;

namespace WinDFIR.Providers.Parsers;

public class PrefetchRecord
{
    public string PrefetchFileName { get; set; } = string.Empty;
    public string PrefetchFilePath { get; set; } = string.Empty;
    public DateTime CreatedTimeUtc { get; set; }
    public DateTime ModifiedTimeUtc { get; set; }
    public long FileSize { get; set; }
    public int Version { get; set; }
    public string ProcessExe { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;
    public int RunCount { get; set; }
    public List<DateTime> RunTimesUtc { get; set; } = new();
    public List<string> ReferencedFiles { get; set; } = new();
}

public static class PrefetchParser
{
    public static PrefetchRecord? Parse(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch
        {
            return null;
        }

        bytes = EnsureDecompressed(bytes);
        if (bytes.Length < 0x84)
            return null;

        var version = BitConverter.ToInt32(bytes, 0x00);
        var signature = Encoding.ASCII.GetString(bytes, 0x04, 4);
        if (!string.Equals(signature, "SCCA", StringComparison.OrdinalIgnoreCase))
            return null;

        // Supported versions: 10/13/15 (legacy), 17 (Vista/7), 23 (Win7), 26 (Win8.1), 30 (Win10), 31 (Win11)
        if (version != 10 && version != 13 && version != 15 && version != 17 && version != 23 && version != 26 && version != 30 && version != 31)
            return null;

        var fileInfoOffset = 0x54;
        if (fileInfoOffset + 0x90 > bytes.Length)
            return null;

        var exeName = ReadUnicodeString(bytes, 0x10, 60);
        var record = new PrefetchRecord
        {
            PrefetchFileName = Path.GetFileName(filePath),
            PrefetchFilePath = filePath,
            CreatedTimeUtc = File.GetCreationTimeUtc(filePath),
            ModifiedTimeUtc = File.GetLastWriteTimeUtc(filePath),
            FileSize = new FileInfo(filePath).Length,
            Version = version,
            ProcessExe = exeName
        };

        var filenameStringsOffset = ReadInt32(bytes, 0x64);
        var filenameStringsSize = ReadInt32(bytes, 0x68);
        if (filenameStringsOffset > 0 && filenameStringsSize > 0 &&
            filenameStringsOffset + filenameStringsSize <= bytes.Length)
        {
            var list = ReadUnicodeStringList(bytes, filenameStringsOffset, filenameStringsSize);
            record.ReferencedFiles = list;
            record.ProcessPath = FindProcessPath(list, exeName);
        }

        record.RunTimesUtc = ReadRunTimes(bytes, version);
        record.RunCount = ReadRunCount(bytes, version, record.RunTimesUtc.Count);

        return record;
    }

    private static List<DateTime> ReadRunTimes(byte[] bytes, int version)
    {
        var runTimes = new List<DateTime>();

        int[] offsets;
        if (version == 17 || version == 10 || version == 13 || version == 15)
        {
            offsets = new[] { 0x78 };
        }
        else if (version == 23)
        {
            offsets = new[] { 0x80 };
        }
        else
        {
            offsets = Enumerable.Range(0, 8).Select(i => 0x80 + (i * 8)).ToArray();
        }

        foreach (var offset in offsets)
        {
            if (offset + 8 > bytes.Length)
                continue;

            var fileTime = BitConverter.ToUInt64(bytes, offset);
            if (fileTime == 0)
                continue;

            var runTime = TimeNormalizer.FromFileTime(fileTime);
            if (runTime > DateTime.MinValue && runTime < DateTime.UtcNow.AddYears(1))
            {
                runTimes.Add(runTime);
            }
        }

        return runTimes.Distinct().OrderByDescending(t => t).ToList();
    }

    private static int ReadRunCount(byte[] bytes, int version, int fallback)
    {
        var offset = version switch
        {
            10 or 13 or 15 or 17 => 0x90,
            23 => 0x98,
            _ => 0xD0
        };
        if (offset + 4 > bytes.Length)
            return fallback;

        var count = BitConverter.ToInt32(bytes, offset);
        return count > 0 ? count : fallback;
    }

    private static string ReadUnicodeString(byte[] bytes, int offset, int maxBytes)
    {
        if (offset + maxBytes > bytes.Length)
            return string.Empty;

        var raw = Encoding.Unicode.GetString(bytes, offset, maxBytes);
        return raw.TrimEnd('\0', ' ');
    }

    private static List<string> ReadUnicodeStringList(byte[] bytes, int offset, int size)
    {
        var raw = Encoding.Unicode.GetString(bytes, offset, size);
        return raw.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ReadInt32(byte[] bytes, int offset)
    {
        if (offset + 4 > bytes.Length)
            return 0;
        return BitConverter.ToInt32(bytes, offset);
    }

    private static string FindProcessPath(List<string> files, string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
            return string.Empty;

        var match = files.FirstOrDefault(f =>
            f.EndsWith("\\" + exeName, StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith("/" + exeName, StringComparison.OrdinalIgnoreCase));

        return match ?? string.Empty;
    }

    private static byte[] EnsureDecompressed(byte[] bytes)
    {
        if (bytes.Length < 8)
            return bytes;

        if (bytes[0] != (byte)'M' || bytes[1] != (byte)'A' || bytes[2] != (byte)'M')
            return bytes;

        var algorithm = bytes[3];
        var hasChecksum = (algorithm & 0x80) != 0;
        var compressionFormat = (ushort)(algorithm & 0x7F);
        var uncompressedSize = BitConverter.ToInt32(bytes, 4);
        const int maxUncompressedSize = 100 * 1024 * 1024;
        if (uncompressedSize <= 0 || uncompressedSize > maxUncompressedSize)
            return bytes;

        var dataOffset = 8 + (hasChecksum ? 4 : 0);
        if (dataOffset >= bytes.Length)
            return bytes;

        var compressed = new byte[bytes.Length - dataOffset];
        Buffer.BlockCopy(bytes, dataOffset, compressed, 0, compressed.Length);

        try
        {
            return DecompressBuffer(compressionFormat, compressed, uncompressedSize);
        }
        catch
        {
            return bytes;
        }
    }

    private static byte[] DecompressBuffer(ushort format, byte[] compressed, int uncompressedSize)
    {
        var status = RtlGetCompressionWorkSpaceSize(format, out var workspaceSize, out _);
        if (status != 0)
            return compressed;

        var workspace = Marshal.AllocHGlobal((int)workspaceSize);
        try
        {
            var output = new byte[uncompressedSize];
            status = RtlDecompressBufferEx(
                format,
                output,
                output.Length,
                compressed,
                compressed.Length,
                out var finalSize,
                workspace);

            if (status != 0)
                return compressed;

            if (finalSize > 0 && finalSize <= output.Length)
            {
                if (finalSize == output.Length)
                    return output;

                var trimmed = new byte[finalSize];
                Buffer.BlockCopy(output, 0, trimmed, 0, finalSize);
                return trimmed;
            }

            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(workspace);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetCompressionWorkSpaceSize(
        ushort CompressionFormat,
        out uint CompressBufferWorkSpaceSize,
        out uint CompressFragmentWorkSpaceSize);

    [DllImport("ntdll.dll")]
    private static extern int RtlDecompressBufferEx(
        ushort CompressionFormat,
        byte[] UncompressedBuffer,
        int UncompressedBufferSize,
        byte[] CompressedBuffer,
        int CompressedBufferSize,
        out int FinalUncompressedSize,
        IntPtr WorkSpace);
}
