using System;

namespace Cactus.Blade.Configuration.ObjectFactory
{
    public class Member
    {
        public Member(string name, Type type, MemberType memberType)
        {
            Name = name;
            Type = type;
            MemberType = memberType;
        }

        public string Name { get; }
        public Type Type { get; }
        public MemberType MemberType { get; }

        public override string ToString()
        {
            return $"{(MemberType == MemberType.Property ? "Property" : "Constructor parameter")}: {Type} {Name}";
        }
    }
}