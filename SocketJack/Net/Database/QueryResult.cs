using System;
using System.Collections.Generic;

namespace SocketJack.Net.Database {

    public class QueryResult {
        /// <summary>Explicitly marks an extension-handled command, including zero-row writes.</summary>
        public bool Handled { get; set; }
        public List<Type> DataTypes { get; set; } = new List<Type>();
        public List<string> Columns { get; set; } = new List<string>();
        /// <summary>
        /// Optional per-column TDS type tokens.  When set, must be the same
        /// length as <see cref="Columns"/>.  Supported values:
        /// <c>0xE7</c> = NVARCHAR (default), <c>0x38</c> = INT (4-byte LE).
        /// If <see langword="null"/>, all columns default to NVARCHAR.
        /// </summary>
        public List<byte> ColumnTypes { get; set; }
        public List<object[]> Rows { get; set; } = new List<object[]>();
        public long RowsAffected { get; set; }
        public bool HasResultSet { get; set; }

        public QueryResult() { }
    }
}
