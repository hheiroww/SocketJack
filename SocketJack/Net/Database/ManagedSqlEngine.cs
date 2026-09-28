using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace SocketJack.Net.Database {
    // The parser consumes complete statements before executing them. Unsupported syntax
    // is an error, never a successful no-op or a partially interpreted write.
    internal sealed class ManagedSqlEngine {
        private readonly DataServer server;
        private readonly SqlSession session;
        private readonly SqlCommand command;
        private readonly CancellationToken cancellation;
        private readonly List<Token> tokens;
        private int pos;
        private sealed class Token {
            public string Text; public object Value; public bool Literal; public bool Quoted;
        }
        private sealed class Field { public string Name; public Type Type; public string Source; }
        private sealed class Source { public List<Field> Fields = new List<Field>(); public List<object[]> Rows = new List<object[]>(); }
        private sealed class Expr {
            public Func<object[], object> Eval;
            public Func<List<object[]>, object> Aggregate;
            public string Name;
            public Type Type = typeof(object);
        }
        private Token Current => tokens[pos];
        private string Word => Current.Text.ToUpperInvariant();
        private bool Is(string word) => !Current.Literal && !Current.Quoted && Word == word;
        private bool Take(string word) { if (!Is(word)) return false; pos++; return true; }
        private void Need(string word) { if (!Take(word)) throw Error("Expected " + word + ", found " + Current.Text); }
        private static SqlExecutionException Error(string text) => new SqlExecutionException(text);
        private void End() { if (!Is(";") && !Is("<END>")) throw Error("Unsupported or unexpected SQL: " + Current.Text); }
        private string Identifier() {
            if (Current.Literal || Is("<END>") || "(),;=<>+-*/".Contains(Current.Text)) throw Error("Expected identifier.");
            return tokens[pos++].Text;
        }
        private string Name() { string name = Identifier(); while (Take(".")) name += "." + Identifier(); return name; }
        internal ManagedSqlEngine(DataServer server, SqlSession session, SqlCommand command, CancellationToken cancellation) {
            this.server = server; this.session = session; this.command = command; this.cancellation = cancellation;
            tokens = Lex(command.Text);
        }
        internal List<QueryResult> Run() {
            var results = new List<QueryResult>();
            while (!Is("<END>")) {
                cancellation.ThrowIfCancellationRequested();
                if (Take(";")) continue;
                QueryResult result;
                if (Take("SELECT")) result = Select();
                else if (Take("INSERT")) result = Insert();
                else if (Take("UPDATE")) result = Update();
                else if (Take("DELETE")) result = Delete();
                else if (Take("CREATE")) result = Create();
                else if (Take("DROP")) result = Drop();
                else if (Take("BEGIN")) { if (!Take("TRANSACTION")) Take("TRAN"); End(); server.BeginTransaction(session); result = Done(); }
                else if (Take("START")) { Need("TRANSACTION"); End(); server.BeginTransaction(session); result = Done(); }
                else if (Take("COMMIT")) { if (!Take("TRANSACTION")) Take("TRAN"); End(); server.CommitTransaction(session); result = Done(); }
                else if (Take("ROLLBACK")) {
                    if (!Take("TRANSACTION")) Take("TRAN");
                    if (Take("TO")) { Take("SAVEPOINT"); string name = Identifier(); End(); server.RollbackToSavepoint(session, name); }
                    else { End(); server.RollbackTransaction(session); }
                    result = Done();
                }
                else if (Take("SAVEPOINT")) { string name = Identifier(); End(); server.Savepoint(session,name); result = Done(); }
                else if (Take("USE")) { var name = Name(); End(); GetDatabase(name); session.CurrentDatabase = name; result = Done(); }
                else if (Take("SET")) result = Set();
                else throw new SqlExecutionException("SQL feature is not implemented: " + Current.Text, "0A000");
                result.Handled = true; results.Add(result); Take(";");
            }
            if (results.Count == 0) results.Add(Done());
            return results;
        }
        private static QueryResult Done(long count = 0) => new QueryResult { Handled = true, RowsAffected = count };
        private void Changed() { if (session.Transaction != null) session.Transaction.Dirty = true; else server.ScheduleSave(); }
        private Database GetDatabase(string name = null) {
            name = name ?? session.CurrentDatabase ?? server.DefaultDatabase;
            Database database;
            if (!(session.Transaction != null ? session.Transaction.Databases.TryGetValue(name, out database) : server.Databases.TryGetValue(name, out database))) throw Error("Unknown database: " + name);
            if (!server.IsSqlAdminAccount(session.Username) &&
                !string.Equals(database.OwnerUsername ?? database.SqlAdminUsername ?? database.Name, session.Username, StringComparison.OrdinalIgnoreCase))
                throw new SqlExecutionException("Database access denied: " + name, "42501", 916);
            return database;
        }
        private Table GetTable(string name, out Database database) {
            var parts = name.Split('.');
            database = GetDatabase(parts.Length == 3 ? parts[0] : null);
            var key = parts.Length == 3 ? parts[1] + "." + parts[2] : name;
            if (database.Tables.TryGetValue(key, out var table)) return table;
            if (key.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase) && database.Tables.TryGetValue(key.Substring(4), out table)) return table;
            if (parts.Length == 1 && database.Tables.TryGetValue("dbo." + key, out table)) return table;
            throw Error("Invalid object name: " + name);
        }
        private Source ReadSource() {
            string name = Name();
            string alias = name.Split('.').Last();
            if (Take("AS")) alias = Identifier();
            else if (!Is("<END>") && !new[] { "WHERE", "ORDER", "LIMIT", "OFFSET", "INNER", "LEFT", "JOIN", "ON", "GROUP", "HAVING", ";", ",", ")" }.Contains(Word)) alias = Identifier();
            var table = Catalog(name) ?? GetTable(name, out _);
            return new Source { Fields = table.Columns.Select(c => new Field { Name = c.Name, Source = alias, Type = c.DataType }).ToList(), Rows = table.Rows.Select(r => r.Select(Unwrap).ToArray()).ToList() };
        }
        private QueryResult Select() {
            bool distinct = Take("DISTINCT");
            int limit = int.MaxValue;
            if (Take("TOP")) { bool paren = Take("("); limit = Integer(); if (paren) Need(")"); }
            int projectionStart = pos, from = FindTopLevel("FROM"), boundary = StatementEnd();
            var source = new Source(); source.Rows.Add(Array.Empty<object>());
            int afterSource = -1;
            if (from >= 0) {
                pos = from + 1; source = ReadSource();
                while (Is("JOIN") || Is("INNER") || Is("LEFT")) {
                    bool left = Take("LEFT"); if (left) Take("OUTER"); else Take("INNER"); Need("JOIN");
                    var other = ReadSource();
                    var fields = source.Fields.Concat(other.Fields).ToList(); Need("ON");
                    var condition = Expression(fields);
                    var rows = new List<object[]>();
                    foreach (var row in source.Rows) {
                        cancellation.ThrowIfCancellationRequested(); bool found = false;
                        foreach (var right in other.Rows) { var joined = row.Concat(right).ToArray(); if (True(condition.Eval(joined))) { rows.Add(joined); found = true; } }
                        if (left && !found) rows.Add(row.Concat(new object[other.Fields.Count]).ToArray());
                    }
                    source = new Source { Fields = fields, Rows = rows };
                }
                afterSource = pos;
            }
            pos = projectionStart;
            var projections = new List<Expr>();
            do {
                if (Take("*")) {
                    if (source.Fields.Count == 0) throw Error("SELECT * requires a source.");
                    for (int i = 0; i < source.Fields.Count; i++) { int ix = i; var f = source.Fields[i]; projections.Add(new Expr { Name = f.Name, Type = f.Type, Eval = r => r[ix] }); }
                } else {
                    var expression = Expression(source.Fields);
                    if (Take("AS")) expression.Name = Identifier();
                    projections.Add(expression);
                }
            } while (Take(","));
            if (from >= 0) { if (pos != from) throw Error("Invalid SELECT projection."); pos = afterSource; }
            if (Take("WHERE")) { var condition = Expression(source.Fields); source.Rows = source.Rows.Where(r => True(condition.Eval(r))).ToList(); }
            var grouping = new List<Expr>();
            if (Take("GROUP")) { Need("BY"); do { grouping.Add(Expression(source.Fields)); } while (Take(",")); }
            if (grouping.Count > 0 || projections.Any(e => e.Aggregate != null)) {
                foreach (var expression in projections.Where(e => e.Aggregate == null))
                    if (expression.Name == null || !grouping.Any(g => g.Name != null && g.Name.Equals(expression.Name,StringComparison.OrdinalIgnoreCase))) throw Error("Non-aggregate columns must occur in GROUP BY.");
                IEnumerable<List<object[]>> groups = grouping.Count == 0 ? new[] { source.Rows } : source.Rows.GroupBy(r => grouping.Select(g => g.Eval(r)).ToArray(), new RowComparer()).Select(g => g.ToList());
                var aggregateRows = groups.Select(g => projections.Select(e => e.Aggregate != null ? e.Aggregate(g) : e.Eval(g[0])).ToArray()).ToList();
                var aggregateFields = projections.Select((e,i) => new Field { Name = e.Name ?? "Column"+(i+1), Type=e.Type }).ToList();
                source = new Source { Fields=aggregateFields, Rows=aggregateRows };
                projections = aggregateFields.Select((f,i) => new Expr { Name=f.Name, Type=f.Type, Eval=r=>r[i] }).ToList();
                if (Take("HAVING")) { var condition=Expression(source.Fields); source.Rows=source.Rows.Where(r=>True(condition.Eval(r))).ToList(); }
            }
            if (Take("ORDER")) {
                Need("BY"); var orders = new List<(Expr expression, bool descending)>();
                do { var e = Expression(source.Fields); bool desc = Take("DESC"); if (!desc) Take("ASC"); orders.Add((e, desc)); } while (Take(","));
                source.Rows.Sort((a,b) => { foreach (var order in orders) { int diff = Compare(order.expression.Eval(a), order.expression.Eval(b)); if (diff != 0) return order.descending ? -diff : diff; } return 0; });
            }
            if (Take("LIMIT")) limit = Integer();
            int offset = 0;
            if (Take("OFFSET")) { offset = Integer(); Take("ROWS"); if (Take("FETCH")) { Need("NEXT"); limit = Integer(); Need("ROWS"); Need("ONLY"); } }
            End();
            var result = new QueryResult { HasResultSet = true, Handled = true };
            result.Columns.AddRange(projections.Select((e,i) => e.Name ?? "Column" + (i+1)));
            result.DataTypes.AddRange(projections.Select(e => e.Type));
            foreach (var row in source.Rows) { cancellation.ThrowIfCancellationRequested(); result.Rows.Add(projections.Select(e => e.Eval(row)).ToArray()); }
            if (distinct) result.Rows = result.Rows.Distinct(new RowComparer()).ToList();
            result.Rows = result.Rows.Skip(offset).Take(limit).ToList();
            for (int i = 0; i < result.DataTypes.Count; i++) if (result.DataTypes[i] == typeof(object)) result.DataTypes[i] = result.Rows.Select(r => r[i]).FirstOrDefault(v => v != null)?.GetType() ?? typeof(string);
            result.ColumnTypes = result.DataTypes.Select(t => t == typeof(int) ? (byte)0x38 : (byte)0xE7).ToList();
            result.RowsAffected = result.Rows.Count; return result;
        }
        private QueryResult Insert() {
            Need("INTO"); var table = GetTable(Name(), out _);
            var columns = Enumerable.Range(0, table.Columns.Count).ToList();
            if (Take("(")) { columns.Clear(); do { int i = ColumnIndex(table, Identifier()); if (columns.Contains(i)) throw Error("Duplicate insert column."); columns.Add(i); } while (Take(",")); Need(")"); }
            Need("VALUES"); var added = new List<object[]>();
            do {
                Need("("); var values = new List<object>();
                do { values.Add(Expression(new List<Field>()).Eval(Array.Empty<object>())); } while (Take(",")); Need(")");
                if (values.Count != columns.Count) throw Error("INSERT column/value count mismatch.");
                var row = new object[table.Columns.Count];
                for (int i = 0; i < columns.Count; i++) row[columns[i]] = ConvertValue(values[i], table.Columns[columns[i]]);
                added.Add(row);
            } while (Take(","));
            End(); table.Rows.AddRange(added); Changed(); return Done(added.Count);
        }
        private QueryResult Update() {
            var table = GetTable(Name(), out _); var fields = Fields(table); Need("SET"); var assignments = new Dictionary<int, Expr>();
            do { int column = ColumnIndex(table, Identifier()); Need("="); if (assignments.ContainsKey(column)) throw Error("Duplicate assignment."); assignments.Add(column, Expression(fields)); } while (Take(","));
            Expr condition = null; if (Take("WHERE")) condition = Expression(fields); End();
            var replacements = new List<(int index, object[] row)>();
            for (int i = 0; i < table.Rows.Count; i++) {
                cancellation.ThrowIfCancellationRequested(); var original = table.Rows[i].Select(Unwrap).ToArray();
                if (condition != null && !True(condition.Eval(original))) continue;
                var next = (object[])original.Clone(); foreach (var pair in assignments) next[pair.Key] = ConvertValue(pair.Value.Eval(original), table.Columns[pair.Key]);
                replacements.Add((i,next));
            }
            foreach (var next in replacements) table.Rows[next.index] = next.row;
            if (replacements.Count > 0) Changed(); return Done(replacements.Count);
        }
        private QueryResult Delete() {
            Need("FROM"); var table = GetTable(Name(), out _); Expr condition = null;
            if (Take("WHERE")) condition = Expression(Fields(table)); End();
            var remove = new List<int>();
            for (int i = 0; i < table.Rows.Count; i++) { cancellation.ThrowIfCancellationRequested(); if (condition == null || True(condition.Eval(table.Rows[i].Select(Unwrap).ToArray()))) remove.Add(i); }
            for (int i = remove.Count - 1; i >= 0; i--) table.Rows.RemoveAt(remove[i]);
            if (remove.Count > 0) Changed(); return Done(remove.Count);
        }
        private QueryResult Create() {
            Need("TABLE"); string name = Name(); var database = GetDatabase(); Need("("); var table = new Table(name);
            do {
                string column = Identifier(); string type = Identifier().ToUpperInvariant(); int length = -1;
                if (Take("(")) { if (!Take("MAX")) length = Integer(); Need(")"); }
                if (table.Columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase))) throw Error("Duplicate column: " + column);
                table.Columns.Add(new Column(column, TypeFor(type), length));
            } while (Take(","));
            Need(")"); End(); if (!database.Tables.TryAdd(name, table)) throw Error("Table already exists: " + name);
            Changed(); return Done();
        }
        private QueryResult Drop() {
            Need("TABLE"); bool optional = Take("IF"); if (optional) Need("EXISTS"); string name = Name(); End();
            var database = GetDatabase();
            if (!database.Tables.TryRemove(name, out _) && !optional) throw Error("Unknown table: " + name);
            Changed(); return Done();
        }
        private QueryResult Set() {
            string setting = Identifier().ToUpperInvariant();
            if (setting == "NAMES" && command.Dialect == SqlDialect.MySql) {
                string charset = Identifier();
                if (!charset.Equals("utf8mb4", StringComparison.OrdinalIgnoreCase) && !charset.Equals("utf8", StringComparison.OrdinalIgnoreCase)) throw new SqlExecutionException("Only UTF-8 is supported.", "0A000");
                if (Take("COLLATE")) { string collation=Identifier(); if (!collation.Equals("utf8mb4_bin",StringComparison.OrdinalIgnoreCase)) throw new SqlExecutionException("Only binary UTF-8 collation is supported.","0A000"); }
                End(); return Done();
            }
            // These switches have no effect on the supported grammar, but accepting them
            // is necessary for driver session initialization. Other settings are rejected.
            if (!new[] { "NOCOUNT", "ANSI_NULLS", "ANSI_WARNINGS", "QUOTED_IDENTIFIER", "ANSI_PADDING", "CONCAT_NULL_YIELDS_NULL", "ARITHABORT" }.Contains(setting))
                throw new SqlExecutionException("Unsupported SET option: " + setting, "0A000");
            bool on = Take("ON"); if (!on) Need("OFF");
            if (!on && setting != "NOCOUNT" && setting != "ARITHABORT") throw new SqlExecutionException("Legacy OFF semantics are not supported: " + setting, "0A000");
            // SET statements sent by drivers may be separated by whitespace.
            if (!Is("SET") && !Is("SELECT")) End(); return Done();
        }
        private Expr Expression(List<Field> fields, int minimum = 0) {
            Expr left;
            if (Take("(")) { left = Expression(fields); Need(")"); }
            else if (Take("NOT")) { var value = Expression(fields, 3); left = new Expr { Type = typeof(bool), Eval = r => value.Eval(r) == null ? null : (object)!True(value.Eval(r)) }; }
            else if (Take("-")) { var value = Expression(fields, 6); left = new Expr { Type = typeof(decimal), Eval = r => value.Eval(r) == null ? null : (object)-Number(value.Eval(r)) }; }
            else if (Current.Literal) { object value = Current.Value; pos++; left = new Expr { Type = value?.GetType() ?? typeof(object), Eval = _ => value }; }
            else if (Take("NULL")) left = new Expr { Eval = _ => null };
            else if (Take("TRUE")) left = new Expr { Type = typeof(bool), Eval = _ => true };
            else if (Take("FALSE")) left = new Expr { Type = typeof(bool), Eval = _ => false };
            else {
                string name = Name();
                if (name.StartsWith("@") && !name.StartsWith("@@")) {
                    if (!command.Parameters.TryGetValue(name, out var value) && !command.Parameters.TryGetValue(name.Substring(1), out value)) throw Error("Missing parameter: " + name);
                    left = new Expr { Type = value?.GetType() ?? typeof(object), Eval = _ => value == DBNull.Value ? null : value };
                } else if (name.Equals("@@VERSION", StringComparison.OrdinalIgnoreCase)) left = new Expr { Type = typeof(string), Eval = _ => "SocketJack managed SQL " + server.ServerVersion };
                else if (Take("(")) {
                    if (name.Equals("COUNT",StringComparison.OrdinalIgnoreCase) && Take("*")) { Need(")"); left=new Expr { Type=typeof(int), Aggregate=g=>g.Count }; }
                    else {
                        var args = new List<Expr>(); if (!Take(")")) { do { args.Add(Expression(fields)); } while (Take(",")); Need(")"); }
                        left = Function(name, args);
                    }
                } else {
                    var match = fields.Select((f,i) => (f,i)).Where(x => name.Equals(x.f.Name, StringComparison.OrdinalIgnoreCase) || name.Equals(x.f.Source + "." + x.f.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (match.Count != 1) throw Error(match.Count == 0 ? "Unknown column: " + name : "Ambiguous column: " + name);
                    int ix = match[0].i; left = new Expr { Name = match[0].f.Name, Type = match[0].f.Type, Eval = r => r[ix] };
                }
            }
            while (true) {
                if (Is("IS") && minimum <= 3) { pos++; bool not = Take("NOT"); Need("NULL"); var operand = left; left = new Expr { Type = typeof(bool), Eval = r => (operand.Eval(r) == null) != not }; continue; }
                string op = Word; int precedence = op == "OR" ? 1 : op == "AND" ? 2 : new[] { "=", "<>", "!=", "<", ">", "<=", ">=" }.Contains(op) ? 3 : op == "+" || op == "-" ? 4 : op == "*" || op == "/" || op == "%" ? 5 : -1;
                if (Current.Literal || Current.Quoted || precedence < minimum || precedence < 0) break;
                pos++; var a = left; var b = Expression(fields, precedence + 1);
                left = new Expr { Type = precedence <= 3 ? typeof(bool) : typeof(object), Eval = r => Binary(op, a.Eval(r), b.Eval(r)) };
            }
            return left;
        }
        private Expr Function(string name, List<Expr> args) {
            name = name.ToUpperInvariant();
            if (new[] { "COUNT", "SUM", "AVG", "MIN", "MAX" }.Contains(name) && args.Count==1 && args[0].Aggregate==null) {
                return new Expr { Type=name=="COUNT"?typeof(int):name=="AVG"||name=="SUM"?typeof(decimal):args[0].Type, Aggregate=group=> {
                    var values=group.Select(r=>args[0].Eval(r)).Where(v=>v!=null).ToList();
                    if(name=="COUNT") return values.Count;
                    if(values.Count==0) return null;
                    if(name=="SUM") return values.Sum(Number);
                    if(name=="AVG") return values.Average(Number);
                    return values.Aggregate((a,b)=> name=="MIN" ? Compare(a,b)<=0?a:b : Compare(a,b)>=0?a:b);
                } };
            }
            if (name == "DB_NAME" && args.Count == 0) return new Expr { Type = typeof(string), Eval = _ => session.CurrentDatabase };
            if ((name == "COALESCE" || name == "ISNULL" || name == "IFNULL") && args.Count >= 2) return new Expr { Eval = r => args.Select(a => a.Eval(r)).FirstOrDefault(v => v != null) };
            if ((name == "UPPER" || name == "LOWER" || name == "LEN" || name == "LENGTH") && args.Count == 1) return new Expr { Eval = r => { var value = args[0].Eval(r); if (value == null) return null; string s = Convert.ToString(value, CultureInfo.InvariantCulture); return name == "UPPER" ? (object)s.ToUpperInvariant() : name == "LOWER" ? s.ToLowerInvariant() : (object)s.Length; } };
            throw new SqlExecutionException("Unsupported function: " + name, "0A000");
        }
        private Table Catalog(string name) {
            if (!name.Equals("INFORMATION_SCHEMA.TABLES", StringComparison.OrdinalIgnoreCase) && !name.Equals("INFORMATION_SCHEMA.COLUMNS", StringComparison.OrdinalIgnoreCase)) return null;
            var db = GetDatabase(); var table = new Table(name); bool columns = name.EndsWith("COLUMNS", StringComparison.OrdinalIgnoreCase);
            foreach (var label in columns ? new[] { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "ORDINAL_POSITION", "DATA_TYPE" } : new[] { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE" }) table.Columns.Add(new Column(label, label == "ORDINAL_POSITION" ? typeof(int) : typeof(string)));
            foreach (var t in db.Tables.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) {
                string schema = t.Key.Contains(".") ? t.Key.Split('.')[0] : "dbo", shortName = t.Key.Split('.').Last();
                if (!columns) table.Rows.Add(new object[] { db.Name, schema, shortName, "BASE TABLE" });
                else for (int i = 0; i < t.Value.Columns.Count; i++) { var c = t.Value.Columns[i]; table.Rows.Add(new object[] { db.Name, schema, shortName, c.Name, i+1, SqlTypeName(c.DataType) }); }
            }
            return table;
        }
        private int Integer() { if (!Current.Literal || !int.TryParse(Convert.ToString(Current.Value, CultureInfo.InvariantCulture), out int value) || value < 0) throw Error("Expected non-negative integer."); pos++; return value; }
        private int StatementEnd() { for (int i = pos; i < tokens.Count; i++) if (!tokens[i].Literal && !tokens[i].Quoted && (tokens[i].Text == ";" || tokens[i].Text == "<END>")) return i; return tokens.Count-1; }
        private int FindTopLevel(string keyword) { int depth = 0; for (int i = pos; i < StatementEnd(); i++) { var t = tokens[i]; if (t.Literal || t.Quoted) continue; if (t.Text == "(") depth++; else if (t.Text == ")") depth--; else if (depth == 0 && t.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return i; } return -1; }
        private static List<Field> Fields(Table table) => table.Columns.Select(c => new Field { Name = c.Name, Source = table.Name, Type = c.DataType }).ToList();
        private static int ColumnIndex(Table table, string name) { int index = table.Columns.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); if (index < 0) throw Error("Unknown column: " + name); return index; }
        private static bool True(object value) => value is bool b && b;
        private static decimal Number(object value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        private static int Compare(object a, object b) { if (a == null) return b == null ? 0 : -1; if (b == null) return 1; if (a is string && b is string) return string.CompareOrdinal((string)a,(string)b); if (a is IConvertible && b is IConvertible && !(a is bool) && !(a is DateTime)) return Number(a).CompareTo(Number(b)); return ((IComparable)a).CompareTo(b); }
        private static object Binary(string op, object a, object b) {
            if (op == "AND") return a is bool ab && !ab || b is bool bb && !bb ? false : a == null || b == null ? null : (object)(True(a) && True(b));
            if (op == "OR") return True(a) || True(b) ? true : a == null || b == null ? null : (object)false;
            if (a == null || b == null) return null;
            switch(op) {
                case "=": return Compare(a,b) == 0; case "!=": case "<>": return Compare(a,b) != 0;
                case "<": return Compare(a,b) < 0; case ">": return Compare(a,b) > 0; case "<=": return Compare(a,b) <= 0; case ">=": return Compare(a,b) >= 0;
                case "+": return a is string && b is string ? (object)((string)a+(string)b) : Number(a)+Number(b);
                case "-": return Number(a)-Number(b); case "*": return Number(a)*Number(b); case "/": return Number(a)/Number(b); case "%": return Number(a)%Number(b);
            }
            throw Error("Unsupported operator: " + op);
        }
        private static object ConvertValue(object value, Column column) {
            if (value == null || value == DBNull.Value) return null;
            object converted = column.DataType == typeof(Guid) ? Guid.Parse(value.ToString()) : column.DataType.IsInstanceOfType(value) ? value : Convert.ChangeType(value, column.DataType, CultureInfo.InvariantCulture);
            if (converted is string text && column.MaxLength >= 0 && text.Length > column.MaxLength) throw Error("Value exceeds column length: " + column.Name);
            return converted;
        }
        private static Type TypeFor(string name) {
            switch(name) { case "INT": case "INTEGER": return typeof(int); case "BIGINT": return typeof(long); case "SMALLINT": return typeof(short); case "BIT": case "BOOLEAN": return typeof(bool); case "DECIMAL": case "NUMERIC": return typeof(decimal); case "FLOAT": case "DOUBLE": return typeof(double); case "REAL": return typeof(float); case "DATE": case "DATETIME": case "DATETIME2": return typeof(DateTime); case "UNIQUEIDENTIFIER": return typeof(Guid); case "VARCHAR": case "NVARCHAR": case "CHAR": case "NCHAR": case "TEXT": return typeof(string); default: throw new SqlExecutionException("Unsupported data type: " + name, "0A000"); }
        }
        private static string SqlTypeName(Type type) => type == typeof(int) ? "int" : type == typeof(long) ? "bigint" : type == typeof(decimal) ? "decimal" : type == typeof(bool) ? "bit" : type == typeof(DateTime) ? "datetime2" : "nvarchar";
        internal static object Unwrap(object value) {
            if (!(value is System.Text.Json.JsonElement json)) return value == DBNull.Value ? null : value;
            switch(json.ValueKind) { case System.Text.Json.JsonValueKind.Null: case System.Text.Json.JsonValueKind.Undefined: return null; case System.Text.Json.JsonValueKind.String: return json.GetString(); case System.Text.Json.JsonValueKind.True: return true; case System.Text.Json.JsonValueKind.False: return false; case System.Text.Json.JsonValueKind.Number: return json.TryGetInt32(out int i) ? (object)i : json.TryGetInt64(out long l) ? l : (object)json.GetDecimal(); default: throw Error("Unsupported persisted SQL value."); }
        }
        private sealed class RowComparer : IEqualityComparer<object[]> {
            public bool Equals(object[] a, object[] b) => a.SequenceEqual(b);
            public int GetHashCode(object[] row) { unchecked { int hash = 17; foreach (var item in row) hash = hash*31+(item?.GetHashCode() ?? 0); return hash; } }
        }
        private static List<Token> Lex(string sql) {
            var result = new List<Token>();
            for (int i = 0; i < sql.Length;) {
                char c = sql[i]; if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '-' && i+1 < sql.Length && sql[i+1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; continue; }
                if (c == '/' && i+1 < sql.Length && sql[i+1] == '*') { int end = sql.IndexOf("*/", i+2, StringComparison.Ordinal); if (end < 0) throw Error("Unterminated comment."); i = end+2; continue; }
                if ((c == 'N' || c == 'n') && i+1 < sql.Length && sql[i+1] == '\'') { i++; c = sql[i]; }
                if (c == '\'' || c == '"' || c == '[' || c == '`') {
                    char end = c == '[' ? ']' : c; bool literal = c == '\''; i++; var text = new StringBuilder(); bool closed = false;
                    while (i < sql.Length) { char next = sql[i++]; if (next == end) { if (i < sql.Length && sql[i] == end) { text.Append(end); i++; } else { closed = true; break; } } else text.Append(next); }
                    if (!closed) throw Error("Unterminated quoted value.");
                    result.Add(new Token { Text = text.ToString(), Value = literal ? text.ToString() : null, Literal = literal, Quoted = !literal }); continue;
                }
                if (char.IsDigit(c)) { int start = i++; while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.')) i++; string number = sql.Substring(start,i-start); object value = int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer) ? (object)integer : decimal.Parse(number,CultureInfo.InvariantCulture); result.Add(new Token { Text = number, Value = value, Literal = true }); continue; }
                if (char.IsLetter(c) || c == '_' || c == '@' || c == '#') { int start = i++; while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || "_@$#".Contains(sql[i]))) i++; result.Add(new Token { Text = sql.Substring(start,i-start) }); continue; }
                string symbol = c.ToString(); i++; if (i < sql.Length && (c == '<' || c == '>' || c == '!') && (sql[i] == '=' || c == '<' && sql[i] == '>')) symbol += sql[i++];
                if (!"(),;.=<>!+-*/%".Contains(c)) throw Error("Unexpected character: " + c);
                result.Add(new Token { Text = symbol });
            }
            result.Add(new Token { Text = "<END>" }); return result;
        }
    }
}
