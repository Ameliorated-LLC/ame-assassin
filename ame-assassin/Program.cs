using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using TrustedUninstaller.Shared.Actions;
using static ame_assassin.Initializer;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace ame_assassin
{
    public static class FileLock
    {
        public static bool HasKilledExplorer = false;
        
        private const int RmRebootReasonNone = 0;
        private const int CCH_RM_MAX_APP_NAME = 255;
        private const int CCH_RM_MAX_SVC_NAME = 63;

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle,
            uint nFiles,
            string[] rgsFilenames,
            uint nApplications,
            [In] RM_UNIQUE_PROCESS[] rgApplications,
            uint nServices,
            string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Auto)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle,
            out uint pnProcInfoNeeded,
            ref uint pnProcInfo,
            [In] [Out] RM_PROCESS_INFO[] rgAffectedApps,
            ref uint lpdwRebootReasons);

        public static List<Process> WhoIsLocking(string path)
        {
            var key = Guid.NewGuid().ToString();
            var processes = new List<Process>();

            var res = RmStartSession(out var handle, 0, key);
            if (res != 0) throw new Exception("Could not begin restart session.  Unable to determine file locker.");

            try {
                const int ERROR_MORE_DATA = 234;
                uint pnProcInfoNeeded = 0,
                    pnProcInfo = 0,
                    lpdwRebootReasons = RmRebootReasonNone;

                string[] resources = { path }; // Just checking on one resource.

                res = RmRegisterResources(handle, (uint)resources.Length, resources, 0, null, 0, null);

                if (res != 0) throw new Exception("Could not register resource.");

                //Note: there's a race condition here -- the first call to RmGetList() returns
                //      the total number of process. However, when we call RmGetList() again to get
                //      the actual processes this number may have increased.
                res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, null, ref lpdwRebootReasons);

                if (res == ERROR_MORE_DATA) {
                    // Create an array to store the process results
                    var processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                    pnProcInfo = pnProcInfoNeeded;

                    // Get the list
                    res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons);
                    if (res == 0) {
                        processes = new List<Process>((int)pnProcInfo);

                        // Enumerate all of the results and add them to the 
                        // list to be returned
                        for (var i = 0; i < pnProcInfo; i++)
                            try {
                                processes.Add(Process.GetProcessById(processInfo[i].Process.dwProcessId));
                            }
                            // catch the error -- in case the process is no longer running
                            catch (ArgumentException) {
                            }
                    } else {
                        throw new Exception("Could not list processes locking resource.");
                    }
                } else if (res != 0) {
                    throw new Exception("Could not list processes locking resource. Could not get size of result.");
                }
            } finally {
                RmEndSession(handle);
            }

            return processes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public readonly int dwProcessId;
            public readonly FILETIME ProcessStartTime;
        }

        private enum RM_APP_TYPE
        {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public readonly RM_UNIQUE_PROCESS Process;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
            public readonly string strAppName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
            public readonly string strServiceShortName;

            public readonly RM_APP_TYPE ApplicationType;
            public readonly uint AppStatus;
            public readonly uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public readonly bool bRestartable;
        }
    }

    internal static class Initializer
    {
        public static SqliteConnection ActiveLink;

        public static void Hook(string database, bool disableConstraints = false)
        {
            if (!File.Exists(database)) {
                Console.WriteLine("\r\nError: Machine database was unable to be located.");
                Environment.Exit(2);
            }

            try {
                ActiveLink = new SqliteConnection($@"Data Source='{database}';");
                ActiveLink.Open();
            } catch (Exception e) {
                Console.WriteLine($"\r\nFatal Error: Could not connect to machine database.\r\nException: {e.Message}");
                Environment.Exit(3);
            }
        }

        public class Transaction
        {
            private readonly SqliteTransaction transaction;
            public SqliteCommand Command;
            private readonly bool keysDisabled;

            public Transaction(string text = null, bool fkeysDisabled = false)
            {
                if (fkeysDisabled) keysDisabled = true;
                transaction = ActiveLink.BeginTransaction();
                Command = ActiveLink.CreateCommand();
                Command.Transaction = transaction;
                if (text != null) Command.CommandText = text;
            }

            public SqliteCommand NewCommand(string text)
            {
                var command = ActiveLink.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = text;

                return command;
            }

            public void Commit()
            {
                transaction.Commit();
            }

            public void Abort()
            {
                if (transaction.Connection != null) {
                    transaction.Rollback();
                    transaction.Commit();
                    transaction.Dispose();
                }
            }

            public void DisableForeignKeys()
            {
                if (!keysDisabled)
                    try {
                        var foreignCheck = NewCommand(@"SELECT ""foreign_keys"" FROM PRAGMA_foreign_keys");
                        var foreignData = foreignCheck.ExecuteReader();

                        while (foreignData.Read()) {
                            var foreignValue = foreignData.GetString(0);

                            if (foreignValue == "1") {
                                if (Program.Verbose) Console.WriteLine("\r\nDisabling foreign keys...");

                                transaction.Commit();
                                try {
                                    var foreignKeysOff = new SqliteCommand(@"PRAGMA foreign_keys=off", ActiveLink);
                                    foreignKeysOff.ExecuteNonQuery();
                                } catch (Exception e) {
                                    Program.ActiveTransaction = new Transaction();
                                    Console.WriteLine($"\r\nError: Could not disable foreign keys.\r\nException: {e.Message}");
                                }

                                Program.ActiveTransaction = new Transaction(fkeysDisabled: true);
                            }
                        }
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not query foreign key setting.\r\nException: {e.Message}");
                    }
            }

            /*
            public void EnableForeignKeys()
            {
                if (keysDisabled) {
                    try {
                        if (Program.Verbose) Console.WriteLine("\r\nEnabling foreign keys...");

                        transaction.Commit();

                        var foreignKeysOn = new SqliteCommand(@"PRAGMA foreign_keys=on", ActiveLink);
                        foreignKeysOn.ExecuteNonQuery();
                    } catch (Exception e) {
                        Program.ActiveTransaction = new Transaction();
                        Console.WriteLine($"\r\nError: Could not re-enable foreign keys.\r\nException: {e.Message}");
                    }

                    Program.ActiveTransaction = new Transaction();
                }
            }
            */
        }
    }

    internal static class Triggers
    {
        private static List<string> TriggerSqlList;

        public static void Save(string database = null)
        {
            TriggerSqlList = new List<string>();
            if (ActiveLink == null && database == null)
                throw new Exception("Could not save triggers. Function supplied with missing parameters.");
            if (ActiveLink == null) Hook(database);

            var saveSql = new Transaction(@"SELECT ""sql"" FROM main.sqlite_master WHERE ""type"" = ""trigger""");

            var sqlContent = saveSql.Command.ExecuteReader();
            while (sqlContent.Read()) {
                var sql = sqlContent.GetString(0);
                TriggerSqlList.Add(sql);
            }

            saveSql.Commit();
        }

        public static void Drop(string database = null)
        {
            if (ActiveLink == null && database == null)
                throw new Exception("Could not drop triggers. Function supplied with missing parameters.");
            if (ActiveLink == null) Hook(database);

            var dropSql = new Transaction(@"SELECT ""name"" FROM main.sqlite_master WHERE ""type"" = ""trigger""");

            var content = dropSql.Command.ExecuteReader();
            while (content.Read()) {
                var trigger = content.GetString(0);
                dropSql.NewCommand($"DROP TRIGGER {trigger}").ExecuteNonQuery();
            }

            dropSql.Commit();
        }

        public static void Restore(string database = null)
        {
            if (ActiveLink == null && database == null)
                throw new Exception("Could not restore triggers. Function supplied with missing parameters.");
            if (ActiveLink == null) Hook(database);

            if (TriggerSqlList == null) throw new Exception("Could not restore triggers. Function supplied with missing parameters.");

            var restoreSql = new Transaction(@"SELECT ""sql"" FROM main.sqlite_master WHERE ""type"" = ""trigger""");

            foreach (var sql in TriggerSqlList) restoreSql.NewCommand(sql).ExecuteNonQuery();

            restoreSql.Commit();
        }
    }
    
    internal static class Assassin
    {
        public static List<string> PackageFilterList;
        public static List<string> PackageDirectoryList = new List<string>();
        public static List<string> AppSubNameList;
        private static List<string> AppFiles = new List<string>();
        public static List<string> ApplicationProgIDList;

        public static void StopService(string serviceName)
        {
            var service = ServiceController.GetServices().FirstOrDefault(serv => serv.ServiceName == serviceName);
            
            if (service != null && service.Status != ServiceControllerStatus.StopPending && service.Status != ServiceControllerStatus.Stopped)
            {
                var selfDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                Console.WriteLine($"\r\nKilling service {serviceName}...");

                var procInfo = new ProcessStartInfo(Program.ProcessHacker, $@"-s -elevate -c -ctype service -cobject {serviceName} -caction stop");
                procInfo.UseShellExecute = false;
                procInfo.CreateNoWindow = true;

                var proc = new Process {
                    StartInfo = procInfo,
                    EnableRaisingEvents = true
                };
                try {
                    proc.Start();
                    proc.WaitForExit(5000);
                } catch (Exception e4) {
                    Console.WriteLine($"\r\nError: Could not start ProcessHacker.\r\nException: {e4.Message}");
                }
                Console.WriteLine("\r\nWaiting for the service to stop...");

                var i = 0;
                while (service.Status != ServiceControllerStatus.Stopped && i <= 30)
                {
                    service.Refresh();
                    //Wait for the service to stop
                    System.Threading.Thread.Sleep(100);
                    i++;
                }
                if (i > 20) {
                    Console.WriteLine($"\r\nError: Service exceeded expected exit time.");
                }
            }
        }
        public static void MachineData(string key, string value, bool app = false)
        {
            if (key == "_PackageID") MachineDependentsData(value);

            if (key == "_ApplicationID") {
                try {
                    var appData = Program.ActiveTransaction.NewCommand($@"SELECT ""ApplicationUserModelID"",""Executable"" FROM ""Application"" WHERE ""{key}"" = ""{value}""").ExecuteReader();
                    while (appData.Read()) {
                        AppSubNameList.Add(appData.GetString(0).Split('!').Last());
                        if (Program.TableList.Contains("ApplicationIdentity")) {
                            var identityName = Program.ActiveTransaction.NewCommand($@"SELECT ""_ApplicationIdentityID"" FROM ""ApplicationIdentity"" WHERE ""ApplicationUserModelID"" = ""{appData.GetString(0)}""").ExecuteReader();
                            while (identityName.Read()) {
                                MachineData("_ApplicationIdentityID", identityName.GetString(0));

                                if (Program.Verbose) Console.WriteLine($"\r\nRemoving _ApplicationIdentityID value {identityName.GetString(0)}\nfrom table ApplicationIdentity...");
                                try {
                                    var eliminate = Program.ActiveTransaction.NewCommand($@"DELETE FROM ""ApplicationIdentity"" WHERE ""_ApplicationIdentityID"" = ""{identityName.GetString(0)}""");
                                    eliminate.ExecuteNonQuery();
                                } catch (Exception e) {
                                    Console.WriteLine($"\r\nError: Could not remove _ApplicationIdentityID value {identityName.GetString(0)} from table ApplicationIdentity.\r\nException: {e.Message}");
                                }
                            }
                        }
                        
                        /*
                        try {
                             var logoSplit = appData.GetString(1).Split('\\');
                            if (logoSplit[1] == "Assets") {
                                AppFiles.Add(logoSplit[0]);
                            }
                         } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not get app asset file location data.\r\nException: {e.Message}");
                        }
                        */

                        try { 
                            AppFiles.Add(appData.GetString(1));
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not get app executable file location data.\r\nException: {e.Message}");
                        }
                    }
                } catch (Exception e) {
                    Console.WriteLine($"\r\nError: Could not get detailed app information.\r\nException: {e.Message}");
                }
            }

            var keyExist = false;
            foreach (var tableItem in Program.TableList) {
                var keyLinkCheck = Program.ActiveTransaction.NewCommand($@"SELECT ""from"" FROM PRAGMA_foreign_key_list(""{tableItem}"") WHERE ""to"" = ""{key}""");
                var keyLinkData = keyLinkCheck.ExecuteReader();


                while (keyLinkData.Read()) {
                    keyExist = true;
                    var column = keyLinkData.GetString(0);

                    if (tableItem == "BundlePackage") {
                        MachineBundleData(column, value);
                        return;
                    }

                    var valueMatchCheck = Program.ActiveTransaction.NewCommand($@"SELECT ""{column}"" FROM ""{tableItem}"" WHERE ""{column}"" = ""{value}""");
                    var valueMatchData = valueMatchCheck.ExecuteReader();

                    while (valueMatchData.Read()) {
                        switch (tableItem) {
                            case "PackageFamily":
                                var familyName = Program.ActiveTransaction.NewCommand($@"SELECT ""PackageFamilyName"" FROM ""{tableItem}"" WHERE ""{column}"" = ""{value}""").ExecuteReader();
                                while (familyName.Read()) Console.WriteLine($"\r\nRemoving family {familyName.GetString(0)}\nfrom machine database...");

                                break;
                            case "Package":
                                var packageNameData = Program.ActiveTransaction.NewCommand($@"SELECT ""PackageFullName"" FROM ""{tableItem}"" WHERE ""{column}"" = ""{value}""").ExecuteReader();

                                while (packageNameData.Read()) {
                                    var packageName = packageNameData.GetString(0);
                                    
                                    var nameSlim = packageName.Remove(packageName.Split('_').First().Length);

                                    PackageFilterList.Add($"*{nameSlim}*");

                                    Console.WriteLine($"\r\nRemoving package {packageName}\nfrom machine database...");
                                }

                                break;
                            case "PackageLocation":
                                try
                                {
                                    var packageLocationData = Program.ActiveTransaction.NewCommand($@"SELECT ""InstalledLocation"" FROM ""{tableItem}"" WHERE ""{key}"" = ""{value}""").ExecuteReader();

                                    while (packageLocationData.Read()) {
                                        PackageDirectoryList.Add(packageLocationData.GetString(0));
                                    }
                                } catch (Exception) { }
                                break;
                            case "PackageExternalLocation":
                                try
                                {
                                    var packageExternLocationData = Program.ActiveTransaction.NewCommand($@"SELECT ""Path"" FROM ""{tableItem}"" WHERE ""{key}"" = ""{value}""").ExecuteReader();

                                    while (packageExternLocationData.Read()) {
                                        PackageDirectoryList.Add(packageExternLocationData.GetString(0));
                                    }
                                } catch (Exception) { }
                                break;
                            case "Application":
                                var appNameData = Program.ActiveTransaction.NewCommand($@"SELECT ""ApplicationUserModelID"" FROM ""{tableItem}"" WHERE ""{column}"" = ""{value}""").ExecuteReader();
                                while (appNameData.Read()) {
                                    var appName = appNameData.GetString(0);

                                    Console.WriteLine($"\r\nRemoving application {appName}\nfrom machine database...");
                                }

                                break;

                            case "AppUriHandler":
                            case "DynamicAppUriHandler":
                            case "FileTypeAssociation":
                            case "Protocol":
                                try {
                                    var progIdFetch = Program.ActiveTransaction.NewCommand($@"SELECT ""ProgID"" FROM ""{tableItem}"" WHERE ""{column}"" = ""{value}""");
                                    var progIdData = progIdFetch.ExecuteReader();

                                    while (progIdData.Read()) {
                                        if (!ApplicationProgIDList.Contains(progIdData.GetString(0))) ApplicationProgIDList.Add(progIdData.GetString(0));
                                    }
                                } catch (Exception e) {
                                    Console.WriteLine($"\r\nError: Could not get ProgID value from table {tableItem}.\r\nException: {e.Message}");
                                }

                                break;
                        }

                        var childKeyFetch = Program.ActiveTransaction.NewCommand($@"SELECT ""name"" FROM PRAGMA_table_info(""{tableItem}"") WHERE ""pk"" = 1");
                        var childKeyData = childKeyFetch.ExecuteReader();

                        while (childKeyData.Read()) {
                            var inputKey = childKeyData.GetString(0);

                            var newValueCheck = Program.ActiveTransaction.NewCommand($@"SELECT ""{inputKey}"" FROM ""{tableItem}"" WHERE ""{column}"" = ""{value}""");
                            var newValueData = newValueCheck.ExecuteReader();

                            while (newValueData.Read()) {
                                var inputValue = newValueData.GetString(0);
                                try {
                                    MachineData(inputKey, inputValue);
                                } catch (Exception e) {
                                    Console.WriteLine($"\r\nError: {e.Message}");
                                }
                            }
                        }

                        /*
                        if (tableItem == "SRHistory" | tableItem == "SRJournal" | tableItem == "SRJournalArchive") {
                            var getId = Program.ActiveTransaction.NewCommand($@"SELECT ""SequenceId"" FROM ""{tableItem}"" WHERE ""{column}"" = ""{value}""");
                            getId.ExecuteReader();
                            UPDATE my_table SET sort_colmn = sort_colmn + 1 WHERE sort_colmn >= 40;
                            INSERT INTO my_table (colmn, sort_colmn ) VALUES ('', 40);
                        }
                        */

                        if (Program.Verbose) Console.WriteLine($"\r\nRemoving {column} value {value}\nfrom table {tableItem}...");

                        try {
                            var eliminate = Program.ActiveTransaction.NewCommand($@"DELETE FROM ""{tableItem}"" WHERE ""{column}"" = ""{value}""");
                            eliminate.ExecuteNonQuery();
                        } catch (Exception e) {
                            throw new Exception($"Could not remove {column} value {value} from table {tableItem}.\r\nException: {e.Message}");
                        }
                    }
                }
            }

            if (!keyExist && "_ApplicationID|_PackageID|_PackageFamilyID".Contains(key)) {
                Console.WriteLine("\r\nNo foreign keys detected within database\nswitching to static method...");
                try {
                    switch (key) {
                        case "_ApplicationID":
                            ManualData(key, value, "Application");
                            return;
                        case "_PackageID":
                            ManualData(key, value, "Package");
                            return;
                        case "_PackageFamilyID":
                            ManualData(key, value, "PackageFamily");
                            return;
                    }
                } catch (Exception e) {
                    throw new Exception($"Could not remove requested data.\r\nException: {e.Message}");
                }
            }
        }

        public static void ManualData(string key, string value, string table, bool switchKeysOnly = false)
        {
            try {
                Program.ActiveTransaction.DisableForeignKeys();
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not disable foreign keys.\r\nException: {e.Message}");
            }

            //try {
            string[] subTables;
            string[] subValues;

            switch (table) {
                case "PackageFamily":
                    subTables = new[] { "PackageFamilyUser", "Package", "ConnectedSetPackageFamily", "DynamicAppUriHandlerGoup", "EndOfLifePackage", "PackageDependency", "PackageFamilyPolicy", "PackageFamilyUser", "PackageIdentity", "ProvisionedPackageExclude", "SRJournal", "SRJournalArchive" };
                    subValues = new[] { "PackageFamily", "PackageFamily", "PackageFamily", "PackageFamily", "PackageFamily", "PackageFamily", "PackageFamily", "PackageFamily", "PackageFamily", "PackageFamily", "PackageFamily", "PackageFamily" };

                    var familyName = Program.ActiveTransaction.NewCommand($@"SELECT ""PackageFamilyName"" FROM ""{table}"" WHERE ""{key}"" = ""{value}""").ExecuteReader();
                    while (familyName.Read()) Console.WriteLine($"\r\nRemoving family {familyName.GetString(0)}\nfrom machine database...");

                    break;
                case "Package":
                    subTables = new[] { "Bundle", "Resource", "TargetDeviceFamily", "PackageUser", "PackageLocation", "PackageExtension", "MrtPackage", "Dependency", "DependencyGraph", "Application", "AppxExtension", "CustomInstallWork", "MrtSharedPri", "MrtUserPri", "NamedDependency", "PackageExternalLocation", "PackagePolicy", "PackageProperty", "WowDependencyGraph", "XboxPackage" };
                    subValues = new[] { "Package", "Package", "Package", "Package", "Package", "Package", "Package", "DependentPackage", "DependentPackage", "Package", "Package", "Package", "Package", "Package", "Package", "Package", "Package", "Package", "DependentPackage", "Package" };

                    MachineDependentsData(value);
                    var packageNameData = Program.ActiveTransaction.NewCommand($@"SELECT ""PackageFullName"" FROM ""{table}"" WHERE ""{key}"" = ""{value}""").ExecuteReader();

                    while (packageNameData.Read()) {
                        var packageName = packageNameData.GetString(0);

                        var nameSlim = packageName.Remove(packageName.Split('_').First().Length);

                        PackageFilterList.Add($"*{nameSlim}*");

                        Console.WriteLine($"\r\nRemoving package {packageName}\nfrom machine database...");
                    }

                    break;
                case "PackageLocation":
                    try
                    {
                        var packageLocationData = Program.ActiveTransaction.NewCommand($@"SELECT ""InstalledLocation"" FROM ""{table}"" WHERE ""{key}"" = ""{value}""").ExecuteReader();

                        while (packageLocationData.Read()) {
                            PackageDirectoryList.Add(packageLocationData.GetString(0));
                        }
                    } catch (Exception) { }
                    return;
                case "PackageExternalLocation":
                    try
                    {
                        var packageExternLocationData = Program.ActiveTransaction.NewCommand($@"SELECT ""Path"" FROM ""{table}"" WHERE ""{key}"" = ""{value}""").ExecuteReader();

                        while (packageExternLocationData.Read()) {
                            PackageDirectoryList.Add(packageExternLocationData.GetString(0));
                        }
                    } catch (Exception) { }
                    return;
                case "Bundle":
                    subTables = new[] { "OptionalBundle" };
                    subValues = new[] { "MainBundle" };

                    try {
                        MachineBundleData("Bundle", value);
                    } catch (Exception e) {
                        throw new Exception($"Could not remove bundle data from table {table}.\r\nException: {e.Message}");
                    }

                    break;
                case "OptionalBundlePackage":
                    subTables = new[] { "OptionalBundleResource" };
                    subValues = new[] { "OptionalBundlePackage" };
                    break;
                case "PackageExtension":
                    subTables = new[] { "PublisherCacheFolder", "HostRuntime" };
                    subValues = new[] { "PackageExtension", "PackageExtension" };
                    break;
                case "PackageIdentity":
                    subTables = new[] { "DeploymentHistory", "PackageMachineStatus", "PackageSuperceded", "PackageUserStatus", "ProvisionedPackage", "ProvisionedPackageDeleted", "SRHistory" };
                    subValues = new[] { "PackageIdentity", "PackageIdentity", "PackageIdentity", "PackageIdentity", "PackageIdentity", "PackageIdentity", "SRHistory" };
                    break;
                case "Application":
                    subTables = new[] { "DefaultTile", "MrtApplication", "ApplicationExtension", "PrimaryTile", "ApplicationUser", "ApplicationContentUriRule", "ApplicationProperty" };
                    subValues = new[] { "Application", "Application", "Application", "Application", "Application", "Application", "Application" };

                    var appData = Program.ActiveTransaction.NewCommand($@"SELECT ""ApplicationUserModelID"",""Executable"" FROM ""{table}"" WHERE ""{key}"" = ""{value}""").ExecuteReader();
                    while (appData.Read()) {
                        var appName = appData.GetString(0);
                        AppSubNameList.Add(appName.Split('!').Last());

                        Console.WriteLine($"\r\nRemoving application {appName}\nfrom machine database...");

                        if (Program.TableList.Contains("ApplicationIdentity")) {
                            var identityName = Program.ActiveTransaction.NewCommand($@"SELECT ""_ApplicationIdentityID"" FROM ""ApplicationIdentity"" WHERE ""ApplicationUserModelID"" = ""{appName}""").ExecuteReader();
                            while (identityName.Read()) {
                                ManualData("_ApplicationIdentityID", identityName.GetString(0), "ApplicationIdentity");
                            }
                        }
                        
                        try { 
                            AppFiles.Add(appData.GetString(1));
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not get app executable file location data.\r\nException: {e.Message}");
                        }
                    }

                    break;
                case "DefaultTile":
                    subTables = new[] { "MrtDefaultTile" };
                    subValues = new[] { "DefaultTile" };
                    break;
                case "ApplicationExtension":
                    subTables = new[] { "ApplicationBackgroundTask", "FileTypeAssociation", "Protocol", "AppExecutionAlias", "AppExtension", "AppExtensionHost", "AppService", "AppUriHandler", "AppUriHandlerGroup" };
                    subValues = new[] { "Extension", "Extension", "Extension", "Extension", "Extension", "Extension", "Extension", "Extension", "Extension" };
                    break;
                case "ApplicationIdentity":
                    subTables = new[] { "PrimaryTileUser", "SecondaryTileUser" };
                    subValues = new[] { "ApplicationIdentity", "ApplicationIdentity" };
                    break;
                case "PackageFamilyUser":
                    subTables = new[] { "PackageFamilyUserResource" };
                    subValues = new[] { "PackageFamilyUser" };
                    break;

                // Deployment
                case "ContentGroup":
                    subTables = new[] { "ContentGroupFile" };
                    subValues = new[] { "ContentGroup" };
                    break;

                // ProgID Fetch
                case "AppUriHandler":
                case "DynamicAppUriHandler":
                case "FileTypeAssociation":
                case "Protocol":
                    try {
                        var progIdFetch = Program.ActiveTransaction.NewCommand($@"SELECT ""ProgID"" FROM ""{table}"" WHERE ""{key}"" = ""{value}""");
                        var progIdData = progIdFetch.ExecuteReader();

                        while (progIdData.Read()) {
                            if (!ApplicationProgIDList.Contains(progIdData.GetString(0))) ApplicationProgIDList.Add(progIdData.GetString(0));
                        }
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not get ProgID value from table {table}.\r\nException: {e.Message}");
                    }

                    return;

                default:
                    subTables = new string[0];
                    subValues = new string[0];
                    break;
            }

            var count = 0;
            foreach (var subTable in subTables.Where(subTable => Program.TableList.Contains(subTable))) {
                var subValue = subValues[count];
                count++;

                var childKeyFetch = Program.ActiveTransaction.NewCommand($@"SELECT ""name"" FROM PRAGMA_table_info(""{subTable}"") WHERE ""pk"" = 1");
                var childKeyData = childKeyFetch.ExecuteReader();

                while (childKeyData.Read()) {
                    var inputKey = childKeyData.GetString(0);

                    var newValueCheck = Program.ActiveTransaction.NewCommand($@"SELECT ""{inputKey}"" FROM ""{subTable}"" WHERE ""{subValue}"" = ""{value}""");
                    var newValueData = newValueCheck.ExecuteReader();

                    while (newValueData.Read()) {
                        var inputValue = newValueData.GetString(0);
                        try {
                            ManualData(inputKey, inputValue, subTable);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: {e.Message}");
                        }
                    }
                }
            }

            if (Program.Verbose) Console.WriteLine($"\r\nRemoving {key} value {value}\nfrom table {table}...");

            try {
                var eliminate = Program.ActiveTransaction.NewCommand($@"DELETE FROM ""{table}"" WHERE ""{key}"" = ""{value}""");
                eliminate.ExecuteNonQuery();
            } catch (Exception e) {
                throw new Exception($"Could not remove {key} value {value} from table {table}.\r\nException: {e.Message}");
            }
            /*
            } catch (Exception e) {
                Program.ActiveTransaction.EnableForeignKeys();
                throw new Exception(e.Message);
            }
            
            try {
                ActiveTransaction.EnableForeignKeys();
            } catch (Exception e) {
                try {
                    ActiveTransaction.Abort();
                    Triggers.Restore();
                } catch (Exception e2) {
                    Console.WriteLine($"\r\nError: {e2.Message}");
                }
                Console.WriteLine($@"\r\nError: Could not re-enable foreign keys.\r\nException: {e.Message}");
                goto deployEnd;
            }
            */
        }

        private static void MachineDependentsData(string id)
        {
            if (Program.TableList.Contains("DependencyGraph")) {
                var dependentsFetch = Program.ActiveTransaction.NewCommand($@"SELECT ""DependentPackage"" FROM ""DependencyGraph"" WHERE ""SupplierPackage"" = ""{id}""");
                var dependentsData = dependentsFetch.ExecuteReader();

                while (dependentsData.Read()) {
                    var inputValue = dependentsData.GetString(0);

                    var nameFetch = Program.ActiveTransaction.NewCommand($@"SELECT ""PackageFullName"" FROM ""Package"" WHERE ""_PackageID"" = ""{inputValue}""");
                    var nameData = nameFetch.ExecuteReader();

                    while (nameData.Read()) {
                        var packageName = nameData.GetString(0);

                        try {
                            var nameSlim = packageName.Remove(packageName.Split('_').First().Length);

                            PackageFilterList.Add($"*{nameSlim}*");

                            var filterNameFetch = Program.ActiveTransaction.NewCommand($@"SELECT ""_PackageID"",""PackageFullName"" FROM ""Package"" WHERE ""PackageFullName"" LIKE ""%{nameSlim}%""");
                            var filterNameData = filterNameFetch.ExecuteReader();

                            while (filterNameData.Read()) {
                                var pkgId = filterNameData.GetString(0);
                                var pkgName = filterNameData.GetString(1);

                                if (!Program.PackageIdList.Contains(pkgId)) {
                                    Program.PackageIdList.Add(pkgId);
                                    Console.WriteLine($"\r\nRemoving dependent package {pkgName}\nfrom machine database...");
                                    MachineData("_PackageID", pkgId);
                                }
                            }
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove dependent packages.\r\nException: {e.Message}");
                        }
                    }
                }
            }
        }

        private static void MachineBundleData(string column, string id)
        {
            var bundleRange = Program.ActiveTransaction.NewCommand($@"SELECT MIN(_BundlePackageID), MAX(_BundlePackageID) FROM ""BundlePackage"" WHERE ""{column}"" = ""{id}""");
            var bundleRangeData = bundleRange.ExecuteReader();

            while (bundleRangeData.Read()) {
                if (Program.Verbose) Console.WriteLine($"\r\nRemoving bundle data for bundle ID {id}...");

                var bundleDel = Program.ActiveTransaction.NewCommand($@"DELETE FROM ""BundleResource"" WHERE ""BundlePackage"" BETWEEN ""{bundleRangeData.GetString(0)}"" AND ""{bundleRangeData.GetString(1)}""; DELETE FROM ""BundlePackage"" WHERE Bundle = ""{id}""; DELETE FROM Bundle WHERE _BundleID = ""{id}""");
                bundleDel.ExecuteNonQuery();
            }
        }

        public static void ClearCache(string filter)
        {
            var dirList = new List<string>();
            try { dirList.AddRange(Directory.EnumerateDirectories($@"{Environment.GetEnvironmentVariable("PROGRAMFILES")}\WindowsApps", filter)); } catch (Exception e) { }
            try { dirList.AddRange(Directory.EnumerateDirectories($@"{Environment.GetEnvironmentVariable("WINDIR")}\SystemApps", filter)); } catch (Exception e) { }

            foreach (var userDir in Directory.EnumerateDirectories($@"{Environment.GetEnvironmentVariable("SYSTEMDRIVE")}\Users")) {
                try { dirList.AddRange(Directory.EnumerateDirectories($@"{userDir}\AppData\Local\Microsoft\WindowsApps", filter)); } catch (Exception e) { }
            }

            foreach (var exeDir in dirList) {
                try {
                    foreach (var executable in Directory.EnumerateFiles(exeDir, "*.exe")) {
                        var exeProc = Path.GetFileNameWithoutExtension(executable);
                        foreach (var process in Process.GetProcessesByName(exeProc).Where(w => w.MainModule.FileName.Contains(exeDir))) {
                            KillProcess(process);
                        }
                    }
                } catch (Exception e) {
                    throw new Exception($"Could not kill cache package process.\r\nException: {e.Message}");
                }
            }

            foreach (var userDir in Directory.EnumerateDirectories($@"{Environment.GetEnvironmentVariable("SYSTEMDRIVE")}\Users")) {
                if (Directory.Exists($@"{userDir}\AppData\Local\Packages")) {
                    foreach (var item in Directory.EnumerateDirectories($@"{userDir}\AppData\Local\Packages", filter)) {
                        try {
                            foreach (var executable in Directory.EnumerateFiles(item, "*.exe")) {
                                var exeProc = Path.GetFileNameWithoutExtension(executable);
                                foreach (var process in Process.GetProcessesByName(exeProc).Where(w => w.MainModule.FileName.Contains(item))) {
                                    KillProcess(process);
                                }
                            }
                        } catch (Exception e) {
                            throw new Exception($"Could not kill cache package process.\r\nException: {e.Message}");
                        }

                        FilterDelete($@"{item}\TempState", "*");
                        //FilterDelete($@"{item}\AppData", "*");
                        if (Directory.Exists($@"{item}\LocalState")) {
                            foreach (var cache in Directory.EnumerateDirectories($@"{item}\LocalState", "*Cache*")) {
                                FilterDelete(cache, "*", "SettingsCache.txt");
                            }
                        }
                    }
                }
            }
        }

        public static void KillProcess(Process process)
        {
            if (process.ProcessName == "System" || process.ProcessName == "Registry") return;
            try
            {
                new TaskKillAction()
                { ProcessName = process.ProcessName, ProcessID = process.Id }.RunTask();
            } catch (Exception e)
            {
                Console.WriteLine($"\r\nError: Could not kill process {process.ProcessName}.\r\nException: {e.Message}");
            }
        }

        public static void Files(string filter, bool app = false)
        {
            if (app) {
                foreach (var subFilter in AppSubNameList.Where(w => !w.Equals("App"))) AppFiles.Add(subFilter + "*");

                var xmlFiles = new List<string>();

                foreach (var packageDir in Program.ApplicationPackageDirList) {
                    foreach (var item in AppFiles) {
                        if (Directory.Exists($@"{packageDir}\{item}")) {
                            FilterDelete(packageDir, $"{item}");
                        } else if (File.Exists($@"{packageDir}\{item}")) {
                            var itemExt = item.Split('.').Last();
                            
                            if (itemExt == "exe") {
                                var exeFilter = item.Remove(item.Length - itemExt.Length).Split('\\').LastOrDefault() + "*";
                                FilterDelete($@"{packageDir}", exeFilter);
                                foreach (var userDir in Directory.EnumerateDirectories($@"{Environment.GetEnvironmentVariable("SYSTEMDRIVE")}\Users")) {
                                    var rmFileCheck = new List<string>();
                                    
                                    try {
                                        rmFileCheck.Add($@"{userDir}\AppData\Local\Microsoft\WindowsApps");
                                        foreach (var WinApps in Directory.EnumerateDirectories($@"{userDir}\AppData\Local\Microsoft\WindowsApps", "*" + packageDir.Split('\\').LastOrDefault() + "*")) {
                                            rmFileCheck.Add(WinApps);
                                        }
                                    } catch (Exception) { }

                                    foreach (var check in rmFileCheck) {
                                        if (!Directory.Exists(check)) continue;
                                        Directory.EnumerateFiles(check, exeFilter).ToList().ForEach(x => FilterDelete(x, "*"));
                                    }
                                    
                                }
                            } else {
                                FilterDelete($@"{packageDir}", $"{item}");
                            }
                        }
                    }

                    xmlFiles.Add(Directory.EnumerateFiles(packageDir, "AppxManifest.xml").FirstOrDefault());
                }

                foreach (var package in Program.ApplicationPackageList) xmlFiles.Add(Directory.EnumerateFiles($@"{Environment.GetEnvironmentVariable("PROGRAMDATA")}\Microsoft\Windows\AppRepository", "*.xml").Where(f => f.Contains(package)).FirstOrDefault());

                foreach (var xmlFile in xmlFiles) {
                    try {
                        var xml = new XmlDocument();
                        xml.Load(xmlFile);

                        foreach (var appName in AppSubNameList) {
                            var appsData = xml.GetElementsByTagName("/Package/Applications");

                            foreach (var apper in appsData) {
                            }

                            var appData = xml.SelectSingleNode($"//*[@Id='{appName}']");
                            try {
                                try {
                                    Console.WriteLine($"\r\nRemoving application xml with Id {appName} from file {xmlFile}...");
                                    appData.ParentNode.RemoveChild(appData);
                                } catch (NullReferenceException) {
                                }
                            } catch (Exception e) {
                                Console.WriteLine($"\r\nError: Could not remove {appName} from xml document {xmlFile}.\r\nException: {e.Message}");
                            }
                        }

                        xml.Save(xmlFile);

                        foreach (var appItem in AppFiles) {
                            var nameSpc = new XmlNamespaceManager(xml.NameTable);
                            nameSpc.AddNamespace("spc", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");

                            var appExts = xml.SelectNodes($"/*/spc:Extensions[1]/*/*/*[starts-with(text(), '{appItem}')]", nameSpc);
                            try {
                                try {
                                    foreach (XmlNode appExt in appExts) {
                                        var exts = appExt.ParentNode.ParentNode.ParentNode;
                                        var ext = appExt.ParentNode.ParentNode;

                                        Console.WriteLine($"\r\nRemoving xml extension item with element {appExt.Name} {appExt.InnerText} from file {xmlFile}...");
                                        exts.RemoveChild(ext);
                                    }
                                } catch (NullReferenceException) {
                                }
                            } catch (Exception e) {
                                Console.WriteLine($"\r\nError: Could not remove {appItem} extension data from xml document {xmlFile}.\r\nException: {e.Message}");
                            }

                            xml.Save(xmlFile);
                        }
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not fetch application package xml values.\r\nException:{e.Message}");
                    }
                }

                return;
            }
            
           

            try {
                foreach (var item in Directory.EnumerateDirectories($@"{Environment.GetEnvironmentVariable("SYSTEMDRIVE")}\Users")) {
                    FilterDelete($@"{item}\AppData\Local\Packages", filter);
                    FilterDelete($@"{item}\AppData\Local\Microsoft\WindowsApps", filter);

                    foreach (var appItem in AppFiles) {
                        
                        if (!Directory.Exists($@"{item}\AppData\Local\Microsoft\WindowsApps")) continue;
                        
                        var itemExt = appItem.Split('.').Last();
                        if (itemExt == "exe") {
                            var exeFilter = appItem.Remove(appItem.Length - itemExt.Length).Split('\\').LastOrDefault() + "*";
                            foreach (var file in Directory.EnumerateFiles($@"{item}\AppData\Local\Microsoft\WindowsApps", exeFilter)) {
                                FilterDelete($@"{item}\AppData\Local\Microsoft\WindowsApps", file.Split('\\').LastOrDefault());
                            }
                        }
                    }
                }
            } catch (Exception e) { Console.WriteLine($"\r\nError: Could not remove user data of specified package or family.\r\nException: {e.Message}"); }

            FilterDelete($@"{Environment.GetEnvironmentVariable("PROGRAMFILES")}\WindowsApps", filter);
            FilterDelete($@"{Environment.GetEnvironmentVariable("WINDIR")}\SystemApps", filter);
            FilterDelete($@"{Environment.GetEnvironmentVariable("PROGRAMDATA")}\Packages", filter);
            FilterDelete($@"{Environment.GetEnvironmentVariable("PROGRAMDATA")}\Microsoft\Windows\AppRepository", filter);
            FilterDelete($@"{Environment.GetEnvironmentVariable("PROGRAMDATA")}\Microsoft\Windows\AppRepository\Packages", filter);
        }

        public static void FilterDelete(string directory, string filter, string exclude = null)
        {
            if (Directory.Exists(directory)) {
                foreach (var item in Directory.EnumerateFiles(directory, filter))
                {
                    if (exclude != null && item.EndsWith(exclude))
                    {
                        continue;
                    }

                    try
                    {
                        Console.WriteLine($"\r\nRemoving file {item}...");
                        File.Delete(item);
                    } catch (Exception e)
                    {
                        Console.WriteLine($"\r\nError: Could not delete file {item}.\r\nException: {e.Message}\n\nAttempting to kill any locking processes...");

                        var processes = new List<Process>();
                        try
                        {
                            processes = FileLock.WhoIsLocking(item);
                        } catch (Exception e2)
                        {
                            Console.WriteLine($"\r\nError: Could not check file locks on file.\r\nException: {e2.Message}");
                        }

                        foreach (var process in processes)
                        {
                            KillProcess(process);
                        }

                        try
                        {
                            Console.WriteLine($"\r\nRetry: Removing file {item}...");
                            File.Delete(item);
                        } catch (Exception e3)
                        {
                            Console.WriteLine($"\r\nError: Could not delete file {item}.\r\nException: {e3.Message}");
                        }
                    }
                }

                foreach (var item in Directory.EnumerateDirectories(directory, filter))
                {
                    if (exclude != null && item.EndsWith(exclude))
                    {
                        continue;
                    }

                    try
                    {
                        Console.WriteLine($"\r\nRemoving folder {item}...");
                        Directory.Delete(item, true);
                    } catch (Exception e)
                    {
                        Console.WriteLine($"\r\nError: Could not delete folder {item}.\r\nException: {e.Message}");
                        FilterDelete(item, "*");
                        try
                        {
                            Console.WriteLine($"\r\nRetry: Removing folder {item}...");
                            Directory.Delete(item, true);
                        } catch (Exception e5)
                        {
                            Console.WriteLine($"\r\nError: Could not delete folder {item}.\r\nException: {e5.Message}");
                        }
                    }
                }
            } else if (!directory.Contains(@"\Users\")) {
                Console.WriteLine($"\r\nDirectory {directory} does not exist, skipping...");
            }
        }

        public static void RegistryKeys(string filter, bool app = false, bool family = false)
        {
            filter = filter.Replace("*", ":AINV:");
            filter = Regex.Escape(filter);
            filter = filter.Replace(":AINV:", ".*");

            if (family) goto familyStart;

            try {
                var appxKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore");
                foreach (var key in appxKey.GetSubKeyNames()) {
                    try {
                        var subKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\{key}", true);
                        foreach (var subSubKey in subKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                            Console.WriteLine($"\r\nRemoving registry key HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Appx\\AppxAllUserStore\\{key}\\{subSubKey}...");
                            try {
                                subKey.DeleteSubKeyTree(subSubKey);
                            } catch (Exception e) {
                                Console.WriteLine($"\r\nError: Could not remove registry key {subSubKey}.\r\nException: {e.Message}");
                            }
                        }
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not open key HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Appx\\AppxAllUserStore\\{key}.\r\nException: {e.Message}");
                    }
                }
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not open key HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Appx\\AppxAllUserStore.\r\nException: {e.Message}");
            }

            if (app) {
                foreach (var userKeyItem in Registry.Users.GetSubKeyNames().Where(w => w.Contains("Classes") && !w.Contains("_Default"))) {
                    var userKey = Registry.Users.OpenSubKey(userKeyItem);

                    foreach (var packageName in Program.ApplicationPackageList) {
                        foreach (var appSubName in AppSubNameList) {
                            try {
                                var contractKey = userKey.OpenSubKey($@"Extensions\ContractId", true);
                                foreach (var type in contractKey.GetSubKeyNames()) {
                                    try {
                                        foreach (var contractPackage in contractKey.OpenSubKey($@"{type}\PackageId").GetSubKeyNames().Where(w => w.Equals(packageName))) {
                                            try {
                                                if (contractKey.OpenSubKey(appSubName) == null) throw new Exception();
                                                Console.WriteLine($"\r\nRemoving registry key HKU\\{userKeyItem}\\Extensions\\ContractId\\{type}\\PackageId\\{packageName}\\ActivatableClassId\\{appSubName}...");
                                                contractKey.DeleteSubKeyTree($@"{type}\PackageId\{contractPackage}\ActivatableClassId\{appSubName}");
                                            } catch {
                                                if (Program.Verbose) Console.WriteLine($"\r\nInfo: Key HKU\\{userKeyItem}\\Extensions\\ContractId\\{type}\\PackageId\\{packageName}\\ActivatableClassId\\{appSubName}\ndoes not exist.");
                                            }
                                        }
                                    } catch {
                                        if (Program.Verbose) Console.WriteLine($"\r\nInfo: Key HKU\\{userKeyItem}\\Extensions\\ContractId\\{type}\\PackageId\ndoes not exist.");
                                    }
                                }
                            } catch (Exception e) {
                                Console.WriteLine($"Could not open key HKU\\{userKeyItem}\\Extensions\\ContractId\\.\r\nException: {e.Message}");
                            }

                            try {
                                var repoKey = userKey.OpenSubKey($@"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\{packageName}", true);
                                try {
                                    if (repoKey.OpenSubKey(appSubName) == null) throw new Exception();
                                    Console.WriteLine($"\r\nRemoving registry key HKU\\{userKeyItem}\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages\\{packageName}\\{appSubName}...");
                                    repoKey.DeleteSubKeyTree(appSubName);
                                } catch {
                                    if (Program.Verbose) Console.WriteLine($"\r\nInfo: Key HKU\\{userKeyItem}\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages\\{packageName}\\{appSubName}\ndoes not exist.");
                                }
                            } catch {
                                if (Program.Verbose) Console.WriteLine($"\r\nInfo: Key HKU\\{userKeyItem}\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages\\{packageName}\ndoes not exist.");
                            }
                        }
                    }
                }

                return;
            }

            try {
                var stateKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\PackageState");
                foreach (var key in stateKey.GetSubKeyNames()) {
                    try {
                        var subKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\PackageState\{key}", true);
                        foreach (var subSubKey in subKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                            Console.WriteLine($"\r\nRemoving registry key HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Appx\\PackageState\\{key}\\{subSubKey}...");
                            try {
                                subKey.DeleteSubKeyTree(subSubKey);
                            } catch (Exception e) {
                                Console.WriteLine($"Could not remove registry key {subSubKey}.\r\nException: {e.Message}");
                            }
                        }
                    } catch (Exception e) {
                        Console.WriteLine($"Could not open key HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Appx\\PackageState\\{key}.\r\nException: {e.Message}");
                    }
                }
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not open key HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Appx\\PackageState.\r\nException: {e.Message}");
            }

            familyStart:
            foreach (var userKeyItem in Registry.Users.GetSubKeyNames().Where(w => w.Contains("Classes") && !w.Contains("_Default"))) {
                var userKey = Registry.Users.OpenSubKey(userKeyItem, writable: true);
                var curVerKey = userKey.OpenSubKey(@"Local Settings\Software\Microsoft\Windows\CurrentVersion");

                if (family) {
                    try {
                        var familiesKey = curVerKey.OpenSubKey($@"AppModel\Repository\Families", true);
                        foreach (var familyKey in familiesKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                            Console.WriteLine($"\r\nRemoving registry key HKU\\{userKeyItem}\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Families\\{familyKey}...");
                            try {
                                familiesKey.DeleteSubKeyTree(familyKey);
                            } catch (Exception e) {
                                Console.WriteLine($"\r\nError: Could not remove registry key {familyKey}.\r\nException: {e.Message}");
                            }
                        }
                    } catch (Exception e) {
                        //Console.WriteLine($"\r\nError: Could not open families subkey in key HKU\\{userKeyItem}\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository.\r\nException: {e.Message}");
                    }

                    continue;
                }

                foreach (var userItem in userKey.GetSubKeyNames().Where(w => ApplicationProgIDList.Contains(w))) {
                    try {
                        userKey.DeleteSubKeyTree(userItem);
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not remove ProgID key {userItem} in user key {userKey}.\r\nException: {e.Message}");
                    }
                }

                try {
                    var contractKey = userKey.OpenSubKey($@"Extensions\ContractId", true);
                    foreach (var type in contractKey.GetSubKeyNames()) {
                        try {
                            foreach (var contractPackage in contractKey.OpenSubKey($@"{type}\PackageId").GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                                Console.WriteLine($"\r\nRemoving registry key HKU\\{userKeyItem}\\Extensions\\ContractId\\{type}\\PackageId\\{contractPackage}...");
                                try {
                                    contractKey.DeleteSubKeyTree($@"{type}\PackageId\{contractPackage}");
                                } catch (Exception e) {
                                    Console.WriteLine($"\r\nError: Could not remove registry key {contractPackage}.\r\nException: {e.Message}");
                                }
                            }
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not open contract subkey {type}\\PackageId in {userKeyItem}.\r\nException: {e.Message}");
                        }
                    }
                } catch (Exception e) {
                    Console.WriteLine($"Could not open key HKU\\{userKeyItem}\\Extensions\\ContractId\\Windows.Launch\\PackageId.\r\nException: {e.Message}");
                }

                try {
                    var mrtKey = userKey.OpenSubKey($@"Local Settings\MrtCache", true);
                    foreach (var mrtSubKey in mrtKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                        Console.WriteLine($"\r\nRemoving registry key HKU\\{userKeyItem}\\Local Settings\\MrtCache\\{mrtSubKey}...");
                        try {
                            mrtKey.DeleteSubKeyTree(mrtSubKey);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove registry key {mrtSubKey}.\r\nException: {e.Message}");
                        }
                    }

                    var mappingsKey = curVerKey.OpenSubKey($@"AppContainer\Mappings", true);
                    foreach (var mapKey in mappingsKey.GetSubKeyNames().Where(w => Regex.Match(mappingsKey.OpenSubKey(w).GetValue("Moniker").ToString(), filter, RegexOptions.IgnoreCase).Success)) {
                        Console.WriteLine($"\r\nRemoving registry key HKU\\{userKeyItem}\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppContainer\\Mappings\\{mapKey}...");
                        try {
                            mappingsKey.DeleteSubKeyTree(mapKey);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove registry key {mapKey}.\r\nException: {e.Message}");
                        }
                    }
                } catch (Exception e) {
                    Console.WriteLine($"\r\nError: Could not open MrtCache or Mappings key.\r\nException: {e.Message}");
                }

                try {
                    var storageKey = curVerKey.OpenSubKey($@"AppContainer\Storage", true);
                    foreach (var storageSubKey in storageKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                        Console.WriteLine($"\r\nRemoving registry key HKU\\{userKeyItem}\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppContainer\\Storage\\{storageSubKey}...");
                        try {
                            storageKey.DeleteSubKeyTree(storageSubKey);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove registry key {storageSubKey}.\r\nException: {e.Message}");
                        }
                    }

                    var policyKey = curVerKey.OpenSubKey($@"AppModel\PolicyCache", true);
                    foreach (var policySubKey in policyKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                        Console.WriteLine($"\r\nRemoving registry key HKU\\{userKeyItem}\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\PolicyCache\\{policySubKey}...");
                        try {
                            policyKey.DeleteSubKeyTree(policySubKey);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove registry key {policySubKey}.\r\nException: {e.Message}");
                        }
                    }

                    var appDataKey = curVerKey.OpenSubKey($@"AppModel\SystemAppData", true);
                    foreach (var appDataSubKey in appDataKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                        Console.WriteLine($"\r\nRemoving registry key HKU\\{userKeyItem}\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\SystemAppData\\{appDataSubKey}...");
                        try {
                            appDataKey.DeleteSubKeyTree(appDataSubKey);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove registry key {appDataSubKey}.\r\nException: {e.Message}");
                        }
                    }
                } catch (Exception e) {
                    Console.WriteLine($"\r\nError: Could not open Storage, PolicyCache, or SystemAppData key.\r\nException: {e.Message}");
                }

                try {
                    var packagesKey = curVerKey.OpenSubKey($@"AppModel\Repository\Packages", true);
                    foreach (var packageKey in packagesKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                        Console.WriteLine($"\r\nRemoving registry key HKU\\{userKeyItem}\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages\\{packageKey}...");
                        try {
                            packagesKey.DeleteSubKeyTree(packageKey);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove registry key {packageKey}.\r\nException: {e.Message}");
                        }
                    }
                } catch (Exception e) {
                    Console.WriteLine($"\r\nError: Could not open packages subkey in key HKU\\{userKeyItem}\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository.\r\nException: {e.Message}");
                }
            }

            var rootCurVerKey = Registry.ClassesRoot.OpenSubKey(@"Local Settings\Software\Microsoft\Windows\CurrentVersion");

            if (family) {
                try {
                    var familiesKey = rootCurVerKey.OpenSubKey($@"AppModel\Repository\Families", true);
                    foreach (var familyKey in familiesKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                        Console.WriteLine($"\r\nRemoving registry key HKCR\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Families\\{familyKey}...");
                        try {
                            familiesKey.DeleteSubKeyTree(familyKey);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove registry key {familyKey}.\r\nException: {e.Message}");
                        }
                    }
                } catch (Exception e) {
                    Console.WriteLine($"\r\nError: Could not open families subkey in key HKCR\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository.\r\nException: {e.Message}");
                }

                return;
            }

            try {
                var progIdsKey = rootCurVerKey.OpenSubKey($@"AppModel\PackageRepository\Extensions\ProgIDs", true);
                foreach (var progId in ApplicationProgIDList) {
                    foreach (var progIdKey in progIdsKey.GetSubKeyNames().Where(w => w.Equals(progId))) {
                        Console.WriteLine($"\r\nRemoving registry key HKCR\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\PackageRepository\\Extensions\\ProgIDs\\{progIdKey}...");
                        try {
                            progIdsKey.DeleteSubKeyTree(progIdKey);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove registry key {progIdKey}.\r\nException: {e.Message}");
                        }
                    }
                }
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not open ProgIDs subkey in HKCR\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\PackageRepository. Exception: {e.Message}");
            }

            try {
                var packagesKey = rootCurVerKey.OpenSubKey($@"AppModel\PackageRepository\Packages", true);
                foreach (var packageKey in packagesKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                    Console.WriteLine($"\r\nRemoving registry key HKCR\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages\\{packageKey}...");
                    try {
                        packagesKey.DeleteSubKeyTree(packageKey);
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not remove registry key {packageKey}.\r\nException: {e.Message}");
                    }
                }
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not open packages subkey in key HKCR\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\PackageRepository.\r\nException: {e.Message}");
            }

            try {
                var appDataKey = rootCurVerKey.OpenSubKey($@"AppModel\SystemAppData", true);
                foreach (var appDataSubKey in appDataKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                    Console.WriteLine($"\r\nRemoving registry key HKCR\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\SystemAppData\\{appDataSubKey}...");
                    try {
                        appDataKey.DeleteSubKeyTree(appDataSubKey);
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not remove registry key {appDataSubKey}.\r\nException: {e.Message}");
                    }
                }
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not open SystemAppData subkey in key HKCR\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository.\r\nException: {e.Message}");
            }

            try {
                var packagesKey = rootCurVerKey.OpenSubKey($@"AppModel\Repository\Packages", true);
                foreach (var packageKey in packagesKey.GetSubKeyNames().Where(w => Regex.Match(w, filter, RegexOptions.IgnoreCase).Success)) {
                    Console.WriteLine($"\r\nRemoving registry key HKCR\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages\\{packageKey}...");
                    try {
                        packagesKey.DeleteSubKeyTree(packageKey);
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not remove registry key {packageKey}.\r\nException: {e.Message}");
                    }
                }
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not open packages subkey in key HKCR\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository.\r\nException: {e.Message}");
            }

            try {
                var mappingsKey = rootCurVerKey.OpenSubKey($@"AppModel\Mappings", true);
                foreach (var mapKey in mappingsKey.GetSubKeyNames().Where(w => Regex.Match(mappingsKey.OpenSubKey(w).GetValue("Moniker").ToString(), filter, RegexOptions.IgnoreCase).Success)) {
                    Console.WriteLine($"\r\nRemoving registry key HKCR\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Mappings\\{mapKey}...");
                    try {
                        mappingsKey.DeleteSubKeyTree(mapKey);
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not remove registry key {mapKey}.\r\nException: {e.Message}");
                    }
                }
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not open HKCR Mappings key.\r\nException: {e.Message}");
            }
        }
    }

    internal static class Program
    {
        public static string ProcessHacker;
        public static bool Verbose;
        public static Transaction ActiveTransaction;
        public static List<string> TableList;
        public static List<string> PackageIdList;
        public static List<string> ApplicationPackageList;
        public static List<string> ApplicationPackageDirList;

        private static void Main(string[] args)
        {
            const string ver = "0.4";
            Verbose = false;

            if (!string.Equals(WindowsIdentity.GetCurrent().User.Value, "S-1-5-18", StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine("\r\nYou must be TrustedInstaller in order to use AME Assassin.");
                Environment.Exit(1);
            }

            if (args.Length == 0 || args[0] == "/?" || args[0] == "-?" || args[0] == "/help" || args[0] == "-help" || args[0] == "--?" || args[0] == "--help") {
                Console.WriteLine($"\r\nAME Assassin v{ver}\nSurgically removes APPX components (mostly).\n\nAME_Assassin [-Family|-Package|-App|-ClearCache] <string> [Optional Arguments]\nAccepts wildcards (*).\n\n-Family        Removes specified package family(s).\n-Package       Removes specified package(s).\n-App           Removes specified application(s) from a package(s).\n-ClearCache    Clears the TempState cache for a given package in all user profiles.\n-Verbose       Provides verbose informational output to console.\n-Unregister    Only unregisters the specified AppX, instead of removing files.\n\nExamples:\n\n    AME_Assassin -Family \"Microsoft.BingWeather_8wekyb3d8bbwe\"\r\n    AME_Assassin -Package *FeedbackHub* -Verbose\n    AME_Assassin -App *WebExperienceHost*");
                Environment.Exit(0);
            }
            
            var selfDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (File.Exists($@"{selfDir}\ProcessHacker\x64\ProcessHacker.exe")) {
                ProcessHacker = $@"""{selfDir}\ProcessHacker\x64\ProcessHacker.exe""";
            } else if (File.Exists($@"{Directory.GetParent(selfDir)}\ProcessHacker\x64\ProcessHacker.exe")) {
                ProcessHacker = $@"""{Directory.GetParent(selfDir)}\ProcessHacker\x64\ProcessHacker.exe""";
            } else {
                Console.WriteLine("\r\nProcessHacker executable not detected.");
                Environment.Exit(0);
            }

            if (args.Length > 0 && args[0].Equals("-SystemPackage", StringComparison.OrdinalIgnoreCase))
            {
                SystemPackage.Start(args);
                RegistryManager.UnhookUserHives();
                RegistryManager.UnhookComponentsHive();
                Environment.Exit(0);
            }

            if (args.Length != 2 && args.Length != 3) {
                Console.WriteLine($"Invalid syntax.\n\nAME Assassin v{ver}\nSurgically removes APPX components (mostly).\n\nAME_Assassin [-Family|-Package|-App|-ClearCache] <string> [Optional Arguments]\nAccepts wildcards (*).\n\n-Family        Removes specified package family(s).\n-Package       Removes specified package(s).\n-App           Removes specified application(s) from a package(s).\n-ClearCache    Clears the TempState cache for a given package in all user profiles.\n-Verbose       Provides verbose informational output to console.\n\nExamples:\n\n    AME_Assassin -Family \"Microsoft.BingWeather_8wekyb3d8bbwe\"\r\n    AME_Assassin -Package *FeedbackHub* -Verbose\n    AME_Assassin -App *WebExperienceHost*");
                Environment.Exit(1);
            } else if (!string.Equals(args[0], "-App", StringComparison.CurrentCultureIgnoreCase) && !string.Equals(args[0], "-Package", StringComparison.CurrentCultureIgnoreCase) && !string.Equals(args[0], "-Family", StringComparison.CurrentCultureIgnoreCase) && !string.Equals(args[0], "-ClearCache", StringComparison.CurrentCultureIgnoreCase)) {
                Console.WriteLine($"Invalid syntax.\n\nAME Assassin v{ver}\nSurgically removes APPX components (mostly).\n\nAME_Assassin [-Family|-Package|-App|-ClearCache] <string> [Optional Arguments]\nAccepts wildcards (*).\n\n-Family        Removes specified package family(s).\n-Package       Removes specified package(s).\n-App           Removes specified application(s) from a package(s).\n-ClearCache    Clears the TempState cache for a given package in all user profiles.\n-Verbose       Provides verbose informational output to console.\n\nExamples:\n\n    AME_Assassin -Family \"Microsoft.BingWeather_8wekyb3d8bbwe\"\r\n    AME_Assassin -Package *FeedbackHub* -Verbose\n    AME_Assassin -App *WebExperienceHost*");
                Environment.Exit(1);
            }

            if (args.Length == 3 && !string.Equals(args[2], "-Verbose", StringComparison.CurrentCultureIgnoreCase)) {
                Console.WriteLine($"Invalid syntax.\n\nAME Assassin v{ver}\nSurgically removes APPX components (mostly).\n\nAME_Assassin [-Family|-Package|-App|-ClearCache] <string> [Optional Arguments]\nAccepts wildcards (*).\n\n-Family        Removes specified package family(s).\n-Package       Removes specified package(s).\n-App           Removes specified application(s) from a package(s).\n-ClearCache    Clears the TempState cache for a given package in all user profiles.\n-Verbose       Provides verbose informational output to console.\n\nExamples:\n\n    AME_Assassin -Family \"Microsoft.BingWeather_8wekyb3d8bbwe\"\r\n    AME_Assassin -Package *FeedbackHub* -Verbose\n    AME_Assassin -App *WebExperienceHost*");
                Environment.Exit(1);
            } else if (args.Length == 3) {
                Verbose = true;
            }

            if (string.Equals(args[0], "-ClearCache", StringComparison.CurrentCultureIgnoreCase)) {
                try {
                    Assassin.ClearCache(args[1]);
                } catch (Exception e) {
                    Console.WriteLine($"\r\nError: {e.Message}");
                    Environment.Exit(10);
                }

                Console.WriteLine("\r\nComplete!");
                Environment.Exit(0);
            }

            /*
            try {
                Assassin.StopService("AppXSvc");
                Assassin.StopService("StateRepository");
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not kill Appx services.\r\nException: {e.Message}");
            }
            */

            try {
                //var selfDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                Hook($@"{Environment.GetEnvironmentVariable("PROGRAMDATA")}\Microsoft\Windows\AppRepository\StateRepository-Machine.srd");
            } catch (Exception e) {
                Console.WriteLine($"\r\nFatal Error: Could not connect to machine database.\r\nException: {e.Message}");
                Environment.Exit(5);
            }

            try {
                Console.WriteLine("\r\nDropping triggers...");
                Triggers.Save();
                Triggers.Drop();
            } catch (Exception e) {
                Console.WriteLine($"\r\nFatal Error: {e.Message}");
                Environment.Exit(5);
            }

            Console.WriteLine("\r\nFetching values...");

            SqliteDataReader content = null;
            try {
                ActiveTransaction = new Transaction(@"SELECT ""Name"" FROM main.sqlite_master WHERE type = ""table""");
                TableList = new List<string>();
                content = ActiveTransaction.Command.ExecuteReader();
                while (content.Read()) {
                    var tableAdd = content.GetString(0);
                    TableList.Add(tableAdd);
                }
            } catch (Exception e) {
                try {
                    ActiveTransaction.Abort();
                    Triggers.Restore();
                } catch (Exception e2) {
                    Console.WriteLine($"\r\nFatal Error: {e2.Message}");
                }

                Console.WriteLine($"\r\nFatal Error: Could not fetch tables from database.\r\nException: {e.Message}");
            }

            ActiveTransaction.Commit();

            string table = null;
            string matchColumn = null;
            string displayName = null;
            Assassin.PackageFilterList = new List<string>();
            Assassin.AppSubNameList = new List<string>();
            Assassin.ApplicationProgIDList = new List<string>();

            switch (args[0].ToLower()) {
                case "-family":
                    Assassin.PackageFilterList.Add(args[1]);
                    table = "PackageFamily";
                    matchColumn = "PackageFamilyName";
                    displayName = "family";
                    break;
                case "-package":
                    Assassin.PackageFilterList.Add(args[1]);
                    table = "Package";
                    matchColumn = "PackageFullName";
                    displayName = "package";
                    break;
                case "-app":
                    table = "Application";
                    matchColumn = "ApplicationUserModelID";
                    displayName = "application";
                    break;
                default:
                    try {
                        Triggers.Restore();
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nFatal Error: {e.Message}");
                    }

                    Console.WriteLine("\r\nFatal Error: Unable to resolve arguments.");
                    Environment.Exit(3);
                    break;
            }

            try {
                ActiveTransaction = new Transaction($@"SELECT ""_{table}ID"",""{matchColumn}"" FROM ""{table}"" WHERE ""{matchColumn}"" LIKE ""{args[1].Replace('*', '%')}""");
            } catch (Exception e) {
                try {
                    ActiveTransaction.Abort();
                    Triggers.Restore();
                } catch (Exception e2) {
                    Console.WriteLine($"\r\nFatal Error: {e2.Message}");
                }

                Console.WriteLine($"\r\nError: Could not initiate transaction.\r\nException: {e.Message}");
                Environment.Exit(5);
            }

            PackageIdList = new List<string>();
            ApplicationPackageList = new List<string>();
            ApplicationPackageDirList = new List<string>();
            var appPkgIdList = new List<string>();
            var found = false;
            bool app = false;

            try {
                content = ActiveTransaction.Command.ExecuteReader();
                while (content.Read()) {
                    found = true;
                    var id = content.GetString(0);
                    if (table == "Package") PackageIdList.Add(id);

                    if (table == "Application") {
                        app = true;
                        var packageIdData = ActiveTransaction.NewCommand($@"SELECT ""Package"" FROM ""{table}"" WHERE ""_ApplicationID"" = ""{id}""").ExecuteReader();
                        while (packageIdData.Read()) {
                            var packageNameData = ActiveTransaction.NewCommand($@"SELECT ""PackageFullName"",""_PackageID"" FROM ""Package"" WHERE ""_PackageID"" = ""{packageIdData.GetString(0)}""").ExecuteReader();
                            while (packageNameData.Read()) {
                                ApplicationPackageList.Add(packageNameData.GetString(0));
                                appPkgIdList.Add(packageNameData.GetString(1));

                            }

                            var packageDirData = ActiveTransaction.NewCommand($@"SELECT ""InstalledLocation"" FROM ""PackageLocation"" WHERE ""Package"" = ""{packageIdData.GetString(0)}""").ExecuteReader();
                            while (packageDirData.Read()) {
                                ApplicationPackageDirList.Add(packageDirData.GetString(0));
                            }
                        }
                    }

                    Console.WriteLine($"\r\nRemoving {displayName} {content.GetString(1)}\nfrom machine database...");
                    Assassin.MachineData($"_{table}ID", id, app: app);

                    /*
                    if (AppKeep) {
                        var nullify = ActiveTransaction.NewCommand($@"UPDATE ""{table}"" SET ""DisplayName"" = """",""Description"" = """",""AppListEntry"" = ""1"" WHERE ""_{table}ID"" = ""{id}""");
                        nullify.ExecuteNonQuery();
                        break;
                    }
                    */

                    try {
                        var eliminateHead = ActiveTransaction.NewCommand($@"DELETE FROM ""{table}"" WHERE ""_{table}ID"" = ""{id}""");
                        eliminateHead.ExecuteNonQuery();
                    } catch (Exception e) {
                        try {
                            ActiveTransaction.Abort();
                            Triggers.Restore();
                        } catch (Exception e2) {
                            Console.WriteLine($"\r\nFatal Error: {e2.Message}");
                        }

                        Console.WriteLine($"\r\nError: Could not remove specified {displayName} from machine database.\r\nException: {e.Message}");
                    }
                }
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not remove\n{displayName} {args[1]}.\r\nException: {e.Message}");
            }

            if (!found) Console.WriteLine("\r\nCould not find any data with the matching criteria.");

            try {
                ActiveTransaction.Commit();
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not commit database changes.\r\nException: {e.Message}");
            }


            Console.WriteLine("\r\nRestoring triggers...");
            try {
                Triggers.Restore();
            } catch (Exception e) {
                Console.WriteLine($"\r\nFatal Error: {e.Message}");
            }

            ActiveLink.Close();
            SqliteConnection.ClearAllPools();

            try {
                Hook($@"{Environment.GetEnvironmentVariable("PROGRAMDATA")}\Microsoft\Windows\AppRepository\StateRepository-Deployment.srd");
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not connect to deployment database.\r\nException: {e.Message}");
                goto deployEnd;
            }

            try {
                Triggers.Save();
                Triggers.Drop();
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: {e.Message}");
                goto deployEnd;
            }

            TableList = new List<string>();
            try {
                ActiveTransaction = new Transaction(@"SELECT ""Name"" FROM main.sqlite_master WHERE type = ""table""");
                content = ActiveTransaction.Command.ExecuteReader();
                while (content.Read()) {
                    var tableAdd = content.GetString(0);
                    TableList.Add(tableAdd);
                }
            } catch (Exception e) {
                try {
                    ActiveTransaction.Abort();
                    Triggers.Restore();
                } catch (Exception e2) {
                    Console.WriteLine($"\r\nError: {e2.Message}");
                    goto deployEnd;
                }

                Console.WriteLine($"\r\nError: Could not fetch tables from database.\r\nException: {e.Message}");
                goto deployEnd;
            }

            if (app && TableList.Contains("AppxManifest")) {
                foreach (var pkgId in appPkgIdList) {
                    try {
                        ActiveTransaction.DisableForeignKeys();
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not disable foreign keys.\r\nException: {e.Message}");
                    }
                    if (Verbose) Console.WriteLine($"\r\nRemoving application package {pkgId} xml data from deployment database...");

                    try {
                        var nullifyXml = ActiveTransaction.NewCommand($@"UPDATE ""AppxManifest"" SET ""Xml"" = ""00"" WHERE ""Package"" = ""{pkgId}""");
                        nullifyXml.ExecuteNonQuery();
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not remove application package xml data fromm deployment database.\r\nException: {e.Message}");
                    }
                }
            }
            
            foreach (var pkgId in PackageIdList) {
                try {
                    ActiveTransaction.DisableForeignKeys();
                } catch (Exception e) {
                    Console.WriteLine($"\r\nError: Could not disable foreign keys.\r\nException: {e.Message}");
                }

                if (Verbose) Console.WriteLine($"\r\nRemoving package value {pkgId} from deployment database...");
                foreach (var deployTable in TableList) {
                    if (deployTable == "ContentGroup") {
                        try {
                            var pkgCheck = ActiveTransaction.NewCommand($@"SELECT ""Package"" FROM ""{deployTable}"" WHERE ""Package"" = ""{pkgId}""");
                            var pkgData = pkgCheck.ExecuteReader();
                            while (pkgData.Read()) Assassin.ManualData("Package", pkgId, deployTable);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: {e.Message}");
                        }
                    } else {
                        try {
                            if (Verbose) Console.WriteLine($"\r\nRemoving row with package ID {pkgId} from deployment table {deployTable}...");
                            var removePkg = ActiveTransaction.NewCommand($@"DELETE FROM ""{deployTable}"" WHERE ""Package"" = ""{pkgId}""");
                            removePkg.ExecuteNonQuery();
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove package {pkgId} from deployment table {deployTable}.\r\nException: {e.Message}");
                        }
                    }
                }
            }

            /*
            try {
                ActiveTransaction.EnableForeignKeys();
            } catch (Exception e) {
                try {
                    ActiveTransaction.Abort();
                    Triggers.Restore();
                } catch (Exception e2) {
                    Console.WriteLine($"\r\nError: {e2.Message}");
                }

                Console.WriteLine($@"\r\nError: Could not re-enable foreign keys.\r\nException: {e.Message}");
                goto deployEnd;
            }
            */

            try {
                ActiveTransaction.Commit();
            } catch (Exception e) {
                Console.WriteLine($"\r\nError: Could not commit deployment database changes.\r\nException: {e.Message}");
            }

            ActiveLink.Close();
            SqliteConnection.ClearAllPools();

            deployEnd:

            switch (table) {
                case "PackageFamily":
                    foreach (var filter in Assassin.PackageFilterList) {
                        try {
                            Assassin.Files(filter);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove files belonging to package {filter}.\r\nException: {e.Message}");
                        }

                        try {
                            Assassin.RegistryKeys(filter);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove registry keys belonging to package {filter}.\r\nException: {e.Message}");
                        }
                    }
                    
                    foreach (var packageDir in Assassin.PackageDirectoryList)
                    {
                        try
                        {
                            Assassin.FilterDelete(Directory.GetParent(packageDir).FullName, Path.GetFileName(packageDir));
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove package directory {packageDir}.\r\nException: {e.Message}");
                        }
                    }

                    try {
                        Assassin.RegistryKeys(args[1], family: true);
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not remove registry keys belonging to family {args[0]}.\r\nException: {e.Message}");
                    }

                    break;
                case "Package":
                    foreach (var filter in Assassin.PackageFilterList) {
                        try {
                            Assassin.Files(filter);
                            Assassin.Files(args[1]);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove files belonging to package {filter}.\r\nException: {e.Message}");
                        }

                        try {
                            Assassin.RegistryKeys(filter);
                            Assassin.Files(args[1]);
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove registry keys belonging to package {filter}.\r\nException: {e.Message}");
                        }
                    }
                    foreach (var packageDir in Assassin.PackageDirectoryList)
                    {
                        try
                        {
                            Assassin.FilterDelete(Directory.GetParent(packageDir).FullName, Path.GetFileName(packageDir));
                        } catch (Exception e) {
                            Console.WriteLine($"\r\nError: Could not remove package directory {packageDir}.\r\nException: {e.Message}");
                        }
                    }

                    break;
                case "Application":
                    try {
                        Assassin.Files("|NONE|", true);
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not remove files belonging to application {args[1]}.\r\nException: {e.Message}");
                    }

                    try {
                        Assassin.RegistryKeys(args[1], true);
                    } catch (Exception e) {
                        Console.WriteLine($"\r\nError: Could not remove registry keys belonging to application {args[1]}.\r\nException: {e.Message}");
                    }

                    break;
            }

            if (FileLock.HasKilledExplorer)
            {
                try
                {
                    var cmdAction = new CmdAction();
                    cmdAction.Command = "start explorer.exe";
                    cmdAction.Wait = false;
                    cmdAction.RunTask();
                } catch (Exception) { }
            }
            
            Console.WriteLine("\r\nComplete!");
            Environment.Exit(0);
        }

    }
    
    public static class Extensions
    {
        public static bool EqualsIC(this string text, string value)
        {
            return text.Equals(value, StringComparison.OrdinalIgnoreCase);
        }
        public static bool ContainsIC(this string text, string value)
        {
            return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string ReplaceIC(this string text, string value, string replacement)
        {
            return Regex.Replace(text.ToString(), Regex.Escape(value), Environment.ExpandEnvironmentVariables(replacement), RegexOptions.IgnoreCase);
        }
    }
}