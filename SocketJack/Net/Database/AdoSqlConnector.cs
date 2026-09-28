using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.Database {
    /// <summary>Connector adapter for caller-supplied ADO.NET providers. No provider dependency is imposed on SocketJack.</summary>
    public sealed class AdoSqlConnector : ISqlConnector {
        private readonly DbConnection connection;
        private readonly bool ownsConnection;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1,1);
        public SqlDialect Dialect { get; }
        public AdoSqlConnector(DbConnection connection, SqlDialect dialect, bool ownsConnection = false) {
            this.connection=connection ?? throw new ArgumentNullException(nameof(connection));
            this.ownsConnection=ownsConnection; Dialect=dialect;
        }
        public async Task OpenAsync(CancellationToken cancellationToken = default) {
            if(connection.State!=ConnectionState.Open) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        public async Task<IReadOnlyList<QueryResult>> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken = default) {
            if(command==null) throw new ArgumentNullException(nameof(command));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try {
                await OpenAsync(cancellationToken).ConfigureAwait(false);
                using(var query=connection.CreateCommand()) {
                    query.CommandText=command.Text;
                    foreach(var item in command.Parameters) { var parameter=query.CreateParameter(); parameter.ParameterName=item.Key; parameter.Value=item.Value ?? DBNull.Value; query.Parameters.Add(parameter); }
                    var results=new List<QueryResult>();
                    using(var reader=await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
                        do {
                            var result=new QueryResult { Handled=true, HasResultSet=reader.FieldCount>0 };
                            for(int i=0;i<reader.FieldCount;i++) { result.Columns.Add(reader.GetName(i)); result.DataTypes.Add(reader.GetFieldType(i)); }
                            while(await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { var row=new object[reader.FieldCount]; for(int i=0;i<row.Length;i++) row[i]=reader.IsDBNull(i)?null:reader.GetValue(i); result.Rows.Add(row); }
                            result.RowsAffected=result.HasResultSet?result.Rows.Count:Math.Max(0,reader.RecordsAffected); results.Add(result);
                        } while(await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                    }
                    return results;
                }
            } finally { gate.Release(); }
        }
        public void Dispose() { if(ownsConnection) connection.Dispose(); gate.Dispose(); }
    }
}
