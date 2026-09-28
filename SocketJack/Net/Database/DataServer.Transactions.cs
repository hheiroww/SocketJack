using System;
using System.Collections.Generic;
using System.Linq;

namespace SocketJack.Net.Database {
    internal sealed class ManagedTransaction {
        internal string BaseHash;
        internal Dictionary<string, Database> Databases;
        internal Dictionary<string, Dictionary<string, Database>> Savepoints = new Dictionary<string, Dictionary<string, Database>>(StringComparer.OrdinalIgnoreCase);
        internal bool Dirty;
    }
    public partial class DataServer {
        /// <summary>Starts an optimistic serializable transaction. Commit detects changes to the committed store.</summary>
        public void BeginTransaction(SqlSession session) {
            if (session == null || !session.IsAuthenticated) throw new SqlExecutionException("Authentication required.", "28000");
            lock (SqlSyncRoot) {
                if (session.Transaction != null) throw new SqlExecutionException("A transaction is already active.", "25001");
                session.Transaction = new ManagedTransaction { BaseHash = ComputePayloadHash(BuildSnapshot()), Databases = CloneDatabases(Databases) };
            }
        }
        public void CommitTransaction(SqlSession session) {
            lock (SqlSyncRoot) {
                var transaction = session?.Transaction ?? throw new SqlExecutionException("No active transaction.", "25000");
                if (transaction.BaseHash != ComputePayloadHash(BuildSnapshot())) throw new SqlExecutionException("Concurrent database change; roll back and retry the transaction.", "40001", 3960);
                if (!transaction.Dirty) { session.Transaction = null; return; }
                var previous = Databases;
                Databases = new System.Collections.Concurrent.ConcurrentDictionary<string, Database>(transaction.Databases, StringComparer.OrdinalIgnoreCase);
                try { ScheduleSave(); session.Transaction = null; }
                catch { Databases = previous; throw; }
            }
        }
        public void RollbackTransaction(SqlSession session) {
            lock (SqlSyncRoot) {
                if (session?.Transaction == null) throw new SqlExecutionException("No active transaction.", "25000");
                session.Transaction = null;
            }
        }
        public void Savepoint(SqlSession session, string name) {
            lock (SqlSyncRoot) {
                var transaction = session?.Transaction ?? throw new SqlExecutionException("No active transaction.", "25000");
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Savepoint name required.", nameof(name));
                transaction.Savepoints[name] = CloneDatabases(transaction.Databases);
            }
        }
        public void RollbackToSavepoint(SqlSession session, string name) {
            lock (SqlSyncRoot) {
                var transaction = session?.Transaction ?? throw new SqlExecutionException("No active transaction.", "25000");
                if (!transaction.Savepoints.TryGetValue(name, out var snapshot)) throw new SqlExecutionException("Unknown savepoint.", "3B001");
                transaction.Databases = CloneDatabases(snapshot);
            }
        }
        private static Dictionary<string, Database> CloneDatabases(IEnumerable<KeyValuePair<string, Database>> databases) {
            var result = new Dictionary<string, Database>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in databases) {
                var source = pair.Value;
                var copy = new Database(source.Name) { OwnerUsername = source.OwnerUsername, SqlAdminUsername = source.SqlAdminUsername, SqlAdminPassword = source.SqlAdminPassword };
                foreach (var item in source.Tables) {
                    var table = new Table(item.Value.Name) { Columns = item.Value.Columns.Select(c => new Column(c.Name, c.DataType, c.MaxLength)).ToList(), Rows = item.Value.Rows.Select(r => r.Select(v => v is byte[] bytes ? (object)bytes.Clone() : v).ToArray()).ToList() };
                    copy.Tables[item.Key] = table;
                }
                result[pair.Key] = copy;
            }
            return result;
        }
    }
}
