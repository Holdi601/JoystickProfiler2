using System;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace JoyPro
{
    internal sealed class LegacyJoyProSerializationBinder : SerializationBinder
    {
        private const string LegacyAssemblyName = "JoyPro, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
        private static readonly string CurrentAssemblyName = typeof(Pr0file).Assembly.FullName;
        private static readonly Regex LegacyAssemblyPattern = new Regex(
            @"JoyPro,\s*Version=[^,\]]+,\s*Culture=[^,\]]+,\s*PublicKeyToken=[^\],]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public override void BindToName(Type serializedType, out string assemblyName, out string typeName)
        {
            typeName = serializedType == null ? null : serializedType.FullName;
            if (serializedType == null)
            {
                assemblyName = CurrentAssemblyName;
                return;
            }

            if (typeName != null)
            {
                typeName = typeName.Replace(CurrentAssemblyName, LegacyAssemblyName);
            }

            assemblyName = serializedType.Namespace == "JoyPro"
                ? LegacyAssemblyName
                : serializedType.Assembly.FullName;
        }

        public override Type BindToType(string assemblyName, string typeName)
        {
            string correctedAssembly = NormalizeAssemblyName(assemblyName);
            string correctedType = ReplaceLegacyAssembly(typeName);
            string candidate = correctedType + ", " + correctedAssembly;

            Type resolved = Type.GetType(candidate, false);
            if (resolved != null)
                return resolved;

            resolved = Type.GetType(candidate, true);
            if (resolved != null)
                return resolved;

            throw new SerializationException("Unable to resolve serialized type '" + typeName + "' from assembly '" + assemblyName + "'.");
        }

        private static string NormalizeAssemblyName(string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName))
                return CurrentAssemblyName;
            if (assemblyName.StartsWith("JoyPro,", StringComparison.OrdinalIgnoreCase))
                return CurrentAssemblyName;
            return assemblyName;
        }

        private static string ReplaceLegacyAssembly(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return typeName;
            return LegacyAssemblyPattern.Replace(typeName, CurrentAssemblyName);
        }
    }
}
