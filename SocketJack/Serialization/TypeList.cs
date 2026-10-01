using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SocketJack.Serialization
{
    public class TypeList : List<string> {
        private readonly Dictionary<string, Type> registered = new Dictionary<string, Type>(StringComparer.Ordinal);
        private readonly object resolveGate = new object();
        private string[] indexedEntries = Array.Empty<string>();
        private Dictionary<string, (Type Type, bool Array)> index;
        private bool unresolved;

        internal Type Resolve(string name) {
            if (string.IsNullOrWhiteSpace(name)) return null;
            lock (resolveGate) {
                // TypeList inherits List<string>; callers can mutate it through the base class.
                // Compare the live entries before reusing metadata, so removals/replacements/reordering
                // take effect immediately. Only locally configured names populate this index.
                bool changed = index == null || unresolved || indexedEntries.Length != Count;
                for (int i = 0; !changed && i < Count; i++) changed = indexedEntries[i] != this[i];
                if (!changed) return index.TryGetValue(name, out var cached) ? (cached.Array ? cached.Type.MakeArrayType() : cached.Type) : null;
                var rebuilt = new Dictionary<string, (Type Type, bool Array)>(StringComparer.Ordinal);
                var entries = ToArray(); unresolved = false;
                foreach (string allowed in entries) {
                    if (string.IsNullOrWhiteSpace(allowed)) continue;
                    Type type;
                    if (!registered.TryGetValue(allowed, out type)) {
                        // Only locally configured allowlist entries are resolved. Never resolve a peer-supplied assembly name.
                        type = System.Type.GetType(allowed, false);
                        if (type == null) foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                            type = assembly.GetType(allowed, false);
                            if (type != null) break;
                        }
                    }
                    if (type == null) { unresolved = true; continue; }
                    // Preserve first-match semantics for ambiguous locally registered names.
                    if (type.FullName != null) {
                        rebuilt.TryAdd(type.FullName, (type, false));
                        rebuilt.TryAdd(type.FullName + "[]", (type, true));
                    }
                    if (type.AssemblyQualifiedName != null) rebuilt.TryAdd(type.AssemblyQualifiedName, (type, false));
                }
                indexedEntries = entries; index = rebuilt;
                return index.TryGetValue(name, out var found) ? (found.Array ? found.Type.MakeArrayType() : found.Type) : null;
            }
        }


        internal TypeList Copy() {
            lock (resolveGate) {
                var copy = new TypeList(ToArray());
                foreach (var entry in registered) copy.registered[entry.Key] = entry.Value;
                return copy;
            }
        }

        public TypeList() {

        }

        public TypeList(Type[] Types) {
            foreach (Type Type in Types) {
                if (Type == null) continue;
                Add(Type);
            }
                
        }

        public TypeList(string[] Types) {
            foreach (string Type in Types) {
                if (Type == null) continue;
                if (!Contains(Type)) Add(Type);
            }
        }

        public bool Contains(Type Type) {
            if (Type == null) return false;
            if (base.Contains(Type.FullName)) return Resolve(Type.FullName) == Type;
            return Type.IsArray && Contains(Type.GetElementType());
        }

        public void Add(Type Type) {
            if (Type == null) return;
            lock (resolveGate) {
                registered[Type.FullName] = Type;
                index = null;
                if (!base.Contains(Type.FullName))
                    Add(Type.FullName);
            }
        }

        internal void Remove(Type Type) {
            if (Type == null) return;
            if (!Contains(Type)) {
                return;
            } else {
                Remove(Type.FullName);
            }
        }
    }
}
