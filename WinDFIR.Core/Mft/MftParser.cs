using System.Text;

namespace WinDFIR.Core.Mft;

/// <summary>
/// Minimal MFT (Master File Table) parser for FILE records. Parses $STANDARD_INFORMATION and $FILE_NAME resident attributes.
/// Skips extension records and invalid/corrupted entries.
/// </summary>
public static class MftParser
{
    public readonly record struct RecordSizeDetectionResult(int RecordSize, int ParsedEntryCount, bool IsAutoDetected, bool IsAmbiguous);

    private const uint FileSignature = 0x454C4946; // "FILE" in little-endian
    private const int UsaOffsetInRecord = 0x04;
    private const int UsaCountInRecord = 0x06;
    private const int FirstAttributeOffsetInRecord = 0x14;
    private const int FlagsOffsetInRecord = 0x16;
    private const int UsedSizeOffsetInRecord = 0x18;
    private const int BaseRecordReferenceOffsetInRecord = 0x20;
    private const uint AttributeTypeStandardInformation = 0x10;
    private const uint AttributeTypeFileName = 0x30;
    private const uint EndOfAttributes = 0xFFFFFFFF;
    private const int AttributeLengthOffset = 0x04;
    private const int NonResidentFlagOffset = 0x08;
    private const int ResidentValueLengthOffset = 0x10;
    private const int ResidentValueOffsetOffset = 0x14;
    private const int FileNameLengthOffsetInContent = 0x40;
    private const int FileNameNamespaceOffsetInContent = 0x41;
    private const int FileNameOffsetInContent = 0x42;
    private const int FileFlagsOffsetInFileNameContent = 0x38;
    private const uint FileDirectoryFlag = 0x10;
    private const int FiletimeSize = 8;
    private const int ParentDirectoryOffsetInFileName = 0x00;
    private const int CreationTimeOffsetInFileName = 0x08;
    private const int ModifiedTimeOffsetInFileName = 0x10;
    private const long ParentRecordMask = 0x0000_FFFF_FFFF_FFFFL;
    private const int TimeStompToleranceSeconds = 2;
    private const int DefaultRecordSize = 1024;
    private const int RecordSizeSampleByteLimit = 8 * 1024 * 1024;
    private static readonly int[] DefaultRecordSizeCandidates = [1024, 4096];

    /// <summary>
    /// Parses MFT records from a stream. Yields one <see cref="MftEntry"/> per valid base FILE record.
    /// </summary>
    public static IEnumerable<MftEntry> Parse(Stream stream, int recordSize = 1024)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));
        if (recordSize < 64)
            throw new ArgumentOutOfRangeException(nameof(recordSize), "Record size must be at least 64.");

        var buffer = new byte[recordSize];
        long recordIndex = 0;

        while (true)
        {
            int read = ReadExactly(stream, buffer, 0, recordSize);
            if (read == 0)
                yield break;
            if (read < recordSize)
                yield break;

            if (TryParseRecord(buffer, recordIndex, out var entry))
                yield return entry;

            recordIndex++;
        }
    }

    /// <summary>
    /// Attempts to identify the most likely MFT record size for raw bytes from an exported $MFT stream.
    /// Returns 1024 when no candidate can be identified confidently.
    /// </summary>
    public static RecordSizeDetectionResult DetectRecordSize(byte[] bytes, IEnumerable<int>? candidateRecordSizes = null)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));

        var candidates = (candidateRecordSizes ?? DefaultRecordSizeCandidates)
            .Where(recordSize => recordSize >= 64)
            .Distinct()
            .ToList();
        if (candidates.Count == 0)
            candidates.Add(DefaultRecordSize);

        var scores = candidates
            .Select(recordSize => ScoreRecordSizeCandidate(bytes, recordSize))
            .OrderByDescending(score => score.ParsedEntryCount)
            .ThenByDescending(score => score.ParseRatio)
            .ThenBy(score => score.RecordSize == DefaultRecordSize ? 0 : 1)
            .ThenBy(score => score.RecordSize)
            .ToList();

        var best = scores[0];
        if (best.ParsedEntryCount == 0)
            return new RecordSizeDetectionResult(DefaultRecordSize, 0, IsAutoDetected: false, IsAmbiguous: false);

        var second = scores.Count > 1 ? scores[1] : default;
        bool isAmbiguous = second.ParsedEntryCount > 0
            && best.ParsedEntryCount < (second.ParsedEntryCount * 2)
            && best.ParseRatio < (second.ParseRatio * 1.5);

        return new RecordSizeDetectionResult(best.RecordSize, best.ParsedEntryCount, IsAutoDetected: true, IsAmbiguous: isAmbiguous);
    }

    private static bool TryParseRecord(byte[] sourceRecord, long recordIndex, out MftEntry entry)
    {
        entry = default!;

        if (sourceRecord.Length < 64 || BitConverter.ToUInt32(sourceRecord, 0) != FileSignature)
            return false;

        var record = (byte[])sourceRecord.Clone();
        if (!TryApplyUsaFixup(record))
            return false;

        int usedSize = ParseUsedSize(record);
        if (usedSize < 64)
            return false;

        long baseRecordReference = BitConverter.ToInt64(record, BaseRecordReferenceOffsetInRecord) & ParentRecordMask;
        if (baseRecordReference != 0)
            return false;

        ushort firstAttrOffset = BitConverter.ToUInt16(record, FirstAttributeOffsetInRecord);
        if (firstAttrOffset < 0x18 || firstAttrOffset >= usedSize)
            return false;

        ushort flags = BitConverter.ToUInt16(record, FlagsOffsetInRecord);
        bool isInUse = (flags & 1) != 0;

        string? fileName = null;
        int bestFileNameScore = int.MinValue;
        bool isDirectory = false;
        DateTime? createdUtc = null;
        DateTime? modifiedUtc = null;
        long? parentRecordIndex = null;
        DateTime? createdUtcFn = null;
        DateTime? modifiedUtcFn = null;

        int attrOffset = firstAttrOffset;
        while (attrOffset <= usedSize - 8)
        {
            uint attrType = BitConverter.ToUInt32(record, attrOffset);
            if (attrType == EndOfAttributes)
                break;

            int attrLength = (int)BitConverter.ToUInt32(record, attrOffset + AttributeLengthOffset);
            if (attrLength <= 0 || attrOffset + attrLength > usedSize)
                break;

            bool nonResident = record[attrOffset + NonResidentFlagOffset] != 0;
            if (!nonResident && TryGetResidentContentBounds(record, attrOffset, attrLength, usedSize, out int contentStart, out int contentLength))
            {
                if (attrType == AttributeTypeStandardInformation)
                {
                    if (contentLength >= 0x10)
                    {
                        createdUtc = ReadFileTimeUtc(record, contentStart);
                        modifiedUtc = ReadFileTimeUtc(record, contentStart + 0x08);
                    }
                }
                else if (attrType == AttributeTypeFileName &&
                         TryParseFileNameAttribute(record, contentStart, contentLength, out var parsedName, out var namespaceId, out var parsedParentRecordIndex, out var parsedCreatedUtcFn, out var parsedModifiedUtcFn, out var parsedIsDirectory))
                {
                    int score = GetFileNameNamespaceScore(namespaceId);
                    if (score >= bestFileNameScore)
                    {
                        bestFileNameScore = score;
                        fileName = parsedName;
                        parentRecordIndex = parsedParentRecordIndex;
                        createdUtcFn = parsedCreatedUtcFn;
                        modifiedUtcFn = parsedModifiedUtcFn;
                        isDirectory = parsedIsDirectory;
                    }
                }
            }

            attrOffset += attrLength;
        }

        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        bool timeStompSuspect = IsTimeStompSuspect(createdUtc, modifiedUtc, createdUtcFn, modifiedUtcFn);
        entry = new MftEntry
        {
            RecordIndex = recordIndex,
            FileName = fileName,
            FullPath = fileName,
            CreatedUtc = createdUtc ?? createdUtcFn,
            ModifiedUtc = modifiedUtc ?? modifiedUtcFn,
            ParentRecordIndex = parentRecordIndex,
            CreatedUtcFn = createdUtcFn,
            ModifiedUtcFn = modifiedUtcFn,
            TimeStompSuspect = timeStompSuspect,
            IsInUse = isInUse,
            IsDirectory = isDirectory
        };
        return true;
    }

    private static bool TryApplyUsaFixup(byte[] record)
    {
        if (record.Length < 64)
            return false;

        int usaOffset = BitConverter.ToUInt16(record, UsaOffsetInRecord);
        int usaCount = BitConverter.ToUInt16(record, UsaCountInRecord);
        if (usaOffset <= 0 || usaCount < 2 || usaOffset + (usaCount * 2) > record.Length)
            return false;

        int sectorCount = usaCount - 1;
        if (sectorCount <= 0 || record.Length % sectorCount != 0)
            return false;

        int sectorSize = record.Length / sectorCount;
        if (sectorSize < 256 || sectorSize > 4096)
            return false;

        ushort usaValue = BitConverter.ToUInt16(record, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = (i * sectorSize) - 2;
            if (sectorEnd < 0 || sectorEnd + 2 > record.Length)
                return false;

            ushort sectorTrailer = BitConverter.ToUInt16(record, sectorEnd);
            if (sectorTrailer != usaValue)
                return false;

            record[sectorEnd] = record[usaOffset + (i * 2)];
            record[sectorEnd + 1] = record[usaOffset + (i * 2) + 1];
        }

        return true;
    }

    private static int ParseUsedSize(byte[] record)
    {
        if (record.Length < UsedSizeOffsetInRecord + 4)
            return 0;

        int usedSize = (int)BitConverter.ToUInt32(record, UsedSizeOffsetInRecord);
        if (usedSize <= 0 || usedSize > record.Length)
            return record.Length;
        return usedSize;
    }

    private static bool TryGetResidentContentBounds(byte[] record, int attrOffset, int attrLength, int usedSize, out int contentStart, out int contentLength)
    {
        contentStart = 0;
        contentLength = 0;

        if (attrOffset + ResidentValueOffsetOffset + 2 > usedSize || attrOffset + ResidentValueLengthOffset + 4 > usedSize)
            return false;

        contentLength = (int)BitConverter.ToUInt32(record, attrOffset + ResidentValueLengthOffset);
        int valueOffset = BitConverter.ToUInt16(record, attrOffset + ResidentValueOffsetOffset);
        if (contentLength < 0 || valueOffset <= 0 || valueOffset >= attrLength)
            return false;

        contentStart = attrOffset + valueOffset;
        if (contentStart < attrOffset || contentStart + contentLength > attrOffset + attrLength || contentStart + contentLength > usedSize)
            return false;

        return true;
    }

    private static bool TryParseFileNameAttribute(
        byte[] record,
        int contentStart,
        int contentLength,
        out string? fileName,
        out byte namespaceId,
        out long? parentRecordIndex,
        out DateTime? createdUtcFn,
        out DateTime? modifiedUtcFn,
        out bool isDirectory)
    {
        fileName = null;
        namespaceId = 0;
        parentRecordIndex = null;
        createdUtcFn = null;
        modifiedUtcFn = null;
        isDirectory = false;

        if (contentLength < FileNameOffsetInContent)
            return false;

        if (contentStart + ParentDirectoryOffsetInFileName + 8 > record.Length)
            return false;

        long parentRef = BitConverter.ToInt64(record, contentStart + ParentDirectoryOffsetInFileName);
        parentRecordIndex = parentRef & ParentRecordMask;
        createdUtcFn = ReadFileTimeUtc(record, contentStart + CreationTimeOffsetInFileName);
        modifiedUtcFn = ReadFileTimeUtc(record, contentStart + ModifiedTimeOffsetInFileName);

        int nameLenOffset = contentStart + FileNameLengthOffsetInContent;
        int namespaceOffset = contentStart + FileNameNamespaceOffsetInContent;
        if (nameLenOffset >= record.Length || namespaceOffset >= record.Length)
            return false;

        int nameLengthChars = record[nameLenOffset];
        namespaceId = record[namespaceOffset];
        int nameLengthBytes = nameLengthChars * 2;
        if (nameLengthChars <= 0 || contentLength < FileNameOffsetInContent + nameLengthBytes)
            return false;

        int nameStartOffset = contentStart + FileNameOffsetInContent;
        if (nameStartOffset < 0 || nameStartOffset + nameLengthBytes > record.Length)
            return false;

        var decodedName = Encoding.Unicode.GetString(record, nameStartOffset, nameLengthBytes);
        decodedName = SanitizeFileName(decodedName);
        if (string.IsNullOrWhiteSpace(decodedName))
            return false;

        if (contentStart + FileFlagsOffsetInFileNameContent + 4 <= record.Length)
        {
            uint fileFlags = BitConverter.ToUInt32(record, contentStart + FileFlagsOffsetInFileNameContent);
            isDirectory = (fileFlags & FileDirectoryFlag) != 0;
        }

        fileName = decodedName;
        return true;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (ch == '\0')
                continue;
            if (char.IsControl(ch))
                continue;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    private static int GetFileNameNamespaceScore(byte namespaceId)
    {
        return namespaceId switch
        {
            1 => 4, // Win32
            3 => 3, // Win32 + DOS
            0 => 2, // POSIX
            2 => 1, // DOS
            _ => 0
        };
    }

    private static bool IsTimeStompSuspect(DateTime? createdUtc, DateTime? modifiedUtc, DateTime? createdUtcFn, DateTime? modifiedUtcFn)
    {
        if (createdUtc.HasValue && createdUtcFn.HasValue && Math.Abs((createdUtc.Value - createdUtcFn.Value).TotalSeconds) > TimeStompToleranceSeconds)
            return true;
        if (modifiedUtc.HasValue && modifiedUtcFn.HasValue && Math.Abs((modifiedUtc.Value - modifiedUtcFn.Value).TotalSeconds) > TimeStompToleranceSeconds)
            return true;
        return false;
    }

    private static DateTime? ReadFileTimeUtc(byte[] buffer, int offset)
    {
        try
        {
            if (offset < 0 || offset + FiletimeSize > buffer.Length)
                return null;

            long ft = BitConverter.ToInt64(buffer, offset);
            if (ft == 0 || ft == long.MaxValue || ft == -1 || ft == long.MinValue)
                return null;

            return DateTime.FromFileTimeUtc(ft);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds full paths for entries using parent references. Root and missing parents result in path = FileName only.
    /// </summary>
    public static IEnumerable<MftEntry> BuildFullPaths(IEnumerable<MftEntry> entries)
    {
        var list = entries.ToList();
        var byIndex = list.ToDictionary(e => e.RecordIndex);

        foreach (var entry in list)
        {
            var path = entry.FileName;
            var current = entry;
            var visited = new HashSet<long> { entry.RecordIndex };
            while (current.ParentRecordIndex is long parentIdx && parentIdx != current.RecordIndex && visited.Add(parentIdx) && byIndex.TryGetValue(parentIdx, out var parent))
            {
                path = string.IsNullOrEmpty(parent.FileName) ? path : parent.FileName + "\\" + path;
                current = parent;
            }

            yield return entry with { FullPath = path };
        }
    }

    private static int ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read <= 0)
                break;
            totalRead += read;
        }

        return totalRead;
    }

    private static RecordSizeCandidateScore ScoreRecordSizeCandidate(byte[] bytes, int recordSize)
    {
        if (recordSize < 64 || bytes.Length < recordSize)
            return new RecordSizeCandidateScore(recordSize, 0, 0);

        int sampleLength = Math.Min(bytes.Length, RecordSizeSampleByteLimit);
        sampleLength -= sampleLength % recordSize;
        if (sampleLength < recordSize)
            return new RecordSizeCandidateScore(recordSize, 0, 0);

        using var stream = new MemoryStream(bytes, 0, sampleLength, writable: false);
        int parsedEntryCount = Parse(stream, recordSize).Count();
        int recordsExamined = sampleLength / recordSize;
        double parseRatio = recordsExamined == 0 ? 0 : (double)parsedEntryCount / recordsExamined;
        return new RecordSizeCandidateScore(recordSize, parsedEntryCount, parseRatio);
    }

    private readonly record struct RecordSizeCandidateScore(int RecordSize, int ParsedEntryCount, double ParseRatio);
}

