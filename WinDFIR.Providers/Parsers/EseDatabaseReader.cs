using System.Collections.Generic;
using System.IO;
using Microsoft.Isam.Esent.Interop;
using Microsoft.Isam.Esent.Interop.Vista;

namespace WinDFIR.Providers.Parsers;

/// <summary>
/// Read-only reader for ESE / JET "blue" databases (SRUDB.dat, BITS qmgr.db, WebCacheV01.dat, …) built on
/// ManagedEsent (the OS <c>esent.dll</c> engine — authoritative parsing, not a hand-rolled binary reader).
/// </summary>
/// <remarks>
/// Forensic copies are frequently captured in a <b>dirty-shutdown</b> state (the engine was still running at
/// collection time). This reader copies the database to a private temp file, sets recovery OFF, and attaches
/// read-only so a dirty database can still be enumerated without replaying logs and without ever modifying the
/// evidence file. The DB page size is read from the header and applied before init (ESE requires an exact
/// match). All rows are surfaced as <c>Dictionary&lt;string, object?&gt;</c> keyed by column name.
/// </remarks>
public sealed class EseDatabaseReader : IDisposable
{
    private static readonly System.Threading.SemaphoreSlim EseGate = new(1, 1);

    private readonly string _workingCopyPath;
    private readonly bool _ownsWorkingCopy;
    private Instance? _instance;
    private Session? _session;
    private JET_DBID _dbid;
    private bool _attached;
    private bool _gateHeld;

    private EseDatabaseReader(string workingCopyPath, bool ownsWorkingCopy)
    {
        _workingCopyPath = workingCopyPath;
        _ownsWorkingCopy = ownsWorkingCopy;
    }

    /// <summary>
    /// Opens <paramref name="databasePath"/> read-only. The file is copied to a temp working location first
    /// (the original is never opened by the engine), so the evidence is untouched and dirty databases are safe.
    /// </summary>
    public static EseDatabaseReader Open(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("ESE database not found.", databasePath);

        var tempPath = Path.Combine(Path.GetTempPath(), "HostWitness_ESE_" + Guid.NewGuid().ToString("N") + ".edb");
        File.Copy(databasePath, tempPath, overwrite: true);
        // Clear read-only/system attributes the copy may inherit so we can delete it on dispose.
        try { File.SetAttributes(tempPath, FileAttributes.Normal); } catch { /* best effort */ }

        var reader = new EseDatabaseReader(tempPath, ownsWorkingCopy: true);
        try
        {
            reader.AttachAndOpen();
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private void AttachAndOpen()
    {
        // ESE state (page size, temp path) is process-global, so concurrent reader instances collide
        // (EsentTempPathInUse / sharing violations). Serialize the whole reader lifecycle — ESE reads here are
        // sequential and not perf-critical. The gate is held until Dispose.
        EseGate.Wait();
        _gateHeld = true;

        var pageSize = ReadPageSize(_workingCopyPath);

        // ESE page size is a PROCESS-GLOBAL parameter that must be set exactly once, BEFORE any instance is
        // created, and must match the database being attached. Different databases can use different page
        // sizes (e.g. SRUDB.dat=4096, qmgr.db=16384), so once a process has opened one, it cannot open another
        // with a different size. Set it once and surface a clear, catchable error on conflict.
        ApplyGlobalPageSize(pageSize);

        _instance = new Instance("HostWitness_ESE_" + Guid.NewGuid().ToString("N"));
        // Recovery OFF + no logging: read a dirty/standalone DB without its transaction logs, read-only.
        _instance.Parameters.Recovery = false;
        _instance.Parameters.CircularLog = true;
        _instance.Parameters.NoInformationEvent = true;
        _instance.Init();

        _session = new Session(_instance);
        Api.JetAttachDatabase(_session, _workingCopyPath, AttachDatabaseGrbit.ReadOnly);
        _attached = true;
        Api.JetOpenDatabase(_session, _workingCopyPath, null, out _dbid, OpenDatabaseGrbit.ReadOnly);
    }

    /// <summary>The table names present in the database.</summary>
    public IReadOnlyList<string> GetTableNames()
    {
        EnsureOpen();
        return new List<string>(Api.GetTableNames(_session!, _dbid));
    }

    /// <summary>
    /// Enumerates every row of <paramref name="tableName"/> as a column-name → value map. Returns an empty
    /// sequence if the table is absent. Values are typed per the column's ESE type (long, double, DateTime,
    /// byte[], string, …); absent/null columns are omitted from the row.
    /// </summary>
    public IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(string tableName)
    {
        EnsureOpen();

        JET_TABLEID tableId;
        try
        {
            Api.JetOpenTable(_session!, _dbid, tableName, null, 0, OpenTableGrbit.ReadOnly, out tableId);
        }
        catch (EsentObjectNotFoundException)
        {
            yield break;
        }

        try
        {
            var columns = new List<ColumnInfo>(Api.GetTableColumns(_session!, tableId));
            Api.MoveBeforeFirst(_session!, tableId);
            while (Api.TryMoveNext(_session!, tableId))
            {
                var row = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var column in columns)
                {
                    var value = RetrieveValue(tableId, column);
                    if (value != null)
                        row[column.Name] = value;
                }
                yield return row;
            }
        }
        finally
        {
            Api.JetCloseTable(_session!, tableId);
        }
    }

    private object? RetrieveValue(JET_TABLEID tableId, ColumnInfo column)
    {
        var sesid = _session!;
        var id = column.Columnid;
        switch (column.Coltyp)
        {
            case JET_coltyp.Bit:
                return Api.RetrieveColumnAsBoolean(sesid, tableId, id);
            case JET_coltyp.UnsignedByte:
                return Api.RetrieveColumnAsByte(sesid, tableId, id);
            case JET_coltyp.Short:
                return Api.RetrieveColumnAsInt16(sesid, tableId, id);
            case JET_coltyp.Long:
                return Api.RetrieveColumnAsInt32(sesid, tableId, id);
            case JET_coltyp.Currency:
                return Api.RetrieveColumnAsInt64(sesid, tableId, id);
            case JET_coltyp.IEEESingle:
                return Api.RetrieveColumnAsFloat(sesid, tableId, id);
            case JET_coltyp.IEEEDouble:
                return Api.RetrieveColumnAsDouble(sesid, tableId, id);
            case JET_coltyp.DateTime:
                return Api.RetrieveColumnAsDateTime(sesid, tableId, id);
            case JET_coltyp.Text:
            case JET_coltyp.LongText:
                return Api.RetrieveColumnAsString(sesid, tableId, id);
            case JET_coltyp.Binary:
            case JET_coltyp.LongBinary:
                return Api.RetrieveColumn(sesid, tableId, id);
            default:
                // Vista+ extended types (UnsignedLong, LongLong, GUID, UnsignedShort) are exposed via the
                // VistaColtyp values; retrieve by their underlying width.
                return RetrieveExtendedValue(tableId, column);
        }
    }

    private object? RetrieveExtendedValue(JET_TABLEID tableId, ColumnInfo column)
    {
        var sesid = _session!;
        var id = column.Columnid;
        switch ((int)column.Coltyp)
        {
            case (int)VistaColtyp.UnsignedShort:
                return (int)Api.RetrieveColumnAsUInt16(sesid, tableId, id).GetValueOrDefault();
            case (int)VistaColtyp.UnsignedLong:
                return (long)Api.RetrieveColumnAsUInt32(sesid, tableId, id).GetValueOrDefault();
            case (int)VistaColtyp.LongLong:
                return Api.RetrieveColumnAsInt64(sesid, tableId, id);
            case (int)VistaColtyp.GUID:
                return Api.RetrieveColumnAsGuid(sesid, tableId, id);
            default:
                return Api.RetrieveColumn(sesid, tableId, id); // raw bytes fallback
        }
    }

    private static readonly object GlobalPageSizeLock = new();
    private static int _processPageSize; // 0 = not yet set this process

    /// <summary>
    /// Thrown when a process tries to open an ESE database whose page size differs from one already opened
    /// this process (the page size is a per-process global the engine cannot change after first use).
    /// </summary>
    public sealed class EsePageSizeConflictException : InvalidOperationException
    {
        public EsePageSizeConflictException(string message) : base(message) { }
    }

    private static void ApplyGlobalPageSize(int pageSize)
    {
        if (pageSize <= 0)
            return;

        lock (GlobalPageSizeLock)
        {
            if (_processPageSize == 0)
            {
                SystemParameters.DatabasePageSize = pageSize;
                _processPageSize = pageSize;
            }
            else if (_processPageSize != pageSize)
            {
                throw new EsePageSizeConflictException(
                    $"This session already opened an ESE database with page size {_processPageSize}; cannot open " +
                    $"one with page size {pageSize} (the ESE page size is a per-process global). Restart the " +
                    "application and open this database first.");
            }
        }
    }

    /// <summary>Reads the database page size from the ESE header (4 bytes at offset 236).</summary>
    private static int ReadPageSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[256];
            if (fs.Read(header, 0, header.Length) < header.Length)
                return 0;
            return BitConverter.ToInt32(header, 236);
        }
        catch
        {
            return 0;
        }
    }

    private void EnsureOpen()
    {
        if (_session == null)
            throw new ObjectDisposedException(nameof(EseDatabaseReader));
    }

    public void Dispose()
    {
        try
        {
            if (_attached && _session != null)
            {
                try { Api.JetCloseDatabase(_session, _dbid, CloseDatabaseGrbit.None); } catch { }
                try { Api.JetDetachDatabase(_session, _workingCopyPath); } catch { }
            }
            _session?.Dispose();
            _instance?.Dispose();
        }
        catch
        {
            // best effort teardown
        }
        finally
        {
            _session = null;
            _instance = null;
            if (_ownsWorkingCopy)
            {
                try { if (File.Exists(_workingCopyPath)) File.Delete(_workingCopyPath); } catch { }
            }
            if (_gateHeld)
            {
                _gateHeld = false;
                EseGate.Release();
            }
        }
    }
}
