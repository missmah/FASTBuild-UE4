using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Xml;
using System.Text.RegularExpressions;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Management;
using System.Text;

namespace UnrealBuildTool
{
    public class FASTBuild : ActionExecutor
    {
        // <----- FastBuild Configuration Settings ----->

        // Enable or Disable the *global* usage of FastBuild
        static public bool IsEnabled
        {
            get
            {
                return true; // <----- Toggle FastBuild on/off <-> (default=on)
            }
        }

        // Change me if you're running into issues with FastBuild taking too long on tiny incremental builds.
        static public int MinimumRequiredActionsToEnableThreshold
        {
            get
            {
                return 0; // <----- Don't build with FastBuild unless there are more than threshold number of actions to build <-> (default=0)
            }
        }

        // Change me if you're running into issues with FastBuild taking too long on medium sized incremental builds.
        static public int MinimumRequiredActionsToEnableDistributionAndCachingThreshold
        {
            get
            {
                return 25; // <----- Don't enable FastBuild Distribution and Caching features unless there are more than threshold number of actions to build <-> (default=25)
            }
        }

        static public bool UseSinglePassCompilation
        {
            get
            {
                return false; // <----- Should build and link in a single FastBuild .bff step on/off <-> (default=off)
            }
        }

        static private bool AllowCache
        {
            get
            {
                return false; // <----- Toggle use of FastBuild Cache on/off <-> (default=on)
            }
        }

        static public bool AllowDistribution
        {
            get
            {
                return true; // <----- Toggle FastBuild Distributed Builds on/off <-> (default=on)
            }
        }

        static public bool ContinueOnError
        {
            get
            {
                return false; // <----- Should keep building after an error on/off <-> (default=off)
            }
        }

        static public bool EnableMonitorAPIMode
        {
            get
            {
                return true; // <----- (-monitor) Toggle FastBuildMonitor Build Visualization Support on/off <-> (default=off)
            }
        }

        static public bool UseIDEMode
        {
            get
            {
                return true; // <----- Toggle FastBuild IDE Integration Mode on/off <-> (default=on)
            }
        }

        static public bool ShowBuildSummary
        {
            get
            {
                return true; // <----- Show a summary at the end of the build: on/off <-> (default=on)
            }
        }

        static public bool EnableBuildReport
        {
            get
            {
                return false; // <----- Output a report at build termination: on/off <-> (default=off) <on->slow>
            }
        }

        static public bool EnableShowCommandLinesInvoked
        {
            get
            {
                return false; // <----- (-showcmds) Displays the full command lines passed to external tools as they are invoked: on/off <-> (default=off) <on->slow>
            }
        }

        static public bool EnableShowDefinedTargets
        {
            get
            {
                return false; // <----- (-showtargets) Displays the list of targets defined in the bff configuraiton file: on/off <-> (default=off) <on->slow>
            }
        }

        static public bool EnableVerboseDebugging
        {
            get
            {
                return false; // <----- Show detailed diagnostic information for debugging: on/off <-> (default=off) <on->slow>
            }
        }

        static public bool EnableInternalDebugging
        {
            get
            {
                return false; // <----- Show detailed internal FastBuild.cs diagnostic information for debugging: on/off <-> (default=off) <on->slow>
            }
        }

        static public bool AppendUEGlobalCompilerDefines
        {
            get
            {
                return false; // <----- Force appending of UE Global Compiler Defines to .CompilerOptions: on/off <-> (default=off)
            }
        }

        static public bool ForceAppendBaseIncludePaths
        {
            get
            {
                return false; // <----- Force appending of .BaseIncludePaths to .CompilerOptions: on/off <-> (default=off)
            }
        }

        // <----- FastBuild Configuration Settings ----->

        static private bool? _allowCacheWrite = null;
        static public bool EnableCacheGenerationMode
        {
            get
            {
                if (!_allowCacheWrite.HasValue)
                {
                    //Add your own way of checking here. I check whether it's running on the CI machine in my case.
                    // TODO: Check whether running on CI machine, set _allowCacheWrite=true if so.
                    _allowCacheWrite = false;
                }
                return _allowCacheWrite.Value;
            }
        }

        static private bool _EnableCache = AllowCache;
        static private bool _EnableDistribution = AllowDistribution;

        static private bool EnableCache
        {
            get
            {
                return (AllowCache && _EnableCache);
            }
        }

        static public bool EnableDistribution
        {
            get
            {
                return (AllowDistribution && _EnableDistribution);
            }
        }

        static public void InternalDebugLog( string Format, params object[] Args )
        {
            if ( EnableInternalDebugging )
            {
                Log.TraceInformation(Format, Args);
            }
        }

        const string FastBuild_Win8Version = "winv6.3";
        //const string FastBuild_Win10Version = "10.0.10240.0";
        //const string FastBuild_Win10Version = "10.0.10586.0";
        const string FastBuild_Win10Version = "10.0.14393.0";
        const string FastBuild_EnvVar = "FASTBUILD_ROOT_DIR";
		const string IVR_DevDir_EnvVar = "INSIGHTFUL_DEV_DIR";

		static public bool GetFastBuildParameters( out string oFastBuildRootPath, out string oSdkDir, out string oSdk10Dir )
        {
            bool bDoesFastBuildExist = false;
            string FastBuildRootPath = Environment.GetEnvironmentVariable( FastBuild_EnvVar );

			// Used if FastBuild Binaries are in git within IvrDevDir
			string IvrDevDir = Environment.GetEnvironmentVariable( IVR_DevDir_EnvVar );

			// FIX For FastBuild 0.96 Environment Variable Preference Bug:
			// https://github.com/fastbuild/fastbuild/issues/499
			Environment.SetEnvironmentVariable("FASTBUILD_CACHE_PATH", "%INSIGHTFUL_DEV_DIR%\\Build\\Output\\.fbuild.cache", EnvironmentVariableTarget.Process);

			string ErrorUnassigned = "error-unassigned";
            string FastBuildExe = ErrorUnassigned;
            string CompilerDir = ErrorUnassigned;
            string SdkDir = ErrorUnassigned;
            string Sdk10Dir = ErrorUnassigned;
            string Sdk10IncludeDir = ErrorUnassigned;

            string VSVersion = ErrorUnassigned;
            switch (WindowsPlatform.Compiler)
            {
                case WindowsCompiler.VisualStudio2013:
                    VSVersion = "13.4";
                    break;
                case WindowsCompiler.VisualStudio2015:
                    VSVersion = "14.0";
                    break;
                case WindowsCompiler.VisualStudio2017:
                    VSVersion = "15.0";
                    break;
                case WindowsCompiler.Default:
                default:
                    throw new Exception("Error, unsupported Compiler Version.");
            };

            if (!String.IsNullOrEmpty(FastBuildRootPath))
            {
                string ExternalVSCompiler = "External/VS" + VSVersion;

				if (!String.IsNullOrEmpty(IvrDevDir))
				{
					// First try INSIGHTFUL_DEV_DIR git copy of FastBuild, if it exists...
					FastBuildExe = Path.Combine(IvrDevDir, "Build/Tools/Windows/FBuild.exe");
				}

				if( !File.Exists(FastBuildExe) )
				{
					// Fallback to generic "installed" copy of FastBuild...
					FastBuildExe = Path.Combine(FastBuildRootPath, "FBuild.exe");
				}

				CompilerDir = Path.Combine(FastBuildRootPath, ExternalVSCompiler);
                SdkDir = Path.Combine(FastBuildRootPath, "External/Windows8.1");
                Sdk10Dir = Path.Combine(FastBuildRootPath, "External/Windows10");
                Sdk10IncludeDir = Path.Combine(Sdk10Dir, "Include", FastBuild_Win10Version);

                bDoesFastBuildExist = File.Exists(FastBuildExe) && Directory.Exists(CompilerDir) && Directory.Exists(SdkDir) && Directory.Exists(Sdk10Dir) && Directory.Exists(Sdk10IncludeDir);
            }

            if (!bDoesFastBuildExist)
            {
                Log.TraceInformation("FastBuildRootPath: {0}, was {1}", FastBuildRootPath, (Directory.Exists(FastBuildRootPath)) ? "found" : "not found");
                Log.TraceInformation("FastBuildExe: {0}, was {1}", FastBuildExe, (File.Exists(FastBuildExe)) ? "found" : "not found");
                Log.TraceInformation("Windows8.1 SDK: {0}, was {1}", SdkDir, (Directory.Exists(SdkDir)) ? "found" : "not found");
                Log.TraceInformation("Windows10 Base SDK: {0}, was {1}", Sdk10Dir, (Directory.Exists(Sdk10Dir)) ? "found" : "not found");
                Log.TraceInformation("Windows10 SDK Version: {0}, is {1}", Sdk10IncludeDir, (Directory.Exists(Sdk10IncludeDir)) ? "found" : "not found");
                Log.TraceInformation("Expected Windows10 SDK Version: {0}, is {1}", FastBuild_Win10Version, (Directory.Exists(Sdk10IncludeDir)) ? "installed" : "not installed");
                Log.TraceError("Error, expected Compiler and/or SDK not found in FastBuildRootPath! Please install the missing component (possibly by updating Visual Studio - please ask Layla if you have questions).");
            }

            oFastBuildRootPath = FastBuildRootPath;
            oSdkDir = SdkDir;
            oSdk10Dir = Sdk10Dir;
            return bDoesFastBuildExist;
        }

        static public bool DoesFastBuildExist( )
        {
            string unused_FastBuildRootPath;
            string unused_SdkDir;
            string unused_Sdk10Dir;
            return GetFastBuildParameters(out unused_FastBuildRootPath, out unused_SdkDir, out unused_Sdk10Dir);
        }

        public static bool ConfigureForBuildParameters( int iNumActionsToPerform )
        {
            // Refresh these to the current Allow settings
            _EnableCache = AllowCache;
            _EnableDistribution = AllowDistribution;

            if( iNumActionsToPerform < MinimumRequiredActionsToEnableThreshold )
            {
                return false; // Don't use FastBuild at all for this build
            }
            else if ( iNumActionsToPerform <= MinimumRequiredActionsToEnableDistributionAndCachingThreshold )
            {
                // Force off Caching and Distribution for this build
                _EnableCache = false;
                _EnableDistribution = false;
            }

            return true; // Do use FastBuild for this build
        }

        static private string _UEGlobalCompilerDefinitions = null;
        public static string GetUEGlobalCompilerDefinitions()
        {
            if( !AppendUEGlobalCompilerDefines )
            {
                return "";
            }

            if (_UEGlobalCompilerDefinitions == null)
            {
                // TODO: Figure out how to get these from GlobalCompileEnvironment.Config.Definitions
                _UEGlobalCompilerDefinitions = "/DUE_BUILD_DEVELOPMENT /DPLATFORM_WINDOWS /DWITH_EDITOR=1 /DWITH_ENGINE=1 /DWITH_UNREAL_DEVELOPER_TOOLS=1 /DWITH_PLUGIN_SUPPORT=1 /DUE_BUILD_MINIMAL=0 /DIS_MONOLITHIC=0 /DIS_PROGRAM=0";

                //InBuildTarget.GlobalCompileEnvironment.Config.Definitions.Add("WITH_EDITOR=0");

            /*  foreach (var define in GlobalCompileEnvironment.Config.Definitions)
                {
                    _UEGlobalCompilerDefinitions += define + " ";
                }
            */
            }

            return _UEGlobalCompilerDefinitions;
        }

        private struct FastBuildCommon
        {
            public static string EntryArguments
            {
                get
                {
                    string FastBuildRootPath    = "error-invalid-fast-build-root-path";
                    string SdkDir               = "error-invalid-fast-build-sdk-dir";
                    string Sdk10Dir             = "error-invalid-fast-build-sdk-10-dir";
                    bool bDoesFastBuildExist = GetFastBuildParameters(out FastBuildRootPath, out SdkDir, out Sdk10Dir);

                    if (!bDoesFastBuildExist)
                    {
                        throw new Exception("Error, expected Compiler and/or SDK not found in FastBuildRootPath! Please install the missing component (possibly by updating Visual Studio - please ask Layla if you have questions).");
                    }

                    string EntryArguments  = ";-------------------------------------------------------------------------------\r\n";
                           EntryArguments += "; Windows Platform\r\n";
                           EntryArguments += ";-------------------------------------------------------------------------------\r\n";

                    if (WindowsPlatform.Compiler == WindowsCompiler.VisualStudio2013)
                    {
                        string FastBuild_VSBasePath = FastBuildRootPath + "/External/VS13.4/VC";
                        EntryArguments += ".VSBasePath          = '";
                        EntryArguments += FastBuild_VSBasePath;
                        EntryArguments += "'\r\n";
                    }
                    else if (WindowsPlatform.Compiler == WindowsCompiler.VisualStudio2015)
                    {
                        string FastBuild_VSBasePath = FastBuildRootPath + "/External/VS14.0/VC";
                        EntryArguments += ".VSBasePath          = '";
                        EntryArguments += FastBuild_VSBasePath;
                        EntryArguments += "'\r\n";
                    }
                    else if (WindowsPlatform.Compiler == WindowsCompiler.VisualStudio2017)
                    {
                        string FastBuild_VSBasePath = FastBuildRootPath + "/External/VS15.0/VC";
                        EntryArguments += ".VSBasePath          = '";
                        EntryArguments += FastBuild_VSBasePath;
                        EntryArguments += "'\r\n";
                    }

                    //EntryArguments += ".ClangBasePath           = '../Extras/FASTBuild/External/LLVM'\r\n";

                    string FastBuild_WinSDKBasePath = SdkDir;
                    EntryArguments += ".WindowsSDKBasePath  = '";
                    EntryArguments += FastBuild_WinSDKBasePath;
                    EntryArguments += "'\r\n";

                    EntryArguments += ".Windows8SDKVersion  = '";
                    EntryArguments += FastBuild_Win8Version;
                    EntryArguments += "'\r\n";

                    EntryArguments += ".Windows10SDKVersion  = '";
                    EntryArguments += FastBuild_Win10Version;
                    EntryArguments += "'\r\n";

                    string FastBuild_WinUMLibraryBasePath = SdkDir + "/lib/$Windows8SDKVersion$/um";
                    EntryArguments += ".WindowsUMLibraryPath  = '";
                    EntryArguments += FastBuild_WinUMLibraryBasePath;
                    EntryArguments += "'\r\n";

                    string FastBuild_WinUCRTBasePath = Sdk10Dir + "/include/$Windows10SDKVersion$/ucrt";
                    EntryArguments += ".WindowsUCRTBasePath  = '";
                    EntryArguments += FastBuild_WinUCRTBasePath;
                    EntryArguments += "'\r\n";

                    string FastBuild_WinUCRTLibraryBasePath = Sdk10Dir + "/lib/$Windows10SDKVersion$/ucrt";
                    EntryArguments += ".WindowsUCRTLibraryPath  = '";
                    EntryArguments += FastBuild_WinUCRTLibraryBasePath;
                    EntryArguments += "'\r\n";

                    //EntryArguments += ".WindowsSDKBasePath  = '../Extras/FASTBuild/External/Windows8.1'\r\n";
                    //EntryArguments += ".OrbisSDK            = '../Extras/FASTBuild/External/Orbis'\r\n";

                    EntryArguments += ";-------------------------------------------------------------------------------\r\n";
                    EntryArguments += "; Base (library) includes\r\n";
                    EntryArguments += ";-------------------------------------------------------------------------------\r\n";
                    EntryArguments += ".BaseIncludePaths        = ' /I\"$VSBasePath$/include/\"'\r\n";
                    EntryArguments += "                         + ' /I\"$VSBasePath$/atlmfc/include/\"'\r\n";
                    EntryArguments += "                         + ' /I\"$WindowsSDKBasePath$/include/um/\"'\r\n";
                    EntryArguments += "                         + ' /I\"$WindowsSDKBasePath$/include/shared/\"'\r\n";
                    EntryArguments += "                         + ' /I\"$WindowsSDKBasePath$/include/winrt/\"'\r\n";
                    EntryArguments += "                         + ' /I\"$WindowsUCRTBasePath$/\"'\r\n";

                    EntryArguments += ";-------------------------------------------------------------------------------\r\n";
                    EntryArguments += "; Base (library) directories (x86)\r\n";
                    EntryArguments += ";-------------------------------------------------------------------------------\r\n";
                    EntryArguments += ".BaseLibraryPathsx86     = ' /LIBPATH:\"$VSBasePath$/lib/\"'\r\n";
                    EntryArguments += "                         + ' /LIBPATH:\"$WindowsUMLibraryPath$/x86/\"'\r\n";
                    EntryArguments += "                         + ' /LIBPATH:\"$WindowsUCRTLibraryPath$/x86/\"'\r\n";

                    EntryArguments += ";-------------------------------------------------------------------------------\r\n";
                    EntryArguments += "; Base (library) directories (x64)\r\n";
                    EntryArguments += ";-------------------------------------------------------------------------------\r\n";
                    EntryArguments += ".BaseLibraryPathsx64     = ' /LIBPATH:\"$VSBasePath$/lib/amd64/\"'\r\n";
                    EntryArguments += "                         + ' /LIBPATH:\"$WindowsUMLibraryPath$/x64/\"'\r\n";
                    EntryArguments += "                         + ' /LIBPATH:\"$WindowsUCRTLibraryPath$/x64/\"'\r\n";
                    
                    return EntryArguments;
                }
            }

            public static string GetAliasTag(string alias, List<string> targets)
            {
                string output = "";
                int targetCount = targets.Count;
                output += "Alias( '" + alias + "' )\r\n";
                output += "{\r\n";
                output += " .Targets = {";
                for (int i = 0; i < targetCount; ++i)
                {
                    output += "'" + targets[i] + "'";
                    if (i < targetCount - 1)
                    {
                        output += ",";
                    }
                }
                output += " }\r\n";
                output += "}\r\n";
                return output;

            }
        }

        private static void DeresponsifyActions(List<Action> actions)
        {
            FASTBuild.InternalDebugLog("The new post-UE4.13/4.15 UBT stores arguments in .response files - Deresponsifying {0} Actions", actions.Count);

            int i = 0;
            // UE4.13 started to shove the entire argument into response files. This does not work
            // well with FASTBuild so we'll have to undo it.
            foreach (var action in actions)
            {
                FASTBuild.InternalDebugLog("Deresponsifying Action {0}, {1}", i, action.ToString());
              //FASTBuild.InternalDebugLog("Deresponsifying Action {0}, {1}", i, action.CommandArguments);

                // Not a response file! Copy these args directly into the output args!!!! (?)
                if(        !(
                                    action.CommandArguments.StartsWith("@\"")
                                ||
                                    action.CommandArguments.StartsWith(" @\"")
                            ))
                {
                    FASTBuild.InternalDebugLog("Deresponsifying Action {0} not required (it is already in argument form).", i++);
                    continue;
                }

                if (
                            (!action.CommandArguments.EndsWith("\""))
                        ||
                            (action.CommandArguments.Count(f => f == '"') != 2)

                )
                {
                    FASTBuild.InternalDebugLog("Deresponsifying Action {0} skipped.", i++);
                    continue;
                }

                var file = Regex.Match(action.CommandArguments, "(?<=@\")(.*?)(?=\")");
                if (!file.Success)
                {
                    FASTBuild.InternalDebugLog("Deresponsifying Action {0} failed because regex did not match.", i++);
                    continue;
                }

                var arg = File.ReadAllText(file.Value);
                var newarg = arg.Replace(System.Environment.NewLine, " ");
                action.CommandArguments = newarg;

                FASTBuild.InternalDebugLog("Deresponsifying Action {0} into: {1}", i++, newarg);
            }
        }

        private static readonly Dictionary<string, Compiler> Compilers = new Dictionary<string, Compiler>();
        private static readonly Dictionary<string, Linker> Linkers = new Dictionary<string, Linker>();
        private static int AliasBase { get; set; } = 1;

        public enum ExecutionResult
        {
            Unavailable,
            TasksFailed,
            TasksSucceeded,
        }

        private enum BuildStep
        {
            CompileObjects,
            Link,
            CompileAndLink
        }

        private enum CompilerType
        {
            MSVC,
            RC,
            Clang,
            OrbisClang,
            OrbisSnarl
        }

        private enum CompilerPlatform
        {
            amd64,
            x86,
            Unknown
        }

        public enum LinkerType
        {
            Static,
            Dynamic
        }

        private class Compiler : Linker
        {
            public Compiler(string exePath)
                : base(exePath)
            {
                LocaliseCompilerPath();
            }

            public override string InputFileRegex
            {
                get
                {
                    return "(?<=( \")|(@\"))(.*?)(?=\")";
                }
            }

            public override string OutputFileRegex
            {
                get
                {
                    if (Type == CompilerType.MSVC || Type == CompilerType.RC)
                    {
                        return "(?<=(/Fo \"|/Fo\"))(.*?)(?=\")";
                    }
                    else
                    {
                        return "(?<=(-o \"|-o\"))(.*?)(?=\")";
                    }
                }
            }

            public override string PCHOutputRegex
            {
                get
                {
                    if (Type == CompilerType.MSVC || Type == CompilerType.RC)
                    {
                        return "(?<=(/Fp \"|/Fp\"))(.*?)(?=\")";
                    }
                    else
                    {
                        return "(?<=(/Fp \"|/Fp\"))(.*?)(?=\")";
                    }
                }
            }
            public string Alias;

            public string GetBffArguments(string Arguments, string AdditionalArguments)
            {
                StringBuilder output = new StringBuilder();
                string myUEGlobalCompilerDefinitions = FASTBuild.GetUEGlobalCompilerDefinitions();
                output.AppendFormat(" .CompilerOptions\t = '{0}{1} {2}'\r\n", Arguments, AdditionalArguments, myUEGlobalCompilerDefinitions);
                FASTBuild.InternalDebugLog("GetBffArguments::ToString(): {0}", output.ToString());
                return output.ToString();
            }

            public override string ToString()
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("Compiler('{0}')\n{{\n", Alias);
                sb.AppendFormat("\t.Executable\t\t            = '{0}' \n " +
                                "\t.ExtraFiles\t\t  = {{ {1} }}\n",
                                ExecPath,
                                string.Join("\n\t\t\t", GetExtraFiles().Select(e => "\t\t'" + e + "'")));

                sb.Append("}\n");

                FASTBuild.InternalDebugLog("COMPILER TOSTRING: {0}", sb.ToString());

                return sb.ToString();
            }

            private IEnumerable<string> GetExtraFiles()
            {
                if (Type != CompilerType.MSVC) return Enumerable.Empty<string>();

                var output = new List<string>();

                string msvcVer = "";
                switch(WindowsPlatform.Compiler)
                {
                    case WindowsCompiler.VisualStudio2013:
                        msvcVer = "120";
                        break;
                    case WindowsCompiler.VisualStudio2015:
                        msvcVer = "140";
                        break;
                    case WindowsCompiler.VisualStudio2017:
                        msvcVer = "150";
                        break;
                };

                string msIncludeDir;
                string compilerDir = Path.GetDirectoryName(ExecPath);
                if (Platform == CompilerPlatform.Unknown)
                {
                    DetectPlatform(compilerDir); // Should already be done by Linker
                }

                if (Type == CompilerType.MSVC)
                {
                    output.Add(compilerDir + "\\1033\\clui.dll");
                    if (WindowsPlatform.Compiler == WindowsCompiler.VisualStudio2013)
                    {
                        output.Add(compilerDir + "\\c1ast.dll");
                        output.Add(compilerDir + "\\c1xxast.dll");
                    }
                    output.Add(compilerDir + "\\c1xx.dll");
                    output.Add(compilerDir + "\\c2.dll");
                    output.Add(compilerDir + "\\c1.dll");

                    if (compilerDir.Contains("x86_amd64") || compilerDir.Contains("amd64_x86"))
                    {
                        msIncludeDir = "$VSBasePath$\\bin"; //We need to include the x86 version of the includes
                    }
                    else
                    {
                        msIncludeDir = compilerDir;
                    }

                    output.Add(compilerDir + "\\msobj" + msvcVer + ".dll");
                    output.Add(compilerDir + "\\mspdb" + msvcVer + ".dll");
                    output.Add(compilerDir + "\\mspdbsrv.exe");
                    output.Add(compilerDir + "\\mspdbcore.dll");
                    output.Add(compilerDir + "\\mspft" + msvcVer + ".dll");
                }
                return output;
            }

            private void LocaliseCompilerPath()
            {
                string compilerPath = "";
                if (ExecPath.Contains("cl.exe"))
                {
                    string[] compilerPathComponents = ExecPath.Replace('\\', '/').Split('/');
                    int startIndex = Array.FindIndex(compilerPathComponents, row => row == "VC");
                    if (startIndex > 0)
                    {
                        Type = CompilerType.MSVC;
                        compilerPath = "$VSBasePath$";
                        for (int i = startIndex + 1; i < compilerPathComponents.Length; ++i)
                        {
                            compilerPath += "/" + compilerPathComponents[i];
                        }
                    }
                    ExecPath = compilerPath;
                }
                else if (ExecPath.Contains("rc.exe"))
                {
                    Type = CompilerType.RC;
                    string[] compilerPathComponents = ExecPath.Replace('\\', '/').Split('/');
                    compilerPath = "$WindowsSDKBasePath$";
                    int startIndex = Array.FindIndex(compilerPathComponents, row => row == "8.1");
                    if (startIndex > 0)
                    {
                        for (int i = startIndex + 1; i < compilerPathComponents.Length; ++i)
                        {
                            compilerPath += "/" + compilerPathComponents[i];
                        }
                    }
                    ExecPath = compilerPath;
                }
                else if (ExecPath.Contains("orbis-clang.exe"))
                {
                    Type = CompilerType.OrbisClang;
                }
            }
        }

        private class Linker
        {
            public Linker(string execPath)
            {
                ExecPath = execPath;
                LocaliseLinkerPath();
                DetectPlatform(ExecPath);
            }

            private void LocaliseLinkerPath()
            {
                string compilerPath = "";
                if (ExecPath.Contains("link.exe") || ExecPath.Contains("lib.exe"))
                {
                    string[] compilerPathComponents = ExecPath.Replace('\\', '/').Split('/');
                    int startIndex = Array.FindIndex(compilerPathComponents, row => row == "VC");
                    if (startIndex > 0)
                    {
                        Type = CompilerType.MSVC;
                        compilerPath = "$VSBasePath$";
                        for (int i = startIndex + 1; i < compilerPathComponents.Length; ++i)
                        {
                            compilerPath += "/" + compilerPathComponents[i];
                        }
                    }
                    ExecPath = compilerPath;
                }
                else if (ExecPath.Contains("orbis-snarl.exe"))
                {
                    Type = CompilerType.OrbisSnarl;
                }
                else if (ExecPath.Contains("orbis-clang.exe"))
                {
                    Type = CompilerType.OrbisClang;
                }
            }

            public static bool IsKnownLinker(string args)
            {
                return args.Contains("lib.exe") || args.Contains("link.exe") || args.Contains("orbis-clang.exe") || args.Contains("orbis-snarl.exe");
            }

            protected void DetectPlatform(string Path)
            {
                // TODO: PS4 and other platform support
                if (Path.Contains("amd64") || Path.Contains("x64"))
                {
                    Platform = CompilerPlatform.amd64;
                }
                else if (Path.Contains("x86"))
                {
                    Platform = CompilerPlatform.x86;
                }
                else
                {
                    Platform = CompilerPlatform.Unknown;
                    Log.TraceInformation("Error, unknown platform type when parsing linker Path: {0}", Path);
                    throw new Exception("Error, unknown platform type! Fix it");
                }
            }

            public string ExecPath { get; set; }
            public CompilerType Type;
            public CompilerPlatform Platform = CompilerPlatform.Unknown;

            private List<string> _allowedInputTypes;
            public virtual List<string> AllowedInputTypes
            {
                get
                {
                    if (_allowedInputTypes != null) return _allowedInputTypes;

                    switch (Type)
                    {
                        case CompilerType.MSVC:
                            _allowedInputTypes = new List<string>() { ".response", ".lib", ".obj" };
                            break;
                        case CompilerType.OrbisClang:
                        case CompilerType.OrbisSnarl:
                            _allowedInputTypes = new List<string>() { ".response", ".a" };
                            break;
                        case CompilerType.RC:
                        case CompilerType.Clang:
                        default:
                            break;
                    };

                    return _allowedInputTypes;
                }
            }

            private List<string> _allowedOutputTypes;
            public virtual List<string> AllowedOutputTypes
            {
                get
                {
                    if (_allowedOutputTypes != null) return _allowedOutputTypes;

                    switch( Type )
                    {
                        case CompilerType.MSVC:
                            _allowedOutputTypes = new List<string>() { ".dll", ".lib", ".exe" };
                            break;
                        case CompilerType.OrbisClang:
                        case CompilerType.OrbisSnarl:
                            _allowedOutputTypes = new List<string>() { ".self", ".a", ".so" };
                            break;
                        case CompilerType.RC:
                        case CompilerType.Clang:
                        default:
                            break;
                    };

                    return _allowedOutputTypes;
                }
            }

            public virtual string InputFileRegex
            {
                get
                {                    
                    return Type == CompilerType.OrbisClang ? "(?<=\")(.*?)(?=\")" : "(?<=@\")(.*?)(?=\")";
                }
            }

            public virtual string OutputFileRegex
            {
                get
                {
                    switch (Type)
                    {
                        case CompilerType.MSVC:
                        case CompilerType.RC:
                            return "(?<=(/OUT: \"|/OUT:\"))(.*?)(?=\")";
                        case CompilerType.Clang:
                        case CompilerType.OrbisClang:
                            return "(?<=(-o \"|-o\"))(.*?)(?=\")";
                        case CompilerType.OrbisSnarl:
                            return "(?<=\")(.*?.a)(?=\")";
                        default:
                            break;
                    };
                    return "";
                }
            }

            public virtual string ImportLibraryRegex
            {
                get
                {
                    if (Type == CompilerType.MSVC || Type == CompilerType.RC)
                    {
                        return "(?<=(/IMPLIB: \"|/IMPLIB:\"))(.*?)(?=\")";
                    }
                    return "";
                }
            }

            public virtual string PCHOutputRegex
            {
                get
                {
                    if (Type == CompilerType.MSVC || Type == CompilerType.RC)
                    {
                        return "(?<=(/Fp \"|/Fp\"))(.*?)(?=\")";
                    }
                    else
                    {
                        return "(?<=(/Fp \"|/Fp\"))(.*?)(?=\")";
                    }
                }
            }

        }

        public abstract class FastbuildAction
        {
            public FastbuildAction()
            {
                Dependencies = new List<FastbuildAction>();
            }
            public Action Action { get; set; }
            public string NodeType { get; set; }
            public int AliasIndex { get; set; }
            public List<FastbuildAction> Dependencies { get; set; }

            public string Alias
            {
                get
                {
                    return NodeType + "-" + AliasIndex;
                }
            }
        }

        public static List<FastbuildAction> CompilationActions { get; set; }
        public static List<FastbuildAction> LinkerActions { get; set; }
        public static List<FastbuildAction> AllActions => CompilationActions.Concat(LinkerActions).ToList();

        private class ExecAction : FastbuildAction
        {
            public ExecAction()
            {
                NodeType = "Exec";
            }
            public Linker Linker;
            public string Arguments { get; set; }

            private bool ParseOutputViaRegex(string regex, string Arguments, string FailureString, out string OutputString)
            {
                // TODO: For some reason, FastBuild is wanting us to transform this to a relative path, relative to UnrealEngine\Engine\Source
                // TODO: Figure out how to keep it as an absolute path instead?
                Regex r = new Regex(regex, RegexOptions.IgnoreCase);
                Match m = r.Match(Arguments);
                if ( m.Success )
                {
                    Group g = m.Groups[1]; // Group[0] is the full string we captured, Group[1] should be the string inside quotes.
                    string CapturedString = g.ToString();

                    FASTBuild.InternalDebugLog("REGEX TOTAL CAPTURE: {0}", m.Groups[1].ToString());
                    FASTBuild.InternalDebugLog("REGEX INSIDE CAPTURE: {0}", CapturedString);

                    var WithoutQuotes = CapturedString.Replace("\"", ""); // Remove double-quotes

                    const string UEEngine = "UnrealEngine\\Engine";
                    int UEEngineLocation = WithoutQuotes.IndexOf(UEEngine);
                    UEEngineLocation += UEEngine.Length;

                    string AfterEngineString = WithoutQuotes.Substring(UEEngineLocation);

                    OutputString = "..\\" + AfterEngineString;

                    return true;
                }

                OutputString = FailureString;

                return false;
            }

            private bool ParseLinkerOutput( string Arguments, string FailureString, out string OutputString)
            {
                // /OUT:"C:\Users\layla\Desktop\Insightful\dev\UE4\UnrealEngine\Engine\Binaries\Win64\UE4Editor-Matinee.dll"
                return ParseOutputViaRegex("/OUT:(\"[^\"\r\n]*\")", Arguments, FailureString, out OutputString);
            }

            private bool ParseCompilerOutput( string Arguments, string FailureString, out string OutputString )
            {
                // /Fo"C:\Users\layla\Desktop\Insightful\dev\UE4\UnrealEngine\Engine\Binaries\Win64\UE4Editor-Matinee.obj"
                return ParseOutputViaRegex("/Fo(\"[^\"\r\n]*\")", Arguments, FailureString, out OutputString);
            }

            public override string ToString()
            {
                string OutputFile = Alias + "-Output.exe";

                string NewOutputFile = "";
                bool bParsedCompilerRegexOK = ParseCompilerOutput(Arguments, OutputFile, out NewOutputFile);
                if (!bParsedCompilerRegexOK)
                {
                    FASTBuild.InternalDebugLog("Failed to parse Compiler Regex from Arguments.");

                    bool bParsedLinkerRegexOK = ParseLinkerOutput(Arguments, OutputFile, out NewOutputFile);
                    if (!bParsedLinkerRegexOK)
                    {
                        FASTBuild.InternalDebugLog("Failed to parse Linker Regex from Arguments.");
                        throw new Exception("Error! Failed to parse either linker or compiler regex from Arguments.");
                    }
                    else
                    {
                        FASTBuild.InternalDebugLog("Parsed Linker Regex from Arguments: {0}", NewOutputFile);
                    }
                }
                else
                {
                    FASTBuild.InternalDebugLog("Parsed Compiler Regex from Arguments: {0}", NewOutputFile);
                }

                OutputFile = NewOutputFile;

                //Carry on here. Need to strip input/output out, or change to not require in/out
                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("Exec('{0}')\n{{\n", Alias);
                sb.AppendFormat("\t.ExecExecutable\t\t  = '{0}' \n " +
                                "\t.ExecArguments\t\t  = '{1}'\n" +
                                "\t.DoNotUseOutput = true\n" +
                              //"\t.ExecOutput\t\t = '{2}-{0}'\n",
                              //"\t.ExecOutput\t\t = '{2}-Output.exe'\n",
                                "\t.ExecOutput\t\t = '{2}'\n",
                                Linker.ExecPath,
                                Arguments, OutputFile /*Alias*/);

                if (Dependencies.Any())
                {
                    sb.AppendFormat("\t.PreBuildDependencies\t\t= {{ {0} }} \n ",
                        string.Join("\n\t\t\t", Dependencies.Select(d => "'" + d.Alias + "'").Distinct()));
                }
                sb.Append("}\n");

                FASTBuild.InternalDebugLog( "EXEC TOSTRING: {0}", sb.ToString() );

                return sb.ToString();
            }
        }

        private class LinkAction : ExecAction
        {
            public LinkAction()
            {
                NodeType = "DLL";
            }

            public string OutputLibrary { get; set; }
            public string ImportLibrary { get; set; }

            public List<string> Inputs { get; set; }

            public bool LocalOnly { get; set; }

            public LinkerType LinkerType
            {
                get
                {
                    // TODO: Support linux .so and other platforms extensions
                    return OutputLibrary.EndsWith(".lib") ? LinkerType.Static : LinkerType.Dynamic;
                }
            }

            public string AdditionalArgumentsToString()
            {
                string AdditionalArguments = "";
                if (ForceAppendBaseIncludePaths)
                {
                    string PlatformLibraryPath = "error-string";
                    switch (Linker.Platform)
                    {
                        case CompilerPlatform.amd64:
                            PlatformLibraryPath = "$BaseLibraryPathsx64$";
                            break;
                        case CompilerPlatform.x86:
                            PlatformLibraryPath = "$BaseLibraryPathsx86$";
                            break;
                        case CompilerPlatform.Unknown:
                        default:
                            throw new Exception("ToString::Error, unknown platform type! Fix it");
                    };
                    AdditionalArguments = (" " + PlatformLibraryPath);
                }
                return AdditionalArguments;
            }

            public override string ToString()
            {

                string myLinkString = ( LinkerType == LinkerType.Static ) ? ToLibString() : ToDllString();
                FASTBuild.InternalDebugLog( "Linker::ToString(): {0}", myLinkString );
                return myLinkString;
            }

            private string ToLibString()
            {
                var linkerArgs = Arguments;

                List<string> libraryInputs = null;
                List<string> systemInputs = null;

                if (Inputs.Count == 1)
                {
                    var responseFile = Inputs[0];
                    linkerArgs = linkerArgs.Replace(Inputs[0], "%1");
                    linkerArgs = linkerArgs.Replace(OutputLibrary, "%2");
                    GetResponseFileInputs(ref responseFile, FASTBuild.UseSinglePassCompilation ? FASTBuild.AllActions : FASTBuild.LinkerActions, out libraryInputs, out systemInputs);

                    //If we found resolvable references in the response file, use those as the inputs and re-add the response file.
                    //If not, use the response file itself just to stop fbuild erroring.
                    if (libraryInputs.Any())
                    {
                        linkerArgs = linkerArgs.Replace("%1", responseFile + "\" \"%1");
                    }
                    else
                    {
                        libraryInputs.Add(responseFile);
                    }
                }
                else
                {
                    throw new Exception("ERROR: Don't know what to do here yet");
                }

                var sb = new StringBuilder();
                sb.AppendFormat("Library('{0}')\n{{\n", Alias);

                //We do not provide file inputs (we use separate ObjectLists) for simplicity, so we do not need to specify an actually defined compiler.
                sb.AppendFormat("\t.Compiler\t\t = 'Null'\n");
                sb.AppendFormat("\t.CompilerOptions\t\t = '%1 %2 %3'\n");
                sb.AppendFormat("\t.CompilerOutputPath\t\t = ' '\n");
                sb.AppendFormat("\t.Librarian\t\t            = '{0}' \n " +
                                "\t.LibrarianOutput\t\t  =  '{1}' \n",
                                Linker.ExecPath,
                                OutputLibrary);

                if (FASTBuild.ForceAppendBaseIncludePaths)
                {
                    string AdditionalArguments = AdditionalArgumentsToString();
                    sb.AppendFormat("\t.LibrarianOptions\t\t = '{0}{1}'\n", linkerArgs, AdditionalArguments);
                }
                else
                {
                    sb.AppendFormat("\t.LibrarianOptions\t\t = '{0}'\n", linkerArgs);
                }

                if (systemInputs.Any())
                {
                    sb.AppendFormat("\t.LibrarianOptions\t\t   {0} \n ",
                    string.Join("\n\t\t\t", systemInputs.Select(d => "+ ' " + d + "'")));
                }
                if (libraryInputs.Any())
                {
                    sb.AppendFormat("\t.LibrarianAdditionalInputs\t\t   = {{ {0} }} \n ",
                                        string.Join("\n\t\t\t", libraryInputs.Select(d => "'" + d + "'")));
                }

                sb.Append("}\n");

                return sb.ToString();
            }

            public string ToDllString()
            {
                string linkerArgs = Arguments;

                List<string> libraryInputs = null;
                List<string> systemInputs = null;

                if (Inputs.Count == 1)
                {
                    var responseFile = Inputs[0];
                    linkerArgs = linkerArgs.Replace(Inputs[0], "%1");
                    linkerArgs = linkerArgs.Replace(OutputLibrary, "%2");
                    GetResponseFileInputs(ref responseFile, FASTBuild.UseSinglePassCompilation ? FASTBuild.AllActions : FASTBuild.LinkerActions, out libraryInputs, out systemInputs);

                    //If we found resolvable references in the response file, use those as the inputs and re-add the response file.
                    //If not, use the response file itself just to stop fbuild erroring.
                    if (libraryInputs.Any())
                    {
                        linkerArgs = linkerArgs.Replace("%1", responseFile + "\" \"%1");
                    }
                    else
                    {
                        libraryInputs.Add(responseFile);
                    }
                }
                else
                {
                    throw new Exception("ERROR: Don't know what to do here yet");
                }

                linkerArgs = linkerArgs.Replace(OutputLibrary, "%2");

                var sb = new StringBuilder();
                sb.AppendFormat("DLL('{0}')\n{{\n", Alias);
                sb.AppendFormat("\t.LinkerLinkObjects\t\t = false\n");
                sb.AppendFormat("\t.Linker\t\t            = '{0}' \n " +
                                "\t.LinkerOutput\t\t  =  '{1}' \n",
                                Linker.ExecPath,
                                OutputLibrary);


                if (FASTBuild.ForceAppendBaseIncludePaths)
                {
                    string AdditionalArguments = AdditionalArgumentsToString();
                    sb.AppendFormat("\t.LinkerOptions\t\t = '{0}{1}'\n", linkerArgs, AdditionalArguments);
                }
                else
                {
                    sb.AppendFormat("\t.LinkerOptions\t\t = '{0}'\n", linkerArgs);
                }

                sb.AppendFormat("\t.Libraries\t\t   = {{ {0} }} \n ",
                                        string.Join("\n\t\t\t", libraryInputs.Select(d => "'" + d + "'")));

                sb.Append("}\n");

                return sb.ToString();
            }


            private void GetResponseFileInputs(ref string file, List<FastbuildAction> resolvableDependencies, out List<string> inputs, out List<string> systemInputs)
            {
                var lines = File.ReadAllLines(file);

                inputs = new List<string>();
                systemInputs = new List<string>();
                var unresolvedLines = lines.Select(n => n).ToList();

                foreach (var line in lines)
                {
                    // Strip the additional quotes from the response file
                    var resolvedLine = line.Replace("\"", "");

                    // UE4 Provides response outputs to project files as absolute paths.
                    // A quick way to check if something is a system include is whether it is a rooted path or not
                    if (!Path.IsPathRooted(resolvedLine)) continue;

                    // We should resolve project includes to see if we're building the node for that this pass as well
                    FastbuildAction matchingDependency = null;

                    foreach (var dependency in resolvableDependencies)
                    {
                        if (dependency is LinkAction)
                        {
                            var linkDependency = (LinkAction)dependency;
                            if (linkDependency.ImportLibrary == resolvedLine || linkDependency.OutputLibrary == resolvedLine)
                            {
                                matchingDependency = dependency;
                                break;
                            }
                        }
                        else if (dependency is ObjectGroup)
                        {
                            var objectDependency = (ObjectGroup)dependency;
                            if (objectDependency.ActionOutputs.Contains(resolvedLine) ||
                                (objectDependency.PchOptions != null &&
                                 Path.ChangeExtension(objectDependency.PchOptions.Output, objectDependency.OutputExt) == resolvedLine))
                            {
                                matchingDependency = dependency;
                                break;
                            }
                        }
                    }

                    //if (FASTBuildConfiguration.UseSinglePassCompilation || matchingDependency != null)
                    {
                        unresolvedLines.Remove(line);
                        if (matchingDependency != null)
                        {
                            resolvedLine = matchingDependency.Alias;
                        }
                        inputs.Add(resolvedLine);
                    }
                }

                FASTBuild.InternalDebugLog("GetResponseFileInputs({0})::ToString(): writing FBUILD file: {1}.fbuild; Contents: \n{3}", file, file, unresolvedLines);

                file += ".fbuild";
                File.WriteAllLines(file, unresolvedLines);
            }

        }

        private class ObjectGroup : FastbuildAction
        {
            public ObjectGroup()
            {
                ActionInputs = new Dictionary<Action, string>();
                NodeType = "ObjG";
            }

            public Dictionary<Action, string> ActionInputs { get; set; }

            public IEnumerable<string> ActionOutputs
            {
                get
                {
                    return ActionInputs.Select(n => OutputPath + Path.GetFileNameWithoutExtension(n.Value) + OutputExt);
                }
            }

            public string MatchHash { get; set; }
            public string CompilerArguments { get; set; }
            public Compiler ObjectCompiler;
            public string OutputPath { get; set; }
            public string OutputExt { get; set; }
            public bool LocalOnly { get; set; }
            public PCHOptions PchOptions { get; set; }

            public override string ToString()
            {
                StringBuilder sb = new StringBuilder();

                string AdditionalArguments = (ForceAppendBaseIncludePaths && (ObjectCompiler.Type == CompilerType.MSVC)) ? (" " + "$BaseIncludePaths$") : "";

                sb.AppendFormat("ObjectList('{0}')\n{{\n", Alias);

                sb.AppendFormat("\t.Compiler\t\t            = '{0}' \n " +
                                "\t.CompilerInputFiles\t\t  = {{ {1} }}\n" +
                                "\t.CompilerOutputPath\t\t  = '{2}'\n" +
                                "\t.CompilerOutputExtension\t\t  = '{3}'\n" +
                                "\t{4}\n", //"\t.CompilerOptions\t = '{4}'\n",
                                ObjectCompiler.Alias,
                                string.Join("\n\t\t\t", ActionInputs.Select(a => "'" + a.Value + "'")),
                                OutputPath,
                                OutputExt,
                                ObjectCompiler.GetBffArguments(CompilerArguments, AdditionalArguments));

                if (PchOptions != null)
                {
                    sb.AppendFormat("\t.PCHInputFile\t\t            = '{0}' \n " +
                                    "\t.PCHOutputFile\t\t            = '{1}' \n " +
                                    "\t.PCHOptions\t\t            = '{2}{3}' \n ",
                                    PchOptions.Input,
                                    PchOptions.Output,
                                    PchOptions.Options,
                                    AdditionalArguments
                                    );
                }

                var dependencyString = "";
                if (Dependencies.Any())
                {
                    foreach (var dependency in Dependencies)
                    {
                        dependencyString += "\n\t\t\t '" + dependency.Alias + "'";
                    }
                    //sb.AppendFormat("\t.PreBuildDependencies\t\t   = {{ {0} }} \n ",
                    //string.Join("\n\t\t\t", Dependencies.Select(d => "'" + d.Alias + "'")));
                }

                if (!string.IsNullOrEmpty(dependencyString))
                {
                    sb.AppendFormat("\t.PreBuildDependencies\t\t   = {{ {0} }} \n ", dependencyString);
                }

                sb.Append("}\n");

                FASTBuild.InternalDebugLog("COMPILER/OBJECT::ToString(): {0}", sb.ToString());

                return sb.ToString();
            }
        }

        private class PCHOptions
        {
            public string Options;
            public string Input;
            public string Output;
        }

        /// <summary>
        /// Used when debugging Actions outputs all action return values to debug out
        /// </summary>
        /// <param name="sender"> Sending object</param>
        /// <param name="e">  Event arguments (In this case, the line of string output)</param>
        static protected void ActionDebugOutput(object sender, DataReceivedEventArgs e)
        {
            var Output = e.Data;
            if (Output == null)
            {
                return;
            }

            Log.TraceInformation(Output);
        }

        internal static ExecutionResult ExecuteLocalActions(List<Action> InLocalActions, Dictionary<Action, ActionThread> InActionThreadDictionary, int TotalNumJobs)
        {
            // Time to sleep after each iteration of the loop in order to not busy wait.
            const float LoopSleepTime = 0.1f;

            ExecutionResult LocalActionsResult = ExecutionResult.TasksSucceeded;

            while (true)
            {
                // Count the number of pending and still executing actions.
                int NumUnexecutedActions = 0;
                int NumExecutingActions = 0;
                foreach (Action Action in InLocalActions)
                {
                    ActionThread ActionThread = null;
                    bool bFoundActionProcess = InActionThreadDictionary.TryGetValue(Action, out ActionThread);
                    if (bFoundActionProcess == false)
                    {
                        NumUnexecutedActions++;
                    }
                    else if (ActionThread != null)
                    {
                        if (ActionThread.bComplete == false)
                        {
                            NumUnexecutedActions++;
                            NumExecutingActions++;
                        }
                    }
                }

                // If there aren't any pending actions left, we're done executing.
                if (NumUnexecutedActions == 0)
                {
                    break;
                }

                // If there are fewer actions executing than the maximum, look for pending actions that don't have any outdated
                // prerequisites.
                foreach (Action Action in InLocalActions)
                {
                    ActionThread ActionProcess = null;
                    bool bFoundActionProcess = InActionThreadDictionary.TryGetValue(Action, out ActionProcess);
                    if (bFoundActionProcess == false)
                    {
                        if (NumExecutingActions < Math.Max(1, MaxActionsToExecuteInParallel))
                        {
                            // Determine whether there are any prerequisites of the action that are outdated.
                            bool bHasOutdatedPrerequisites = false;
                            bool bHasFailedPrerequisites = false;
                            foreach (FileItem PrerequisiteItem in Action.PrerequisiteItems)
                            {
                                if (PrerequisiteItem.ProducingAction != null && InLocalActions.Contains(PrerequisiteItem.ProducingAction))
                                {
                                    ActionThread PrerequisiteProcess = null;
                                    bool bFoundPrerequisiteProcess = InActionThreadDictionary.TryGetValue(PrerequisiteItem.ProducingAction, out PrerequisiteProcess);
                                    if (bFoundPrerequisiteProcess == true)
                                    {
                                        if (PrerequisiteProcess == null)
                                        {
                                            bHasFailedPrerequisites = true;
                                        }
                                        else if (PrerequisiteProcess.bComplete == false)
                                        {
                                            bHasOutdatedPrerequisites = true;
                                        }
                                        else if (PrerequisiteProcess.ExitCode != 0)
                                        {
                                            bHasFailedPrerequisites = true;
                                        }
                                    }
                                    else
                                    {
                                        bHasOutdatedPrerequisites = true;
                                    }
                                }
                            }

                            // If there are any failed prerequisites of this action, don't execute it.
                            if (bHasFailedPrerequisites)
                            {
                                // Add a null entry in the dictionary for this action.
                                InActionThreadDictionary.Add(Action, null);
                            }
                            // If there aren't any outdated prerequisites of this action, execute it.
                            else if (!bHasOutdatedPrerequisites)
                            {
                                ActionThread ActionThread = new ActionThread(Action, JobNumber, TotalNumJobs);
                                ActionThread.Run();

                                InActionThreadDictionary.Add(Action, ActionThread);

                                NumExecutingActions++;
                                JobNumber++;
                            }
                        }
                    }
                }

                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(LoopSleepTime));
            }

            return LocalActionsResult;
        }

        public static ExecutionResult ExecuteActionsInternal(List<Action> Actions)
        {
            ExecutionResult FASTBuildResult = ExecutionResult.TasksSucceeded;
            CompilationActions = null;
            LinkerActions = null;

            if (Actions.Count <= 0)
            {
                return FASTBuildResult;
            }

            if (IsAvailable() == false)
            {
                return ExecutionResult.Unavailable;
            }


            List<Action> unassignedObjects = new List<Action>();
            List<Action> unassignedLinks = new List<Action>();
            List<Action> miscActions = Actions.Where(a => a.ActionType != ActionType.Compile && a.ActionType != ActionType.Link).ToList();
            Dictionary<Action, FastbuildAction> fastbuildActions = new Dictionary<Action, FastbuildAction>();

            DeresponsifyActions(Actions);

            CompilationActions = GatherActionsObjects(Actions.Where(a => a.ActionType == ActionType.Compile), ref unassignedObjects, ref fastbuildActions);
            LinkerActions = GatherActionsLink(Actions.Where(a => a.ActionType == ActionType.Link), ref unassignedLinks, ref fastbuildActions);
            ResolveDependencies(CompilationActions, LinkerActions, fastbuildActions);

            var compilerActionsCount = 0;
            var linkerActionsCount = 0;
            if (CompilationActions != null) compilerActionsCount = CompilationActions.Count;
            if (LinkerActions != null) linkerActionsCount = LinkerActions.Count;

            Log.TraceInformation("-------------------------------------");
            Log.TraceInformation("----- FASTBuild Actions Summary -----");
            Log.TraceInformation("-------------------------------------");
            Log.TraceInformation("");
            Log.TraceInformation("Total Actions: " + Actions.Count + " - Unassigned Objects: " + unassignedObjects.Count);
            Log.TraceInformation("Misc Actions: " + miscActions.Count);
            Log.TraceInformation("Compile Actions: \t" + compilerActionsCount);
            Log.TraceInformation("Link Actions: \t" + linkerActionsCount);

            if (unassignedLinks.Count > 0)
            {
                throw new Exception("Error, unaccounted for lib! Cannot guarantee there will be no prerequisite issues. Fix it");
            }

            if (unassignedObjects.Count > 0)
            {
                var actionThreadDictionary = new Dictionary<Action, ActionThread>();
                ExecuteLocalActions(unassignedObjects, actionThreadDictionary, unassignedObjects.Count);
            }

            if (FASTBuild.UseSinglePassCompilation)
            {
                FASTBuildResult = RunFBuild(BuildStep.CompileAndLink, GenerateFBuildFileString(BuildStep.CompileAndLink, CompilationActions.Concat(LinkerActions).ToList()));
            }
            else
            {
                if (CompilationActions.Any())
                {
                    FASTBuildResult = RunFBuild(BuildStep.CompileObjects, GenerateFBuildFileString(BuildStep.CompileObjects, CompilationActions));
                }

                if (FASTBuildResult == ExecutionResult.TasksSucceeded)
                {
                    if (LinkerActions != null && LinkerActions.Any())
                    {
                        //if (!BuildConfiguration.bFastbuildNoLinking)
                        {
                            FASTBuildResult = RunFBuild(BuildStep.Link, GenerateFBuildFileString(BuildStep.Link, LinkerActions)); // Run Linker Step
                        }
                    }
                }
            }

            return FASTBuildResult;
        }

        private static void ResolveDependencies(List<FastbuildAction> objectGroups, List<FastbuildAction> linkGroups, Dictionary<Action, FastbuildAction> fastbuildActions)
        {
            var actions = objectGroups.Concat(linkGroups);

            foreach (var action in fastbuildActions)
            {
                var prerequisites = action.Key.PrerequisiteItems
                    .Where(n => n.ProducingAction != null && (n.ProducingAction.ActionType == action.Key.ActionType));

                foreach (var prerequisite in prerequisites)
                {
                    if (fastbuildActions.ContainsKey(prerequisite.ProducingAction))
                    {
                        if (!action.Value.Dependencies.Contains(fastbuildActions[prerequisite.ProducingAction]))
                        {
                            action.Value.Dependencies.Add(fastbuildActions[prerequisite.ProducingAction]);
                        }
                    }
                }
            }
        }

        private static string GenerateFBuildFileString(BuildStep step, List<FastbuildAction> actions)
        {
            var sb = new StringBuilder();
            sb.Append(FastBuildCommon.EntryArguments);
            if (step == BuildStep.CompileObjects || step == BuildStep.CompileAndLink)
            {
                sb.Append(GenerateFBuildCompilers());
            }

            if (step == BuildStep.CompileAndLink)
            {
                sb.Append(GenerateFBuildNodeList("DLLListAlias", actions));
                sb.Append(GenerateFBuildTargets(false, actions.Any()));
            }
            else
            {
                if (actions.Count > 0)
                {
                    sb.Append(GenerateFBuildNodeList(step == BuildStep.CompileObjects ? "ObjectsListsAlias" : "DLLListAlias", actions));
                }
                sb.Append(GenerateFBuildTargets(step == BuildStep.CompileObjects && actions.Any(),
                                                        step == BuildStep.Link && actions.Any()));
            }
            return sb.ToString();
        }

        private static string GenerateFBuildCompilers()
        {
            StringBuilder sb = new StringBuilder();

            int objectCount = 0;
            foreach (var compiler in Compilers)
            {
                compiler.Value.Alias = "Compiler-" + objectCount;
                sb.Append(compiler.Value.ToString());
                objectCount++;
            }

            return sb.ToString();
        }
        private static string GenerateFBuildNodeList(string aliasName, IList<FastbuildAction> objectGroups)
        {
            var sb = new StringBuilder();
            var aliases = new List<string>();

            // Ensure nodes are placed before nodes which depend on them.
            bool changed;
            do
            {
                changed = false;
                for (var i = 0; i < objectGroups.Count; ++i)
                {
                    if (!objectGroups[i].Dependencies.Any()) continue;

                    var highest = objectGroups[i].Dependencies.Select(objectGroups.IndexOf).OrderBy(n => n).Last();
                    if (highest > i)
                    {
                        var thisObject = objectGroups[i];
                        objectGroups.RemoveAt(i);
                        objectGroups.Insert(highest, thisObject);
                        changed = true;
                    }
                }
            } while (changed);

            foreach (var objectGroup in objectGroups)
            {
                aliases.Add(objectGroup.Alias);
                sb.Append(objectGroup.ToString());
            }

            if (aliases.Any())
            {
                sb.Append(FastBuildCommon.GetAliasTag(aliasName, aliases));
            }
            return sb.ToString();
        }

        private static string GenerateFBuildTargets(bool anyObjects, bool anyLibs)
        {
            var aliases = new List<string>();
            if (anyObjects)
            {
                aliases.Add("ObjectsListsAlias");
            }
            if (anyLibs)
            {
                aliases.Add("DLLListAlias");
            }
            if (anyObjects || anyLibs)
            {
                return FastBuildCommon.GetAliasTag("all", aliases);
            }
            else
            {
                return "";
            }
        }

        private static List<FastbuildAction> GatherActionsObjects(IEnumerable<Action> compileActions, ref List<Action> unassignedActions, ref Dictionary<Action, FastbuildAction> actionLinks)
        {
            var objectGroup = new List<ObjectGroup>();

            foreach (var action in compileActions)
            {
                var obj = ParseCompilerAction(action);
                if (obj != null)
                {
                    var group = objectGroup.FirstOrDefault(n => n.MatchHash == obj.MatchHash);
                    if (group != null && !FASTBuild.UseSinglePassCompilation)
                    {
                        group.ActionInputs[action] = obj.ActionInputs.FirstOrDefault().Value;
                    }
                    else
                    {
                        obj.AliasIndex = AliasBase++;
                        objectGroup.Add(obj);
                    }
                    actionLinks[action] = obj;
                }
                else
                {
                    Log.TraceInformation("Local Action - \n-Path:" + action.CommandPath + "\n-Args:" + action.CommandArguments);
                    unassignedActions.Add(action);
                }
            }
            return objectGroup.Cast<FastbuildAction>().ToList();
        }

        private static List<FastbuildAction> GatherActionsLink(IEnumerable<Action> localLinkActions, ref List<Action> unassignedActions, ref Dictionary<Action, FastbuildAction> actionLinks)
        {
            var linkActions = new List<ExecAction>();

            foreach (var action in localLinkActions)
            {
                var linkAction = ParseLinkAction(action);
                if (linkAction != null)
                {
                    linkActions.Add(linkAction);
                    linkAction.AliasIndex = AliasBase++;
                    actionLinks[action] = linkAction;
                }
                else
                {
                    Log.TraceInformation("Local Action - \n-Path:" + action.CommandPath + "\n-Args:" + action.CommandArguments);
                    unassignedActions.Add(action);
                }
            }
            return linkActions.Cast<FastbuildAction>().ToList();
        }

        

        private static string LocaliseFilePath(string filepath)
        {
            Uri inputUri = new Uri(filepath);
            Uri currentUri = new Uri(Directory.GetCurrentDirectory());
            Uri relativeInputPath = currentUri.MakeRelativeUri(inputUri);
            return "../" + relativeInputPath.ToString();
        }

        private static ObjectGroup ParseCompilerAction(Action action)
        {
            string[] compatableExtensions = { ".c", ".cpp", ".rc", ".inl" };
            ObjectGroup outputAction = null;
            Compiler compiler;

            if (Compilers.ContainsKey(action.CommandPath))
            {
                compiler = Compilers[action.CommandPath];
            }
            else
            {
                compiler = new Compiler(action.CommandPath);
                Compilers[action.CommandPath] = compiler;
            }

            var inputMatches = Regex.Matches(action.CommandArguments, compiler.InputFileRegex, RegexOptions.IgnoreCase);
            var outputMatch = Regex.Match(action.CommandArguments, compiler.OutputFileRegex, RegexOptions.IgnoreCase);
            var usingPch = action.CommandArguments.Contains("/Yc");

            if (inputMatches.Count > 0)
            {
                var input = "";
                var outputPath = "";
                var outputExt = "";
                var matchHash = "";
                var args = action.CommandArguments;
                PCHOptions pchOptions = null;

                foreach (Match inputMatch in inputMatches)
                {
                    if (compatableExtensions.Any(ext => inputMatch.Value.EndsWith(ext)))
                    {
                        input = inputMatch.Value;
                    }
                    if (!string.IsNullOrWhiteSpace(input))
                        break;
                }

                var output = outputMatch.Value;

                if (usingPch)
                {
                    pchOptions = new PCHOptions();
                    outputMatch = Regex.Match(args, compiler.PCHOutputRegex, RegexOptions.IgnoreCase);
                    pchOptions.Input = input;
                    pchOptions.Output = outputMatch.Value;
                    pchOptions.Options = args;

                    pchOptions.Options = pchOptions.Options.Replace(input, "%1");
                    pchOptions.Options = pchOptions.Options.Replace(outputMatch.Value, "%2");

                    args = args.Replace("/Yc", "/Yu");
                }
                args = args.Replace(" c ", "");
                args = args.Replace(output, "%2");
                args = args.Replace(input, "%1");

                var pathExtSplit = output.Split('.');
                outputPath = Path.GetDirectoryName(output) + Path.DirectorySeparatorChar;
                outputExt = "." + pathExtSplit[pathExtSplit.Length - 2] + "." + pathExtSplit[pathExtSplit.Length - 1];

                if (outputExt == ".h.obj")
                {
                    outputExt = ".obj";
                }

                var dependencies = action.PrerequisiteItems.OrderBy(n => n.AbsolutePath).Select(n => n.AbsolutePath);

                if (pchOptions != null)
                    matchHash = args + outputPath + outputExt + action.CommandPath + pchOptions.Options + pchOptions.Input + pchOptions.Output + dependencies;
                else
                    matchHash = args + outputPath + outputExt + action.CommandPath + dependencies;

                outputAction = new ObjectGroup()
                {
                    MatchHash = matchHash,
                    CompilerArguments = args,
                    OutputPath = outputPath,
                    OutputExt = outputExt,
                    LocalOnly = !action.bCanExecuteRemotely,
                    PchOptions = pchOptions,
                    ObjectCompiler = compiler
                };
                outputAction.ActionInputs[action] = input;
            }
            return outputAction;
        }

        private static ExecAction ParseLinkAction(Action action)
        {
            ExecAction output = null;
            Linker linker;

            if (Linkers.ContainsKey(action.CommandPath))
            {
                linker = Linkers[action.CommandPath];
            }
            else
            {
                linker = new Linker(action.CommandPath);
                Linkers[action.CommandPath] = linker;
            }

            var linkerFound = false;
            if (Linker.IsKnownLinker(action.CommandPath))
            {
                var inputMatchesRegex = Regex.Matches(action.CommandArguments, linker.InputFileRegex, RegexOptions.IgnoreCase);
                var importLibMatchesRegex = Regex.Matches(action.CommandArguments, linker.ImportLibraryRegex, RegexOptions.IgnoreCase);
                var outputMatchesRegex = Regex.Matches(action.CommandArguments, linker.OutputFileRegex, RegexOptions.IgnoreCase);
                var inputMatches = inputMatchesRegex.Cast<Match>().Where(n => linker.AllowedInputTypes.Any(a => n.Value.EndsWith(a))).ToList();
                var outputMatches = outputMatchesRegex.Cast<Match>().Where(n => linker.AllowedOutputTypes.Any(a => n.Value.EndsWith(a))).ToList();
                var importMatches = importLibMatchesRegex.Cast<Match>().ToList();

                if (inputMatches.Count > 0 && outputMatches.Count == 1)
                {
                    linkerFound = true;
                    output = new LinkAction()
                    {
                        Action = action,
                        Linker = linker,
                        Arguments = action.CommandArguments,
                        OutputLibrary = outputMatches[0].Value,
                        ImportLibrary = importMatches.Count > 0 ? importMatches[0].Value : null,
                        Inputs = inputMatches.Select(n => n.Value).ToList(),
                        LocalOnly = !action.bCanExecuteRemotely
                    };
                }
            }

            if (!linkerFound)
            {
                output = new ExecAction()
                {
                    Action = action,
                    Arguments = action.CommandArguments,
                    Linker = linker
                };
            }
            return output;
        }

        private static ExecutionResult RunFBuild(BuildStep step, string bffString)
        {
            ExecutionResult result;
            try
            {
                var watch = Stopwatch.StartNew();
                Log.TraceInformation(step == BuildStep.CompileObjects ? "Building Objects" : "Linking Objects");
                StreamWriter ScriptFile;
                string distScriptFilename = Path.Combine(BuildConfiguration.BaseIntermediatePath, "fbuild.bff");
                FileStream distScriptFileStream = new FileStream(distScriptFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                ScriptFile = new StreamWriter(distScriptFileStream);
                ScriptFile.AutoFlush = true;
                ScriptFile.WriteLine(ActionThread.ExpandEnvironmentVariables(bffString));
                ScriptFile.Flush();
                ScriptFile.Close();
                ScriptFile.Dispose();
                ScriptFile = null;
                result = DispatchFBuild();
                watch.Stop();
                var elapsedMs = watch.ElapsedMilliseconds;
                Log.TraceInformation((step == BuildStep.CompileObjects ? "Object Compilation" : "Object Linking") + " Finished. Execution Time: {0}", elapsedMs);
                
            }
            catch (Exception)
            {
                result = ExecutionResult.TasksFailed;
            }

            return result;
        }

        private static ExecutionResult DispatchFBuild()
        {

			string IvrDevDir = Environment.GetEnvironmentVariable("INSIGHTFUL_DEV_DIR");
			string FastBuildRootPath = Environment.GetEnvironmentVariable("FASTBUILD_ROOT_DIR");
            string FastBuildPath = "";
            bool bDoesFastBuildBinaryExist = false;

			if( !String.IsNullOrEmpty(IvrDevDir))
			{
				// First try INSIGHTFUL_DEV_DIR git copy of FastBuild, if it exists...
				FastBuildPath = Path.Combine(IvrDevDir, "Build/Tools/Windows/FBuild.exe");
				bDoesFastBuildBinaryExist = File.Exists(FastBuildPath);
			}

			if (!bDoesFastBuildBinaryExist)
			{
				// Fallback to installed copy of FastBuild if git copy was not found...
				if (!String.IsNullOrEmpty(FastBuildRootPath))
				{
					FastBuildPath = Path.Combine(FastBuildRootPath, "FBuild.exe");
					bDoesFastBuildBinaryExist = File.Exists(FastBuildPath);
				}
			}

            if (!bDoesFastBuildBinaryExist)
            {
                Log.TraceInformation("FastBuildRootPath: {0}", FastBuildRootPath);
                throw new Exception("Error, FBuild.exe not found in FastBuildRootPath!");
            }
            else
            {
                Log.TraceInformation("Dispatching FastBuild: FastBuildRootPath: {0}", FastBuildRootPath);
            }

            //BuildConfiguration.BaseIntermediatePath
            string BuildWorkingDirectory = Path.GetFullPath(".");
            Log.TraceInformation("FastBuild Working Directory: {0}", BuildWorkingDirectory);

            ProcessStartInfo PSI = new ProcessStartInfo(FastBuildPath, ""
                + (EnableDistribution ? "-dist " : "") + (UseIDEMode ? "-ide " : "") + (ShowBuildSummary ? "-summary " : "")
                + (EnableBuildReport ? "-report " : "") + (EnableVerboseDebugging ? "-verbose -j0 " : "")
                + (EnableShowCommandLinesInvoked ? "-showcmds " : "") + (EnableShowDefinedTargets ? "-showtargets " : "")
                + (EnableCache ? (EnableCacheGenerationMode ? "-cache " : "-cacheread ") : "")
                + (ContinueOnError ? "-nostoponerror " : "")
                + (EnableMonitorAPIMode ? "-monitor " : "")
               + " -config " + BuildConfiguration.BaseIntermediatePath + "/fbuild.bff");

			// FIX For FastBuild 0.96 Environment Variable Preference Bug:
			// https://github.com/fastbuild/fastbuild/issues/499
			PSI.EnvironmentVariables["FASTBUILD_CACHE_PATH"] = "%INSIGHTFUL_DEV_DIR%\\Build\\Output\\.fbuild.cache";

			PSI.RedirectStandardOutput = true;
            PSI.RedirectStandardError = true;
            PSI.UseShellExecute = false;
            PSI.CreateNoWindow = true;
            PSI.WorkingDirectory = BuildWorkingDirectory;
            Process NewProcess = new Process();
            NewProcess.StartInfo = PSI;
            var output = new DataReceivedEventHandler(ActionDebugOutput);
            NewProcess.OutputDataReceived += output;
            NewProcess.ErrorDataReceived += output;
            NewProcess.Start();
            NewProcess.BeginOutputReadLine();
            NewProcess.BeginErrorReadLine();
            NewProcess.WaitForExit();
            NewProcess.OutputDataReceived -= output;
            NewProcess.ErrorDataReceived -= output;

            return NewProcess.ExitCode == 0 ? ExecutionResult.TasksSucceeded : ExecutionResult.TasksFailed;
        }

        public static bool IsDisabled()
        {
            return !IsEnabled; // Warning: disabling this is at a later stage than where PCH usage is decided. If you disable this here, PCH files will be disabled.
        }

        public static bool IsAvailable()
        {
            if( IsDisabled() )
            {
                return false;
            }

			return DoesFastBuildExist();
        }

        // <----- ActionExecutor Members ----->
        static private int MaxActionsToExecuteInParallel = 0;
        static private int JobNumber;

        public override string Name
        {
            get { return "FastBuild"; }
        }

        public override bool ExecuteActions(List<Action> Actions)
        {
            ExecutionResult result = ExecuteActionsInternal(Actions);
            switch (result)
            {
                case ExecutionResult.TasksSucceeded:
                    return true;
                case ExecutionResult.TasksFailed:
                    return false;
                case ExecutionResult.Unavailable:
                default:
                    return false;
            };
        }

    }
}
