using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Cactus.Blade.Configuration.ObjectFactory
{
    public class ConstructorOrderInfo : IComparable<ConstructorOrderInfo>
    {
        public ConstructorOrderInfo(ConstructorInfo constructor, IReadOnlyDictionary<string, IConfigurationSection> availableMembers, IResolver resolver)
        {
            Constructor = constructor;
            var parameters = constructor.GetParameters();
            TotalParameters = parameters.Length;
            if (TotalParameters == 0)
            {
                IsInvokableWithoutDefaultParameters = true;
                IsInvokableWithDefaultParameters = true;
                MissingParameterNames = Enumerable.Empty<string>();
                MatchedParameters = 0;
            }
            else
            {
                bool HasAvailableValue(ParameterInfo p)
                {
                    return p.GetNames().Any(availableMembers.ContainsKey) || resolver.CanResolve(p);
                }

                IsInvokableWithoutDefaultParameters = parameters.Count(HasAvailableValue) == TotalParameters;
                IsInvokableWithDefaultParameters = parameters.Count(p => HasAvailableValue(p) || p.HasDefaultValue) == TotalParameters;
                MissingParameterNames = parameters.Where(p => !HasAvailableValue(p) && !p.HasDefaultValue).Select(p => p.Name);
                MatchedParameters = parameters.Count(HasAvailableValue);
            }
        }

        public ConstructorInfo Constructor { get; }
        public bool IsInvokableWithoutDefaultParameters { get; }
        public bool IsInvokableWithDefaultParameters { get; }
        public int MatchedParameters { get; }
        public int TotalParameters { get; }
        public IEnumerable<string> MissingParameterNames { get; }

        public int CompareTo(ConstructorOrderInfo other)
        {
            if (IsInvokableWithoutDefaultParameters && !other.IsInvokableWithoutDefaultParameters) return -1;
            if (!IsInvokableWithoutDefaultParameters && other.IsInvokableWithoutDefaultParameters) return 1;
            if (IsInvokableWithDefaultParameters && !other.IsInvokableWithDefaultParameters) return -1;
            if (!IsInvokableWithDefaultParameters && other.IsInvokableWithDefaultParameters) return 1;
            if (MatchedParameters > other.MatchedParameters) return -1;
            if (MatchedParameters < other.MatchedParameters) return 1;
            if (TotalParameters > other.TotalParameters) return -1;

            return TotalParameters < other.TotalParameters ? 1 : 0;
        }
    }
}
