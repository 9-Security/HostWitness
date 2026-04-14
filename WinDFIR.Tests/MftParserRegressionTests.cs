using System.Text;
using WinDFIR.Core.Mft;
using Xunit;

namespace WinDFIR.Tests;

public class MftParserRegressionTests
{
    [Fact]
    public void MftParser_SkipsExtensionRecords_AndPrefersWin32FileName()
    {
        var created = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var modified = new DateTime(2024, 1, 2, 6, 7, 8, DateTimeKind.Utc);
        var streamBytes = new byte[2048];

        var extensionRecord = CreateFileRecord(
            baseRecordReference: 42,
            win32Name: "ShouldBeSkipped.txt",
            dosName: "SHOULD~1.TXT",
            createdUtc: created,
            modifiedUtc: modified,
            parentRecordIndex: 5);

        var baseRecord = CreateFileRecord(
            baseRecordReference: 0,
            win32Name: "LongName.txt",
            dosName: "LONGNA~1.TXT",
            createdUtc: created,
            modifiedUtc: modified,
            parentRecordIndex: 5);

        Array.Copy(extensionRecord, 0, streamBytes, 0, extensionRecord.Length);
        Array.Copy(baseRecord, 0, streamBytes, 1024, baseRecord.Length);

        using var stream = new MemoryStream(streamBytes);
        var entries = MftParser.Parse(stream, 1024).ToList();

        var entry = Assert.Single(entries);
        Assert.Equal(1, entry.RecordIndex);
        Assert.Equal("LongName.txt", entry.FileName);
        Assert.Equal(created, entry.CreatedUtc);
        Assert.Equal(modified, entry.ModifiedUtc);
        Assert.Equal(5, entry.ParentRecordIndex);
    }

    [Fact]
    public void MftParser_FallsBackToFileNameTimestamps_WhenStandardInformationTimesMissing()
    {
        var createdFn = new DateTime(2023, 11, 10, 1, 2, 3, DateTimeKind.Utc);
        var modifiedFn = new DateTime(2023, 11, 11, 4, 5, 6, DateTimeKind.Utc);
        using var stream = new MemoryStream(CreateFileRecord(
            baseRecordReference: 0,
            win32Name: "Recovered.docx",
            dosName: null,
            createdUtc: null,
            modifiedUtc: null,
            parentRecordIndex: 12,
            createdUtcFn: createdFn,
            modifiedUtcFn: modifiedFn));

        var entry = Assert.Single(MftParser.Parse(stream, 1024));
        Assert.Equal("Recovered.docx", entry.FileName);
        Assert.Equal(createdFn, entry.CreatedUtc);
        Assert.Equal(modifiedFn, entry.ModifiedUtc);
        Assert.Equal(createdFn, entry.CreatedUtcFn);
        Assert.Equal(modifiedFn, entry.ModifiedUtcFn);
    }

    [Fact]
    public void MftParser_DetectRecordSize_Prefers4096ByteRecords()
    {
        var created = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var modified = new DateTime(2024, 2, 3, 7, 8, 9, DateTimeKind.Utc);
        var streamBytes = new byte[8192];

        var firstRecord = CreateFileRecord(
            baseRecordReference: 0,
            win32Name: "LargeRecordOne.bin",
            dosName: null,
            createdUtc: created,
            modifiedUtc: modified,
            parentRecordIndex: 5,
            recordSize: 4096);

        var secondRecord = CreateFileRecord(
            baseRecordReference: 0,
            win32Name: "LargeRecordTwo.bin",
            dosName: null,
            createdUtc: created,
            modifiedUtc: modified,
            parentRecordIndex: 5,
            recordSize: 4096);

        Array.Copy(firstRecord, 0, streamBytes, 0, firstRecord.Length);
        Array.Copy(secondRecord, 0, streamBytes, 4096, secondRecord.Length);

        var detection = MftParser.DetectRecordSize(streamBytes);

        Assert.True(detection.IsAutoDetected);
        Assert.Equal(4096, detection.RecordSize);

        using var stream = new MemoryStream(streamBytes);
        var entries = MftParser.Parse(stream, detection.RecordSize).ToList();
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void MftParser_PreservesPre1980FileTimes_AndStillFlagsTimeStomp()
    {
        var siCreated = new DateTime(1975, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var siModified = new DateTime(1975, 1, 6, 7, 8, 9, DateTimeKind.Utc);
        var fnCreated = new DateTime(1974, 12, 31, 23, 0, 0, DateTimeKind.Utc);
        var fnModified = new DateTime(1974, 12, 30, 22, 0, 0, DateTimeKind.Utc);

        using var stream = new MemoryStream(CreateFileRecord(
            baseRecordReference: 0,
            win32Name: "TimeWarp.txt",
            dosName: null,
            createdUtc: siCreated,
            modifiedUtc: siModified,
            parentRecordIndex: 7,
            createdUtcFn: fnCreated,
            modifiedUtcFn: fnModified));

        var entry = Assert.Single(MftParser.Parse(stream, 1024));
        Assert.Equal(siCreated, entry.CreatedUtc);
        Assert.Equal(siModified, entry.ModifiedUtc);
        Assert.Equal(fnCreated, entry.CreatedUtcFn);
        Assert.Equal(fnModified, entry.ModifiedUtcFn);
        Assert.True(entry.TimeStompSuspect);
    }

    private static byte[] CreateFileRecord(
        long baseRecordReference,
        string win32Name,
        string? dosName,
        DateTime? createdUtc,
        DateTime? modifiedUtc,
        long parentRecordIndex,
        DateTime? createdUtcFn = null,
        DateTime? modifiedUtcFn = null,
        int recordSize = 1024)
    {
        var record = new byte[recordSize];
        const ushort usaOffset = 0x30;
        int usaCount = (recordSize / 512) + 1;
        ushort firstAttributeOffset = (ushort)Align8(usaOffset + (usaCount * 2));

        Encoding.ASCII.GetBytes("FILE").CopyTo(record, 0);
        BitConverter.GetBytes(usaOffset).CopyTo(record, 0x04);
        BitConverter.GetBytes((ushort)usaCount).CopyTo(record, 0x06);
        BitConverter.GetBytes(firstAttributeOffset).CopyTo(record, 0x14);
        BitConverter.GetBytes((ushort)1).CopyTo(record, 0x16);
        BitConverter.GetBytes((long)baseRecordReference).CopyTo(record, 0x20);

        int attrOffset = firstAttributeOffset;
        if (createdUtc.HasValue || modifiedUtc.HasValue)
        {
            var standardInfo = CreateStandardInformationAttribute(createdUtc ?? DateTime.MinValue, modifiedUtc ?? DateTime.MinValue);
            Array.Copy(standardInfo, 0, record, attrOffset, standardInfo.Length);
            attrOffset += standardInfo.Length;
        }

        if (!string.IsNullOrEmpty(dosName))
        {
            var dosNameAttribute = CreateFileNameAttribute(dosName, 2, parentRecordIndex, createdUtcFn ?? createdUtc ?? DateTime.UtcNow, modifiedUtcFn ?? modifiedUtc ?? DateTime.UtcNow, isDirectory: false);
            Array.Copy(dosNameAttribute, 0, record, attrOffset, dosNameAttribute.Length);
            attrOffset += dosNameAttribute.Length;
        }

        var win32NameAttribute = CreateFileNameAttribute(win32Name, 1, parentRecordIndex, createdUtcFn ?? createdUtc ?? DateTime.UtcNow, modifiedUtcFn ?? modifiedUtc ?? DateTime.UtcNow, isDirectory: false);
        Array.Copy(win32NameAttribute, 0, record, attrOffset, win32NameAttribute.Length);
        attrOffset += win32NameAttribute.Length;

        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(record, attrOffset);
        attrOffset += 4;

        BitConverter.GetBytes((uint)attrOffset).CopyTo(record, 0x18);
        BitConverter.GetBytes((uint)record.Length).CopyTo(record, 0x1C);
        ApplyUsaFixupPlaceholders(record, usaOffset, 512);
        return record;
    }

    private static byte[] CreateStandardInformationAttribute(DateTime createdUtc, DateTime modifiedUtc)
    {
        var content = new byte[0x30];
        BitConverter.GetBytes(createdUtc.ToFileTimeUtc()).CopyTo(content, 0x00);
        BitConverter.GetBytes(modifiedUtc.ToFileTimeUtc()).CopyTo(content, 0x08);
        return CreateResidentAttribute(0x10u, content);
    }

    private static byte[] CreateFileNameAttribute(string name, byte namespaceId, long parentRecordIndex, DateTime createdUtc, DateTime modifiedUtc, bool isDirectory)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var content = new byte[0x42 + nameBytes.Length];
        BitConverter.GetBytes(parentRecordIndex).CopyTo(content, 0x00);
        BitConverter.GetBytes(createdUtc.ToFileTimeUtc()).CopyTo(content, 0x08);
        BitConverter.GetBytes(modifiedUtc.ToFileTimeUtc()).CopyTo(content, 0x10);
        BitConverter.GetBytes((uint)(isDirectory ? 0x10u : 0u)).CopyTo(content, 0x38);
        content[0x40] = (byte)name.Length;
        content[0x41] = namespaceId;
        Array.Copy(nameBytes, 0, content, 0x42, nameBytes.Length);
        return CreateResidentAttribute(0x30u, content);
    }

    private static byte[] CreateResidentAttribute(uint attributeType, byte[] content)
    {
        const ushort valueOffset = 0x18;
        int attributeLength = Align8(valueOffset + content.Length);
        var attribute = new byte[attributeLength];
        BitConverter.GetBytes(attributeType).CopyTo(attribute, 0x00);
        BitConverter.GetBytes((uint)attributeLength).CopyTo(attribute, 0x04);
        attribute[0x08] = 0;
        BitConverter.GetBytes((uint)content.Length).CopyTo(attribute, 0x10);
        BitConverter.GetBytes(valueOffset).CopyTo(attribute, 0x14);
        Array.Copy(content, 0, attribute, valueOffset, content.Length);
        return attribute;
    }

    private static int Align8(int value) => (value + 7) & ~7;

    private static void ApplyUsaFixupPlaceholders(byte[] record, ushort usaOffset, int bytesPerSector)
    {
        const ushort usaValue = 0xAAAA;
        int sectorCount = record.Length / bytesPerSector;
        int usaCount = sectorCount + 1;

        BitConverter.GetBytes(usaValue).CopyTo(record, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            ushort originalValue = (ushort)(0x1110 + i);
            BitConverter.GetBytes(originalValue).CopyTo(record, usaOffset + (i * 2));
            int sectorEnd = (i * bytesPerSector) - 2;
            BitConverter.GetBytes(usaValue).CopyTo(record, sectorEnd);
        }
    }
}
