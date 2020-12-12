using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Cactus.Blade.Configuration.ObjectFactory
{
    internal static class Members
    {
        public static IEnumerable<Member> Find(Type declaringType, string memberName)
        {
            if (declaringType.IsNull() || memberName.IsNull())
                return Enumerable.Empty<Member>();

            var constructorParameters = FindConstructorParameters(declaringType, memberName).ToList();

            return FindProperties(declaringType, memberName, constructorParameters).Concat(constructorParameters);
        }

        private static IEnumerable<Member> FindConstructorParameters(Type declaringType, string memberName)
        {
            return declaringType.GetTypeInfo()
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Where(p => StringComparer.OrdinalIgnoreCase.Equals(p.Name, memberName))
                .Select(p => new Member(p.Name, p.ParameterType, MemberType.ConstructorParameter));
        }

        private static IEnumerable<Member> FindProperties(Type declaringType, string memberName,
            IReadOnlyCollection<Member> constructorParameters)
        {
            return declaringType.GetTypeInfo().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => StringComparer.OrdinalIgnoreCase.Equals(p.Name, memberName) &&
                            (p.CanWrite || (p.IsReadonlyList() || p.IsReadonlyDictionary()) &&
                                !constructorParameters.Any(c =>
                                    StringComparer.OrdinalIgnoreCase.Equals(c.Name, memberName))))
                .Select(p => new Member(p.Name, p.PropertyType, MemberType.Property));
        }

        public static bool IsReadonlyList(this PropertyInfo p)
        {
            return p.CanRead &&
                   !p.CanWrite && (p.PropertyType.GetTypeInfo().IsGenericType &&
                                   (p.PropertyType.GetGenericTypeDefinition() == typeof(List<>) ||
                                    p.PropertyType.GetGenericTypeDefinition() == typeof(IList<>) ||
                                    p.PropertyType.GetGenericTypeDefinition() == typeof(ICollection<>)) ||
                                   p.PropertyType.IsNonGenericList());
        }

        public static bool IsReadonlyDictionary(this PropertyInfo p)
        {
            return p.CanRead && !p.CanWrite && p.PropertyType.GetTypeInfo().IsGenericType &&
                   (p.PropertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
                    p.PropertyType.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        }
    }
}
