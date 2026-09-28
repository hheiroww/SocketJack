using System;
using System.Collections.Generic;

namespace SocketJack.Net.Database {

    internal class DatabaseSnapshot {
        public string OwnerUsername { get; set; }
        public string SqlAdminUsername { get; set; }
        public string SqlAdminPassword { get; set; }
        public string Name { get; set; }
        public Dictionary<string, TableSnapshot> Tables { get; set; } = new Dictionary<string, TableSnapshot>(StringComparer.OrdinalIgnoreCase);
    }
}
