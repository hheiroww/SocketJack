using System;

namespace SocketJack.Serialization
{
    public class TypeNotAllowedException : Exception {
        public string Type;
        public bool Blacklisted { get; set; } = false;

        private string _message = "Type cannot be deserialized.";
        public override string Message => _message;

        public TypeNotAllowedException(string Type, bool isBlacklisted = false) {
            Initialize(Type, isBlacklisted);
        }

        public TypeNotAllowedException(Type Type, bool isBlacklisted = false) {
            Initialize(Type.AssemblyQualifiedName, isBlacklisted);
        }

        private void Initialize(string Type, bool isBlacklisted = false) {
            this.Type = Type;
            // Do not resolve an attacker-controlled assembly/type merely to format a rejection.
            Blacklisted = isBlacklisted;
            _message = isBlacklisted ? "Type is blacklisted and cannot be deserialized." : "Type has not been allowlisted and cannot be deserialized.";
        }
    }
}
