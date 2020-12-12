using Cactus.Blade.Configuration;
using Cactus.Blade.Configuration.ObjectFactory;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System;

namespace Configuration.Sample
{
    internal class Program
    {
        internal static void Main(string[] args)
        {
            Console.WriteLine("Configuration Manager Example Harness");

            try
            {
                var key1 = Config.AppSettings["Key1"];
                var defaultConnectionString = Config.Root.GetConnectionString("Default");

                var fooConfigSection = Config.Root.GetSection("Foo");
                var foo = fooConfigSection.Create<Foo>();

                var foo2ConfigSection = Config.Root.GetSection("Foo");
                var foo2 = foo2ConfigSection.Create<Foo>();

                Console.WriteLine($"key1: {key1}");
                Console.WriteLine($"defaultConnectionString: {defaultConnectionString}");
                Console.WriteLine($"foo: {JsonConvert.SerializeObject(foo)}");
                Console.WriteLine($"foo is same instance as foo2: {foo.ReferenceEquals(foo2)}");

                var notFound = Config.AppSettings["notFound"];
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }
}
