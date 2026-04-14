using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinDFIR.Core.Snapshot;

namespace WinDFIR.Core.IO;

/// <summary>
/// Reads raw sectors from a physical drive (e.g. \\.\PhysicalDrive0) or MFT from a volume (e.g. \\.\C:).
/// Requires Administrator. Use for offline hive/artifact read when VSS is unavailable.
/// </summary>
public static class RawDiskReader
{
    public readonly record struct MftReadResult(
        byte[]? Bytes,
        int RecordSize,
        string? FailureReason,
        bool UsedPhysicalDriveFallback = false,
        bool WasTruncated = false,
        long? LogicalSizeBytes = null)
    {
        public int ReadLength => Bytes?.Length ?? 0;
    }

    /// <summary>
    /// Operator-facing explanation when $MFT bytes were capped by <see cref="MftReadLimitBytes"/>.
    /// Returns null when the read was not truncated by the cap (success path may still omit this note).
    /// </summary>
    public static string? GetPartialMftLoadOperatorNote(MftReadResult result)
    {
        if (!result.WasTruncated)
            return null;

        var loaded = result.ReadLength;
        if (result.LogicalSizeBytes is { } logical && logical > 0)
        {
            var pct = Math.Min(100d, 100d * loaded / logical);
            return FormattableString.Invariant(
                $"PARTIAL $MFT (100 MB read cap): loaded {loaded:n0} of {logical:n0} bytes (~{pct:0.#}% of logical $MFT). Record list and paths may be incomplete beyond this segment; export or correlate accordingly.");
        }

        return FormattableString.Invariant(
            $"PARTIAL $MFT (100 MB read cap): loaded {loaded:n0} bytes; full $MFT size could not be determined from the source. Results may be incomplete.");
    }

    private const uint SectorSize = 512;
    private const uint FileSignature = 0x454C4946;
    private const uint AttributeTypeData = 0x80;
    private const uint EndOfAttributes = 0xFFFFFFFF;
    private const int UsaOffsetInRecord = 0x04;
    private const int UsaCountInRecord = 0x06;
    private const int FirstAttributeOffsetInRecord = 0x14;
    private const int AttributeTypeOffset = 0x00;
    private const int AttributeLengthOffset = 0x04;
    private const int AttributeNonResidentFlagOffset = 0x08;
    private const int AttributeRunListOffset = 0x20;
    private const int AttributeRealSizeOffset = 0x30;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>Maximum bytes to read for MFT when loading by drive letter (100 MB).</summary>
    public const int MftReadLimitBytes = 100 * 1024 * 1024;

    /// <summary>Maximum bytes for any single raw read (ReadSectors/ReadBytes) to prevent OOM.</summary>
    public const int RawReadLimitBytes = 100 * 1024 * 1024;

    /// <summary>
    /// Read $MFT from an NTFS volume by drive letter (e.g. 'C'). Opens \\.\X:, parses boot sector for MFT LCN and record size, then
    /// reads the logical $MFT stream via record 0's non-resident DATA run list. If the live volume is busy, automatically
    /// falls back to the containing PhysicalDrive plus partition offset and retries there.
    /// Requires Administrator. Returns null if volume is not NTFS or read fails; <paramref name="failureReason"/> describes why.
    /// <paramref name="mftRecordSize"/> is set from boot sector (0x40); typically 1024 or 4096. Use for MftParser.Parse(stream, mftRecordSize).</summary>
    public static byte[]? ReadMftFromVolume(char driveLetter, out int mftRecordSize, out bool usedPhysicalDriveFallback, out string? failureReason)
    {
        var result = ReadMftFromVolumeDetailed(driveLetter);
        mftRecordSize = result.RecordSize;
        usedPhysicalDriveFallback = result.UsedPhysicalDriveFallback;
        failureReason = result.FailureReason;
        return result.Bytes;
    }

    public static MftReadResult ReadMftFromVolumeDetailed(char driveLetter)
    {
        var letter = char.ToUpperInvariant(driveLetter);
        if (letter < 'A' || letter > 'Z')
            return new MftReadResult(null, 1024, "Invalid drive letter.");

        var liveVolumePath = $@"\\.\{letter}:";
        string? liveVolumeFailure = null;
        try
        {
            using var liveVolume = OpenReadOnlyDeviceStream(liveVolumePath);
            var liveVolumeResult = ReadMftFromStreamDetailed(liveVolume, 0);
            if (liveVolumeResult.Bytes is { Length: > 0 })
                return liveVolumeResult;

            liveVolumeFailure = liveVolumeResult.FailureReason;
        }
        catch (IOException ex)
        {
            liveVolumeFailure = $"Unable to open live volume {liveVolumePath}: {ex.Message}";
        }

        var physicalResult = ReadMftFromPhysicalDriveForVolumeDetailed(letter);
        if (physicalResult.Bytes is { Length: > 0 })
            return physicalResult with
            {
                UsedPhysicalDriveFallback = true,
                FailureReason = null
            };

        return new MftReadResult(
            Bytes: null,
            RecordSize: physicalResult.RecordSize,
            FailureReason: CombineFailureReasons(liveVolumeFailure, physicalResult.FailureReason),
            UsedPhysicalDriveFallback: false,
            WasTruncated: physicalResult.WasTruncated,
            LogicalSizeBytes: physicalResult.LogicalSizeBytes);
    }

    /// <summary>Convenience overload when only the failure reason is needed.</summary>
    public static byte[]? ReadMftFromVolume(char driveLetter, out string? failureReason)
    {
        return ReadMftFromVolume(driveLetter, out _, out failureReason);
    }

    /// <summary>Convenience overload when record size is needed but fallback source detail is not.</summary>
    public static byte[]? ReadMftFromVolume(char driveLetter, out int mftRecordSize, out string? failureReason)
    {
        return ReadMftFromVolume(driveLetter, out mftRecordSize, out _, out failureReason);
    }

    /// <summary>Convenience overload without failure reason. Use ReadMftFromVolume(letter, out _, out _) for diagnostics.</summary>
    public static byte[]? ReadMftFromVolume(char driveLetter)
    {
        return ReadMftFromVolume(driveLetter, out _, out _, out _);
    }

    /// <summary>
    /// Read $MFT by opening \\.\X:\$MFT with SeBackupPrivilege and FILE_FLAG_BACKUP_SEMANTICS.
    /// Tries first; if it succeeds, avoids VSS and raw sector read. Requires Administrator.
    /// </summary>
    public static byte[]? ReadMftFromVolumeViaBackupPrivilege(char driveLetter, out int mftRecordSize, out string? failureReason)
    {
        var result = ReadMftFromVolumeViaBackupPrivilegeDetailed(driveLetter);
        mftRecordSize = result.RecordSize;
        failureReason = result.FailureReason;
        return result.Bytes;
    }

    public static MftReadResult ReadMftFromVolumeViaBackupPrivilegeDetailed(char driveLetter)
    {
        var backupLetter = char.ToUpperInvariant(driveLetter);
        if (backupLetter < 'A' || backupLetter > 'Z')
            return new MftReadResult(null, 1024, "Invalid drive letter.");

        if (!BackupPrivilege.EnableBackupPrivilege())
            return new MftReadResult(null, 1024, "Unable to enable SeBackupPrivilege.");

        var volumePrefix = $@"\\.\{backupLetter}:";
        var bootPath = volumePrefix + @"\$Boot";
        var mftPath = volumePrefix + @"\$MFT";

        var bootResult = BackupPrivilege.ReadFileWithBackupSemanticsDetailed(bootPath, 512);
        if (bootResult.Bytes == null || bootResult.Bytes.Length < 512)
            return new MftReadResult(null, 1024, string.IsNullOrWhiteSpace(bootResult.FailureReason) ? "Unable to read $Boot." : bootResult.FailureReason);

        if (!IsNtfsBootSector(bootResult.Bytes))
            return new MftReadResult(null, 1024, $"Drive {backupLetter}: source is not NTFS.");

        int mftRecordSize = ParseMftRecordSizeFromBoot(bootResult.Bytes);

        var mftResult = BackupPrivilege.ReadFileWithBackupSemanticsDetailed(mftPath, MftReadLimitBytes);
        if (mftResult.Bytes == null || mftResult.Bytes.Length == 0)
            return new MftReadResult(null, mftRecordSize, string.IsNullOrWhiteSpace(mftResult.FailureReason) ? "Unable to read $MFT." : mftResult.FailureReason);

        return new MftReadResult(
            Bytes: mftResult.Bytes,
            RecordSize: mftRecordSize,
            FailureReason: null,
            UsedPhysicalDriveFallback: false,
            WasTruncated: mftResult.WasTruncated,
            LogicalSizeBytes: mftResult.LogicalSizeBytes);

    }

    /// <summary>
    /// Read $MFT from an NTFS volume by creating a VSS snapshot first, then reading \$MFT from the shadow.
    /// Avoids "file is being used by another process" when the live volume is locked. Requires Administrator and VSS service.
    /// Snapshot is created, $MFT and record size are read, then the snapshot is deleted.
    /// </summary>
    public static byte[]? ReadMftFromVolumeViaVss(char driveLetter, out int mftRecordSize, out string? failureReason)
    {
        var result = ReadMftFromVolumeViaVssDetailed(driveLetter);
        mftRecordSize = result.RecordSize;
        failureReason = result.FailureReason;
        return result.Bytes;
    }

    public static MftReadResult ReadMftFromVolumeViaVssDetailed(char driveLetter)
    {
        var vssLetter = char.ToUpperInvariant(driveLetter);
        if (vssLetter < 'A' || vssLetter > 'Z')
            return new MftReadResult(null, 1024, "Invalid drive letter.");

        var volumeRoot = $"{vssLetter}:\\";
        var vss = new VssSnapshotService();
        using var context = vss.TryCreateContextForPaths(new[] { volumeRoot }, out var warning);
        if (context == null)
            return new MftReadResult(null, 1024, string.IsNullOrWhiteSpace(warning) ? "Unable to create a VSS snapshot." : warning);

        try
        {
            var bootPath = context.ResolvePath(volumeRoot + "$Boot");
            var mftPath = context.ResolvePath(volumeRoot + "$MFT");
            if (string.IsNullOrEmpty(bootPath) || string.IsNullOrEmpty(mftPath))
                return new MftReadResult(null, 1024, "Unable to resolve snapshot paths for $Boot and $MFT.");

            var boot = new byte[512];
            using (var bootStream = new FileStream(bootPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (ReadExactly(bootStream, boot, 0, boot.Length) != boot.Length)
                    return new MftReadResult(null, 1024, "Unable to read the VSS $Boot data.");
            }

            if (!IsNtfsBootSector(boot))
                return new MftReadResult(null, 1024, $"Drive {vssLetter}: source is not NTFS.");

            int mftRecordSize = ParseMftRecordSizeFromBoot(boot);

            using var mftStream = new FileStream(mftPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long? logicalSizeBytes = TryGetStreamLength(mftStream);
            int bufferLength = logicalSizeBytes.HasValue
                ? (int)Math.Min(MftReadLimitBytes, logicalSizeBytes.Value)
                : MftReadLimitBytes;
            var buffer = new byte[bufferLength];
            int read = ReadExactly(mftStream, buffer, 0, buffer.Length);
            if (read <= 0)
                return new MftReadResult(null, mftRecordSize, "Unable to read the VSS $MFT data.");

            if (read < buffer.Length)
                Array.Resize(ref buffer, read);

            bool wasTruncated = logicalSizeBytes.HasValue && logicalSizeBytes.Value > read;
            return new MftReadResult(
                Bytes: buffer,
                RecordSize: mftRecordSize,
                FailureReason: null,
                UsedPhysicalDriveFallback: false,
                WasTruncated: wasTruncated,
                LogicalSizeBytes: logicalSizeBytes);
        }
        catch (UnauthorizedAccessException)
        {
            return new MftReadResult(null, 1024, "Access denied while reading the VSS snapshot.");
        }
        catch (IOException ex)
        {
            return new MftReadResult(null, 1024, $"VSS read failed: {ex.Message}");
        }

    }

    /// <summary>
    /// Read $MFT from an NTFS volume by resolving the selected drive letter to its containing PhysicalDrive plus
    /// partition starting offset, then reading the NTFS boot sector and reconstructing the logical $MFT stream from
    /// record 0 data runs. Requires Administrator, but does not require VSS.
    /// </summary>
    public static byte[]? ReadMftFromVolumeViaPhysicalDrivePartition(char driveLetter, out int mftRecordSize, out string? failureReason)
    {
        var result = ReadMftFromVolumeViaPhysicalDrivePartitionDetailed(driveLetter);
        mftRecordSize = result.RecordSize;
        failureReason = result.FailureReason;
        return result.Bytes;
    }

    public static MftReadResult ReadMftFromVolumeViaPhysicalDrivePartitionDetailed(char driveLetter)
    {
        var letter = char.ToUpperInvariant(driveLetter);
        if (letter < 'A' || letter > 'Z')
            return new MftReadResult(null, 1024, "Invalid drive letter.");

        return ReadMftFromPhysicalDriveForVolumeDetailed(letter);
    }

    private static byte[]? ReadMftFromStream(Stream stream, long volumeStartOffsetBytes, out int mftRecordSize, out string? failureReason)
    {
        var result = ReadMftFromStreamDetailed(stream, volumeStartOffsetBytes);
        mftRecordSize = result.RecordSize;
        failureReason = result.FailureReason;
        return result.Bytes;
    }

    private static MftReadResult ReadMftFromStreamDetailed(Stream stream, long volumeStartOffsetBytes)
    {
        if (!stream.CanRead || !stream.CanSeek)
            return new MftReadResult(null, 1024, "The raw stream must support reading and seeking.");

        if (volumeStartOffsetBytes < 0)
            return new MftReadResult(null, 1024, "The volume start offset cannot be negative.");

        var boot = new byte[(int)SectorSize];
        stream.Seek(volumeStartOffsetBytes, SeekOrigin.Begin);
        if (ReadExactly(stream, boot, 0, boot.Length) != boot.Length)
            return new MftReadResult(null, 1024, "Unable to read the NTFS boot sector.");

        if (!IsNtfsBootSector(boot))
            return new MftReadResult(null, 1024, "Selected source is not an NTFS volume.");

        ushort bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
        byte sectorsPerCluster = boot[0x0D];
        long mftLcn = BitConverter.ToInt64(boot, 0x30);

        if (bytesPerSector == 0 || sectorsPerCluster == 0)
            return new MftReadResult(null, 1024, "The NTFS boot sector contains invalid geometry.");

        int clusterSize = bytesPerSector * sectorsPerCluster;
        int mftRecordSize = ParseMftRecordSizeFromBoot(boot);

        long mftOffsetBytes;
        try
        {
            mftOffsetBytes = checked(volumeStartOffsetBytes + checked(mftLcn * (long)clusterSize));
        }
        catch (OverflowException)
        {
            return new MftReadResult(null, mftRecordSize, "The calculated $MFT offset overflowed.");
        }

        if (mftOffsetBytes < volumeStartOffsetBytes)
            return new MftReadResult(null, mftRecordSize, "The calculated $MFT offset is invalid.");

        return ReadMftFromRunsDetailed(stream, mftOffsetBytes, mftRecordSize, bytesPerSector, clusterSize, volumeStartOffsetBytes);
    }


    private static MftReadResult ReadMftFromPhysicalDriveForVolumeDetailed(char driveLetter)
    {
        if (!TryResolveVolumeToPhysicalDisk(driveLetter, out var physicalDriveNumber, out var partitionStartOffsetBytes, out var failureReason))
            return new MftReadResult(null, 1024, failureReason);

        try
        {
            using var physicalDrive = OpenReadOnlyDeviceStream($@"\\.\PhysicalDrive{physicalDriveNumber}");
            var result = ReadMftFromStreamDetailed(physicalDrive, partitionStartOffsetBytes);
            if (result.Bytes is { Length: > 0 })
                return result;

            return result with
            {
                FailureReason = CombineFailureReasons(
                    result.FailureReason,
                    $"Resolved {char.ToUpperInvariant(driveLetter)}: to PhysicalDrive{physicalDriveNumber} at offset {partitionStartOffsetBytes}, but the NTFS read still failed.")
            };
        }
        catch (IOException ex)
        {
            return new MftReadResult(null, 1024, $"Unable to open \\.\\PhysicalDrive{physicalDriveNumber}: {ex.Message}");
        }
    }

    internal static bool TryResolveVolumeToPhysicalDisk(char driveLetter, out int physicalDriveNumber, out long partitionStartOffsetBytes, out string? failureReason)
    {
        failureReason = null;
        physicalDriveNumber = -1;
        partitionStartOffsetBytes = 0;

        var letter = char.ToUpperInvariant(driveLetter);
        if (letter < 'A' || letter > 'Z')
        {
            failureReason = "Invalid drive letter.";
            return false;
        }

        var logicalDiskId = $"{letter}:";
        string? partitionDeviceId = null;

        try
        {
            using (var partitionSearcher = new ManagementObjectSearcher(
                       "root\\CIMV2",
                       $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{logicalDiskId}'}} WHERE AssocClass = Win32_LogicalDiskToPartition"))
            using (var partitions = partitionSearcher.Get())
            {
                foreach (ManagementObject partition in partitions)
                {
                    partitionDeviceId = partition["DeviceID"]?.ToString();
                    if (!long.TryParse(partition["StartingOffset"]?.ToString(), out partitionStartOffsetBytes) || partitionStartOffsetBytes < 0)
                    {
                        failureReason = $"Unable to determine the starting offset for {logicalDiskId}.";
                        return false;
                    }

                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(partitionDeviceId))
            {
                failureReason = $"Unable to map {logicalDiskId} to a disk partition.";
                return false;
            }

            using (var diskSearcher = new ManagementObjectSearcher(
                       "root\\CIMV2",
                       $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{EscapeWqlString(partitionDeviceId)}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition"))
            using (var disks = diskSearcher.Get())
            {
                foreach (ManagementObject disk in disks)
                {
                    if (!int.TryParse(disk["Index"]?.ToString(), out physicalDriveNumber) || physicalDriveNumber < 0)
                    {
                        failureReason = $"Unable to determine the physical drive number for {logicalDiskId}.";
                        return false;
                    }

                    return true;
                }
            }

            failureReason = $"Unable to resolve {logicalDiskId} to a physical drive.";
            return false;
        }
        catch (ManagementException ex)
        {
            failureReason = $"Unable to query Windows storage metadata for {logicalDiskId}: {ex.Message}";
            return false;
        }
        catch (COMException ex)
        {
            failureReason = $"Windows storage metadata lookup failed for {logicalDiskId}: {ex.Message}";
            return false;
        }
    }

    private static string? CombineFailureReasons(params string?[] reasons)
    {
        var parts = new List<string>();
        foreach (var reason in reasons)
        {
            if (!string.IsNullOrWhiteSpace(reason))
                parts.Add(reason.Trim());
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static FileStream OpenReadOnlyDeviceStream(string path)
    {
        var handle = CreateFile(
            path,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == InvalidHandleValue)
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException($"CreateFile failed for {path} (Win32 0x{error:X}).", Marshal.GetHRForLastWin32Error());
        }

        return new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.Read, 64 * 1024, false);
    }

    private static string EscapeWqlString(string value)
    {
        return value.Replace("'", "''");
    }

    private static long? TryGetStreamLength(Stream stream)
    {
        try
        {
            return stream.Length;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }


    private static MftReadResult ReadMftFromRunsDetailed(Stream volume, long mftOffsetBytes, int recordSize, int bytesPerSector, int clusterSize, long logicalVolumeStartOffsetBytes = 0)
    {
        if (recordSize <= 0 || bytesPerSector <= 0 || clusterSize <= 0)
            return new MftReadResult(null, recordSize <= 0 ? 1024 : recordSize, "The NTFS geometry values are invalid.");

        var recordBuffer = new byte[recordSize];
        volume.Seek(mftOffsetBytes, SeekOrigin.Begin);
        if (ReadExactly(volume, recordBuffer, 0, recordBuffer.Length) != recordBuffer.Length)
            return new MftReadResult(null, recordSize, "Unable to read MFT record 0 from the raw source.");

        if (!TryExtractMftDataRuns(recordBuffer, bytesPerSector, clusterSize, out var runs, out var logicalSizeBytes, out var failureReason))
            return new MftReadResult(null, recordSize, failureReason);

        int cappedLength = (int)Math.Min(MftReadLimitBytes, logicalSizeBytes > 0 ? logicalSizeBytes : (long)MftReadLimitBytes);
        if (cappedLength <= 0)
            return new MftReadResult(null, recordSize, "$MFT logical size is empty.");

        bool wasTruncated = logicalSizeBytes > cappedLength;
        var buffer = new byte[cappedLength];
        int totalRead = 0;
        foreach (var run in runs)
        {
            if (totalRead >= cappedLength)
                break;

            int bytesToRead = (int)Math.Min(run.LengthBytes, cappedLength - totalRead);
            long runOffsetBytes;
            try
            {
                runOffsetBytes = checked(logicalVolumeStartOffsetBytes + run.OffsetBytes);
            }
            catch (OverflowException)
            {
                return new MftReadResult(null, recordSize, "The $MFT run-list offset overflowed.");
            }

            if (runOffsetBytes < logicalVolumeStartOffsetBytes)
                return new MftReadResult(null, recordSize, "The $MFT run-list offset is invalid.");

            volume.Seek(runOffsetBytes, SeekOrigin.Begin);
            int read = ReadExactly(volume, buffer, totalRead, bytesToRead);
            if (read != bytesToRead)
                return new MftReadResult(null, recordSize, "Unable to read the full $MFT byte range described by the run list.");

            totalRead += read;
        }

        if (totalRead == 0)
            return new MftReadResult(null, recordSize, "Unable to read any $MFT bytes from the raw source.");

        if (totalRead < buffer.Length)
            Array.Resize(ref buffer, totalRead);

        return new MftReadResult(
            Bytes: buffer,
            RecordSize: recordSize,
            FailureReason: null,
            UsedPhysicalDriveFallback: false,
            WasTruncated: wasTruncated,
            LogicalSizeBytes: logicalSizeBytes);

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

    private static int ParseMftRecordSizeFromBoot(byte[] boot)
    {
        if (boot == null || boot.Length < 0x44)
            return 1024;
        ushort bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
        byte sectorsPerCluster = boot[0x0D];
        if (bytesPerSector == 0 || sectorsPerCluster == 0)
            return 1024;
        int clusterSize = bytesPerSector * sectorsPerCluster;
        sbyte clustersPerMftRecord = unchecked((sbyte)boot[0x40]);
        int size = clustersPerMftRecord < 0
            ? 1 << (-clustersPerMftRecord)
            : (clustersPerMftRecord == 0 ? 1 : clustersPerMftRecord) * clusterSize;
        if (size < 256 || size > 65536)
            return 1024;
        return size;
    }

    private static bool IsNtfsBootSector(byte[] boot)
    {
        if (boot == null || boot.Length < 11)
            return false;
        return boot[0x03] == (byte)'N' && boot[0x04] == (byte)'T' && boot[0x05] == (byte)'F' && boot[0x06] == (byte)'S';
    }

    internal static bool TryExtractMftDataRuns(
        byte[] recordBytes,
        int bytesPerSector,
        int clusterSize,
        out List<(long OffsetBytes, long LengthBytes)> runs,
        out long logicalSizeBytes,
        out string? failureReason)
    {
        runs = new List<(long OffsetBytes, long LengthBytes)>();
        logicalSizeBytes = 0;
        failureReason = null;

        if (recordBytes == null || recordBytes.Length < 64)
        {
            failureReason = "MFT record 0 is missing or too short.";
            return false;
        }

        var record = (byte[])recordBytes.Clone();
        if (!TryApplyUsaFixup(record, bytesPerSector, out failureReason))
            return false;

        if (BitConverter.ToUInt32(record, 0) != FileSignature)
        {
            failureReason = "MFT record 0 does not contain a valid FILE signature.";
            return false;
        }

        int attributeOffset = BitConverter.ToUInt16(record, FirstAttributeOffsetInRecord);
        while (attributeOffset <= record.Length - 8)
        {
            uint attributeType = BitConverter.ToUInt32(record, attributeOffset + AttributeTypeOffset);
            if (attributeType == EndOfAttributes)
                break;

            int attributeLength = (int)BitConverter.ToUInt32(record, attributeOffset + AttributeLengthOffset);
            if (attributeLength <= 0 || attributeOffset + attributeLength > record.Length)
            {
                failureReason = "MFT record 0 contains an invalid attribute length.";
                return false;
            }

            bool nonResident = record[attributeOffset + AttributeNonResidentFlagOffset] != 0;
            if (attributeType == AttributeTypeData && nonResident)
            {
                int runListOffset = BitConverter.ToUInt16(record, attributeOffset + AttributeRunListOffset);
                int runListLength = attributeLength - runListOffset;
                if (runListOffset <= 0 || runListLength <= 0 || attributeOffset + runListOffset > record.Length)
                {
                    failureReason = "MFT record 0 contains an invalid run-list offset.";
                    return false;
                }

                ulong logicalSize = BitConverter.ToUInt64(record, attributeOffset + AttributeRealSizeOffset);
                if (logicalSize > long.MaxValue)
                {
                    failureReason = "$MFT logical size exceeds the supported range.";
                    return false;
                }

                logicalSizeBytes = (long)logicalSize;

                return TryParseDataRuns(record.AsSpan(attributeOffset + runListOffset, runListLength), clusterSize, out runs, out failureReason);
            }

            attributeOffset += attributeLength;
        }

        failureReason = "Could not find the non-resident $MFT DATA attribute.";
        return false;
    }

    private static bool TryApplyUsaFixup(byte[] record, int bytesPerSector, out string? failureReason)
    {
        failureReason = null;
        if (bytesPerSector <= 0 || record.Length < bytesPerSector)
        {
            failureReason = "The MFT record size or sector size is invalid.";
            return false;
        }

        int usaOffset = BitConverter.ToUInt16(record, UsaOffsetInRecord);
        int usaCount = BitConverter.ToUInt16(record, UsaCountInRecord);
        if (usaOffset <= 0 || usaCount <= 0 || usaOffset + (usaCount * 2) > record.Length)
        {
            failureReason = "MFT record 0 contains an invalid update sequence array.";
            return false;
        }

        ushort usaValue = BitConverter.ToUInt16(record, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = (i * bytesPerSector) - 2;
            if (sectorEnd < 0 || sectorEnd + 2 > record.Length)
            {
                failureReason = "MFT record 0 fixup extends beyond the record boundary.";
                return false;
            }

            ushort sectorTrailer = BitConverter.ToUInt16(record, sectorEnd);
            if (sectorTrailer != usaValue)
            {
                failureReason = "MFT record 0 update sequence verification failed.";
                return false;
            }

            record[sectorEnd] = record[usaOffset + (i * 2)];
            record[sectorEnd + 1] = record[usaOffset + (i * 2) + 1];
        }

        return true;
    }

    private static bool TryParseDataRuns(
        ReadOnlySpan<byte> runList,
        int clusterSize,
        out List<(long OffsetBytes, long LengthBytes)> runs,
        out string? failureReason)
    {
        runs = new List<(long OffsetBytes, long LengthBytes)>();
        failureReason = null;
        long currentLcn = 0;
        int index = 0;

        try
        {
            while (index < runList.Length)
            {
                byte header = runList[index++];
                if (header == 0)
                    break;

                int lengthByteCount = header & 0x0F;
                int offsetByteCount = (header >> 4) & 0x0F;
                if (lengthByteCount <= 0 || offsetByteCount <= 0 || index + lengthByteCount + offsetByteCount > runList.Length)
                {
                    failureReason = "The MFT run list is malformed.";
                    return false;
                }

                long clusterCount = ReadUnsignedLittleEndian(runList.Slice(index, lengthByteCount));
                index += lengthByteCount;

                long lcnDelta = ReadSignedLittleEndian(runList.Slice(index, offsetByteCount));
                index += offsetByteCount;

                if (clusterCount <= 0)
                {
                    failureReason = "The MFT run list contains an invalid cluster count.";
                    return false;
                }

                currentLcn = checked(currentLcn + lcnDelta);
                if (currentLcn < 0)
                {
                    failureReason = "The MFT run list resolved to a negative LCN.";
                    return false;
                }

                long offsetBytes = checked(currentLcn * (long)clusterSize);
                long lengthBytes = checked(clusterCount * (long)clusterSize);
                runs.Add((offsetBytes, lengthBytes));
            }
        }
        catch (OverflowException)
        {
            failureReason = "An overflow occurred while calculating the MFT run list.";
            return false;
        }

        if (runs.Count == 0)
        {
            failureReason = "MFT record 0 did not yield any data runs.";
            return false;
        }

        return true;
    }

    private static long ReadUnsignedLittleEndian(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = 0; i < bytes.Length; i++)
            value |= (long)bytes[i] << (8 * i);
        return value;
    }

    private static long ReadSignedLittleEndian(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = 0; i < bytes.Length; i++)
            value |= (long)bytes[i] << (8 * i);

        int shift = (8 - bytes.Length) * 8;
        return (value << shift) >> shift;
    }

    /// <summary>
    /// Read sectors from a physical drive. Requires Administrator.
    /// </summary>
    /// <param name="driveNumber">0-based physical drive number (0 = first disk).</param>
    /// <param name="startSector">Starting sector offset.</param>
    /// <param name="sectorCount">Number of sectors to read (each 512 bytes).</param>
    /// <returns>Read bytes, or null on failure (e.g. access denied, invalid drive).</returns>
    public static byte[]? ReadSectors(int driveNumber, long startSector, int sectorCount)
    {
        if (driveNumber < 0 || sectorCount <= 0)
            return null;

        var totalBytes = (long)sectorCount * SectorSize;
        if (totalBytes > int.MaxValue || totalBytes > RawReadLimitBytes)
            return null;

        var path = $@"\\.\PhysicalDrive{driveNumber}";
        try
        {
            using var stream = OpenReadOnlyDeviceStream(path);
            var offsetBytes = startSector * SectorSize;
            if (offsetBytes > 0)
                stream.Seek(offsetBytes, SeekOrigin.Begin);
            var buffer = new byte[(int)totalBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read != buffer.Length)
                Array.Resize(ref buffer, read);
            return buffer;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Read a contiguous byte range from a physical drive. Offset and size should be sector-aligned (512) for best compatibility.
    /// Requires Administrator.
    /// </summary>
    public static byte[]? ReadBytes(int driveNumber, long offsetBytes, int sizeBytes)
    {
        if (driveNumber < 0 || sizeBytes <= 0 || sizeBytes > RawReadLimitBytes)
            return null;

        var path = $@"\\.\PhysicalDrive{driveNumber}";
        try
        {
            using var stream = OpenReadOnlyDeviceStream(path);
            stream.Seek(offsetBytes, SeekOrigin.Begin);
            var buffer = new byte[sizeBytes];
            var read = stream.Read(buffer, 0, sizeBytes);
            if (read != sizeBytes)
                Array.Resize(ref buffer, read);
            return buffer;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}


