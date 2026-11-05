using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SocketJack.Multiplayer.Web
{
    // Small parameterized adapter to the operating system SQLite C API. No third-party managed wrapper,
    // dynamic SQL supplied by users, private native binary, ORM or runtime code generation is involved.
    internal sealed class NativeSqliteConnection : IDisposable
    {
        private readonly string path;
        internal IntPtr Handle { get; private set; }
        internal NativeSqliteConnection(string path) { this.path = path; }
        internal void Open()
        {
            if (Handle != IntPtr.Zero) throw new InvalidOperationException("SQLite connection is already open.");
            NativeSqlite.RequirePlatform();
            if (NativeSqlite.Version() < 3024000) throw new PlatformNotSupportedException("Account persistence requires system SQLite 3.24 or newer.");
            int result = NativeSqlite.Open(Utf8(path), out IntPtr handle, 2 | 4 | 0x10000);
            if (result != 0) { if (handle != IntPtr.Zero) NativeSqlite.Close(handle); throw new IOException("SQLite could not open the account database (code " + result + ")."); }
            Handle = handle;
        }
        internal NativeSqliteCommand CreateCommand() => new NativeSqliteCommand(this);
        internal NativeSqliteTransaction BeginTransaction() { Execute("BEGIN IMMEDIATE;"); return new NativeSqliteTransaction(this); }
        internal int Execute(string text)
        {
            RequireOpen(); int code = NativeSqlite.Exec(Handle, Utf8(text), out IntPtr error);
            if (error != IntPtr.Zero) NativeSqlite.Free(error); Check(code); return NativeSqlite.Changes(Handle);
        }
        internal static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
        internal static void Check(int code) { if (code != 0) throw new IOException("SQLite operation failed (code " + code + ")."); }
        internal void RequireOpen() { if (Handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeSqliteConnection)); }
        public void Dispose() { IntPtr previous = Handle; Handle = IntPtr.Zero; if (previous != IntPtr.Zero) NativeSqlite.Close(previous); }
    }
    internal sealed class NativeSqliteTransaction : IDisposable
    {
        private NativeSqliteConnection connection;
        private bool committed;
        internal NativeSqliteTransaction(NativeSqliteConnection connection) { this.connection = connection; }
        internal void Commit() { if (connection == null) throw new ObjectDisposedException(nameof(NativeSqliteTransaction)); connection.Execute("COMMIT;"); committed = true; }
        public void Dispose() { var current = connection; connection = null; if (current != null && !committed) current.Execute("ROLLBACK;"); }
    }
    internal sealed class NativeSqliteParameters
    {
        internal readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);
        internal void AddWithValue(string name, object value) => Values.Add(name, value);
    }
    internal sealed class NativeSqliteCommand : IDisposable
    {
        private readonly NativeSqliteConnection connection;
        internal string CommandText { get; set; }
        internal NativeSqliteTransaction Transaction { get; set; }
        internal NativeSqliteParameters Parameters { get; } = new NativeSqliteParameters();
        internal NativeSqliteCommand(NativeSqliteConnection connection) { this.connection = connection; }
        internal NativeSqliteReader ExecuteReader() => new NativeSqliteReader(Prepare());
        internal object ExecuteScalar() { using (var reader = ExecuteReader()) return reader.Read() ? reader.GetValue(0) : null; }
        internal int ExecuteNonQuery()
        {
            if (Parameters.Values.Count == 0) return connection.Execute(CommandText);
            using (var reader = ExecuteReader()) { while (reader.Read()) { } return NativeSqlite.Changes(connection.Handle); }
        }
        private IntPtr Prepare()
        {
            connection.RequireOpen(); if (string.IsNullOrWhiteSpace(CommandText) || CommandText.Length > 65536) throw new InvalidOperationException("Invalid bounded SQL command.");
            byte[] sql = NativeSqliteConnection.Utf8(CommandText); int code = NativeSqlite.Prepare(connection.Handle, sql, sql.Length, out IntPtr statement);
            NativeSqliteConnection.Check(code);
            if (statement == IntPtr.Zero) throw new InvalidOperationException("SQL command did not produce a statement.");
            try
            {
                foreach (var parameter in Parameters.Values)
                {
                    int index = NativeSqlite.ParameterIndex(statement, NativeSqliteConnection.Utf8(parameter.Key));
                    if (index < 1) throw new InvalidOperationException("SQL parameter was not declared.");
                    object value = parameter.Value;
                    if (value == null || value is DBNull) code = NativeSqlite.BindNull(statement, index);
                    else if (value is byte[] blob) code = NativeSqlite.BindBlob(statement, index, blob.Length == 0 ? new byte[1] : blob, blob.Length);
                    else if (value is string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); code = NativeSqlite.BindText(statement, index, bytes.Length == 0 ? new byte[1] : bytes, bytes.Length); }
                    else if (value is int integer) code = NativeSqlite.BindInt64(statement, index, integer);
                    else if (value is long longInteger) code = NativeSqlite.BindInt64(statement, index, longInteger);
                    else if (value is bool boolean) code = NativeSqlite.BindInt64(statement, index, boolean ? 1 : 0);
                    else if (value is double real) code = NativeSqlite.BindDouble(statement, index, real);
                    else throw new InvalidOperationException("Unsupported explicit SQL parameter type.");
                    NativeSqliteConnection.Check(code);
                }
                return statement;
            }
            catch { NativeSqlite.Finalize(statement); throw; }
        }
        public void Dispose() { }
    }
    internal sealed class NativeSqliteReader : IDisposable
    {
        private IntPtr statement;
        private bool done;
        internal NativeSqliteReader(IntPtr statement) { this.statement = statement; }
        internal bool Read()
        {
            if (done) return false;
            if (statement == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeSqliteReader));
            int code = NativeSqlite.Step(statement); if (code == 100) return true; if (code == 101) { done = true; return false; }
            NativeSqliteConnection.Check(code); return false;
        }
        internal string GetString(int ordinal) => (string)GetValue(ordinal);
        internal int GetInt32(int ordinal) => checked((int)(long)GetValue(ordinal));
        internal object GetValue(int ordinal)
        {
            if (statement == IntPtr.Zero || ordinal < 0 || ordinal >= NativeSqlite.ColumnCount(statement)) throw new ArgumentOutOfRangeException(nameof(ordinal));
            int type = NativeSqlite.ColumnType(statement, ordinal);
            if (type == 1) return NativeSqlite.ColumnInt64(statement, ordinal);
            if (type == 2) return NativeSqlite.ColumnDouble(statement, ordinal);
            if (type == 5) return DBNull.Value;
            IntPtr value = type == 4 ? NativeSqlite.ColumnBlob(statement, ordinal) : NativeSqlite.ColumnText(statement, ordinal);
            int count = NativeSqlite.ColumnBytes(statement, ordinal); if (count < 0 || count > 32 * 1024 * 1024) throw new InvalidDataException("Stored SQLite value exceeds its supported bound.");
            byte[] bytes = new byte[count]; if (count > 0) Marshal.Copy(value, bytes, 0, count);
            return type == 4 ? (object)bytes : new UTF8Encoding(false, true).GetString(bytes);
        }
        public void Dispose() { var previous = statement; statement = IntPtr.Zero; if (previous != IntPtr.Zero) NativeSqlite.Finalize(previous); }
    }
    internal static class NativeSqlite
    {
        private static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly IntPtr Transient = new IntPtr(-1);
        internal static void RequirePlatform()
        {
            if (!Windows && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) throw new PlatformNotSupportedException("The included SQLite account store supports Windows 10/11 and Linux with libsqlite3.so.0. Supply IAccountStore for another server platform.");
        }
        internal static int Version() => Windows ? Win.Version() : Linux.Version();
        internal static int Open(byte[] path, out IntPtr db, int flags) => Windows ? Win.Open(path, out db, flags, IntPtr.Zero) : Linux.Open(path, out db, flags, IntPtr.Zero);
        internal static int Close(IntPtr db) => Windows ? Win.Close(db) : Linux.Close(db);
        internal static int Exec(IntPtr db, byte[] sql, out IntPtr error) => Windows ? Win.Exec(db, sql, IntPtr.Zero, IntPtr.Zero, out error) : Linux.Exec(db, sql, IntPtr.Zero, IntPtr.Zero, out error);
        internal static void Free(IntPtr value) { if (Windows) Win.Free(value); else Linux.Free(value); }
        internal static int Prepare(IntPtr db, byte[] sql, int size, out IntPtr statement) => Windows ? Win.Prepare(db, sql, size, out statement, IntPtr.Zero) : Linux.Prepare(db, sql, size, out statement, IntPtr.Zero);
        internal static int Step(IntPtr statement) => Windows ? Win.Step(statement) : Linux.Step(statement);
        internal static int Finalize(IntPtr statement) => Windows ? Win.Finalize(statement) : Linux.Finalize(statement);
        internal static int Changes(IntPtr db) => Windows ? Win.Changes(db) : Linux.Changes(db);
        internal static int ParameterIndex(IntPtr statement, byte[] name) => Windows ? Win.ParameterIndex(statement, name) : Linux.ParameterIndex(statement, name);
        internal static int BindNull(IntPtr statement, int index) => Windows ? Win.BindNull(statement, index) : Linux.BindNull(statement, index);
        internal static int BindInt64(IntPtr statement, int index, long value) => Windows ? Win.BindInt64(statement, index, value) : Linux.BindInt64(statement, index, value);
        internal static int BindDouble(IntPtr statement, int index, double value) => Windows ? Win.BindDouble(statement, index, value) : Linux.BindDouble(statement, index, value);
        internal static int BindText(IntPtr statement, int index, byte[] value, int count) => Windows ? Win.BindText(statement, index, value, count, Transient) : Linux.BindText(statement, index, value, count, Transient);
        internal static int BindBlob(IntPtr statement, int index, byte[] value, int count) => Windows ? Win.BindBlob(statement, index, value, count, Transient) : Linux.BindBlob(statement, index, value, count, Transient);
        internal static int ColumnType(IntPtr statement, int ordinal) => Windows ? Win.ColumnType(statement, ordinal) : Linux.ColumnType(statement, ordinal);
        internal static int ColumnCount(IntPtr statement) => Windows ? Win.ColumnCount(statement) : Linux.ColumnCount(statement);
        internal static long ColumnInt64(IntPtr statement, int ordinal) => Windows ? Win.ColumnInt64(statement, ordinal) : Linux.ColumnInt64(statement, ordinal);
        internal static double ColumnDouble(IntPtr statement, int ordinal) => Windows ? Win.ColumnDouble(statement, ordinal) : Linux.ColumnDouble(statement, ordinal);
        internal static IntPtr ColumnText(IntPtr statement, int ordinal) => Windows ? Win.ColumnText(statement, ordinal) : Linux.ColumnText(statement, ordinal);
        internal static IntPtr ColumnBlob(IntPtr statement, int ordinal) => Windows ? Win.ColumnBlob(statement, ordinal) : Linux.ColumnBlob(statement, ordinal);
        internal static int ColumnBytes(IntPtr statement, int ordinal) => Windows ? Win.ColumnBytes(statement, ordinal) : Linux.ColumnBytes(statement, ordinal);

        private static class Win
        {
            const string Library = "winsqlite3.dll";
            [DllImport(Library, EntryPoint = "sqlite3_libversion_number", CallingConvention = CallingConvention.Cdecl)] internal static extern int Version();
            [DllImport(Library, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)] internal static extern int Open(byte[] path, out IntPtr db, int flags, IntPtr vfs);
            [DllImport(Library, EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)] internal static extern int Close(IntPtr db);
            [DllImport(Library, EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)] internal static extern int Exec(IntPtr db, byte[] text, IntPtr callback, IntPtr context, out IntPtr error);
            [DllImport(Library, EntryPoint = "sqlite3_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void Free(IntPtr value);
            [DllImport(Library, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)] internal static extern int Prepare(IntPtr db, byte[] text, int count, out IntPtr statement, IntPtr tail);
            [DllImport(Library, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)] internal static extern int Step(IntPtr statement);
            [DllImport(Library, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)] internal static extern int Finalize(IntPtr statement);
            [DllImport(Library, EntryPoint = "sqlite3_changes", CallingConvention = CallingConvention.Cdecl)] internal static extern int Changes(IntPtr db);
            [DllImport(Library, EntryPoint = "sqlite3_bind_parameter_index", CallingConvention = CallingConvention.Cdecl)] internal static extern int ParameterIndex(IntPtr statement, byte[] name);
            [DllImport(Library, EntryPoint = "sqlite3_bind_null", CallingConvention = CallingConvention.Cdecl)] internal static extern int BindNull(IntPtr statement, int index);
            [DllImport(Library, EntryPoint = "sqlite3_bind_int64", CallingConvention = CallingConvention.Cdecl)] internal static extern int BindInt64(IntPtr statement, int index, long value);
            [DllImport(Library, EntryPoint = "sqlite3_bind_double", CallingConvention = CallingConvention.Cdecl)] internal static extern int BindDouble(IntPtr statement, int index, double value);
            [DllImport(Library, EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)] internal static extern int BindText(IntPtr statement, int index, byte[] value, int count, IntPtr destructor);
            [DllImport(Library, EntryPoint = "sqlite3_bind_blob", CallingConvention = CallingConvention.Cdecl)] internal static extern int BindBlob(IntPtr statement, int index, byte[] value, int count, IntPtr destructor);
            [DllImport(Library, EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)] internal static extern int ColumnType(IntPtr statement, int ordinal);
            [DllImport(Library, EntryPoint = "sqlite3_column_count", CallingConvention = CallingConvention.Cdecl)] internal static extern int ColumnCount(IntPtr statement);
            [DllImport(Library, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)] internal static extern long ColumnInt64(IntPtr statement, int ordinal);
            [DllImport(Library, EntryPoint = "sqlite3_column_double", CallingConvention = CallingConvention.Cdecl)] internal static extern double ColumnDouble(IntPtr statement, int ordinal);
            [DllImport(Library, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ColumnText(IntPtr statement, int ordinal);
            [DllImport(Library, EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ColumnBlob(IntPtr statement, int ordinal);
            [DllImport(Library, EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)] internal static extern int ColumnBytes(IntPtr statement, int ordinal);
        }
        private static class Linux
        {
            const string Library = "libsqlite3.so.0";
            [DllImport(Library, EntryPoint = "sqlite3_libversion_number", CallingConvention = CallingConvention.Cdecl)] internal static extern int Version();
            [DllImport(Library, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)] internal static extern int Open(byte[] path, out IntPtr db, int flags, IntPtr vfs);
            [DllImport(Library, EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)] internal static extern int Close(IntPtr db);
            [DllImport(Library, EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)] internal static extern int Exec(IntPtr db, byte[] text, IntPtr callback, IntPtr context, out IntPtr error);
            [DllImport(Library, EntryPoint = "sqlite3_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void Free(IntPtr value);
            [DllImport(Library, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)] internal static extern int Prepare(IntPtr db, byte[] text, int count, out IntPtr statement, IntPtr tail);
            [DllImport(Library, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)] internal static extern int Step(IntPtr statement);
            [DllImport(Library, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)] internal static extern int Finalize(IntPtr statement);
            [DllImport(Library, EntryPoint = "sqlite3_changes", CallingConvention = CallingConvention.Cdecl)] internal static extern int Changes(IntPtr db);
            [DllImport(Library, EntryPoint = "sqlite3_bind_parameter_index", CallingConvention = CallingConvention.Cdecl)] internal static extern int ParameterIndex(IntPtr statement, byte[] name);
            [DllImport(Library, EntryPoint = "sqlite3_bind_null", CallingConvention = CallingConvention.Cdecl)] internal static extern int BindNull(IntPtr statement, int index);
            [DllImport(Library, EntryPoint = "sqlite3_bind_int64", CallingConvention = CallingConvention.Cdecl)] internal static extern int BindInt64(IntPtr statement, int index, long value);
            [DllImport(Library, EntryPoint = "sqlite3_bind_double", CallingConvention = CallingConvention.Cdecl)] internal static extern int BindDouble(IntPtr statement, int index, double value);
            [DllImport(Library, EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)] internal static extern int BindText(IntPtr statement, int index, byte[] value, int count, IntPtr destructor);
            [DllImport(Library, EntryPoint = "sqlite3_bind_blob", CallingConvention = CallingConvention.Cdecl)] internal static extern int BindBlob(IntPtr statement, int index, byte[] value, int count, IntPtr destructor);
            [DllImport(Library, EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)] internal static extern int ColumnType(IntPtr statement, int ordinal);
            [DllImport(Library, EntryPoint = "sqlite3_column_count", CallingConvention = CallingConvention.Cdecl)] internal static extern int ColumnCount(IntPtr statement);
            [DllImport(Library, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)] internal static extern long ColumnInt64(IntPtr statement, int ordinal);
            [DllImport(Library, EntryPoint = "sqlite3_column_double", CallingConvention = CallingConvention.Cdecl)] internal static extern double ColumnDouble(IntPtr statement, int ordinal);
            [DllImport(Library, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ColumnText(IntPtr statement, int ordinal);
            [DllImport(Library, EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ColumnBlob(IntPtr statement, int ordinal);
            [DllImport(Library, EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)] internal static extern int ColumnBytes(IntPtr statement, int ordinal);
        }
    }
}
