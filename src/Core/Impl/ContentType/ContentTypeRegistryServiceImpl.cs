//
//  Copyright (c) Microsoft Corporation. All rights reserved.
//  Licensed under the MIT License. See License.txt in the project root for license information.
//
// This file contain implementations details that are subject to change without notice.
// Use at your own risk.
//
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Microsoft.VisualStudio.Utilities.Implementation
{
    public interface IContentTypeDefinitionMetadata
    {
        string Name { get; }

        [System.ComponentModel.DefaultValue(null)]
        IEnumerable<string> BaseDefinition { get; }

        [System.ComponentModel.DefaultValue(null)]
        string MimeType { get; }
    }

    [Export(typeof(IFileExtensionRegistryService))]
    [Export(typeof(IFileExtensionRegistryService2))]
    [Export(typeof(IContentTypeRegistryService))]
    [Export(typeof(IContentTypeRegistryService2))]
    internal sealed partial class ContentTypeRegistryImpl : IContentTypeRegistryService2, IFileExtensionRegistryService, IFileExtensionRegistryService2
    {
        [ImportMany]
        internal List<Lazy<ContentTypeDefinition, IContentTypeDefinitionMetadata>> ContentTypeDefinitions { get; set; }

        [ImportMany]
        internal List<IContentTypeDefinitionSource> ExternalSources { get; set; }

        [ImportMany]
        internal List<Lazy<FileExtensionToContentTypeDefinition, IFileToContentTypeMetadata>> FileToContentTypeProductions { get; set; }

        private MapCollection maps;

        /// <summary>
        /// The name of the unknown content type, guaranteed to exists no matter what other content types are produced
        /// </summary>
        private const string UnknownContentTypeName = "UNKNOWN";
        internal readonly static ContentTypeImpl UnknownContentTypeImpl = new ContentTypeImpl(ContentTypeRegistryImpl.UnknownContentTypeName, null, null);

        /// <summary>
        /// Builds the list of available content types
        /// Note: This function must be called after acquiring a lock on syncLock
        /// </summary>
        /// <remarks>
        /// Building the content type mappings should not throw exceptions, but should rather be logging issues 
        /// with some kind of common error reporting service and try to recover by ignoring the asset productions 
        /// that are deemed to cause the problem.
        /// </remarks>
        private void BuildContentTypes()
        {
            var oldMaps = Volatile.Read(ref this.maps);
            if (oldMaps == null)
            {
                var nameToContentTypeBuilder = MapCollection.Empty.NameToContentTypeMap.ToBuilder();
                var mimeTypeToContentTypeBuilder = MapCollection.Empty.MimeTypeToContentTypeMap.ToBuilder();

                // Add the singleton Unknown content type to the dictionary
                nameToContentTypeBuilder.Add(ContentTypeRegistryImpl.UnknownContentTypeName, ContentTypeRegistryImpl.UnknownContentTypeImpl);


                // For each content type provision, create an IContentType.
                foreach (Lazy<ContentTypeDefinition, IContentTypeDefinitionMetadata> contentTypeDefinition in ContentTypeDefinitions)
                {
                    AddContentTypeFromMetadata(contentTypeDefinition.Metadata.Name,
                                               contentTypeDefinition.Metadata.MimeType,
                                               contentTypeDefinition.Metadata.BaseDefinition, nameToContentTypeBuilder, mimeTypeToContentTypeBuilder);
                }

                // Now consider the external sources. This allows us to consider legacy content types together with MEF-defined
                // content types.
                foreach (IContentTypeDefinitionSource source in this.ExternalSources)
                {
                    if (source.Definitions != null)
                    {
                        foreach (IContentTypeDefinition metadata in source.Definitions)
                        {
                            AddContentTypeFromMetadata(metadata.Name,
                                                       /* mimeType*/ null,
                                                       metadata.BaseDefinitions, nameToContentTypeBuilder, mimeTypeToContentTypeBuilder);
                        }
                    }
                }

                List<ContentTypeImpl> allTypes = new List<ContentTypeImpl>(nameToContentTypeBuilder.Count);
                allTypes.AddRange(nameToContentTypeBuilder.Values);
                foreach (var type in allTypes)
                {
                    type.ProcessBaseTypes(nameToContentTypeBuilder, mimeTypeToContentTypeBuilder);
                }

#if DEBUG
                foreach (var type in nameToContentTypeBuilder.Values)
                {
                    Debug.Assert(type.IsProcessed);
                }
#endif

                foreach (var type in nameToContentTypeBuilder.Values)
                {
                    type.CheckForCycle(breakCycle: true);
                }

                var fileExtensionToContentTypeMapBuilder = MapCollection.Empty.FileExtensionToContentTypeMap.ToBuilder();
                var fileNameToContentTypeMapBuilder = MapCollection.Empty.FileNameToContentTypeMap.ToBuilder();
                foreach (var fileExtensionDefinition in this.FileToContentTypeProductions)
                {
                    // MEF ensures that there will be at least one content type in the metadata. We take the first one. 
                    // We prefer this over defining a different attribute from ContentType[] for this purpose.
                    var contentTypeName = fileExtensionDefinition.Metadata.ContentTypes.FirstOrDefault();
                    ContentTypeImpl contentType;
                    if ((contentTypeName != null) && nameToContentTypeBuilder.TryGetValue(contentTypeName, out contentType))
                    {
                        if (!string.IsNullOrEmpty(fileExtensionDefinition.Metadata.FileExtension))
                        {
                            foreach (var ext in fileExtensionDefinition.Metadata.FileExtension.Split(';'))
                            {
                                if (ext != null)
                                {
                                    var extension = RemoveExtensionDot(ext);
                                    if (!(string.IsNullOrWhiteSpace(extension) || fileExtensionToContentTypeMapBuilder.ContainsKey(extension)))
                                        fileExtensionToContentTypeMapBuilder.Add(extension, contentType);
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(fileExtensionDefinition.Metadata.FileName))
                        {
                            foreach (var name in fileExtensionDefinition.Metadata.FileName.Split(';'))
                            {
                                if (!(string.IsNullOrWhiteSpace(name) || fileNameToContentTypeMapBuilder.ContainsKey(name)))
                                    fileNameToContentTypeMapBuilder.Add(name, contentType);
                            }
                        }
                    }
                }

                var newMaps = new MapCollection(nameToContentTypeBuilder.ToImmutable(), mimeTypeToContentTypeBuilder.ToImmutable(), fileExtensionToContentTypeMapBuilder.ToImmutable(), fileNameToContentTypeMapBuilder.ToImmutable());
                Interlocked.CompareExchange(ref this.maps, newMaps, oldMaps);

                // We actually don't care whether or not the CompareExchange succeeded.
                // Eitehr it succeeded (normally the case) or someone else successfully completed BuildContentTypes on another thread and we shouldn't do anything.
            }
        }

        private const string BaseMimePrefix = @"text/";
        private const string MimePrefix = BaseMimePrefix + @"x-";

        internal static ContentTypeImpl AddContentTypeFromMetadata(string contentTypeName, string mimeType, IEnumerable<string> baseTypes,
                                                                   IDictionary<string, ContentTypeImpl> nameToContentTypeBuilder,
                                                                   IDictionary<string, ContentTypeImpl> mimeTypeToContentTypeBuilder)
        {
            if (!string.IsNullOrEmpty(contentTypeName))
            {
                ContentTypeImpl type;
                if (!nameToContentTypeBuilder.TryGetValue(contentTypeName, out type))
                {
                    bool addToMimeTypeMap = false;
                    if (string.IsNullOrWhiteSpace(mimeType))
                    {
                        mimeType = MimePrefix + contentTypeName.ToLowerInvariant();
                    }
                    else if (mimeTypeToContentTypeBuilder.ContainsKey(mimeType))
                    {
                        mimeType = null;
                    }
                    else
                    {
                        addToMimeTypeMap = true;
                    }

                    type = new ContentTypeImpl(contentTypeName, mimeType, baseTypes);

                    nameToContentTypeBuilder.Add(contentTypeName, type);
                    if (addToMimeTypeMap)
                    {
                        mimeTypeToContentTypeBuilder.Add(mimeType, type);
                    }
                }
                else
                {
                    type.AddUnprocessedBaseTypes(baseTypes);
                }

                return type;
            }

            return null;
        }

        /// <summary>
        /// Checks whether the specified type is base type for another content type
        /// </summary>
        /// <param name="typeToCheck">The type to check for being a base type</param>
        /// <param name="derivedType">An out parameter to receive the first discovered derived type</param>
        /// <returns><c>True</c> if the given <paramref name="typeToCheck"/> content type is a base type</returns>
        private bool IsBaseType(ContentTypeImpl typeToCheck, out ContentTypeImpl derivedType)
        {
            derivedType = null;

            foreach (ContentTypeImpl type in this.maps.NameToContentTypeMap.Values)
            {
                if (type != typeToCheck)
                {
                    foreach (IContentType baseType in type.BaseTypes)
                    {
                        if (baseType == typeToCheck)
                        {
                            derivedType = type;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        #region IContentTypeRegistryService Members
        public IContentType GetContentType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new ArgumentException(nameof(typeName));
            }

            this.BuildContentTypes();

            ContentTypeImpl contentType = null;
            this.maps.NameToContentTypeMap.TryGetValue(typeName, out contentType);

            return contentType;
        }

        public IContentType UnknownContentType
        {
            get { return ContentTypeRegistryImpl.UnknownContentTypeImpl; }
        }

        public IEnumerable<IContentType> ContentTypes
        {
            get
            {
                this.BuildContentTypes();
                var map = this.maps.NameToContentTypeMap;

                return map.Values;
            }
        }

        public IContentType AddContentType(string typeName, IEnumerable<string> baseTypeNames)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new ArgumentException(nameof(typeName));
            }

            // This has the side effect of building the content types.
            if (this.GetContentType(typeName) != null)
            {
                // Cannot dynamically add a new content type if a content type with the same name already exists
                throw new ArgumentException(String.Format(System.Globalization.CultureInfo.CurrentUICulture, Strings.ContentTypeRegistry_CannotAddExistentType, typeName));
            }

            var oldMaps = Volatile.Read(ref this.maps);
            while (true)
            {
                var nameToContentTypeMap = new PseudoBuilder<string, ContentTypeImpl>(oldMaps.NameToContentTypeMap);
                var mimeTypeToContentTypeMap = new PseudoBuilder<string, ContentTypeImpl>(oldMaps.MimeTypeToContentTypeMap);

                var type = AddContentTypeFromMetadata(typeName, null, baseTypeNames,
                                                      nameToContentTypeMap, mimeTypeToContentTypeMap);

                type.ProcessBaseTypes(nameToContentTypeMap, mimeTypeToContentTypeMap);

                if (type.CheckForCycle(breakCycle: false))
                {
                    throw new InvalidOperationException(String.Format(System.Globalization.CultureInfo.CurrentUICulture, Strings.ContentTypeRegistry_CausesCycles, type.TypeName));
                }

                var newMaps = new MapCollection(nameToContentTypeMap.Source, mimeTypeToContentTypeMap.Source, oldMaps.FileExtensionToContentTypeMap, oldMaps.FileNameToContentTypeMap);
                var results = Interlocked.CompareExchange(ref this.maps, newMaps, oldMaps);
                if (results == oldMaps)
                {
                    return type;
                }

                // Two people tried to add content types simultaneously.
                oldMaps = results;
            }
        }

        public void RemoveContentType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new ArgumentException(nameof(typeName));
            }

            this.BuildContentTypes();

            var oldMaps = Volatile.Read(ref this.maps);
            while (true)
            {
                ContentTypeImpl type;
                if (!oldMaps.NameToContentTypeMap.TryGetValue(typeName, out type))
                {
                    // No type == no type to remove;
                    return;
                }

                if (type == ContentTypeRegistryImpl.UnknownContentTypeImpl)
                {
                    // Check if the type to be removed is not the Unknown content type
                    throw new InvalidOperationException(Strings.ContentTypeRegistry_CannotRemoveTheUnknownType);
                }

                ContentTypeImpl derivedType;
                if (IsBaseType(type, out derivedType))
                {
                    // Check if the type is base type for another registered type
                    throw new InvalidOperationException(String.Format(System.Globalization.CultureInfo.CurrentUICulture, Strings.ContentTypeRegistry_CannotRemoveBaseType, type.TypeName, derivedType.TypeName));
                }

                // If there are file extensions using this content type we won't allow removing it
                if (this.maps.FileExtensionToContentTypeMap.Values.Any(c => c == type))
                {
                    // If there are file extensions using this content type we won't allow removing it
                    throw new InvalidOperationException(String.Format(System.Globalization.CultureInfo.CurrentUICulture, Strings.ContentTypeRegistry_CannotRemoveTypeUsedByFileExtensions, type.TypeName));
                }

                // If there are file extensions using this content type we won't allow removing it
                if (this.maps.FileNameToContentTypeMap.Values.Any(c => c == type))
                {
                    // If there are file extensions using this content type we won't allow removing it
                    throw new InvalidOperationException(String.Format(System.Globalization.CultureInfo.CurrentUICulture, Strings.ContentTypeRegistry_CannotRemoveTypeUsedByFileExtensions, type.TypeName));
                }

                var newMaps = new MapCollection(oldMaps.NameToContentTypeMap.Remove(typeName),
                                                (type.MimeType != null) ? oldMaps.MimeTypeToContentTypeMap.Remove(type.MimeType) : oldMaps.MimeTypeToContentTypeMap,
                                                oldMaps.FileExtensionToContentTypeMap, oldMaps.FileNameToContentTypeMap);
                var results = Interlocked.CompareExchange(ref this.maps, newMaps, oldMaps);
                if (results == oldMaps)
                {
                    return;
                }

                // Two people tried to remove content types simultaneously.
                oldMaps = results;
            }
        }
        #endregion

        #region IContentTypeRegistryService2 Members
        public string GetMimeType(IContentType type)
        {
            var typeImpl = type as ContentTypeImpl;
            if (typeImpl == null)
            {
                throw new ArgumentException(nameof(type));
            }
            else if (typeImpl == UnknownContentTypeImpl)
            {
                return null;
            }

            return typeImpl.MimeType;
        }

        public IContentType GetContentTypeForMimeType(string mimeType)
        {
            if (string.IsNullOrWhiteSpace(mimeType))
            {
                throw new ArgumentException(nameof(mimeType));
            }

            this.BuildContentTypes();

            ContentTypeImpl contentType = null;
            if (!this.maps.MimeTypeToContentTypeMap.TryGetValue(mimeType, out contentType))
            {
                if (mimeType.StartsWith(BaseMimePrefix))
                {
                    if (!(mimeType.StartsWith(MimePrefix) && this.maps.NameToContentTypeMap.TryGetValue(mimeType.Substring(MimePrefix.Length), out contentType)))
                    {
                        this.maps.NameToContentTypeMap.TryGetValue(mimeType.Substring(BaseMimePrefix.Length), out contentType);
                    }
                }
            }

            return contentType;
        }
        #endregion

        #region IFileExtensionRegistryService Members
        public IContentType GetContentTypeForExtension(string extension)
        {
            if (extension == null)
            {
                throw new ArgumentNullException(nameof(extension));
            }

            this.BuildContentTypes();

            ContentTypeImpl contentType = null;
            this.maps.FileExtensionToContentTypeMap.TryGetValue(RemoveExtensionDot(extension), out contentType);

            // TODO: should we return null if contentType is null?
            return contentType ?? ContentTypeRegistryImpl.UnknownContentTypeImpl;
        }

        public IEnumerable<string> GetExtensionsForContentType(IContentType contentType)
        {
            if (contentType == null)
            {
                throw new ArgumentNullException(nameof(contentType));
            }

            this.BuildContentTypes();

            // We don't expect this to be called on a perf critical thread so we can use the dictionary.
            foreach (var kvp in this.maps.FileExtensionToContentTypeMap)
            {
                if (contentType == kvp.Value)
                {
                    yield return kvp.Key;
                }
            }
        }

        public void AddFileExtension(string extension, IContentType contentType)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                throw new ArgumentException(nameof(extension));
            }

            var contentTypeImpl = contentType as ContentTypeImpl;
            if ((contentTypeImpl == null) || (contentTypeImpl == UnknownContentTypeImpl))
            {
                throw new ArgumentException(nameof(contentType));
            }

            this.BuildContentTypes();
            extension = RemoveExtensionDot(extension);

            var oldMaps = Volatile.Read(ref this.maps);
            while (true)
            {
                ContentTypeImpl type;
                if (oldMaps.FileExtensionToContentTypeMap.TryGetValue(extension, out type))
                {
                    if (type != contentTypeImpl)
                    {
                        throw new InvalidOperationException
                                    (String.Format(System.Globalization.CultureInfo.CurrentUICulture,
                                        Strings.FileExtensionRegistry_NoMultipleContentTypes, extension));
                    }

                    return;
                }

                var newMaps = new MapCollection(oldMaps.NameToContentTypeMap, oldMaps.MimeTypeToContentTypeMap,
                                                oldMaps.FileExtensionToContentTypeMap.Add(extension, contentTypeImpl),
                                                oldMaps.FileNameToContentTypeMap);

                var results = Interlocked.CompareExchange(ref this.maps, newMaps, oldMaps);
                if (results == oldMaps)
                {
                    return;
                }

                // Two people tried to remove content types simultaneously.
                oldMaps = results;
            }
        }

        public void RemoveFileExtension(string extension)
        {
            if (extension == null)
            {
                throw new ArgumentNullException(nameof(extension));
            }

            this.BuildContentTypes();

            extension = RemoveExtensionDot(extension);

            var oldMaps = Volatile.Read(ref this.maps);
            while (true)
            {
                if (!oldMaps.FileExtensionToContentTypeMap.ContainsKey(extension))
                {
                    return;
                }

                var newMaps = new MapCollection(oldMaps.NameToContentTypeMap, oldMaps.MimeTypeToContentTypeMap,
                                                oldMaps.FileExtensionToContentTypeMap.Remove(extension),
                                                oldMaps.FileNameToContentTypeMap);

                var results = Interlocked.CompareExchange(ref this.maps, newMaps, oldMaps);
                if (results == oldMaps)
                {
                    return;
                }

                // Two people tried to remove content types simultaneously.
                oldMaps = results;
            }
        }
        #endregion

        #region IFileExtensionRegistryService2 Members
        public IContentType GetContentTypeForFileName(string fileName)
        {
            if (fileName == null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            this.BuildContentTypes();

            ContentTypeImpl contentType = null;
            this.maps.FileNameToContentTypeMap.TryGetValue(fileName, out contentType);

            // TODO: should we return null if contentType is null?
            return contentType ?? ContentTypeRegistryImpl.UnknownContentTypeImpl;
        }

        public IContentType GetContentTypeForFileNameOrExtension(string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            // No need to lock, we are calling locking public method.
            var fileNameContentType = this.GetContentTypeForFileName(name);

            // Attempt to use extension as fallback ContentType if file name isn't recognized.
            if (fileNameContentType == ContentTypeRegistryImpl.UnknownContentTypeImpl)
            {
                var extension = Path.GetExtension(name);

                if (!string.IsNullOrEmpty(extension))
                {
                    // No need to lock, we are calling locking public method.
                    return this.GetContentTypeForExtension(extension);
                }
            }

            return fileNameContentType;
        }

        public IEnumerable<string> GetFileNamesForContentType(IContentType contentType)
        {
            if (contentType == null)
            {
                throw new ArgumentNullException(nameof(contentType));
            }

            this.BuildContentTypes();

            // We don't expect this to be called on a perf critical thread so we can use the dictionary.
            foreach (var kvp in this.maps.FileNameToContentTypeMap)
            {
                if (contentType == kvp.Value)
                {
                    yield return kvp.Key;
                }
            }
        }

        public void AddFileName(string fileName, IContentType contentType)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException(nameof(fileName));
            }

            var contentTypeImpl = contentType as ContentTypeImpl;
            if ((contentTypeImpl == null) || (contentTypeImpl == UnknownContentTypeImpl))
            {
                throw new ArgumentException(nameof(contentType));
            }

            this.BuildContentTypes();

            var oldMaps = Volatile.Read(ref this.maps);
            while (true)
            {
                ContentTypeImpl type;
                if (oldMaps.FileNameToContentTypeMap.TryGetValue(fileName, out type))
                {
                    if (type != contentTypeImpl)
                    {
                        throw new InvalidOperationException
                                    (String.Format(System.Globalization.CultureInfo.CurrentUICulture,
                                        Strings.FileExtensionRegistry_NoMultipleContentTypes, fileName));
                    }

                    return;
                }

                var newMaps = new MapCollection(oldMaps.NameToContentTypeMap, oldMaps.MimeTypeToContentTypeMap,
                                                oldMaps.FileExtensionToContentTypeMap,
                                                oldMaps.FileNameToContentTypeMap.Add(fileName, contentTypeImpl));

                var results = Interlocked.CompareExchange(ref this.maps, newMaps, oldMaps);
                if (results == oldMaps)
                {
                    return;
                }

                // Two people tried to remove content types simultaneously.
                oldMaps = results;
            }
        }

        public void RemoveFileName(string fileName)
        {
            if (fileName == null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            this.BuildContentTypes();

            var oldMaps = Volatile.Read(ref this.maps);
            while (true)
            {
                if (!oldMaps.FileNameToContentTypeMap.ContainsKey(fileName))
                {
                    return;
                }

                var newMaps = new MapCollection(oldMaps.NameToContentTypeMap, oldMaps.MimeTypeToContentTypeMap,
                                                oldMaps.FileExtensionToContentTypeMap,
                                                oldMaps.FileNameToContentTypeMap.Remove(fileName));

                var results = Interlocked.CompareExchange(ref this.maps, newMaps, oldMaps);
                if (results == oldMaps)
                {
                    return;
                }

                // Two people tried to remove content types simultaneously.
                oldMaps = results;
            }
        }
        #endregion

        private static string RemoveExtensionDot(string extension)
        {
            if (extension.StartsWith("."))
            {
                return extension.TrimStart('.');
            }
            else
            {
                return extension;
            }
        }

        class MapCollection
        {
            public readonly static MapCollection Empty = new MapCollection();

            public readonly ImmutableDictionary<string, ContentTypeImpl> NameToContentTypeMap;
            public readonly ImmutableDictionary<string, ContentTypeImpl> MimeTypeToContentTypeMap;
            public readonly ImmutableDictionary<string, ContentTypeImpl> FileExtensionToContentTypeMap;
            public readonly ImmutableDictionary<string, ContentTypeImpl> FileNameToContentTypeMap;

            private MapCollection()
            {
                this.NameToContentTypeMap = ImmutableDictionary<string, ContentTypeImpl>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
                this.MimeTypeToContentTypeMap = ImmutableDictionary<string, ContentTypeImpl>.Empty.WithComparers(StringComparer.Ordinal);
                this.FileExtensionToContentTypeMap = ImmutableDictionary<string, ContentTypeImpl>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
                this.FileNameToContentTypeMap = ImmutableDictionary<string, ContentTypeImpl>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
            }

            public MapCollection(ImmutableDictionary<string, ContentTypeImpl> nameToContentType, ImmutableDictionary<string, ContentTypeImpl> mimeTypeToContentTypeMap,
                                 ImmutableDictionary<string, ContentTypeImpl> fileExtensionToContentTypeMap, ImmutableDictionary<string, ContentTypeImpl> fileNameToContentTypeMap)
            {
                this.NameToContentTypeMap = nameToContentType;
                this.MimeTypeToContentTypeMap = mimeTypeToContentTypeMap;
                this.FileExtensionToContentTypeMap = fileExtensionToContentTypeMap;
                this.FileNameToContentTypeMap = fileNameToContentTypeMap;

#if DEBUG
                foreach (var c in nameToContentType.Values)
                {
                    Debug.Assert(c.IsCheckedForCycles);
                }
#endif
            }
        }

        class PseudoBuilder<K, V> : IDictionary<K, V>
        {
            public ImmutableDictionary<K, V> Source { get; private set; }

            public PseudoBuilder(ImmutableDictionary<K, V> source)
            {
                this.Source = source;
            }

            public void Add(K key, V value)
            {
                this.Source = this.Source.Add(key, value);
            }

            public bool ContainsKey(K key)
            {
                return this.Source.ContainsKey(key);
            }

            public bool TryGetValue(K key, out V value)
            {
                return this.Source.TryGetValue(key, out value);
            }

            #region NotImplemented
            public V this[K key] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

            public ICollection<K> Keys => throw new NotImplementedException();

            public ICollection<V> Values => throw new NotImplementedException();

            public int Count => throw new NotImplementedException();

            public bool IsReadOnly => throw new NotImplementedException();

            public void Add(KeyValuePair<K, V> item)
            {
                throw new NotImplementedException();
            }

            public void Clear()
            {
                throw new NotImplementedException();
            }

            public bool Contains(KeyValuePair<K, V> item)
            {
                throw new NotImplementedException();
            }

            public void CopyTo(KeyValuePair<K, V>[] array, int arrayIndex)
            {
                throw new NotImplementedException();
            }

            public IEnumerator<KeyValuePair<K, V>> GetEnumerator()
            {
                throw new NotImplementedException();
            }

            public bool Remove(K key)
            {
                throw new NotImplementedException();
            }

            public bool Remove(KeyValuePair<K, V> item)
            {
                throw new NotImplementedException();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                throw new NotImplementedException();
            }
            #endregion
        }
    }
}
