using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;

namespace JoyPro
{
    // The BinaryFormatter that writes a profile (.pr0file, the history's versions, the server's self-test).
    // A relation without extra binds (Relation.ExtraBinds, experimental multi-bind) is written exactly as before the
    // field existed: the same members in the same order with the same types, so a profile without them keeps its bytes
    // (old vs new engine, checked on a copy of a real profile) and every older JoyPro reads it as always. A relation with
    // extra binds gets the member too; an older JoyPro 2 skips it on reading (version tolerant serialization) and keeps
    // the main bind.
    internal static class ProfileFormatter
    {
        public static BinaryFormatter Writer()
        {
            SurrogateSelector selector = new SurrogateSelector();
            selector.AddSurrogate(typeof(Relation), new StreamingContext(StreamingContextStates.All), new RelationSurrogate());
            return new BinaryFormatter
            {
                Binder = new LegacyJoyProSerializationBinder(),
                SurrogateSelector = selector
            };
        }

        public static BinaryFormatter Reader()
        {
            return new BinaryFormatter { Binder = new LegacyJoyProSerializationBinder() };
        }

        private sealed class RelationSurrogate : ISerializationSurrogate
        {
            private static readonly MemberInfo[] Members = FormatterServices.GetSerializableMembers(typeof(Relation), new StreamingContext(StreamingContextStates.All));

            public void GetObjectData(object obj, SerializationInfo info, StreamingContext context)
            {
                Relation relation = (Relation)obj;
                object[] values = FormatterServices.GetObjectData(relation, Members);
                bool hasExtras = relation.ExtraBinds != null && relation.ExtraBinds.Count > 0;
                for (int i = 0; i < Members.Length; i++)
                {
                    FieldInfo field = (FieldInfo)Members[i];
                    if (!hasExtras && field.Name == nameof(Relation.ExtraBinds))
                        continue;
                    info.AddValue(field.Name, values[i], field.FieldType);
                }
            }

            // Never used: profiles are read without the surrogate (Reader).
            public object SetObjectData(object obj, SerializationInfo info, StreamingContext context, ISurrogateSelector selector)
            {
                throw new NotSupportedException();
            }
        }
    }
}
