using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.Database {
    public enum SqlDialect { Standard, SqlServer, MySql }

    /// <summary>A command carries values separately from SQL text.</summary>
    public sealed class SqlCommand {
        public string Text { get; set; }
        public SqlDialect Dialect { get; set; } = SqlDialect.SqlServer;
        public Dictionary<string, object> Parameters { get; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public SqlCommand(string text) { Text = text ?? throw new ArgumentNullException(nameof(text)); }
    }

    public sealed class SqlExecutionException : Exception {
        public string SqlState { get; }
        public int Number { get; }
        public SqlExecutionException(string message, string sqlState = "42000", int number = 102) : base(message) {
            SqlState = sqlState; Number = number;
        }
    }

    /// <summary>Implement this interface to add a database connection without changing the query engine.</summary>
    public interface ISqlConnector : IDisposable {
        SqlDialect Dialect { get; }
        Task OpenAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<QueryResult>> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken = default);
    }

    public sealed class EmbeddedSqlConnector : ISqlConnector {
        private readonly DataServer server;
        private readonly SqlSession session;
        public SqlDialect Dialect { get; }
        public EmbeddedSqlConnector(DataServer server, SqlSession session, SqlDialect dialect = SqlDialect.SqlServer) {
            this.server = server ?? throw new ArgumentNullException(nameof(server));
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            Dialect = dialect;
        }
        public Task OpenAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<IReadOnlyList<QueryResult>> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken = default) {
            return Task.FromResult(server.Execute(session, command, cancellationToken));
        }
        public void Dispose() { }
    }
}
