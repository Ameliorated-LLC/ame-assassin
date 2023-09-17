using System.Collections.Generic;

namespace ame_assassin
{
    public static partial class SystemPackage
    {
        public static List<string> RemovedRegistryKeys { get; set; } = new List<string>();
        internal static List<RegistryValue> RemovedRegistryValues { get; set; } = new List<RegistryValue>();
        public static List<string> RemovedFiles { get; set; } = new List<string>();
        public static List<string> RemovedDirectories { get; set; } = new List<string>();
        public static List<string> RemovedEventProviders { get; set; } = new List<string>();
        public static List<string> RemovedEventChannels { get; set; } = new List<string>();
        public static List<string> RemovedScheduledTasks { get; set; } = new List<string>();
        public static List<string> RemovedServices { get; set; } = new List<string>();
        public static List<string> RemovedDevices { get; set; } = new List<string>();
        public static List<string> RemovedCounters { get; set; } = new List<string>();

        private class ParsedXML
        {
            // assembly > dependency > dependentAssembly > assemblyIdentity > [@name]
            public List<AssemblyIdentity> Dependents { get; set; } = new List<AssemblyIdentity>();
            public AssemblyIdentity Identity { get; set; }

            // assembly > registryKeys > registryKey > [@keyName]
            public List<string> RegistryKeys { get; set; } = new List<string>();

            // assembly > registryKeys > registryKey > registryValue > [@name]
            public List<RegistryValue> RegistryValues { get; set; } = new List<RegistryValue>();

            // assembly > file > [@destinationPath] + [@name] >
            public List<string> Files { get; set; } = new List<string>();

            // assembly > directories > directory > [@destinationPath]
            public List<string> Directories { get; set; } = new List<string>();

            // assembly > instrumentation > events > provider > [@guid]
            // HKLM\Software\Microsoft\Windows NT\CurrentVersion\WINEVT\Publishers\[GUID]
            public List<string> EventProviders { get; set; } = new List<string>();

            // assembly > instrumentation > events > provider > channels > channel > [@name]
            // HKLM\Software\Microsoft\Windows NT\CurrentVersion\WINEVT\Channels\[NAME]
            public List<string> EventChannels { get; set; } = new List<string>();

            // assembly > taskScheduler > Task > RegistrationInfo > URI
            public List<string> ScheduledTasks { get; set; } = new List<string>();

            // assembly > memberships > categoryMembership > categoryInstance > serviceData > [@name]
            public List<string> Services { get; set; } = new List<string>();
            
            // assembly > memberships > categoryMembership > (ChildNode "id" with typeName "SvcHost") categoryInstance > [@subcategory]
            // HKLM\Software\Microsoft\Windows NT\CurrentVersion\Svchost\[subcategory]
            // public List<string> SvcGroups { get; set; } = new List<string>();
            
            // assembly > memberships > catagoryMembership > catagoryInstance > serviceData (attribute [@type='kernelDriver'|'fileSystemDriver'])> [@name]
            public List<string> Devices { get; set; } = new List<string>();

            // assembly > instrumentation > counters > provider > [@providerGuid]
            // HKLM\Software\Microsoft\Windows NT\CurrentVersion\Perflib\_V2Providers\[GUID]
            public List<string> Counters { get; set; } = new List<string>();
        }
    }
}