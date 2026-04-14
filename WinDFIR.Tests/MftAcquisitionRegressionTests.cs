using System.Collections.Generic;
using System.Reflection;
using System.Text;
using WinDFIR.Core.IO;
using WinDFIR.UI.ViewModels;
using Xunit;

namespace WinDFIR.Tests;

public class MftAcquisitionRegressionTests
{
    [Fact]
    public void BackupPrivilege_HelperRejectsNotAllAssigned()
    {
        var backupPrivilegeType = typeof(RawDiskReader).Assembly.GetType("WinDFIR.Core.IO.BackupPrivilege", throwOnError: true)!;
        var method = backupPrivilegeType.GetMethod("DidAdjustTokenPrivilegesSucceed", BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.False((bool)method.Invoke(null, new object?[] { true, 1300 })!);
        Assert.False((bool)method.Invoke(null, new object?[] { false, 0 })!);
        Assert.True((bool)method.Invoke(null, new object?[] { true, 0 })!);
    }

    [Fact]
    public void RawDiskReader_ParsesMultipleMftRunsFromRecordZero()
    {
        var method = typeof(RawDiskReader).GetMethod("TryExtractMftDataRuns", BindingFlags.Static | BindingFlags.NonPublic)!;
        var record = CreateRecordZeroWithRuns();
        object?[] args = { record, 512, 4096, null, 0L, null };

        var ok = (bool)method.Invoke(null, args)!;

        Assert.True(ok, args[5]?.ToString());
        var runs = Assert.IsType<List<(long OffsetBytes, long LengthBytes)>>(args[3]);
        Assert.Equal(12288L, Assert.IsType<long>(args[4]));
        Assert.Equal(2, runs.Count);
        Assert.Equal((409600L, 8192L), runs[0]);
        Assert.Equal((491520L, 4096L), runs[1]);
    }

    [Fact]
    public void RawDiskReader_ReadMftFromStreamHonorsPartitionOffset()
    {
        var method = typeof(RawDiskReader).GetMethod("ReadMftFromStream", BindingFlags.Static | BindingFlags.NonPublic)!;
        const long partitionStartOffset = 4096;
        using var stream = new MemoryStream(CreatePhysicalDriveImageWithPartitionOffset(partitionStartOffset));
        object?[] args = { stream, partitionStartOffset, 0, null };

        var bytes = Assert.IsType<byte[]>(method.Invoke(null, args));

        Assert.Equal(1024, Assert.IsType<int>(args[2]));
        Assert.Null(args[3]);
        Assert.Equal(1536, bytes.Length);
        Assert.All(bytes[..1024], b => Assert.Equal((byte)0x41, b));
        Assert.All(bytes[1024..], b => Assert.Equal((byte)0x42, b));
    }

    [Fact]
    public void RawDiskReader_ReadMftFromStreamDetailedFlagsTruncationWhenMftExceedsReadLimit()
    {
        var method = typeof(RawDiskReader).GetMethod("ReadMftFromStreamDetailed", BindingFlags.Static | BindingFlags.NonPublic)!;
        const long partitionStartOffset = 4096;
        const int bytesPerSector = 512;
        const byte sectorsPerCluster = 8;
        const int clusterSize = bytesPerSector * sectorsPerCluster;
        long logicalSizeBytes = RawDiskReader.MftReadLimitBytes + 4096L;
        long clusterCount = (logicalSizeBytes + clusterSize - 1) / clusterSize;
        long dataRunLcn = 40;

        var streamLength = partitionStartOffset + ((dataRunLcn + clusterCount) * clusterSize);
        var stream = new SparsePatternStream(streamLength);
        stream.AddBytes(partitionStartOffset, CreateNtfsBootSector(mftLcn: 4, bytesPerSector: bytesPerSector, sectorsPerCluster: sectorsPerCluster, mftRecordSize: 1024));
        stream.AddBytes(partitionStartOffset + (4 * clusterSize), CreateRecordZeroWithRunList((ulong)logicalSizeBytes, CreateRunList((clusterCount, dataRunLcn))));
        stream.AddFill(partitionStartOffset + (dataRunLcn * clusterSize), RawDiskReader.MftReadLimitBytes, 0x5A);

        var result = Assert.IsType<RawDiskReader.MftReadResult>(method.Invoke(null, new object?[] { stream, partitionStartOffset }));

        Assert.Null(result.FailureReason);
        Assert.Equal(1024, result.RecordSize);
        Assert.True(result.WasTruncated);
        Assert.Equal(logicalSizeBytes, result.LogicalSizeBytes);
        Assert.NotNull(result.Bytes);
        Assert.Equal(RawDiskReader.MftReadLimitBytes, result.Bytes!.Length);
        Assert.Equal(RawDiskReader.MftReadLimitBytes, result.ReadLength);
        Assert.Equal((byte)0x5A, result.Bytes[0]);
        Assert.Equal((byte)0x5A, result.Bytes[^1]);
    }

    [Fact]
    public void RawDiskReader_ParseMftRecordSizeFromBoot_UsesSignedByteEncoding()
    {
        var method = typeof(RawDiskReader).GetMethod("ParseMftRecordSizeFromBoot", BindingFlags.Static | BindingFlags.NonPublic)!;
        var boot = CreateNtfsBootSector(mftLcn: 4, bytesPerSector: 512, sectorsPerCluster: 16, mftRecordSize: 4096);

        var recordSize = Assert.IsType<int>(method.Invoke(null, new object?[] { boot }));

        Assert.Equal(4096, recordSize);
    }

    [Fact]
    public void GetOrCreateVolumeTab_ReusesExistingTabPerDriveLetter()
    {
        var viewModel = new MftViewModel();
        var getOrCreate = typeof(MftViewModel).GetMethod("GetOrCreateVolumeTab", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var first = getOrCreate.Invoke(viewModel, new object?[] { 'd' });
        var second = getOrCreate.Invoke(viewModel, new object?[] { 'D' });

        Assert.Same(first, second);
        Assert.Single(viewModel.Tabs);
        Assert.Equal("D:", viewModel.Tabs[0].Header);
        Assert.Equal("D", viewModel.Tabs[0].SourceKey);
    }

    private static byte[] CreateRecordZeroWithRuns(
        ulong logicalSizeBytes = 12288,
        byte firstRunLcn = 0x64,
        byte firstRunLengthClusters = 0x02,
        byte secondRunDelta = 0x14,
        byte secondRunLengthClusters = 0x01)
    {
        return CreateRecordZeroWithRunList(
            logicalSizeBytes,
            CreateRunList(
                (firstRunLengthClusters, firstRunLcn),
                (secondRunLengthClusters, secondRunDelta)));
    }

    private static byte[] CreateRecordZeroWithRunList(ulong logicalSizeBytes, byte[] runList)
    {
        var record = new byte[1024];
        Encoding.ASCII.GetBytes("FILE").CopyTo(record, 0);
        BitConverter.GetBytes((ushort)0x30).CopyTo(record, 0x04);
        BitConverter.GetBytes((ushort)3).CopyTo(record, 0x06);
        BitConverter.GetBytes((ushort)0x38).CopyTo(record, 0x14);

        BitConverter.GetBytes((ushort)0xAAAA).CopyTo(record, 0x30);
        BitConverter.GetBytes((ushort)0x1111).CopyTo(record, 0x32);
        BitConverter.GetBytes((ushort)0x2222).CopyTo(record, 0x34);
        BitConverter.GetBytes((ushort)0xAAAA).CopyTo(record, 510);
        BitConverter.GetBytes((ushort)0xAAAA).CopyTo(record, 1022);

        const int attrOffset = 0x38;
        int attrLength = 0x40 + runList.Length;
        BitConverter.GetBytes(0x80u).CopyTo(record, attrOffset + 0x00);
        BitConverter.GetBytes((uint)attrLength).CopyTo(record, attrOffset + 0x04);
        record[attrOffset + 0x08] = 1;
        BitConverter.GetBytes((ushort)0x40).CopyTo(record, attrOffset + 0x20);
        BitConverter.GetBytes(logicalSizeBytes).CopyTo(record, attrOffset + 0x30);

        Array.Copy(runList, 0, record, attrOffset + 0x40, runList.Length);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(record, attrOffset + attrLength);
        return record;
    }

    private static byte[] CreatePhysicalDriveImageWithPartitionOffset(long partitionStartOffset)
    {
        var image = new byte[20480];
        var boot = CreateNtfsBootSector(mftLcn: 4, bytesPerSector: 512, sectorsPerCluster: 1, mftRecordSize: 1024);
        Array.Copy(boot, 0, image, (int)partitionStartOffset, boot.Length);

        var record = CreateRecordZeroWithRuns(
            logicalSizeBytes: 1536,
            firstRunLcn: 20,
            firstRunLengthClusters: 2,
            secondRunDelta: 4,
            secondRunLengthClusters: 1);

        Array.Copy(record, 0, image, (int)(partitionStartOffset + (4 * 512L)), record.Length);
        Array.Fill(image, (byte)0x41, (int)(partitionStartOffset + (20 * 512L)), 1024);
        Array.Fill(image, (byte)0x42, (int)(partitionStartOffset + (24 * 512L)), 512);
        return image;
    }

    private static byte[] CreateNtfsBootSector(long mftLcn, ushort bytesPerSector, byte sectorsPerCluster, int mftRecordSize)
    {
        var boot = new byte[512];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(boot, 3);
        BitConverter.GetBytes(bytesPerSector).CopyTo(boot, 0x0B);
        boot[0x0D] = sectorsPerCluster;
        BitConverter.GetBytes(mftLcn).CopyTo(boot, 0x30);

        if (mftRecordSize <= 0 || (mftRecordSize & (mftRecordSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(mftRecordSize));

        int clusterSize = bytesPerSector * sectorsPerCluster;
        int recordSizeField = mftRecordSize >= clusterSize && (mftRecordSize % clusterSize) == 0
            ? mftRecordSize / clusterSize
            : -(int)Math.Log2(mftRecordSize);

        if (recordSizeField < sbyte.MinValue || recordSizeField > sbyte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(mftRecordSize));

        boot[0x40] = unchecked((byte)(sbyte)recordSizeField);
        return boot;
    }

    private static byte[] CreateRunList(params (long ClusterCount, long LcnDelta)[] runs)
    {
        using var stream = new MemoryStream();
        foreach (var run in runs)
        {
            var lengthBytes = EncodeUnsignedLittleEndian(run.ClusterCount);
            var offsetBytes = EncodeSignedLittleEndian(run.LcnDelta);
            stream.WriteByte((byte)((offsetBytes.Length << 4) | lengthBytes.Length));
            stream.Write(lengthBytes, 0, lengthBytes.Length);
            stream.Write(offsetBytes, 0, offsetBytes.Length);
        }

        stream.WriteByte(0);
        return stream.ToArray();
    }

    private static byte[] EncodeUnsignedLittleEndian(long value)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value));

        var bytes = new List<byte>();
        long remaining = value;
        while (remaining > 0)
        {
            bytes.Add((byte)(remaining & 0xFF));
            remaining >>= 8;
        }

        return bytes.ToArray();
    }

    private static byte[] EncodeSignedLittleEndian(long value)
    {
        var bytes = new List<byte>();
        long remaining = value;
        bool more;
        do
        {
            byte current = (byte)(remaining & 0xFF);
            remaining >>= 8;
            bool signBitSet = (current & 0x80) != 0;
            more = !((remaining == 0 && !signBitSet) || (remaining == -1 && signBitSet));
            bytes.Add(current);
        }
        while (more);

        return bytes.ToArray();
    }

    private sealed class SparsePatternStream : Stream
    {
        private readonly long _length;
        private readonly List<(long Start, byte[] Data)> _segments = new();
        private readonly List<(long Start, long Length, byte Value)> _fills = new();
        private long _position;

        public SparsePatternStream(long length)
        {
            _length = length;
        }

        public void AddBytes(long start, byte[] data) => _segments.Add((start, data));

        public void AddFill(long start, long length, byte value) => _fills.Add((start, length, value));

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length)
                return 0;

            int readCount = (int)Math.Min(count, _length - _position);
            Array.Clear(buffer, offset, readCount);
            long readStart = _position;
            long readEnd = readStart + readCount;

            foreach (var fill in _fills)
            {
                long fillEnd = fill.Start + fill.Length;
                long overlapStart = Math.Max(readStart, fill.Start);
                long overlapEnd = Math.Min(readEnd, fillEnd);
                if (overlapStart >= overlapEnd)
                    continue;

                Array.Fill(buffer, fill.Value, offset + (int)(overlapStart - readStart), (int)(overlapEnd - overlapStart));
            }

            foreach (var segment in _segments)
            {
                long segmentEnd = segment.Start + segment.Data.Length;
                long overlapStart = Math.Max(readStart, segment.Start);
                long overlapEnd = Math.Min(readEnd, segmentEnd);
                if (overlapStart >= overlapEnd)
                    continue;

                Array.Copy(
                    segment.Data,
                    (int)(overlapStart - segment.Start),
                    buffer,
                    offset + (int)(overlapStart - readStart),
                    (int)(overlapEnd - overlapStart));
            }

            _position += readCount;
            return readCount;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (newPosition < 0)
                throw new IOException("Attempted to seek before the beginning of the stream.");

            _position = newPosition;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}


