using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace JoyPro.Engine
{
    // A deep copy of an object graph, field by field (also private fields of collections), with shared references and
    // cycles kept as they are. The engine server (EngineServer) hands every command a copy of the profile it read once,
    // instead of reading the 4 MB profile again: the copy of a graph BinaryFormatter made is the graph BinaryFormatter
    // would make again from the same file (same fields, same collection layout; [NonSerialized] fields start empty as
    // after a read). Strings, primitives, enums, comparers and types are shared, they cannot change.
    // CloneAsSerialized copies a graph as a write and read back would give it (after the server saved the profile).
    internal sealed class ObjectGraphCloner
    {
        private delegate object ClonePlan(object source, ObjectGraphCloner cloner);

        private delegate void ElementFix(Array array, int index, ObjectGraphCloner cloner);

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        // Plans per kind of copy: a plain copy shares strings; a copy "as serialized" gives them new instances.
        private sealed class Mode
        {
            public bool AsSerialized;
            public readonly ConcurrentDictionary<Type, ClonePlan> Plans = new ConcurrentDictionary<Type, ClonePlan>();
            public readonly ConcurrentDictionary<Type, bool> Shareable = new ConcurrentDictionary<Type, bool>();
            public readonly ConcurrentDictionary<Type, ElementFix> ElementFixes = new ConcurrentDictionary<Type, ElementFix>();
        }

        private static readonly Mode Plain = new Mode();
        private static readonly Mode Serialized = new Mode { AsSerialized = true };
        private static readonly Func<object, object> Memberwise = (Func<object, object>)Delegate.CreateDelegate(
            typeof(Func<object, object>), typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic));
        private static readonly MethodInfo CloneMethod = typeof(ObjectGraphCloner).GetMethod(nameof(CloneObject), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo RegisterMethod = typeof(ObjectGraphCloner).GetMethod(nameof(Register), BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo SerializedDictionaryMethod = typeof(ObjectGraphCloner).GetMethod(nameof(SerializedDictionary), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly ConcurrentDictionary<Type, ClonePlan> SerializedDictionaryPlans = new ConcurrentDictionary<Type, ClonePlan>();

        private readonly Dictionary<object, object> copies = new Dictionary<object, object>(ReferenceComparer.Instance);
        private readonly Mode mode;

        private ObjectGraphCloner(Mode mode)
        {
            this.mode = mode;
        }

        public static T Clone<T>(T root) where T : class
        {
            return (T)new ObjectGraphCloner(Plain).CloneObject(root);
        }

        // A copy as BinaryFormatter would read it back after writing it:
        // - every Dictionary is built new from its pairs in their order with its table size and version (as Dictionary's
        //   own deserialization does), so one that had entries removed has no free slots in the copy either and later
        //   additions land where they would after a read;
        // - every string except "" is a new instance (one per instance written, as a read gives them), so a string a later
        //   command takes from a literal is a different instance than the profile's and the next save writes the same.
        public static T CloneAsSerialized<T>(T root) where T : class
        {
            return (T)new ObjectGraphCloner(Serialized).CloneObject(root);
        }

        internal object CloneObject(object source)
        {
            if (source == null)
                return null;
            Type type = source.GetType();
            object copy;
            if (mode.AsSerialized && type == typeof(string))
            {
                if (((string)source).Length == 0)
                    return string.Empty;
                if (!copies.TryGetValue(source, out copy))
                    copies[source] = copy = new string(((string)source).ToCharArray());
                return copy;
            }
            if (IsShareable(type, mode))
                return source;
            if (copies.TryGetValue(source, out copy))
                return copy;
            if (mode.AsSerialized && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                return SerializedDictionaryPlans.GetOrAdd(type, t => (ClonePlan)Delegate.CreateDelegate(typeof(ClonePlan), SerializedDictionaryMethod.MakeGenericMethod(t.GetGenericArguments())))(source, this);
            return mode.Plans.GetOrAdd(type, t => BuildPlan(t, mode))(source, this);
        }

        private static object SerializedDictionary<TKey, TValue>(object source, ObjectGraphCloner cloner)
        {
            Dictionary<TKey, TValue> dictionary = (Dictionary<TKey, TValue>)source;
            FieldInfo bucketsField = typeof(Dictionary<TKey, TValue>).GetField("buckets", BindingFlags.Instance | BindingFlags.NonPublic);
            int[] buckets = bucketsField == null ? null : (int[])bucketsField.GetValue(dictionary);
            int size = buckets == null ? 0 : buckets.Length;
            IEqualityComparer<TKey> comparer = (IEqualityComparer<TKey>)cloner.CloneObject(dictionary.Comparer);
            Dictionary<TKey, TValue> copy = size > 0 ? new Dictionary<TKey, TValue>(size, comparer) : new Dictionary<TKey, TValue>(comparer);
            cloner.Register(source, copy);
            foreach (KeyValuePair<TKey, TValue> pair in dictionary)
                copy.Add((TKey)cloner.CloneObject(pair.Key), (TValue)cloner.CloneObject(pair.Value));
            // Deserialization restores the version counter (it is written with the dictionary): so does the copy.
            FieldInfo versionField = typeof(Dictionary<TKey, TValue>).GetField("version", BindingFlags.Instance | BindingFlags.NonPublic);
            if (versionField != null)
                versionField.SetValue(copy, versionField.GetValue(dictionary));
            return copy;
        }

        internal void Register(object source, object copy)
        {
            copies[source] = copy;
        }

        // Types whose instances never change (or hold nothing that could): shared between original and plain copy.
        internal static bool IsShareable(Type type)
        {
            return IsShareable(type, Plain);
        }

        private static bool IsShareable(Type type, Mode mode)
        {
            bool shareable;
            if (mode.Shareable.TryGetValue(type, out shareable))
                return shareable;
            // As serialized: strings and comparers are new instances after a read (StringComparer.OrdinalIgnoreCase of a
            // dictionary read back is not the singleton any more), so the copy gives them new instances too.
            if (type == typeof(string) || (mode.AsSerialized && IsComparer(type)))
            {
                shareable = !mode.AsSerialized;
            }
            else if (type.IsPrimitive || type.IsEnum || type.IsPointer || type == typeof(decimal) || type == typeof(DateTime) ||
                     type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid) || typeof(Type).IsAssignableFrom(type) ||
                     typeof(Delegate).IsAssignableFrom(type) || IsComparer(type))
            {
                shareable = true;
            }
            else if (type.IsValueType)
            {
                // A struct is shareable when every field is (checked before recursing: a struct cannot contain itself).
                mode.Shareable[type] = false;
                shareable = InstanceFields(type).All(f => IsShareable(f.FieldType, mode));
            }
            else
            {
                shareable = false;
            }
            mode.Shareable[type] = shareable;
            return shareable;
        }

        private static bool IsComparer(Type type)
        {
            if (typeof(StringComparer).IsAssignableFrom(type))
                return true;
            // EqualityComparer<T>.Default and friends (GenericEqualityComparer, ObjectEqualityComparer, ...): no state.
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.IsGenericType)
                {
                    Type definition = t.GetGenericTypeDefinition();
                    if (definition == typeof(EqualityComparer<>) || definition == typeof(Comparer<>))
                        return true;
                }
            }
            return false;
        }

        private static List<FieldInfo> InstanceFields(Type type)
        {
            List<FieldInfo> fields = new List<FieldInfo>();
            for (Type t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
                fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            return fields;
        }

        private static ClonePlan BuildPlan(Type type, Mode mode)
        {
            if (type.IsArray)
                return BuildArrayPlan(type, mode);
            if (type.IsValueType)
                return BuildBoxPlan(type, mode);
            return BuildClassPlan(type, mode);
        }

        // An object: MemberwiseClone, registered before its fields are copied (cycles), then every field that holds
        // something changeable gets its copy; [NonSerialized] fields start empty.
        private static ClonePlan BuildClassPlan(Type type, Mode mode)
        {
            ParameterExpression source = Expression.Parameter(typeof(object), "source");
            ParameterExpression cloner = Expression.Parameter(typeof(ObjectGraphCloner), "cloner");
            ParameterExpression typedSource = Expression.Variable(type, "src");
            ParameterExpression copy = Expression.Variable(type, "dst");
            List<Expression> body = new List<Expression>
            {
                Expression.Assign(typedSource, Expression.Convert(source, type)),
                Expression.Assign(copy, Expression.Convert(Expression.Invoke(Expression.Constant(Memberwise), source), type)),
                Expression.Call(cloner, RegisterMethod, source, copy)
            };
            List<FieldInfo> slow = new List<FieldInfo>();
            List<FieldInfo> cleared = new List<FieldInfo>();
            foreach (FieldInfo field in InstanceFields(type))
            {
                bool notSerialized = field.IsNotSerialized;
                if (!notSerialized && IsShareable(field.FieldType, mode))
                    continue;
                if (field.IsInitOnly)
                {
                    if (notSerialized)
                        cleared.Add(field);
                    else
                        slow.Add(field);
                    continue;
                }
                Expression value = notSerialized
                    ? (Expression)Expression.Default(field.FieldType)
                    : Expression.Convert(Expression.Call(cloner, CloneMethod, Expression.Convert(Expression.Field(typedSource, field), typeof(object))), field.FieldType);
                body.Add(Expression.Assign(Expression.Field(copy, field), value));
            }
            body.Add(Expression.Convert(copy, typeof(object)));
            Func<object, ObjectGraphCloner, object> compiled = Expression.Lambda<Func<object, ObjectGraphCloner, object>>(
                Expression.Block(new[] { typedSource, copy }, body), source, cloner).Compile();
            if (slow.Count == 0 && cleared.Count == 0)
                return (s, c) => compiled(s, c);
            return (s, c) =>
            {
                object result = compiled(s, c);
                foreach (FieldInfo field in slow)
                    field.SetValue(result, c.CloneObject(field.GetValue(s)));
                foreach (FieldInfo field in cleared)
                    field.SetValue(result, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
                return result;
            };
        }

        // A boxed struct that holds something changeable (rare): the box is copied, its fields get their copies.
        private static ClonePlan BuildBoxPlan(Type type, Mode mode)
        {
            List<FieldInfo> fields = InstanceFields(type).Where(f => f.IsNotSerialized || !IsShareable(f.FieldType, mode)).ToList();
            return (source, cloner) =>
            {
                object box = Memberwise(source);
                cloner.Register(source, box);
                foreach (FieldInfo field in fields)
                {
                    object value = field.IsNotSerialized
                        ? (field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null)
                        : cloner.CloneObject(field.GetValue(source));
                    field.SetValue(box, value);
                }
                return box;
            };
        }

        private static ClonePlan BuildArrayPlan(Type type, Mode mode)
        {
            Type element = type.GetElementType();
            bool vector = type == element.MakeArrayType();
            if (IsShareable(element, mode))
            {
                return (source, cloner) =>
                {
                    object copy = ((Array)source).Clone();
                    cloner.Register(source, copy);
                    return copy;
                };
            }
            if (!vector)
            {
                // Multi-dimensional or non-zero-based arrays (none in a profile): element by element.
                return (source, cloner) =>
                {
                    Array array = (Array)source;
                    Array copy = (Array)array.Clone();
                    cloner.Register(source, copy);
                    int[] index = new int[array.Rank];
                    foreach (object unused in EnumerateIndices(array, index, 0))
                        copy.SetValue(cloner.CloneObject(array.GetValue(index)), index);
                    return copy;
                };
            }
            if (!element.IsValueType)
            {
                return (source, cloner) =>
                {
                    object[] array = (object[])source;
                    Array copy = Array.CreateInstance(element, array.Length);
                    cloner.Register(source, copy);
                    object[] target = (object[])copy;
                    for (int i = 0; i < array.Length; i++)
                        target[i] = cloner.CloneObject(array[i]);
                    return copy;
                };
            }
            // Structs with references (Dictionary entries of a class value): copy the array, then each element's fields.
            ElementFix fix = mode.ElementFixes.GetOrAdd(element, e => BuildElementFix(e, mode));
            return (source, cloner) =>
            {
                Array copy = (Array)((Array)source).Clone();
                cloner.Register(source, copy);
                for (int i = 0; i < copy.Length; i++)
                    fix(copy, i, cloner);
                return copy;
            };
        }

        private static IEnumerable<object> EnumerateIndices(Array array, int[] index, int dimension)
        {
            for (int i = array.GetLowerBound(dimension); i <= array.GetUpperBound(dimension); i++)
            {
                index[dimension] = i;
                if (dimension == array.Rank - 1)
                {
                    yield return null;
                }
                else
                {
                    foreach (object unused in EnumerateIndices(array, index, dimension + 1))
                        yield return null;
                }
            }
        }

        // In place on the copied array: element.field = Clone(element.field) for every changeable field.
        private static ElementFix BuildElementFix(Type element, Mode mode)
        {
            ParameterExpression array = Expression.Parameter(typeof(Array), "array");
            ParameterExpression index = Expression.Parameter(typeof(int), "index");
            ParameterExpression cloner = Expression.Parameter(typeof(ObjectGraphCloner), "cloner");
            ParameterExpression typed = Expression.Variable(element.MakeArrayType(), "typed");
            List<Expression> body = new List<Expression> { Expression.Assign(typed, Expression.Convert(array, element.MakeArrayType())) };
            List<FieldInfo> slow = new List<FieldInfo>();
            foreach (FieldInfo field in InstanceFields(element))
            {
                if (!field.IsNotSerialized && IsShareable(field.FieldType, mode))
                    continue;
                if (field.IsInitOnly)
                {
                    slow.Add(field);
                    continue;
                }
                Expression slot = Expression.Field(Expression.ArrayAccess(typed, index), field);
                Expression value = field.IsNotSerialized
                    ? (Expression)Expression.Default(field.FieldType)
                    : Expression.Convert(Expression.Call(cloner, CloneMethod, Expression.Convert(slot, typeof(object))), field.FieldType);
                body.Add(Expression.Assign(slot, value));
            }
            body.Add(Expression.Empty());
            ElementFix compiled = Expression.Lambda<ElementFix>(Expression.Block(new[] { typed }, body), array, index, cloner).Compile();
            if (slow.Count == 0)
                return compiled;
            return (a, i, c) =>
            {
                compiled(a, i, c);
                object box = a.GetValue(i);
                foreach (FieldInfo field in slow)
                    field.SetValue(box, field.IsNotSerialized ? null : c.CloneObject(field.GetValue(box)));
                a.SetValue(box, i);
            };
        }
    }
}
