using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Microsoft.Win32;
using TrustedUninstaller.Shared.Actions;

namespace ame_assassin
{
    public static partial class SystemPackage
    {
        private static class Manifest
        {
            public static ParsedName ParseName(string name)
            {
                var result = new ParsedName();
                result.RawName = name;
                
                var splitFileName = name.Split('_');
                
                result.Arch = (Architecture)Enum.Parse(typeof(Architecture), splitFileName.First());
                result.ShortName = string.Join("_", splitFileName.Take(splitFileName.Length - 4).Skip(1));
                result.PublicKey = splitFileName[splitFileName.Length - 4];
                result.Version = splitFileName[splitFileName.Length - 3];
                result.Language = splitFileName[splitFileName.Length - 2];
                result.Randomizer = splitFileName[splitFileName.Length - 1];

                return result;
            }

            public class ParsedName
            {
                public string ShortName { get; set; }
                public Architecture Arch { get; set; }
                public string Language { get; set; }
                public string PublicKey { get; set; }
                public string Version { get; set; }
                public string Randomizer { get; set; }
                public string RawName { get; set; }

                public string RawPath
                {
                    get
                    {
                        return Environment.ExpandEnvironmentVariables(@"%WINDIR%\WinSxS\manifests\") + RawName;
                    }
                }

                public bool IdentityMatches(AssemblyIdentity identity)
                {
                    if (ShortName.Contains(".."))
                    {
                        var sides = ShortName.Split(new string[]
                        { ".." }, StringSplitOptions.None);
                        
                        if (!(identity.Name.ToLower().EndsWith(sides.Last()) && identity.Name.ToLower().StartsWith(sides.First())))
                        {
                            return false;
                        }
                    }
                    else if (!ShortName.Equals(identity.Name.ToLower())) return false;

                    if (identity.Arch != Architecture.All)
                    {
                        if (identity.Arch != Arch) return false;
                    }

                    if (identity.Language != "*")
                    {
                        if (identity.Language.ToLower() != Language.ToLower() && !(identity.Language.ToLower().Equals("neutral") && Language.ToLower() == "none")) return false;
                    }

                    /*
                    if (identity.Version != "*")
                    {
                        if (identity.Version != Version) return false;
                    }
                    */

                    if (identity.PublicKey != "*")
                    {
                        if (identity.PublicKey.ToLower() != PublicKey.ToLower()) return false;
                    }

                    return true;
                }
            }

            [DllImport("assassin-helper.dll", EntryPoint="ParseFile")]
            static extern StringBuilder ParseFile(string path);
            internal static string FetchXML(string manifest, bool fullPath = false)
            {
                string filePath;
                if (fullPath) filePath = manifest;
                else filePath = Environment.ExpandEnvironmentVariables(@"%WINDIR%\WinSxS\manifests\") + manifest;
                
                var xml = ParseFile(filePath).ToString();
                // Assassin-helper output will sometimes have broken leading and ending characters
                xml = xml.Substring(xml.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase));
                xml = xml.Substring(0, xml.IndexOf("\n</assembly>", StringComparison.OrdinalIgnoreCase) + 12);

                return xml;
            }

            public static void ReplaceIC(ref StringBuilder builder, string value, string replacement)
            {
                var replaced = Regex.Replace(builder.ToString(), Regex.Escape(value), Environment.ExpandEnvironmentVariables(replacement), RegexOptions.IgnoreCase);
                builder = new StringBuilder(replaced);
            }

            private static string TranslateVariables(string text, Architecture arch)
            {
                StringBuilder str = new StringBuilder(text);
                ReplaceIC(ref str, "$(runtime.windows)", @"%WINDIR%");

                ReplaceIC(ref str, "$(runtime.help)", @"%WINDIR%\Help");
                ReplaceIC(ref str, "$(runtime.programData)", @"%PROGRAMDATA%");

                ReplaceIC(ref str, "$(runtime.bootdrive)", @"%SYSTEMDRIVE%");
                ReplaceIC(ref str, "$(runtime.Public)", @"%SYSTEMDRIVE%\Users\Public");

                ReplaceIC(ref str, "$(runtime.apppatch)", @"%WINDIR%\apppatch");

                ReplaceIC(ref str, "$(runtime.inf)", @"%WINDIR%\INF");
                ReplaceIC(ref str, "$(runtime.systemRoot)", @"%SYSTEMROOT%");
                ReplaceIC(ref str, "$(runtime.windir)", @"%WINDIR%");
                ReplaceIC(ref str, "$(runtime.fonts)", @"%WINDIR%\Fonts");


                if (arch == Architecture.amd64)
                {
                    ReplaceIC(ref str, "$(runtime.system32)", @"%WINDIR%\System32");
                    ReplaceIC(ref str, "$(runtime.drivers)", @"%WINDIR%\System32\drivers");
                    ReplaceIC(ref str, "$(runtime.wbem)", @"%WINDIR%\System32\wbem");
                    
                    ReplaceIC(ref str, "$(runtime.programFiles)", @"%PROGRAMFILES%");
                    ReplaceIC(ref str, "$(runtime.commonFiles)", @"%PROGRAMFILES%\Common Files");
                }
                else
                {
                    ReplaceIC(ref str, "$(runtime.system32)", @"%WINDIR%\SysWOW64");
                    ReplaceIC(ref str, "$(runtime.drivers)", @"%WINDIR%\SysWOW64\drivers");
                    ReplaceIC(ref str, "$(runtime.wbem)", @"%WINDIR%\SysWOW64\wbem");
                    
                    ReplaceIC(ref str, "$(runtime.programFiles)", @"%PROGRAMFILES(x86)%");
                    ReplaceIC(ref str, "$(runtime.commonFiles)", @"%PROGRAMFILES(x86)%\Common Files");
                }

                ReplaceIC(ref str, "$(runtime.userProfile)", @"%SYSTEMDRIVE%\Users\Default");
                ReplaceIC(ref str, "$(runtime.startMenu)", @"%PROGRAMDATA%\Microsoft\Windows\Start Menu");

                return str.ToString();
            }

            public class ManifestData
            {
                public string xml { get; set; }
                public AssemblyIdentity Identity { get; set; }
                public ParsedName ParsedName { get; set; }
            }
            internal static List<ManifestData> FindManifestsFromIdentity(AssemblyIdentity identity)
            {
                Console.WriteLine(@$"Searching for manifest of '{identity.Name}'...");
                string start = identity.Arch + "_" + identity.Name.Substring(0, Math.Min(identity.Name.Length, 19)).ToLower();
                var result = new List<ManifestData>();
                var manifestFiles = Directory.EnumerateFiles(Environment.ExpandEnvironmentVariables("%WINDIR%\\WinSxS\\manifests"), start + "*").Where(x => ParseName(Path.GetFileName(x)).IdentityMatches(identity));

                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        var xml = new XmlDocument();

                        var xmlText = FetchXML(manifestFile, true);
                        xml.LoadXml(xmlText);
                        
                        var manifestIdentity = ParseIdentity((XmlElement)xml.DocumentElement.FirstChild);

                        if (manifestIdentity.IdentityMatches(identity))
                        {
                            var name = Path.GetFileName(manifestFile);
                            result.Add(new ManifestData() { xml = xmlText, ParsedName = ParseName(name), Identity = manifestIdentity });
                        }

                    } catch (Exception e)
                    {
                        Console.WriteLine($"\r\nError: Could not identify manifest file '{manifestFile}'.\r\nException: " + e.Message);
                    }
                }
                return result;
            }

            public static AssemblyIdentity ParseIdentity(XmlElement identityNode)
            {
                var result = new AssemblyIdentity();

                try
                {
                    result.Name = identityNode.Attributes["name"].InnerText;
                } catch (Exception e)
                {
                    Console.WriteLine("\r\nError: Could not parse identity property.\r\nException: " + e.Message);
                }

                try
                {
                    result.Arch = (Architecture)Enum.Parse(typeof(Architecture), identityNode.Attributes["processorArchitecture"].InnerText);
                } catch (Exception e)
                {
                    Console.WriteLine("\r\nError: Could not parse identity property.\r\nException: " + e.Message);
                }

                try
                {
                    result.Language = identityNode.Attributes["language"].InnerText;
                } catch (Exception e)
                {
                    Console.WriteLine("\r\nError: Could not parse identity property.\r\nException: " + e.Message);
                }

                /*
                try
                {
                    result.Version = identityNode.Attributes["version"].InnerText;
                } catch (Exception e)
                {
                    Console.WriteLine("\r\nError: Could not parse identity property.\r\nException: " + e.Message);
                }
                */

                /*
                try
                {
                    result.BuildType = identityNode.Attributes["buildType"].InnerText;
                } catch (Exception e)
                {
                    Console.WriteLine("\r\nError: Could not parse identity property.\r\nException: " + e.Message);
                }
                */

                try
                {
                    result.PublicKey = identityNode.Attributes["publicKeyToken"].InnerText;
                } catch (Exception e)
                {
                    Console.WriteLine("\r\nError: Could not parse identity property.\r\nException: " + e.Message);
                }

                /*
                try
                {
                    result.VersionScope = identityNode.Attributes["versionScope"].InnerText;
                } catch (Exception e)
                {
                    Console.WriteLine("\r\nError: Could not parse identity property.\r\nException: " + e.Message);
                }
                */

                return result;
            }

            public static ParsedXML ParseManifest(ManifestData manifest)
            {
                Console.WriteLine($@"Parsing manifest...");
                ParsedXML result = new ParsedXML();

                XmlElement xmlRoot;

                try
                {
                    var xml = new XmlDocument();
                    xml.LoadXml(manifest.xml);
                    
                    xmlRoot = xml.DocumentElement;
                    if (xmlRoot == null)
                    {
                        Console.WriteLine("\r\nError: No primary xml node found in xml.");
                        return null;
                    }
                } catch (Exception e)
                {
                    Console.WriteLine("\r\nError: Could not parse xml.\r\nException: " + e.Message);
                    return null;
                }
                
                result.Identity = manifest.Identity;

                if (manifest.Identity.Arch == Architecture.msil)
                {
                    result.Directories.Add(Environment.ExpandEnvironmentVariables(@"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\" + manifest.Identity.Name));
                }

                var dependents = xmlRoot.GetElementsByTagName("dependency");
                foreach (XmlElement dependent in dependents)
                {
                    try
                    {
                        if (dependent.ParentNode.Name == "assembly")
                        {
                            var dependentIdentities = dependent.GetElementsByTagName("assemblyIdentity");

                            foreach (XmlElement dependentIdentity in dependentIdentities)
                            {
                                result.Dependents.Add(ParseIdentity(dependentIdentity));
                            }
                        }
                    } catch (Exception e)
                    {
                        Console.WriteLine("\r\nError: Could not parse a dependent XML element.\r\nException: " + e.Message);
                    }
                }

                var keys = xmlRoot.GetElementsByTagName("registryKey");
                foreach (XmlElement key in keys)
                {
                    var keyName = key.Attributes["keyName"].InnerText.TrimEnd('\\');
                    if (result.Identity.Arch != Architecture.amd64 && result.Identity.Arch != Architecture.msil)
                    {
                        if (!keyName.ContainsIC("HKEY_LOCAL_MACHINE\\SOFTWARE") && !keyName.ContainsIC("HKLM\\SOFTWARE")) continue;
                        
                        keyName = keyName.ReplaceIC("HKEY_LOCAL_MACHINE\\SOFTWARE", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node");
                        keyName = keyName.ReplaceIC("HKLM\\SOFTWARE", "HKLM\\SOFTWARE\\Wow6432Node");
                    }
                    
                    if (key.ParentNode.Name.Equals("registryKeys") && !key.ChildNodes.Cast<XmlElement>().Any(x => x.Name == "registryValue"))
                    {
                        result.RegistryKeys.Add(keyName);
                    }
                    else if (key.ParentNode.Name.Equals("registryKeys") && key.HasChildNodes)
                    {
                        bool hadValue = false;
                        foreach (XmlElement value in key.ChildNodes)
                        {
                            if (value.Name != "registryValue") continue;
                            hadValue = true;

                            if (value.Attributes["name"].InnerText == "FileName" && (
                                    keyName.StartsWith(@"HKEY_LOCAL_MACHINE\System\CurrentControlSet\Control\WMI\Autologger\", StringComparison.OrdinalIgnoreCase) ||
                                    keyName.StartsWith(@"HKLM\System\CurrentControlSet\Control\WMI\Autologger\", StringComparison.OrdinalIgnoreCase)))
                            {
                                result.Files.Add(Environment.ExpandEnvironmentVariables(value.Attributes["name"].InnerText) + "*");
                            }

                            var resultValue = new RegistryValue()
                            { Key = keyName, Value = value.Attributes["name"].InnerText };
                            
                            result.RegistryValues.Add(resultValue);
                        }

                        if (!hadValue) result.RegistryKeys.Add(keyName);
                    }
                }

                var files = xmlRoot.GetElementsByTagName("file");
                foreach (XmlElement file in files)
                {
                    try
                    {
                        if (result.Identity.Arch != Architecture.amd64 && result.Identity.Arch != Architecture.msil)
                        {
                            var filePath = TranslateVariables(file.Attributes["destinationPath"].InnerText, manifest.Identity.Arch);
                            if (!filePath.ContainsIC("\\Program Files (x86)\\") && !filePath.ContainsIC("Windows\\SysWOW64"))
                            {
                                continue;
                            }
                        }
                        if (file.ParentNode.Name == "assembly")
                        {
                            if (file.GetElementsByTagName("infFile").Cast<XmlElement>().Any() && file.Attributes["destinationPath"] == null)
                            {
                                result.Files.Add(TranslateVariables("$(runtime.inf)\\" + file.Attributes["name"].InnerText, manifest.Identity.Arch));
                            }
                            else if (file.Attributes["destinationPath"] != null)
                            {
                                var fullPath = TranslateVariables(file.Attributes["destinationPath"].InnerText, manifest.Identity.Arch) + file.Attributes["name"].InnerText;
                                result.Files.Add(fullPath);
                            }
                        }
                    } catch (Exception e)
                    {
                        Console.WriteLine("\r\nError: Could not parse a file XML element.\r\nException: " + e.Message);
                    }
                }

                var directories = xmlRoot.GetElementsByTagName("file");
                foreach (XmlElement directory in directories)
                {
                    try
                    {
                        if (result.Identity.Arch != Architecture.amd64 && result.Identity.Arch != Architecture.msil)
                        {
                            var dir = TranslateVariables(directory.Attributes["destinationPath"].InnerText, manifest.Identity.Arch);
                            if (!dir.ContainsIC("\\Program Files (x86)\\") && !dir.ContainsIC("Windows\\SysWOW64"))
                            {
                                continue;
                            }
                        }
                        
                        if (directory.ParentNode.Name == "directories")
                        {
                            var fullPath = TranslateVariables(directory.Attributes["destinationPath"].InnerText, manifest.Identity.Arch).TrimEnd('\\');
                            result.Directories.Add(fullPath);
                        }
                    } catch (Exception e)
                    {
                        Console.WriteLine("\r\nError: Could not parse a directory XML element.\r\nException: " + e.Message);
                    }
                }

                var events = xmlRoot.GetElementsByTagName("events");
                foreach (XmlElement _event in events)
                {
                    try
                    {
                        if (_event.ParentNode.Name == "instrumentation")
                        {
                            foreach (XmlElement provider in _event.GetElementsByTagName("provider"))
                            {
                                result.EventProviders.Add(provider.Attributes["guid"].InnerText);

                                foreach (XmlElement channel in _event.GetElementsByTagName("channel"))
                                {
                                    result.EventChannels.Add(channel.Attributes["name"].InnerText);
                                }
                            }
                        }
                    } catch (Exception e)
                    {
                        Console.WriteLine("\r\nError: Could not parse an event XML element.\r\nException: " + e.Message);
                    }
                }

                var counters = xmlRoot.GetElementsByTagName("counters");
                foreach (XmlElement counter in counters)
                {
                    try
                    {
                        if (counter.ParentNode.Name == "instrumentation")
                        {
                            foreach (XmlElement provider in counter.GetElementsByTagName("provider"))
                            {
                                result.Counters.Add(provider.Attributes["providerGuid"].InnerText);
                            }
                        }
                    } catch (Exception e)
                    {
                        Console.WriteLine("\r\nError: Could not parse a counter XML element.\r\nException: " + e.Message);
                    }
                }

                var tasks = xmlRoot.GetElementsByTagName("Task");
                foreach (XmlElement task in tasks)
                {
                    try
                    {
                        if (task.ParentNode.Name == "taskScheduler")
                        {
                            foreach (XmlElement uri in task.GetElementsByTagName("URI"))
                            {
                                result.ScheduledTasks.Add(uri.InnerText);
                            }
                        }
                    } catch (Exception e)
                    {
                        Console.WriteLine("\r\nError: Could not parse a scheduled task XML element.\r\nException: " + e.Message);
                    }
                }

                var services = xmlRoot.GetElementsByTagName("serviceData");
                foreach (XmlElement service in services)
                {
                    try
                    {
                        if (service.ParentNode.Name == "categoryInstance")
                        {
                            if (service.Attributes["type"].InnerText.EqualsIC("kernelDriver") || service.Attributes["type"].InnerText.EqualsIC("fileSystemDriver"))
                            {
                                result.Devices.Add(service.Attributes["name"].InnerText);
                            }
                            else result.Services.Add(service.Attributes["name"].InnerText);
                        }
                    } catch (Exception e)
                    {
                        Console.WriteLine("\r\nError: Could not parse a service XML element.\r\nException: " + e.Message);
                    }
                }
                
                var shortcuts = xmlRoot.GetElementsByTagName("shortCut");
                foreach (XmlElement shortcut in shortcuts)
                {
                    try
                    {
                        if (shortcut.ParentNode.Name == "categoryInstance")
                        {
                            if (shortcut.Attributes["destinationPath"] != null)
                            {
                                var fullPath = TranslateVariables(shortcut.Attributes["destinationPath"].InnerText, manifest.Identity.Arch) + "\\" + shortcut.Attributes["destinationName"].InnerText;
                                result.Files.Add(fullPath);
                            }
                        }
                    } catch (Exception e)
                    {
                        Console.WriteLine("\r\nError: Could not parse a shortcut XML element.\r\nException: " + e.Message);
                    }
                }

                try
                {
                    var svcMemberships = xmlRoot.GetElementsByTagName("id").Cast<XmlElement>().Where(x => x.ParentNode.Name == "categoryMembership" && x.Attributes["name"].InnerText.EqualsIC("Microsoft.Windows.Categories") && x.Attributes["typeName"].InnerText.EqualsIC("SvcHost"));
                    foreach (XmlElement svcMembership in svcMemberships)
                    {
                        try
                        {
                            var svcInstances = svcMembership.ParentNode.ChildNodes.Cast<XmlElement>().Where(x => x.Name == "categoryInstance");
                            foreach (var svcInstance in svcInstances)
                            {
                                var subCategory = svcInstance.Attributes["subcategory"].InnerText;
                                foreach (XmlElement serviceTag in svcInstance.GetElementsByTagName("serviceGroup"))
                                {
                                    var registryValue = new RegistryValue();

                                    if (result.Identity.Arch != Architecture.amd64 && result.Identity.Arch != Architecture.msil)
                                    {
                                        registryValue.Key = @"HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows NT\CurrentVersion\Svchost\" + subCategory;
                                    }
                                    else
                                    {
                                        registryValue.Key = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Svchost\" + subCategory;
                                    }
                                    
                                    registryValue.Value = serviceTag.Attributes["serviceName"].InnerText;
                                    result.RegistryValues.Add(registryValue);
                                }
                            }
                        } catch (Exception e)
                        {
                            Console.WriteLine("\r\nError: Could not parse a svc XML element (1).\r\nException: " + e.Message);
                        }
                    }
                } catch (Exception e)
                {
                    Console.WriteLine("\r\nError: Could not parse a svc XML element (2).\r\nException: " + e.Message);
                }

                return result;
            }
        }
    }
}