using System;
using System.Collections.Generic;
using SocketJack.Net;
using SocketJack.Net.Database;

namespace SocketJack.Examples {

    /// <summary>
    /// Example usage of DataServer — standalone, MutableTcpServer integration, and MSSQL import.
    /// </summary>
    public class DataServerExample {

        /// <summary>
        /// Standalone mode — DataServer owns the TCP listener on port 1433.
        /// </summary>
        public static void StandaloneExample() {
            var server = new DataServer(1433, "MyDataServer");

            // Persistence
            server.DataPath = "myserver_data.json";
            server.AutoSave = true;
            server.AutoSaveDebounceMs = 500;

            // Authentication
            server.Username = "sa";
            server.Password = "YourStrong!Passw0rd";
            server.AllowSqlAuth = true;
            server.ServerName = "MYSQLSERVER";

            server.Users.TryAdd("admin", "admin123");

            // Seed data
            var testDb = new Database("TestDB");
            var usersTable = new Table("Users");
            usersTable.Columns.Add(new Column("ID", typeof(int)));
            usersTable.Columns.Add(new Column("Name", typeof(string), 100));
            usersTable.Columns.Add(new Column("Email", typeof(string), 200));
            usersTable.Rows.Add(new object[] { 1, "John Doe", "john@example.com" });
            usersTable.Rows.Add(new object[] { 2, "Jane Smith", "jane@example.com" });
            testDb.Tables.TryAdd("Users", usersTable);
            server.Databases.TryAdd("TestDB", testDb);
            server.Save();

            // Query handler
            server.QueryExecuting += delegate (SqlSession session, string query, ref QueryResult result) {
                Console.WriteLine($"Query from {session.Username}: {query}");
                HandleQuery(server, session, query, ref result);
            };

            Console.WriteLine("Starting standalone MSSQL Server Emulator on port 1433...");
            if (server.Listen()) {
                Console.WriteLine("Server started. Connect with SSMS:");
                Console.WriteLine($"  Server : localhost,1433");
                Console.WriteLine($"  Login  : {server.Username}");
                Console.ReadKey();
                server.StopListening();
                server.Dispose();
            }
        }

        /// <summary>
        /// MutableTcpServer mode — TDS/MSSQL shares a port with HTTP, WebSocket, and SocketJack.
        /// </summary>
        public static void MutableTcpServerExample() {
            // Create a MutableTcpServer that serves multiple protocols on one port
            var mutable = new MutableTcpServer(1433, "MultiServer");
            mutable.EnabledProtocols.UnionWith(new[] { MutableTcpProtocols.Http, MutableTcpProtocols.WebSocket, MutableTcpProtocols.Tds });

            // Create TDS handler with a hosted-mode DataServer (no standalone listener).
            // The MutableTcpServer owns the TCP listener — DataServer only provides
            // the in-memory database engine, persistence, and import capabilities.
            var tds = new TdsProtocolHandler();
            var dataServer = tds.Server;
            dataServer.DataPath = "myserver_data.json";
            dataServer.Username = "sa";
            dataServer.Password = "YourStrong!Passw0rd";
            dataServer.ServerName = "MYSQLSERVER";

            // Register TDS protocol handler so MSSQL clients are detected automatically
            mutable.RegisterProtocol(tds);

            // Query handler
            dataServer.QueryExecuting += delegate (SqlSession session, string query, ref QueryResult result) {
                Console.WriteLine($"[TDS] Query from {session.Username}: {query}");
                HandleQuery(dataServer, session, query, ref result);
            };

            // You can also set up HTTP routes on the same port
            mutable.Http.Map("GET", "/", (conn, req, ct) => "<h1>Hello from MutableTcpServer!</h1>");

            Console.WriteLine("Starting MutableTcpServer on port 1433 (TDS + HTTP + WebSocket)...");
            if (mutable.Listen()) {
                Console.WriteLine("Server started.");
                Console.WriteLine("  SSMS       : localhost,1433  (SQL Auth)");
                Console.WriteLine("  Browser    : http://localhost:1433/");
                Console.ReadKey();
                mutable.StopListening();
                mutable.Dispose();
            }
        }

        /// <summary>
        /// Import from an existing MSSQL database.
        /// Requires Microsoft.Data.SqlClient in your application project:
        ///   dotnet add package Microsoft.Data.SqlClient
        /// </summary>
        public static void ImportExample() {
            var server = new DataServer(1433, "ImportServer");
            server.DataPath = "imported_data.json";
            server.Username = "sa";
            server.Password = "YourStrong!Passw0rd";

            // -------------------------------------------------------------------
            // Import from a real MSSQL database.
            // The user must add Microsoft.Data.SqlClient to their own project.
            //
            //   using Microsoft.Data.SqlClient;
            //
            //   var sqlConn = new SqlConnection(
            //       "Server=.;Database=AdventureWorks;Trusted_Connection=True;TrustServerCertificate=True;");
            //
            //   // Import all tables:
            //   server.ImportFromMssql(sqlConn);
            //
            //   // Or import specific tables only:
            //   server.ImportFromMssql(sqlConn, tableFilter: new[] { "Person.Person", "Sales.SalesOrderHeader" });
            //
            //   // Import schema only (no row data):
            //   server.ImportFromMssql(sqlConn, importData: false);
            //
            //   // Limit rows per table:
            //   server.ImportFromMssql(sqlConn, maxRowsPerTable: 1000);
            //
            //   // After import, save to disk for future startups:
            //   server.Save();
            // -------------------------------------------------------------------

            Console.WriteLine("Import example — uncomment the SqlConnection lines above to use.");
        }

        private static void HandleQuery(DataServer server, SqlSession session, string query, ref QueryResult result) {
            string upper = query.Trim().ToUpperInvariant();

            if (upper.StartsWith("SELECT")) {
                if (server.Databases.TryGetValue(session.CurrentDatabase, out var db)) {
                    foreach (var tkvp in db.Tables) {
                        if (upper.Contains(tkvp.Key.ToUpperInvariant())) {
                            result.HasResultSet = true;
                            result.Columns.AddRange(tkvp.Value.Columns.ConvertAll(c => c.Name));
                            foreach (var row in tkvp.Value.Rows)
                                result.Rows.Add(row);
                            result.RowsAffected = result.Rows.Count;
                            return;
                        }
                    }
                }
                result.HasResultSet = true;
                result.RowsAffected = 0;
            } else if (upper.StartsWith("INSERT") || upper.StartsWith("UPDATE") || upper.StartsWith("DELETE")) {
                result.HasResultSet = false;
                result.RowsAffected = 1;
                server.ScheduleSave();
            } else {
                result.HasResultSet = false;
                result.RowsAffected = 0;
            }
        }
    }
}
