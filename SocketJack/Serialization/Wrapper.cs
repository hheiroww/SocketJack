using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.Net;
using SocketJack.Serialization.Json;
using System.Collections.Concurrent;

namespace SocketJack.Serialization {

#nullable enable
    public class Wrapper {
        private static readonly ConcurrentDictionary<string, Type?> ResolvedTypeCache = new(StringComparer.Ordinal);

        internal static Type? ResolveTypeCached(string typeName) {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;
            if (ResolvedTypeCache.TryGetValue(typeName, out var t))
                return t;
            t = System.Type.GetType(typeName);
            ResolvedTypeCache[typeName] = t;
            return t;
        }
#nullable restore

        public string Type { get; set; }
        public object value { get; set; }
        protected static bool hasRecieved = false;

        private readonly ConcurrentDictionary<string, Type> TypeCache = new();

        private static string RemoveNamespace(string fullTypeName) {
            if (string.IsNullOrEmpty(fullTypeName))
                return fullTypeName;

            int lastDotIndex = fullTypeName.LastIndexOf('.');
            if (lastDotIndex >= 0 && lastDotIndex < fullTypeName.Length - 1) {
                fullTypeName = fullTypeName.Remove(0, lastDotIndex + 1);
            }
            return fullTypeName;
        }

        private static string GetNamespace(string fullTypeName) {
            if (string.IsNullOrEmpty(fullTypeName))
                return fullTypeName;

            int lastDotIndex = fullTypeName.LastIndexOf('.');
            if (lastDotIndex > 0) {
                return fullTypeName.Substring(0, lastDotIndex + 1);
            }
            return string.Empty;
        }

        private static string RemoveAssembly(string fullTypeName) {
            if (string.IsNullOrEmpty(fullTypeName))
                return fullTypeName;
            int commaIndex = fullTypeName.IndexOf(',');
            if (commaIndex > 0) {
                return fullTypeName.Substring(0, commaIndex);
            }
            return fullTypeName;
        }

        public Type GetValueType() {

            
            if (!TypeCache.ContainsKey(Type))
                TypeCache.AddOrUpdate(Type, GetValueType(Type));
            return TypeCache[Type];
        }

        private static Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();

        public static Type GetValueType(string FullTypeName) {
            var typeName = RemoveAssembly(FullTypeName);//RemoveNamespace(FullTypeName);
            int backtickIndex = FullTypeName.IndexOf('`');

            if (FullTypeName.EndsWith("[]")) {
                //var ns = GetNamespace(FullTypeName);
                //var elementTypeName = typeName.Remove(typeName.Length - 2);
                var elementTypeName = FullTypeName.Substring(0, FullTypeName.Length - 2);
                var elementType = GetValueType(elementTypeName);
                if (elementType != null)
                    return elementType.MakeArrayType();
            } else if(backtickIndex > 0) {
                string genericTypeName = FullTypeName.Substring(0, backtickIndex);
                int arity;
                if (int.TryParse(FullTypeName.Substring(backtickIndex + 1), out arity)) {
                    var genericType = loadedAssemblies
                        .SelectMany(asm => asm.GetTypes())
                        .FirstOrDefault(t => t.IsGenericTypeDefinition && $"{t.FullName}" == genericTypeName && t.GetGenericArguments().Length == arity);
                    if (genericType != null)
                        return genericType;
                }
            } else {
                var exTypes = Assembly.GetCallingAssembly().DefinedTypes;
                Type exFoundType = exTypes.FirstOrDefault(t => $"{RemoveAssembly(t.FullName)}" == typeName);
                if(exFoundType == null) {
                    Type foundType = null;
                    foreach (var assembly in loadedAssemblies) {
                        if (assembly.IsDynamic) continue;
                        try {
                            foundType = assembly.GetType(typeName);
                        } catch (ReflectionTypeLoadException) {
                            continue;
                        }
                        if (foundType != null)
                            break;
                    }
                    if (foundType != null)
                        return foundType;
                } else {
                    return exFoundType;
                }
               
            }

            return default;
        }

        public static BindingFlags ReflectionFlags { get; set; } = BindingFlags.Instance | BindingFlags.Public;

        public Wrapper() {
        }

        public static string GetTypeName(object Obj) {
            return GetTypeName(Obj.GetType());
        }

        public static string GetTypeName(Type type) {
            return $"{type.FullName}";
        }

        public Wrapper(object Obj, ISocket sender) {
            if (Obj == null) return;
            Type T = Obj.GetType();
            Type = GetTypeName(T);
            if (IsTypeAllowed(Obj, sender)) {
                if (T.IsArray || T.IsValueType || T == typeof(string)) {
                    value = Obj;
                } else {
                    lock (Obj) {
                        object stripped = Strip(T, Obj, sender);
                        value = stripped;
                    }
                }
            } else {
                return;
            }
        }

        private object Strip(Type T, object obj, ISocket sender) {
            var instance = FormatterServices.GetSafeUninitializedObject(T);
            GetPropertyReferences(T).ForEach(r => MethodExtensions.TryInvoke(() => SetProperty(obj, ref instance, r, sender)));
            return instance;
        }

        private TypeNotAllowedException CreateTypeException(ref ISocket sender, string Type, bool isBlacklisted = false) {
            var exception = new TypeNotAllowedException(Type, isBlacklisted);
            sender.InvokeOnError(sender.Connection, exception);
            return exception;
        }

        private TypeNotAllowedException CreateTypeException(ref ISocket sender, Type Type, bool isBlacklisted = false) {
            return CreateTypeException(ref sender, GetTypeName(Type), isBlacklisted);
        }

        private bool IsTypeAllowed(Type Type, ISocket sender) {
            if (sender.Options.Blacklist.Contains(Type)) {
                throw CreateTypeException(ref sender, Type, true);
            } else if(!sender.Options.Whitelist.Contains(Type) ){
                throw CreateTypeException(ref sender, Type);
            }
            return true;
        }

        private bool IsTypeAllowed(object Obj, ISocket sender) {
            Type T = Obj.GetType();
            if (T == typeof(PeerRedirect)) {
                PeerRedirect peerRedirect = (PeerRedirect)Obj;
                Type redirectType = ResolveTypeCached(peerRedirect.Type);
                if (redirectType == null) {
                    Exception exception = new Exception("Type '" + peerRedirect.Type + "' is not found in any referenced assembly.");
                    throw exception;
                }
                return IsTypeAllowed(redirectType, sender);
            } else {
                return IsTypeAllowed(T, sender);
            }
        }

        public object Unwrap(Type Type, ISocket sender) {
            bool isAllowed = IsTypeAllowed(Type, sender);
            if (Type == null) {
                Exception exception = new Exception("Type 'null' is not found in any referenced assembly.");
                throw exception;
            } else if (!isAllowed) {
                this.Type = Type.Name;
                return null;
            }
            if (Type.IsValueType || Type.IsArray || Type == typeof(string)) {
                return sender.Options.Serializer.GetValue(value, Type, true);
            } else {
                var instance = FormatterServices.GetSafeUninitializedObject(Type);
                var references = GetPropertyReferences(Type);
                int index = 0;

                for (int i = 0, loopTo = references.Count - 1; i <= loopTo; i++) {
                    index = i;
                    var reference = references[i];
                    try {
                        bool isAllowedProperty = IsTypeAllowed(reference.Info.PropertyType, sender);
                        if (isAllowedProperty)
                            SetProperty(ref instance, reference, sender);
                    } catch (Exception ex) {
                        var r = references[index];
                        if (ex.Message.Contains("Object of type 'System.Int64' cannot be converted to type 'System.Int32'.")) {
                            throw new Int32NotSupportedException(instance.GetType().Name + "." + r.Info.Name);
                        } else {
                            string errorMessage = "Deserialization Error @ " + r.Index + " (" + instance.GetType().Name + "." + r.Info.Name + ")" + Environment.NewLine +
                                                  (ex.Message == string.Empty ? string.Empty : "     " + ex.Message) +
                                                  (ex.StackTrace == string.Empty ? string.Empty : Environment.NewLine + ex.StackTrace) + Environment.NewLine;
                            Exception exception = new Exception(errorMessage, ex);
                            sender.InvokeOnError(sender.Connection, exception);
                        }
                    }
                }
                return instance;
            }
        }

        public object Unwrap(ISocket sender) {
            Type Type = GetValueType();
            bool isAllowed = IsTypeAllowed(Type, sender);
            if (Type == null) {
                Exception exception = new Exception("Type 'null' is not found in any referenced assembly.");
                throw exception;
            } else if(!isAllowed) {
                this.Type = Type.Name;
                return null;
            }
            if (Type.IsValueType || Type.IsArray || Type == typeof(string)) {
                return sender.Options.Serializer.GetValue(value, Type, true);
            } else {
                var instance = FormatterServices.GetSafeUninitializedObject(Type);
                var references = GetPropertyReferences(Type);
                int index = 0;

                for (int i = 0, loopTo = references.Count - 1; i <= loopTo; i++) {
                    index = i;
                    var reference = references[i];
                    try {
                        bool isAllowedProperty = IsTypeAllowed(reference.Info.PropertyType, sender);    
                        if (isAllowedProperty)
                            SetProperty(ref instance, reference, sender);
                    } catch (Exception ex) {
                        var r = references[index];
                        if (ex.Message.Contains("Object of type 'System.Int64' cannot be converted to type 'System.Int32'.")) {
                            throw new Int32NotSupportedException(instance.GetType().Name + "." + r.Info.Name);
                        } else {
                            string errorMessage = "Deserialization Error @ " + r.Index + " (" + instance.GetType().Name + "." + r.Info.Name + ")" + Environment.NewLine +
                                                  (ex.Message == string.Empty ? string.Empty : "     " + ex.Message) +
                                                  (ex.StackTrace == string.Empty ? string.Empty : Environment.NewLine + ex.StackTrace) + Environment.NewLine;
                            Exception exception = new Exception(errorMessage, ex);
                            sender.InvokeOnError(sender.Connection, exception);
                        }
                    }
                }
                return instance;
            }
        }

        public object GetPropertyValue(ISocket sender, PropertyReference Reference) {
            return sender.Options.Serializer.GetPropertyValue(new PropertyValueArgs(Reference.Info.Name, value, Reference));
        }

        //public object GetPropertyValueFromRedirect(ISocket sender, PropertyReference Reference, ref PeerRedirect Instance) {
        //    Type T = System.Type.GetType(Instance.Type);
        //    if (T == null) return null;
        //    return sender.Options.Serializer.GetPropertyValue(new PropertyValueArgs(Reference.Info.Name, Value, Reference));
        //}

        public void SetProperty(ref object Instance, PropertyReference Reference, ISocket sender) {
            if (Reference.Info.CanWrite) {
                Type iType = Instance.GetType();
                object v = null;
                //if (iType == typeof(PeerRedirect)) {
                //    var r = (PeerRedirect)Instance;
                //    v = GetPropertyValueFromRedirect(sender, Reference, ref r);
                //} else {
                if (iType == typeof(Wrapper)) {
                    v = ((Wrapper)GetPropertyValue(sender, Reference)).Unwrap(sender);
                } else {
                    v = GetPropertyValue(sender, Reference);
                }
                //}

                if (v == null) return;
                string PropertyTypeName = Reference.Info.PropertyType.FullName;
                Type vType = v.GetType();
                if (iType == typeof(PeerRedirect) && vType == typeof(Wrapper)) {
                    PeerRedirect redirect = (PeerRedirect)Instance;
                    Type redirectType = ResolveTypeCached(redirect.Type);
                    v = redirectType != null ? ((Wrapper)v).Unwrap(redirectType, sender) : ((Wrapper)v).Unwrap(sender);
                } else if(vType == typeof(Wrapper)) {
                    Wrapper wrapper = (Wrapper)v;
                    if (wrapper.Type == null || wrapper.value == null) {
                        return;
                    }
                    v = wrapper.Unwrap(sender);
                }
                if (v.GetType() == typeof(Wrapper)) {
                   
                } else if (Reference.Info.PropertyType.IsEnum) {
                    var enumType = Reference.Info.Module.Assembly.GetType(PropertyTypeName);
                    v = Enum.ToObject(enumType, v);
                    //} else if() {

                }
                    Reference.Info.SetValue(Instance, v, (object[])null);
            }
        }

        public void SetProperty(ref object Instance,PeerRedirect redirect, PropertyReference Reference, ISocket sender) {
            if (Reference.Info.CanWrite) {
                Type iType = Instance.GetType();
                object v = null;
                //if (iType == typeof(PeerRedirect)) {
                //    var r = (PeerRedirect)Instance;
                //    v = GetPropertyValueFromRedirect(sender, Reference, ref r);
                //} else {
                if (iType == typeof(Wrapper)) {
                    v = ((Wrapper)GetPropertyValue(sender, Reference)).Unwrap(sender);
                } else {
                    v = GetPropertyValue(sender, Reference);
                }
                //}

                if (v == null) return;
                string PropertyTypeName = Reference.Info.PropertyType.FullName;
                Type vType = v.GetType();
                if (iType == typeof(PeerRedirect) && vType == typeof(Wrapper)) {
                    Type redirectType = System.Type.GetType(redirect.Type);
                    v = Unwrap(redirectType, sender);
                } else if (vType == typeof(Wrapper)) {
                    Wrapper wrapper = (Wrapper)v;
                    if (wrapper.Type == null || wrapper.value == null) {
                        return;
                    }
                    v = wrapper.Unwrap(sender);
                }
                if (v.GetType() == typeof(Wrapper)) {

                } else if (Reference.Info.PropertyType.IsEnum) {
                    var enumType = Reference.Info.Module.Assembly.GetType(PropertyTypeName);
                    v = Enum.ToObject(enumType, v);
                    //} else if() {

                }
                Reference.Info.SetValue(Instance, v, (object[])null);
            }
        }

        public void SetProperty(object ValueInstance, ref object Instance, PropertyReference Reference, ISocket sender) {
            if (Reference.Info.CanWrite) {
                var v = Reference.GetValue(ValueInstance); // GetValue(sender, ValueInstance, Reference)
                var isJson = sender.Options.Serializer.GetType() == typeof(JsonSerializer);
                string PropertyTypeName = Reference.Info.PropertyType.FullName;
                string valueTypeName = ValueInstance.GetType().Name;

                if (Reference.Info.PropertyType == typeof(object)) {
                    if (sender.Options.Serializer is BinarySerializer && v != null) {
                        v = new Wrapper(v, sender);
                    } else if (isJson && v != null) {
                        JsonSerializer serializer = (JsonSerializer)sender.Options.Serializer;
                        if (!serializer.HasConverter(v.GetType())) {
                            var wrappedValue = new Wrapper(v, sender);
                            v = wrappedValue;
                        }
                    }
                } else if (Reference.Info.PropertyType.IsEnum) {
                    var enumType = Reference.Info.Module.Assembly.GetType(PropertyTypeName);
                    v = Enum.ToObject(enumType, v);
                }

                Reference.Info.SetValue(Instance, v, (object[])null);
            }
        }

        public static string GetVariableName<T>(Expression<Func<T>> expr) {
            MemberExpression body = (MemberExpression)expr.Body;
            return body.Member.Name;
        }

        #region GetEnumReferences

        public static List<EnumReference> GetEnumReferences(Type EnumType, BindingFlags ReflectionFlags) {
            string[] enums = (string[])Enum.GetValues(typeof(Type));
            var pRefs = new List<EnumReference>();
            for (int index = 0, loopTo = enums.Length - 1; index <= loopTo; index++)
                pRefs.Add(new EnumReference(enums[index], index));
            return pRefs;
        }

        public static List<EnumReference> GetEnumReferences(Type EnumType) {
            return GetEnumReferences(EnumType, ReflectionFlags);
        }

        #endregion

        #region GetFieldReferences

        public static List<FieldReference> GetFieldReferences(object target) {
            return GetFieldReferences(target.GetType(), ReflectionFlags);
        }

        public static List<FieldReference> GetFieldReferences(Type Type) {
            return GetFieldReferences(Type, ReflectionFlags);
        }

        public static List<FieldReference> GetFieldReferences(object target, BindingFlags ReflectionFlags) {
            return GetFieldReferences(target.GetType(), ReflectionFlags);
        }

        public static List<FieldReference> GetFieldReferences(Type Type, BindingFlags ReflectionFlags) {
            FieldInfo[] objProperties = Type.GetFields(ReflectionFlags);
            var References = new List<FieldReference>();

            for (int Index = 0, loopTo = objProperties.Length - 1; Index <= loopTo; Index++)
                References.Add(new FieldReference(objProperties[Index], Index));

            return References;
        }

        #endregion

        #region GetPropertyReferences

        public static PropertyReference GetPropertyReference(Type type, string PropertyName) {
            var properties = GetPropertyReferences(type, ReflectionFlags);
            return properties.Where((pr) => string.Equals(pr.Info.Name, PropertyName, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        }

        public static PropertyReference GetPropertyReference(object target, string PropertyName) {
            return GetPropertyReference(target.GetType(), PropertyName);
        }

        public static List<PropertyReference> GetPropertyReferences(object target) {
            return GetPropertyReferences(target.GetType(), ReflectionFlags);
        }

        public static List<PropertyReference> GetPropertyReferences(Type Type) {
            return GetPropertyReferences(Type, ReflectionFlags);
        }

        public static List<PropertyReference> GetPropertyReferences(Type Type, BindingFlags ReflectionFlags) {
            PropertyInfo[] objProperties = Type.GetProperties(ReflectionFlags);
            var References = new List<PropertyReference>();
            try {
                var instance = FormatterServices.GetSafeUninitializedObject(Type);
                for (int Index = 0, loopTo = objProperties.Length - 1; Index <= loopTo; Index++) {
                    var p = objProperties[Index];
                    References.Add(new PropertyReference(p, Index));
                }
            } catch (Exception) {
            }
            return References;
        }

        public static List<PropertyReference> GetPropertyReferences(object Instance, BindingFlags ReflectionFlags, bool CatchAllProperties = false) {
            return GetPropertyReferences(Instance.GetType(), ReflectionFlags, CatchAllProperties);
        }

        #endregion

        #region GetMethodReferences

        public static List<string> ExcludedMethods = new List<string>() { "GETTYPE", "TOSTRING", "GETHASHCODE", "EQUALS", "GETDBCONTEXT" };

        public static List<MethodReference> GetMethodReferences(object target) {
            return GetMethodReferences(target.GetType(), ReflectionFlags);
        }

        public static List<MethodReference> GetMethodReferences(object target, bool includeFunctions) {
            return GetMethodReferences(target.GetType(), ReflectionFlags, includeFunctions);
        }

        public static List<MethodReference> GetMethodReferences(object target, BindingFlags ReflectionFlags) {
            return GetMethodReferences(target.GetType(), ReflectionFlags, false);
        }

        public static List<MethodReference> GetMethodReferences(object target, BindingFlags ReflectionFlags, bool includeFunctions) {
            return GetMethodReferences(target.GetType(), ReflectionFlags, includeFunctions, false);
        }

        public static List<MethodReference> GetMethodReferences(Type Type) {
            return GetMethodReferences(Type, ReflectionFlags);
        }

        public static List<MethodReference> GetMethodReferences(Type Type, bool includeFunctions) {
            return GetMethodReferences(Type, ReflectionFlags, includeFunctions);
        }

        public static List<MethodReference> GetMethodReferences(Type Type, BindingFlags ReflectionFlags, bool includeFunctions, bool CatchAllMethods = false) {
            MethodInfo[] objMethods = Type.GetMethods(ReflectionFlags | ~BindingFlags.SetProperty | ~BindingFlags.GetProperty);
            var instance = FormatterServices.GetSafeUninitializedObject(Type);
            int index = 0;
            var References = new List<MethodReference>();
            if (!includeFunctions) {
                foreach (MethodInfo m in objMethods) {
                    if (m.ReturnType is null) {
                        if (!CatchAllMethods && ExcludedMethods.Contains(m.Name.ToUpper()))
                            continue;
                        References.Add(new MethodReference(m, index));
                        index += 1;
                    }
                }
            } else {
                foreach (MethodInfo m in objMethods) {
                    if (!CatchAllMethods && ExcludedMethods.Contains(m.Name.ToUpper()))
                        continue;
                    References.Add(new MethodReference(m, index));
                    index += 1;
                }
            }

            return References;
        }

        #endregion

        #region GetEventReferences
        public static List<EventReference> GetEventReferences(Type Type) {
            EventInfo[] objProperties = Type.GetEvents(ReflectionFlags);
            var instance = FormatterServices.GetSafeUninitializedObject(Type);
            int index = 0;
            var References = new List<EventReference>();
            foreach (EventInfo p in objProperties) {
                References.Add(new EventReference(p, index));
                index += 1;
            }
            return References;
        }
        #endregion

    }

    public class PropertyValueArgs {

        public PropertyValueArgs(string Name, object Value, PropertyReference Reference) {
            this.Name = Name;
            this.Value = Value;
            this.Reference = Reference;
        }

        public string Name { get; set; }
        public object Value { get; set; }
        public PropertyReference Reference { get; set; }

    }

    public abstract class BaseReference<T> {

        public BaseReference(T Info, int Index) {
            this.Info = Info;
            this.Index = Index;
        }

        public T Info { get; set; }
        public int Index { get; set; }

    }

    public class EventReference : BaseReference<EventInfo> {

        public EventReference(EventInfo EventInfo, int Index) : base(EventInfo, Index) {
        }

        public override string ToString() {
            return Info.Name + "(Event)";
        }
    }

    public class PropertyReference : BaseReference<PropertyInfo> {

        public PropertyReference(PropertyInfo PropertyInfo, int Index) : base(PropertyInfo, Index) {
        }

        public object GetValue(object Instance) {
            return Info.GetValue(Instance);
        }

        public override string ToString() {
            return Info.Name;
        }
    }

    public class MethodReference : BaseReference<MethodInfo> {

        public MethodReference(MethodInfo MethodInfo, int Index) : base(MethodInfo, Index) {
        }

        public override string ToString() {
            return Info.Name + "()";
        }
    }

    public class FieldReference : BaseReference<FieldInfo> {

        public FieldReference(FieldInfo FieldInfo, int Index) : base(FieldInfo, Index) {
        }

        public override string ToString() {
            return Info.Name;
        }
    }

    public class EnumReference : BaseReference<string> {

        public EnumReference(string EnumName, int Index) : base(EnumName, Index) {
        }
    }

}