//
//  Copyright (c) Microsoft Corporation. All rights reserved.
//  Licensed under the MIT License. See License.txt in the project root for license information.
//
using System.Reflection;
using System.Runtime.ConstrainedExecution;
using System.Runtime.Versioning;
using System.Security.Permissions;

//
// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
//

[assembly: ComponentGuarantees(ComponentGuaranteesOptions.Stable)]


[assembly: AssemblyTrademark ("")]
[assembly: AssemblyCulture ("")]
#pragma warning disable 618
[assembly: SecurityPermission (SecurityAction.RequestMinimum, Flags = SecurityPermissionFlag.Execution)]
#pragma warning restore 618
[assembly: ReliabilityContract(Consistency.MayCorruptProcess, Cer.MayFail)]
