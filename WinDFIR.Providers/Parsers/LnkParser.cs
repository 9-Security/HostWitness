using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WinDFIR.Providers.Parsers;

public class LnkParseResult
{
    public string TargetPath { get; set; } = string.Empty;
    public string LocalBasePath { get; set; } = string.Empty;
    public string CommonPathSuffix { get; set; } = string.Empty;
    public string NetworkPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string NameString { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public DateTime? LinkCreationTimeUtc { get; set; }
    public DateTime? LinkAccessTimeUtc { get; set; }
    public DateTime? LinkWriteTimeUtc { get; set; }
}

public static class LnkParser
{
    private const uint HasLinkTargetIdList = 0x00000001;
    private const uint HasLinkInfo = 0x00000002;
    private const uint HasName = 0x00000004;
    private const uint HasRelativePath = 0x00000008;
    private const uint HasWorkingDir = 0x00000010;
    private const uint HasArguments = 0x00000020;
    private const uint HasIconLocation = 0x00000040;
    private const uint IsUnicode = 0x00000080;

    public static LnkParseResult Parse(byte[] bytes)
    {
        var result = new LnkParseResult();

        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);

        if (bytes.Length < 0x4C)
            return result;

        var headerSize = reader.ReadUInt32();
        if (headerSize != 0x4C)
            return result;

        stream.Seek(0x14, SeekOrigin.Begin);
        var linkFlags = reader.ReadUInt32();

        stream.Seek(0x1C, SeekOrigin.Begin);
        result.LinkCreationTimeUtc = ReadFileTime(reader);
        result.LinkAccessTimeUtc = ReadFileTime(reader);
        result.LinkWriteTimeUtc = ReadFileTime(reader);

        stream.Seek(0x4C, SeekOrigin.Begin);

        if ((linkFlags & HasLinkTargetIdList) != 0)
        {
            var idListSize = reader.ReadUInt16();
            stream.Seek(idListSize, SeekOrigin.Current);
        }

        if ((linkFlags & HasLinkInfo) != 0 && stream.Position + 4 <= stream.Length)
        {
            var linkInfoStart = stream.Position;
            var linkInfoSize = reader.ReadUInt32();
            var linkInfoHeaderSize = reader.ReadUInt32();
            var linkInfoFlags = reader.ReadUInt32();
            var volumeIdOffset = reader.ReadUInt32();
            var localBasePathOffset = reader.ReadUInt32();
            var commonNetworkRelativeLinkOffset = reader.ReadUInt32();
            var commonPathSuffixOffset = reader.ReadUInt32();

            uint localBasePathOffsetUnicode = 0;
            uint commonPathSuffixOffsetUnicode = 0;
            uint commonNetworkRelativeLinkOffsetUnicode = 0;
            if (linkInfoHeaderSize >= 0x24 && stream.Position + 12 <= stream.Length)
            {
                localBasePathOffsetUnicode = reader.ReadUInt32();
                commonPathSuffixOffsetUnicode = reader.ReadUInt32();
                commonNetworkRelativeLinkOffsetUnicode = reader.ReadUInt32();
            }

            result.LocalBasePath = ReadLinkInfoString(bytes, linkInfoStart, localBasePathOffset, localBasePathOffsetUnicode);
            result.CommonPathSuffix = ReadLinkInfoString(bytes, linkInfoStart, commonPathSuffixOffset, commonPathSuffixOffsetUnicode);

            var networkOffset = commonNetworkRelativeLinkOffsetUnicode != 0
                ? commonNetworkRelativeLinkOffsetUnicode
                : commonNetworkRelativeLinkOffset;

            if (networkOffset != 0)
            {
                result.NetworkPath = ReadNetworkPath(bytes, linkInfoStart + networkOffset);
            }

            if (!string.IsNullOrWhiteSpace(result.LocalBasePath))
            {
                result.TargetPath = result.LocalBasePath + result.CommonPathSuffix;
            }
        }

        var isUnicode = (linkFlags & IsUnicode) != 0;

        if ((linkFlags & HasName) != 0)
        {
            result.NameString = ReadStringData(reader, isUnicode);
        }
        if ((linkFlags & HasRelativePath) != 0)
        {
            result.RelativePath = ReadStringData(reader, isUnicode);
        }
        if ((linkFlags & HasWorkingDir) != 0)
        {
            result.WorkingDirectory = ReadStringData(reader, isUnicode);
        }
        if ((linkFlags & HasArguments) != 0)
        {
            result.Arguments = ReadStringData(reader, isUnicode);
        }
        if ((linkFlags & HasIconLocation) != 0)
        {
            ReadStringData(reader, isUnicode);
        }

        return result;
    }

    private static DateTime? ReadFileTime(BinaryReader reader)
    {
        var low = reader.ReadUInt32();
        var high = reader.ReadUInt32();
        var fileTime = ((long)high << 32) | low;
        if (fileTime == 0)
            return null;

        return DateTime.FromFileTimeUtc(fileTime);
    }

    private static string ReadLinkInfoString(byte[] bytes, long linkInfoStart, uint offsetAnsi, uint offsetUnicode)
    {
        if (offsetUnicode != 0)
        {
            return ReadNullTerminatedString(bytes, (int)(linkInfoStart + offsetUnicode), true);
        }
        if (offsetAnsi != 0)
        {
            return ReadNullTerminatedString(bytes, (int)(linkInfoStart + offsetAnsi), false);
        }

        return string.Empty;
    }

    private static string ReadNetworkPath(byte[] bytes, long networkStructOffset)
    {
        if (networkStructOffset + 4 > bytes.Length)
            return string.Empty;

        var netNameOffset = BitConverter.ToUInt32(bytes, (int)networkStructOffset + 8);
        var netNameOffsetUnicode = 0u;
        var networkStructSize = BitConverter.ToUInt32(bytes, (int)networkStructOffset);
        var networkStructHeaderSize = BitConverter.ToUInt32(bytes, (int)networkStructOffset + 4);
        if (networkStructHeaderSize >= 0x14 && networkStructOffset + 0x14 <= bytes.Length)
        {
            netNameOffsetUnicode = BitConverter.ToUInt32(bytes, (int)networkStructOffset + 0x14);
        }

        if (netNameOffsetUnicode != 0)
        {
            return ReadNullTerminatedString(bytes, (int)(networkStructOffset + netNameOffsetUnicode), true);
        }
        if (netNameOffset != 0)
        {
            return ReadNullTerminatedString(bytes, (int)(networkStructOffset + netNameOffset), false);
        }

        return string.Empty;
    }

    private static string ReadStringData(BinaryReader reader, bool unicode)
    {
        if (reader.BaseStream.Position + 2 > reader.BaseStream.Length)
            return string.Empty;

        var length = reader.ReadUInt16();
        if (length == 0)
            return string.Empty;

        if (unicode)
        {
            var byteLength = length * 2;
            if (reader.BaseStream.Position + byteLength > reader.BaseStream.Length)
                return string.Empty;

            var bytes = reader.ReadBytes(byteLength);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        else
        {
            if (reader.BaseStream.Position + length > reader.BaseStream.Length)
                return string.Empty;

            var bytes = reader.ReadBytes(length);
            return Encoding.Default.GetString(bytes).TrimEnd('\0');
        }
    }

    private static string ReadNullTerminatedString(byte[] bytes, int offset, bool unicode)
    {
        if (offset < 0 || offset >= bytes.Length)
            return string.Empty;

        if (unicode)
        {
            var end = offset;
            while (end + 1 < bytes.Length)
            {
                if (bytes[end] == 0 && bytes[end + 1] == 0)
                    break;
                end += 2;
            }
            return Encoding.Unicode.GetString(bytes, offset, Math.Max(0, end - offset)).TrimEnd('\0');
        }
        else
        {
            var end = offset;
            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }
            return Encoding.Default.GetString(bytes, offset, Math.Max(0, end - offset)).TrimEnd('\0');
        }
    }
}
