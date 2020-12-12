using Microsoft.Extensions.Configuration;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;

namespace Cactus.Blade.Configuration.ObjectFactory
{
    /// <summary>
    /// Static class that allows the creation of dynamic proxy objects that reload their
    /// backing fields upon configuration change.
    /// </summary>
    public static class ConfigReloadingProxyFactory
    {
        private delegate object CreateProxyDelegate(IConfiguration configuration, DefaultTypes defaultTypes,
            ValueConverters valueConverters, Type declaringType, string memberName, IResolver resolver);

        private const TypeAttributes ProxyClassAttributes =
            TypeAttributes.Public | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit;

        private const MethodAttributes ExplicitInterfaceMethodAttributes = MethodAttributes.Private |
                                                                           MethodAttributes.HideBySig |
                                                                           MethodAttributes.NewSlot |
                                                                           MethodAttributes.Virtual |
                                                                           MethodAttributes.Final;

        private const MethodAttributes TransferStateAttributes = MethodAttributes.FamORAssem |
                                                                 MethodAttributes.HideBySig | MethodAttributes.Virtual |
                                                                 MethodAttributes.Final;

        private const MethodAttributes ConstructorAttributes = MethodAttributes.Public | MethodAttributes.HideBySig |
                                                               MethodAttributes.SpecialName |
                                                               MethodAttributes.RTSpecialName;

        private static readonly MethodInfo DisposeMethod =
            typeof(IDisposable).GetTypeInfo().GetMethod(nameof(IDisposable.Dispose));

        private static readonly MethodInfo DelegateCombineMethod = typeof(Delegate).GetTypeInfo()
            .GetMethod(nameof(Delegate.Combine), new[] { typeof(Delegate), typeof(Delegate) });

        private static readonly MethodInfo DelegateRemoveMethod = typeof(Delegate).GetTypeInfo()
            .GetMethod(nameof(Delegate.Remove), new[] { typeof(Delegate), typeof(Delegate) });

        private static readonly CustomAttributeBuilder DebuggerBrowsableNeverAttribute =
            new CustomAttributeBuilder(
                typeof(DebuggerBrowsableAttribute).GetTypeInfo().GetConstructor(new[] { typeof(DebuggerBrowsableState) }),
                new object[] { DebuggerBrowsableState.Never });

        private static readonly ConcurrentDictionary<Type, CreateProxyDelegate> ProxyFactories =
            new ConcurrentDictionary<Type, CreateProxyDelegate>();

        /// <summary>
        /// Create an object of type <typeparamref name="TInterface"/> based on the specified configuration. The returned
        /// object delegates its functionality to a backing field that is reloaded when the configuration changes.
        /// </summary>
        /// <typeparam name="TInterface">The interface type to create.</typeparam>
        /// <param name="configuration">The configuration to create the object from.</param>
        /// <param name="defaultTypes">
        /// An object that defines the default types to be used when a type is not explicitly specified by a
        /// configuration section.
        /// </param>
        /// <param name="valueConverters">
        /// An object that defines custom converter functions that are used to convert string configuration
        /// values to a target type.
        /// </param>
        /// <returns>
        /// An object of type <typeparamref name="TInterface"/> with values set from the configuration that reloads
        /// itself when the configuration changes.
        /// </returns>
        public static TInterface CreateReloadingProxy<TInterface>(this IConfiguration configuration,
            DefaultTypes defaultTypes, ValueConverters valueConverters)
        {
            return configuration.CreateReloadingProxy<TInterface>(defaultTypes, valueConverters, null);
        }

        /// <summary>
        /// Create an object of type <typeparamref name="TInterface"/> based on the specified configuration. The returned
        /// object delegates its functionality to a backing field that is reloaded when the configuration changes.
        /// </summary>
        /// <typeparam name="TInterface">The interface type to create.</typeparam>
        /// <param name="configuration">The configuration to create the object from.</param>
        /// <param name="defaultTypes">
        /// An object that defines the default types to be used when a type is not explicitly specified by a
        /// configuration section.
        /// </param>
        /// <param name="valueConverters">
        /// An object that defines custom converter functions that are used to convert string configuration
        /// values to a target type.
        /// </param>
        /// <param name="resolver">
        /// An object that can retrieve constructor parameter values that are not found in configuration. This
        /// object is an adapter for dependency injection containers, such as Ninject, Unity, Autofac, or
        /// StructureMap. Consider using the <see cref="Resolver"/> class for this parameter, as it supports
        /// most dependency injection containers.
        /// </param>
        /// <returns>
        /// An object of type <typeparamref name="TInterface"/> with values set from the configuration that reloads
        /// itself when the configuration changes.
        /// </returns>
        public static TInterface CreateReloadingProxy<TInterface>(this IConfiguration configuration,
            DefaultTypes defaultTypes = null, ValueConverters valueConverters = null, IResolver resolver = null)
        {
            return (TInterface)configuration.CreateReloadingProxy(typeof(TInterface), defaultTypes, valueConverters,
                resolver);
        }

        /// <summary>
        /// Create an object of type <paramref name="interfaceType"/> based on the specified configuration. The returned
        /// object delegates its functionality to a backing field that is reloaded when the configuration changes.
        /// </summary>
        /// <param name="configuration">The configuration to create the object from.</param>
        /// <param name="interfaceType">The interface type to create.</param>
        /// <param name="defaultTypes">
        /// An object that defines the default types to be used when a type is not explicitly specified by a
        /// configuration section.
        /// </param>
        /// <param name="valueConverters">
        /// An object that defines custom converter functions that are used to convert string configuration
        /// values to a target type.
        /// </param>
        /// <returns>
        /// An object of type <paramref name="interfaceType"/> with values set from the configuration that reloads
        /// itself when the configuration changes.
        /// </returns>
        public static object CreateReloadingProxy(this IConfiguration configuration, Type interfaceType,
            DefaultTypes defaultTypes, ValueConverters valueConverters)
        {
            return configuration.CreateReloadingProxy(interfaceType, defaultTypes, valueConverters, null);
        }

        /// <summary>
        /// Create an object of type <paramref name="interfaceType"/> based on the specified configuration. The returned
        /// object delegates its functionality to a backing field that is reloaded when the configuration changes.
        /// </summary>
        /// <param name="configuration">The configuration to create the object from.</param>
        /// <param name="interfaceType">The interface type to create.</param>
        /// <param name="defaultTypes">
        /// An object that defines the default types to be used when a type is not explicitly specified by a
        /// configuration section.
        /// </param>
        /// <param name="valueConverters">
        /// An object that defines custom converter functions that are used to convert string configuration
        /// values to a target type.
        /// </param>
        /// <param name="resolver">
        /// An object that can retrieve constructor parameter values that are not found in configuration. This
        /// object is an adapter for dependency injection containers, such as Ninject, Unity, Autofac, or
        /// StructureMap. Consider using the <see cref="Resolver"/> class for this parameter, as it supports
        /// most dependency injection containers.
        /// </param>
        /// <returns>
        /// An object of type <paramref name="interfaceType"/> with values set from the configuration that reloads
        /// itself when the configuration changes.
        /// </returns>
        public static object CreateReloadingProxy(this IConfiguration configuration, Type interfaceType,
            DefaultTypes defaultTypes = null, ValueConverters valueConverters = null, IResolver resolver = null)
        {
            return configuration.CreateReloadingProxy(interfaceType, defaultTypes, valueConverters, null, null,
                resolver ?? Resolver.Empty);
        }

        internal static object CreateReloadingProxy(this IConfiguration configuration, Type interfaceType,
            DefaultTypes defaultTypes, ValueConverters valueConverters, Type declaringType, string memberName,
            IResolver resolver)
        {
            if (configuration.IsNull())
                throw new ArgumentNullException(nameof(configuration));

            if (interfaceType.IsNull())
                throw new ArgumentNullException(nameof(interfaceType));

            if (!interfaceType.GetTypeInfo().IsInterface)
                throw new ArgumentException($"Specified type is not an interface: '{interfaceType.FullName}'.",
                    nameof(interfaceType));

            if (interfaceType == typeof(IEnumerable))
                throw new ArgumentException("The IEnumerable interface is not supported.");

            if (typeof(IEnumerable).GetTypeInfo().IsAssignableFrom(interfaceType))
                throw new ArgumentException(
                    $"Interfaces that inherit from IEnumerable are not supported: '{interfaceType.FullName}'",
                    nameof(interfaceType));

            if (configuration[ConfigurationObjectFactory.TypeKey].IsNotNullOrEmpty()
                && string.Equals(configuration[ConfigurationObjectFactory.ReloadOnChangeKey], "false",
                    StringComparison.OrdinalIgnoreCase))
                return configuration.BuildTypeSpecifiedObject(interfaceType, declaringType, memberName,
                    valueConverters ?? new ValueConverters(), defaultTypes ?? new DefaultTypes(), resolver);

            var createReloadingProxy = ProxyFactories.GetOrAdd(interfaceType, CreateProxyTypeFactoryMethod);

            return createReloadingProxy.Invoke(configuration, defaultTypes, valueConverters, declaringType, memberName,
                resolver);
        }

        private static CreateProxyDelegate CreateProxyTypeFactoryMethod(Type interfaceType)
        {
            var proxyType = CreateProxyType(interfaceType);

            return CompileFactoryMethod(proxyType);
        }

        private static TypeInfo CreateProxyType(Type interfaceType)
        {
            var baseClass = typeof(ConfigReloadingProxy<>).MakeGenericType(interfaceType);

            var baseConstructor =
                baseClass.GetTypeInfo().GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0];
            var baseGetObjectMethod = baseClass.GetTypeInfo().GetProperty("Object")?.GetMethod;
            var baseTransferStateMethod = baseClass.GetTypeInfo()
                .GetMethod("TransferState", BindingFlags.NonPublic | BindingFlags.Instance);

            var eventFields = new Dictionary<EventInfo, FieldBuilder>();
            var implementedMethods = new List<MethodInfo>();

            var proxyTypeBuilder = CreateProxyTypeBuilder(interfaceType, baseClass);

            AddConstructor(proxyTypeBuilder, baseConstructor);

            foreach (var property in interfaceType.GetAllProperties())
                AddProperty(proxyTypeBuilder, property, baseGetObjectMethod, implementedMethods);

            foreach (var evt in interfaceType.GetAllEvents())
                AddEvent(proxyTypeBuilder, evt, baseGetObjectMethod, eventFields, implementedMethods);

            foreach (var method in interfaceType.GetAllMethods().Where(method => !implementedMethods.Contains(method)))
                AddMethod(proxyTypeBuilder, method, baseGetObjectMethod);

            AddTransferStateOverrideMethod(proxyTypeBuilder, interfaceType, eventFields, baseTransferStateMethod);

            return proxyTypeBuilder.CreateTypeInfo();
        }

        private static TypeBuilder CreateProxyTypeBuilder(Type interfaceType, Type baseType)
        {
            var assemblyName = $"<{interfaceType.Name}>a__RockLibDynamicAssembly";
            var name = $"<{interfaceType.Name}>c__RockLibConfigReloadingProxyClass";
            var assemblyBuilder =
                AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");

            return moduleBuilder.DefineType(name, ProxyClassAttributes, baseType, new[] { interfaceType });
        }

        private static void AddConstructor(TypeBuilder proxyTypeBuilder, ConstructorInfo baseConstructor)
        {
            var constructorBuilder = proxyTypeBuilder.DefineConstructor(ConstructorAttributes,
                baseConstructor.CallingConvention,
                new[]
                {
                    typeof(IConfiguration), typeof(DefaultTypes), typeof(ValueConverters), typeof(Type), typeof(string),
                    typeof(IResolver)
                });

            var generator = constructorBuilder.GetILGenerator();

            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ldarg_2);
            generator.Emit(OpCodes.Ldarg_3);
            generator.Emit(OpCodes.Ldarg_S, 4);
            generator.Emit(OpCodes.Ldarg_S, 5);
            generator.Emit(OpCodes.Ldarg_S, 6);
            generator.Emit(OpCodes.Call, baseConstructor);
            generator.Emit(OpCodes.Ret);
        }

        private static void AddProperty(TypeBuilder proxyTypeBuilder, PropertyInfo interfaceProperty,
            MethodInfo baseGetObjectMethod, ICollection<MethodInfo> implementedMethods)
        {
            var parameters = interfaceProperty.GetIndexParameters().Select(p => p.ParameterType).ToArray();
            var propertyBuilder = proxyTypeBuilder.DefineProperty(interfaceProperty.Name, interfaceProperty.Attributes,
                interfaceProperty.PropertyType, parameters);

            if (interfaceProperty.CanRead)
            {
                var getMethodBuilder = proxyTypeBuilder.DefineMethod("get_" + interfaceProperty.Name,
                    ExplicitInterfaceMethodAttributes, interfaceProperty.PropertyType, parameters);

                var generator = getMethodBuilder.GetILGenerator();

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Call, baseGetObjectMethod);

                for (var i = 0; i < parameters.Length; i++)
                    generator.Emit(OpCodes.Ldarg, i + 1);

                generator.Emit(OpCodes.Callvirt, interfaceProperty.GetMethod);
                generator.Emit(OpCodes.Ret);

                propertyBuilder.SetCustomAttribute(DebuggerBrowsableNeverAttribute);
                propertyBuilder.SetGetMethod(getMethodBuilder);
                proxyTypeBuilder.DefineMethodOverride(getMethodBuilder, interfaceProperty.GetMethod);
                implementedMethods.Add(interfaceProperty.GetMethod);
            }

            if (!interfaceProperty.CanWrite) return;

            var setMethodBuilder = proxyTypeBuilder.DefineMethod("set_" + interfaceProperty.Name,
                ExplicitInterfaceMethodAttributes, typeof(void),
                parameters.Concat(new[] { interfaceProperty.PropertyType }).ToArray());

            var ilGenerator = setMethodBuilder.GetILGenerator();

            ilGenerator.Emit(OpCodes.Ldarg_0);
            ilGenerator.Emit(OpCodes.Call, baseGetObjectMethod);
            ilGenerator.Emit(OpCodes.Ldarg_1);

            for (var i = 0; i < parameters.Length; i++)
                ilGenerator.Emit(OpCodes.Ldarg, i + 2);

            ilGenerator.Emit(OpCodes.Callvirt, interfaceProperty.SetMethod);
            ilGenerator.Emit(OpCodes.Ret);

            propertyBuilder.SetSetMethod(setMethodBuilder);
            proxyTypeBuilder.DefineMethodOverride(setMethodBuilder, interfaceProperty.SetMethod);
            implementedMethods.Add(interfaceProperty.SetMethod);
        }

        private static void AddEvent(TypeBuilder proxyTypeBuilder, EventInfo interfaceEvent,
            MethodInfo baseGetObjectMethod, IDictionary<EventInfo, FieldBuilder> eventFields,
            ICollection<MethodInfo> implementedMethods)
        {
            var eventField = proxyTypeBuilder.DefineField(
                $"_{interfaceEvent.DeclaringType.FullName.Replace(".", "_")}_{interfaceEvent.Name}",
                interfaceEvent.EventHandlerType, FieldAttributes.Private);
            eventField.SetCustomAttribute(DebuggerBrowsableNeverAttribute);
            eventFields.Add(interfaceEvent, eventField);

            var parameters = new[] { interfaceEvent.EventHandlerType };

            var eventBuilder = proxyTypeBuilder.DefineEvent(interfaceEvent.Name, interfaceEvent.Attributes,
                interfaceEvent.EventHandlerType);
            var addMethod = proxyTypeBuilder.DefineMethod(interfaceEvent.AddMethod.Name,
                ExplicitInterfaceMethodAttributes, typeof(void), parameters);
            var removeMethod = proxyTypeBuilder.DefineMethod(interfaceEvent.RemoveMethod.Name,
                ExplicitInterfaceMethodAttributes, typeof(void), parameters);

            var generator = addMethod.GetILGenerator();

            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Call, baseGetObjectMethod);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Callvirt, interfaceEvent.AddMethod);

            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, eventField);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Call, DelegateCombineMethod);
            generator.Emit(OpCodes.Castclass, interfaceEvent.EventHandlerType);
            generator.Emit(OpCodes.Stfld, eventField);
            generator.Emit(OpCodes.Ret);

            eventBuilder.SetAddOnMethod(addMethod);
            proxyTypeBuilder.DefineMethodOverride(addMethod, interfaceEvent.AddMethod);
            implementedMethods.Add(interfaceEvent.AddMethod);

            generator = removeMethod.GetILGenerator();

            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Call, baseGetObjectMethod);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Callvirt, interfaceEvent.RemoveMethod);

            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, eventField);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Call, DelegateRemoveMethod);
            generator.Emit(OpCodes.Castclass, interfaceEvent.EventHandlerType);
            generator.Emit(OpCodes.Stfld, eventField);
            generator.Emit(OpCodes.Ret);

            eventBuilder.SetRemoveOnMethod(removeMethod);
            proxyTypeBuilder.DefineMethodOverride(removeMethod, interfaceEvent.RemoveMethod);
            implementedMethods.Add(interfaceEvent.RemoveMethod);
        }

        private static void AddMethod(TypeBuilder proxyTypeBuilder, MethodInfo interfaceMethod,
            MethodInfo baseGetObjectMethod)
        {
            var parameters = interfaceMethod.GetParameters().Select(p => p.ParameterType).ToArray();
            var methodBuilder = proxyTypeBuilder.DefineMethod(interfaceMethod.Name, ExplicitInterfaceMethodAttributes,
                interfaceMethod.ReturnType, parameters);

            var generator = methodBuilder.GetILGenerator();

            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Call, baseGetObjectMethod);

            for (var i = 0; i < parameters.Length; i++)
                generator.Emit(OpCodes.Ldarg, i + 1);

            generator.Emit(OpCodes.Callvirt, interfaceMethod);
            generator.Emit(OpCodes.Ret);

            proxyTypeBuilder.DefineMethodOverride(methodBuilder, interfaceMethod);
        }

        private static void AddTransferStateOverrideMethod(TypeBuilder proxyTypeBuilder, Type interfaceType,
            IReadOnlyDictionary<EventInfo, FieldBuilder> eventFields, MethodInfo baseTransferStateMethod)
        {
            var transferStateMethod = proxyTypeBuilder.DefineMethod("TransferState", TransferStateAttributes,
                typeof(void), new[] { interfaceType, interfaceType });

            var generator = transferStateMethod.GetILGenerator();

            foreach (var eventInfo in interfaceType.GetAllEvents())
            {
                generator.Emit(OpCodes.Ldarg_2);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldfld, eventFields[eventInfo]);
                generator.Emit(OpCodes.Callvirt, eventInfo.AddMethod);
            }

            foreach (var property in interfaceType.GetAllProperties()
                .Where(p => p.CanRead && p.CanWrite && !p.PropertyType.GetTypeInfo().IsValueType))
            {
                var doNotCopyProperty = generator.DefineLabel();

                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Callvirt, property.GetMethod);
                generator.Emit(OpCodes.Brfalse_S, doNotCopyProperty);
                generator.Emit(OpCodes.Ldarg_2);
                generator.Emit(OpCodes.Callvirt, property.GetMethod);
                generator.Emit(OpCodes.Brtrue_S, doNotCopyProperty);
                generator.Emit(OpCodes.Ldarg_2);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Callvirt, property.GetMethod);
                generator.Emit(OpCodes.Callvirt, property.SetMethod);
                generator.MarkLabel(doNotCopyProperty);
            }

            generator.Emit(OpCodes.Ret);

            proxyTypeBuilder.DefineMethodOverride(transferStateMethod, baseTransferStateMethod);
        }

        private static CreateProxyDelegate CompileFactoryMethod(TypeInfo proxyType)
        {
            var constructor = proxyType.GetConstructors()[0];

            var sectionParameter = Expression.Parameter(typeof(IConfiguration), "section");
            var defaultTypesParameter = Expression.Parameter(typeof(DefaultTypes), "defaultTypes");
            var valueConvertersParameter = Expression.Parameter(typeof(ValueConverters), "valueConverters");
            var declaringTypeParameter = Expression.Parameter(typeof(Type), "declaringType");
            var memberNameParameter = Expression.Parameter(typeof(string), "memberName");
            var resolverParameter = Expression.Parameter(typeof(IResolver), "resolver");

            var lambda = Expression.Lambda<CreateProxyDelegate>(
                Expression.New(constructor, sectionParameter, defaultTypesParameter, valueConvertersParameter,
                    declaringTypeParameter, memberNameParameter, resolverParameter),
                sectionParameter, defaultTypesParameter, valueConvertersParameter, declaringTypeParameter,
                memberNameParameter, resolverParameter);

            return lambda.Compile();
        }

        private static IEnumerable<PropertyInfo> GetAllProperties(this Type type)
        {
            return type.GetTypeInfo().GetProperties()
                .Concat(type.GetTypeInfo().GetInterfaces().SelectMany(i => i.GetTypeInfo().GetProperties()));
        }

        private static IEnumerable<MethodInfo> GetAllMethods(this Type type)
        {
            return type.GetTypeInfo().GetMethods()
                .Concat(type.GetTypeInfo().GetInterfaces().SelectMany(i => i.GetTypeInfo().GetMethods()));
        }

        private static IEnumerable<EventInfo> GetAllEvents(this Type type)
        {
            return type.GetTypeInfo().GetEvents()
                .Concat(type.GetTypeInfo().GetInterfaces().SelectMany(i => i.GetTypeInfo().GetEvents()));
        }
    }
}