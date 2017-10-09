//
//  Copyright (c) Microsoft Corporation. All rights reserved.
//  Licensed under the MIT License. See License.txt in the project root for license information.
//
using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.VisualStudio.Text.Editor
{
    /// <summary>
    /// Specifies optional deferred creation semantics.
    /// </summary>
    [MetadataAttribute]
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class DeferCreationAttribute : SingletonBaseMetadataAttribute
    {
        private string optionName = string.Empty;

        /// <summary>
        /// Instantiates a new instance of a <see cref="DeferCreationAttribute"/>.
        /// </summary>
        public DeferCreationAttribute()
        {
        }

        /// <summary>
        /// The optional OptionName that controls creation.
        /// </summary>
        public string OptionName
        {
            get
            {
                return this.optionName;
            }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException("value");
                }
                this.optionName = value;
            }
        }
    }
}