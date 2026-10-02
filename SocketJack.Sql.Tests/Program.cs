using SocketJack.Net.Database;
using SocketJack.Net;
using Command = SocketJack.Net.Database.SqlCommand;

var root = Path.Combine(Path.GetTempPath(), "socketjack-sql-" + Guid.NewGuid().ToString("N"));
var server = new DataServer(0, "SQL regression", loadFromDisk: false) { AutoSave = false, DataPath = Path.Combine(root, "data.json") };
server.SetSqlAdminAccount("tester", "Test-only-Password-123!");
server.Databases["db"].OwnerUsername = "tester";
var session = new SqlSession { Username = "tester", IsAuthenticated = true, CurrentDatabase = "db", ConnectionId = Guid.NewGuid() };
int passed = 0;
QueryResult Run(string sql) => server.Execute(session, new Command(sql)).Last();
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
void Reject(string sql) { bool rejected = false; try { Run(sql); } catch (SqlExecutionException) { rejected = true; } Check(rejected, "reject " + sql); }
Run("CREATE TABLE Jobs (Id int, Name nvarchar(100), Amount decimal)");
Run("INSERT INTO Jobs (Name,Id,Amount) VALUES ('Oil, filter',1,19.25),('Brake service',2,100),('O''Brien',3,NULL)");
Check((int)Run("SELECT Id FROM Jobs WHERE Amount > 20 ORDER BY Id DESC").Rows.Single()[0] == 2, "filter and ordered projection");
Check((string)Run("SELECT Name FROM Jobs WHERE Id=3").Rows[0][0] == "O'Brien", "escaped string");
var parameterized = new Command("SELECT Name FROM Jobs WHERE Id=@id"); parameterized.Parameters["id"] = 1;
Check((string)server.Execute(session, parameterized).Single().Rows[0][0] == "Oil, filter", "typed parameter");
Check(Run("SELECT Id FROM Jobs WHERE Amount IS NULL").Rows.Count == 1, "SQL NULL predicate");
Check(Run("SELECT Id FROM Jobs WHERE Amount = NULL").Rows.Count == 0, "SQL unknown predicate");
Check(Run("SELECT Id FROM Jobs WHERE (Id=1 OR Id=2) AND Amount > 20").Rows.Count == 1, "boolean precedence");
Check(Convert.ToDecimal(Run("SELECT 2+3*4 AS Value").Rows[0][0]) == 14, "scalar arithmetic");
Check(Run("SELECT 'FROM WHERE ;' AS Text").Rows.Count == 1, "keywords inside literals");
Check(Run("SELECT Id FROM Jobs ORDER BY Id DESC LIMIT 1 OFFSET 1").Rows[0][0].Equals(2), "limit offset");
Run("CREATE TABLE Parts (JobId int, Description nvarchar(50))"); Run("INSERT INTO Parts VALUES (1,'Filter'),(2,'Pads')");
Check(Run("SELECT j.Id,p.Description FROM Jobs AS j INNER JOIN Parts AS p ON j.Id=p.JobId ORDER BY j.Id").Rows.Count == 2, "inner join");
Check(Run("SELECT j.Id,p.Description FROM Jobs AS j LEFT JOIN Parts AS p ON j.Id=p.JobId WHERE p.JobId IS NULL").Rows.Count == 1, "left join");
Check(Run("UPDATE Jobs SET Amount=Amount+5 WHERE Id=1").RowsAffected == 1, "expression update");
Check(Convert.ToDecimal(Run("SELECT Amount FROM Jobs WHERE Id=1").Rows[0][0]) == 24.25m, "decimal preserved");
Reject("UPDATE Jobs SET Amount=999 GARBAGE");
Check(Convert.ToDecimal(Run("SELECT Amount FROM Jobs WHERE Id=1").Rows[0][0]) == 24.25m, "rejected update is atomic");
Reject("DELETE FROM Jobs WHERE Missing=1"); Check(Run("SELECT * FROM Jobs").Rows.Count == 3, "rejected delete preserves rows");
Reject("CREATE TABLE Jobs (Other int)"); Check(Run("SELECT * FROM Jobs").Rows.Count == 3, "create does not replace table");
Reject("SELECT Missing FROM Jobs"); Check(Run("SELECT Id FROM Jobs GROUP BY Id").Rows.Count==3,"grouping without aggregate");
Check((int)Run("SELECT COUNT(*) AS Count FROM Jobs").Rows[0][0]==3,"COUNT star");
Check((int)Run("SELECT COUNT(Amount) AS Count FROM Jobs").Rows[0][0]==2,"COUNT excludes NULL");
Check((decimal)Run("SELECT SUM(Amount) AS Total FROM Jobs").Rows[0][0]==124.25m,"SUM decimal");
Check((int)Run("SELECT JobId,COUNT(*) AS Count FROM Parts GROUP BY JobId HAVING Count>0 ORDER BY JobId").Rows[0][1]==1,"GROUP BY HAVING ORDER BY");
Check(Run("SELECT SUM(Amount) FROM Jobs WHERE Id=999").Rows[0][0]==null,"empty SUM is NULL"); Run("BEGIN TRANSACTION"); Run("UPDATE Jobs SET Amount=999 WHERE Id=1");
Check(Convert.ToDecimal(Run("SELECT Amount FROM Jobs WHERE Id=1").Rows[0][0]) == 999m, "transaction sees staged changes");
var observer = new SqlSession { Username="tester", IsAuthenticated=true, CurrentDatabase="db" };
Check(Convert.ToDecimal(server.Execute(observer,new Command("SELECT Amount FROM Jobs WHERE Id=1")).Single().Rows[0][0]) == 24.25m, "other session cannot see uncommitted changes");
Run("SAVEPOINT first"); Run("DELETE FROM Jobs WHERE Id=2"); Run("ROLLBACK TO SAVEPOINT first");
Check(Run("SELECT * FROM Jobs").Rows.Count == 3, "savepoint rollback");
Run("ROLLBACK"); Check(Convert.ToDecimal(Run("SELECT Amount FROM Jobs WHERE Id=1").Rows[0][0]) == 24.25m, "rollback restores values");
Run("BEGIN TRANSACTION"); Run("UPDATE Jobs SET Amount=30 WHERE Id=1"); Run("COMMIT");
Check(Convert.ToDecimal(Run("SELECT Amount FROM Jobs WHERE Id=1").Rows[0][0]) == 30m, "commit publishes changes");
Run("BEGIN TRANSACTION"); Run("UPDATE Jobs SET Amount=40 WHERE Id=1"); server.Execute(observer,new Command("UPDATE Jobs SET Amount=50 WHERE Id=1"));
Reject("COMMIT"); Run("ROLLBACK"); Check(Convert.ToDecimal(Run("SELECT Amount FROM Jobs WHERE Id=1").Rows[0][0]) == 50m, "conflict cannot overwrite concurrent write");
Check(Run("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='Jobs'").Rows.Count == 1, "schema discovery");
Check(server.Execute(session, new Command("SELECT 1; SELECT 2;")).Count == 2, "multiple result sets");
bool denied = false;
try { server.Execute(new SqlSession { Username="intruder", IsAuthenticated=true, CurrentDatabase="db" }, new Command("SELECT * FROM Jobs")); } catch (SqlExecutionException e) { denied = e.SqlState == "42501"; }
Check(denied, "database isolation");
server.QueryExecuting += (SqlSession s, string text, ref QueryResult result) => { if (text == "extension no-op") result.Handled = true; };
Check(Run("extension no-op").RowsAffected == 0, "explicit zero row hook");
Directory.CreateDirectory(root); server.Save();
var reloaded = new DataServer(0, "reload", loadFromDisk:false) { AutoSave=false, DataPath=server.DataPath }; reloaded.Load();
Check(reloaded.Execute(session, new Command("SELECT Name FROM Jobs WHERE Id=3")).Single().Rows[0][0].Equals("O'Brien"), "persisted rows query after reload");
Console.WriteLine($"{passed} SQL checks passed. Isolated data: {root}");

var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
using var wire = new DataServer(port, "wire tests", loadFromDisk:false) { AutoSave=false, Databases=server.Databases, EncryptionMode=1 };
wire.SetSqlAdminAccount("tester", "Test-only-Password-123!"); wire.AllowSqlLoginIpAddress("127.0.0.1");
wire.LogOutput += text => Console.WriteLine("SERVER: " + text);
wire.QueryExecuting += (SqlSession ss, string sql, ref QueryResult qr) => Console.WriteLine("QUERY: " + sql);
Check(wire.Listen(), "TDS listener starts");
try {
    using var client = new Microsoft.Data.SqlClient.SqlConnection($"Server=127.0.0.1,{port};Database=db;User ID=tester;Password=Test-only-Password-123!;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5;Pooling=False");
    client.Open(); Check(client.State==System.Data.ConnectionState.Open, "Microsoft SqlClient encrypted login");
    using var query = client.CreateCommand(); query.CommandTimeout=5;
    query.CommandText="SELECT Id,Amount,Name FROM Jobs WHERE Id=1";
    using(var reader=query.ExecuteReader()) { Check(reader.Read() && reader.GetInt32(0)==1 && reader.GetDecimal(1)==50m, "SqlClient typed integer and decimal result"); }
    query.CommandText="SELECT Name FROM Jobs WHERE Id=@id"; query.Parameters.AddWithValue("@id",3);
    Check((string)query.ExecuteScalar()=="O'Brien", "SqlClient typed RPC parameters"); query.Parameters.Clear();
    query.CommandText="SELECT 1 AS One; SELECT 2 AS Two";
    using(var reader=query.ExecuteReader()) { Check(reader.Read() && reader.GetInt32(0)==1 && reader.NextResult() && reader.Read() && reader.GetInt32(0)==2 && !reader.NextResult(), "SqlClient multiple results"); }
    server.Databases["db"].Tables["Large"] = new Table("Large") { Columns=new(){ new Column("Text",typeof(string)) }, Rows=new(){ new object[]{new string('漢',50000)} } };
    query.CommandText="SELECT Text FROM Large"; Check(((string)query.ExecuteScalar()).Length==50000, "SqlClient 100KB fragmented response");
    query.CommandText="SELECT missing FROM Jobs";
    bool error=false; try { query.ExecuteScalar(); } catch(Microsoft.Data.SqlClient.SqlException) { error=true; } Check(error,"SqlClient gets actual SQL error");
    query.CommandText="SELECT 7"; Check((int)query.ExecuteScalar()==7,"SqlClient connection reusable after error");
    using var managed = new DataClient("127.0.0.1","db","tester","Test-only-Password-123!") { Port=port, Encrypt=true, TrustServerCertificate=true, ConnectionTimeout=5, CommandTimeout=5 };
    managed.Open(); Check(managed.State==System.Data.ConnectionState.Open,"SocketJack DataClient encrypted login");
    var rows=managed.ExecuteQuery("SELECT Id,Amount,Name FROM Jobs WHERE Id=1");
    Check((int)rows.Rows[0][0]==1 && (decimal)rows.Rows[0][1]==50m,"SocketJack DataClient typed decoding");
    Check(((string)managed.ExecuteQuery("SELECT Text FROM Large").Rows[0][0]).Length==50000,"SocketJack DataClient packet reassembly");
} finally { wire.StopListening(); }
Console.WriteLine($"TOTAL: {passed} checks passed");

using var mysql = new MySqlProtocolServer(server,0);
mysql.ConnectionError += error => Console.WriteLine("MYSQL: " + error.Message);
mysql.Start(); server.AllowSqlLoginIpAddress("127.0.0.1");
using(var client = new MySqlConnector.MySqlConnection($"Server=127.0.0.1;Port={mysql.Port};Database=db;User ID=tester;Password=Test-only-Password-123!;SslMode=Required;Pooling=False;ConnectionTimeout=5")) {
    client.Open(); Check(client.State==System.Data.ConnectionState.Open,"MySQL driver encrypted login");
    using var query=client.CreateCommand(); query.CommandTimeout=5;
    query.CommandText="SELECT Id,Amount,Name FROM Jobs WHERE Id=1";
    using(var reader=query.ExecuteReader()) Check(reader.Read() && reader.GetInt32(0)==1 && reader.GetDecimal(1)==50m,"MySQL driver typed results");
    query.CommandText="SELECT Name FROM Jobs WHERE Id=@id"; query.Parameters.AddWithValue("@id",3); Check((string)query.ExecuteScalar()=="O'Brien","MySQL driver text query parameters"); query.Parameters.Clear();
    query.CommandText="SELECT 1; SELECT 2";
    using(var reader=query.ExecuteReader()) Check(reader.Read() && reader.GetInt32(0)==1 && reader.NextResult() && reader.Read() && reader.GetInt32(0)==2,"MySQL multiple results");
    query.CommandText="BEGIN; UPDATE Jobs SET Amount=10 WHERE Id=1; ROLLBACK; SELECT Amount FROM Jobs WHERE Id=1";
    Check(Convert.ToDecimal(query.ExecuteScalar())==50m,"MySQL rollback");
    using var connector=new AdoSqlConnector(client,SqlDialect.MySql);
    var cmd=new Command("SELECT Name FROM Jobs WHERE Id=@id") { Dialect=SqlDialect.MySql }; cmd.Parameters["@id"]=2;
    Check((string)(await connector.ExecuteAsync(cmd)).Single().Rows[0][0]=="Brake service","extensible ADO connector");
}
Console.WriteLine($"TOTAL: {passed} checks passed");
