using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Cactus.Blade.Configuration.ObjectFactory
{
    /// <summary>
    /// The base class for reloading proxy classes.
    /// </summary>
    [DebuggerDisplay("{" + nameof(Object) + "}")]
    public abstract class ConfigReloadingProxy<TInterface> : IDisposable
    {
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly HashAlgorithm _hashAlgorithm = MD5.Create();

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly IConfiguration _section;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly DefaultTypes _defaultTypes;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly ValueConverters _valueConverters;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly Type _declaringType;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly string _memberName;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly IResolver _resolver;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string _hash;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigReloadingProxy{TInterface}"/> class.
        /// </summary>
        /// <param name="section">The configuration section that defines the object that this class creates.</param>
        /// <param name="defaultTypes">
        /// An object that defines the default types to be used when a type is not explicitly specified by a
        /// configuration section.
        /// </param>
        /// <param name="valueConverters">
        /// An object that defines custom converter functions that are used to convert string configuration
        /// values to a target type.
        /// </param>
        /// <param name="declaringType">If present the declaring type of the member that this instance is a value of.</param>
        /// <param name="memberName">If present, the name of the member that this instance is the value of.</param>
        /// <param name="resolver">
        /// An object that can retrieve constructor parameter values that are not found in configuration. This
        /// object is an adapter for dependency injection containers, such as Ninject, Unity, Autofac, or
        /// StructureMap. Consider using the <see cref="Resolver"/> class for this parameter, as it supports
        /// most dependency injection containers.
        /// </param>
        protected ConfigReloadingProxy(IConfiguration section, DefaultTypes defaultTypes,
            ValueConverters valueConverters, Type declaringType, string memberName, IResolver resolver)
        {
            if (typeof(TInterface) == typeof(IEnumerable<>))
                throw new InvalidOperationException("The IEnumerable interface is not supported.");

            if (typeof(IEnumerable).GetTypeInfo().IsAssignableFrom(typeof(TInterface)))
                throw new InvalidOperationException(
                    $"Interfaces that inherit from IEnumerable are not supported: '{typeof(TInterface).FullName}'");

            _section = section ?? throw new ArgumentNullException(nameof(section));
            _defaultTypes = defaultTypes ?? ConfigurationObjectFactory.EmptyDefaultTypes;
            _valueConverters = valueConverters ?? ConfigurationObjectFactory.EmptyValueConverters;
            _declaringType = declaringType;
            _memberName = memberName;
            _resolver = resolver ?? Resolver.Empty;
            _hash = GetHash();

            Object = CreateObject();

            ChangeToken.OnChange(section.GetReloadToken, () => ReloadObject(false));
        }

        /// <summary>
        /// Force the underlying object to reload from the current configuration.
        /// </summary>
        public void Reload()
        {
            ReloadObject(true);
        }

        /// <summary>
        /// Gets the underlying object.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public TInterface Object { get; private set; }

        /// <summary>
        /// Occurs immediately before the underlying object is reloaded.
        /// </summary>
        public event EventHandler Reloading;

        /// <summary>
        /// Occurs immediately after the underlying object is reloaded.
        /// </summary>
        public event EventHandler Reloaded;

        /// <summary>
        /// Dispose the underlying object if it implements <see cref="IDisposable"/>.
        /// </summary>
        public void Dispose()
        {
            (Object as IDisposable)?.Dispose();
        }

        private TInterface CreateObject()
        {
            Type concreteType;
            IConfiguration valueSection;

            var typeValue = _section[ConfigurationObjectFactory.TypeKey];

            if (typeValue.IsNotNull())
            {
                concreteType = Type.GetType(typeValue, true);

                if (!typeof(TInterface).GetTypeInfo().IsAssignableFrom(concreteType))
                    throw Exceptions.ConfigurationSpecifiedTypeIsNotAssignableToTargetType(typeof(TInterface),
                        concreteType);

                valueSection = _section.GetSection(ConfigurationObjectFactory.ValueKey);
            }

            else if (ConfigurationObjectFactory.TryGetDefaultType(_defaultTypes, typeof(TInterface), _declaringType,
                _memberName, out concreteType))
            {
                valueSection =
                    string.Equals(_section[ConfigurationObjectFactory.ReloadOnChangeKey]?.ToLowerInvariant(), "true")
                        ? _section.GetSection(ConfigurationObjectFactory.ValueKey)
                        : _section;
            }
            else
            {
                throw Exceptions.TypeNotSpecifiedForReloadingProxy;
            }

            return (TInterface)valueSection.Create(concreteType, _defaultTypes, _valueConverters, _resolver);
        }

        private void ReloadObject(bool force)
        {
            lock (this)
            {
                var newHash = GetHash();

                if (!force &&
                    (string.Equals(_section[ConfigurationObjectFactory.ReloadOnChangeKey], "false",
                         StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(newHash, _hash, StringComparison.Ordinal)))
                    return;

                _hash = newHash;

                Reloading?.Invoke(this, EventArgs.Empty);

                var oldObject = Object;
                var newObject = CreateObject();

                TransferState(oldObject, newObject);

                Object = newObject;

                (oldObject as IDisposable)?.Dispose();

                Reloaded?.Invoke(this, EventArgs.Empty);
            }
        }

        private string GetHash()
        {
            var settingsDump = GetSettingsDump(_section);
            var buffer = Encoding.UTF8.GetBytes(settingsDump);
            var hash = _hashAlgorithm.ComputeHash(buffer);

            return Convert.ToBase64String(hash);

            static string GetSettingsDump(IConfiguration config)
            {
                var builder = new StringBuilder();
                AddSettingsDump(config, builder);
                return builder.ToString();
            }

            static void AddSettingsDump(IConfiguration config, StringBuilder builder)
            {
                if (config is IConfigurationSection section && section.Value != null)
                    builder.Append(section.Path).Append(section.Value);

                foreach (var child in config.GetChildren())
                    AddSettingsDump(child, builder);
            }
        }

        /// <summary>
        /// Transfer state from the old object to the new object, specifically event handlers
        /// and the values of reference-type read/write properties where the new object has a
        /// null value and the old object has a non-null value.
        /// </summary>
        /// <param name="oldObject">
        /// The object that is the current value of the <see cref="Object"/> property which is about
        /// to be replaced by <paramref name="newObject"/>.
        /// </param>
        /// <param name="newObject">
        /// The object (not yet in use) that is about to replace <paramref name="oldObject"/> as the
        /// value of the <see cref="Object"/> property.
        /// </param>
        protected abstract void TransferState(TInterface oldObject, TInterface newObject);
    }
}
