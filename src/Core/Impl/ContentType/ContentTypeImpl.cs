//
//  Copyright (c) Microsoft Corporation. All rights reserved.
//  Licensed under the MIT License. See License.txt in the project root for license information.
//
// This file contain implementations details that are subject to change without notice.
// Use at your own risk.
//
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.VisualStudio.Utilities.Implementation
{
    internal partial class ContentTypeImpl : IContentType
    {
        private readonly string name;
        private readonly static IReadOnlyList<ContentTypeImpl> emptyBaseTypes = new ContentTypeImpl[0];
        private IReadOnlyList<ContentTypeImpl> baseTypeList = emptyBaseTypes;

        internal ContentTypeImpl(string name, string mimeType = null, IEnumerable<string> baseTypes = null)
        {
            this.name = name;
            this.MimeType = mimeType;
            this.UnprocessedBaseTypes = baseTypes;
        }

        public string TypeName
        {
            get { return this.name; }
        }

        public string DisplayName
        {
            get { return this.name; }
        }

        public string MimeType { get; }

        public bool IsOfType(string type)
        {
            if (String.Compare(type, this.name, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return true;
            }
            else
            {
                foreach (IContentType baseType in this.baseTypeList)
                {
                    if (baseType.IsOfType(type))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public IEnumerable<IContentType> BaseTypes
        {
            get { return this.baseTypeList; }
        }

        public override string ToString()
        {
            return this.name;
        }

        internal void ProcessBaseTypes(IDictionary<string, ContentTypeImpl> nameToContentTypeBuilder,
                                       IDictionary<string, ContentTypeImpl> mimeTypeToContentTypeBuilder)
        {
            if (this.UnprocessedBaseTypes != null)
            {
                List<ContentTypeImpl> newBaseTypes = new List<ContentTypeImpl>();
                foreach (var baseTypeName in this.UnprocessedBaseTypes)
                {
                    // The expectation is that the base type will already exists but (if it doesn't) add a stub for it (& the ctor for a stub base type leaves it in a state
                    // where the basetypes/state are set appropriately).
                    var baseType = ContentTypeRegistryImpl.AddContentTypeFromMetadata(baseTypeName, /* mime type */null, /* base types */null, nameToContentTypeBuilder, mimeTypeToContentTypeBuilder);
                    if (baseType == ContentTypeRegistryImpl.UnknownContentTypeImpl)
                    {
                        throw new InvalidOperationException(String.Format(System.Globalization.CultureInfo.CurrentUICulture,
                                                            Strings.ContentTypeRegistry_ContentTypesCannotDeriveFromUnknown, this.TypeName));
                    }

                    if (!newBaseTypes.Contains(baseType))
                        newBaseTypes.Add(baseType);
                }

                if (newBaseTypes.Count > 0)
                {
                    this.baseTypeList = newBaseTypes.ToArray();
                    this.state = VisitState.NotVisited;
                }
                else
                {
                    Debug.Assert(object.ReferenceEquals(this.baseTypeList, emptyBaseTypes));
                }

                this.UnprocessedBaseTypes = null;
            }
        }

        // used internally for cycle detection
        internal enum VisitState
        {
            NotVisited = 0, // The node hasn't been visited yet
            Visiting,       // The node (or one of its children) is being visited
            Visited         // The node and its children have been visited before
        }

        private VisitState state = VisitState.Visited;

        internal bool CheckForCycle(bool breakCycle)
        {
            try
            {
                if (this.baseTypeList.Count != 0)
                {
                    this.state = VisitState.Visiting;
                    foreach (var baseType in this.baseTypeList)
                    {
                        if (baseType.state == VisitState.Visiting)
                        {
                            if (breakCycle)
                            {
                                // There is a cycle of this -> basetype -> ... -> this
                                // Don't try a surgical fix: simply break the cycle the easiest way possible
                                // since this is an error in the definitions that shouldn't happen.
                                // TODO: log the error.
                                this.baseTypeList = emptyBaseTypes;
                            }

                            return true;
                        }
                        else if ((baseType.state == VisitState.NotVisited) && baseType.CheckForCycle(breakCycle))
                        {
                            return true;
                        }
                    }
                }
            }
            finally
            {
                this.state = VisitState.Visited;
            }

            return false;
        }

        // used internally when building up content types
        internal void AddUnprocessedBaseTypes(IEnumerable<string> newBaseTypes)
        {
            if (newBaseTypes != null)
            {
                if (object.ReferenceEquals(this.UnprocessedBaseTypes, emptyBaseTypes))
                {
                    this.UnprocessedBaseTypes = newBaseTypes;
                }
                else
                {
                    var allBaseTypes = new List<string>(this.UnprocessedBaseTypes);
                    allBaseTypes.AddRange(newBaseTypes);
                    this.UnprocessedBaseTypes = allBaseTypes;
                }
            }
        }

        internal IEnumerable<string> UnprocessedBaseTypes;

#if DEBUG
        internal bool IsProcessed
        {
            get
            {
                return (this.UnprocessedBaseTypes == null) && (this.baseTypeList != null) &&
                         ((this.baseTypeList.Count == 0)
                          ? (object.ReferenceEquals(this.baseTypeList, ContentTypeImpl.emptyBaseTypes) && (this.state == VisitState.Visited))
                          : (this.state == VisitState.NotVisited));
            }
        }

        internal bool IsCheckedForCycles
        {
            get { return (this.state == VisitState.Visited) && (this.UnprocessedBaseTypes == null) && (object.ReferenceEquals(this.baseTypeList, ContentTypeImpl.emptyBaseTypes) || (this.baseTypeList.Count > 0)); }
        }
#endif
    }
}
