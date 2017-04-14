## Forked from: https://github.com/ClxS/FASTBuild-UE4

## Still in progress so beware of potential issues. I also recommend checking out an alternative here: https://github.com/liamkf/Unreal_FASTBuild

There's been a bit of interest in all the steps required to get fastbuild to compile Unreal Engine 4, so we thought we'd release our 4.15 updates to ClxS's original FastBuild.cs.
For reference we're using a heavily modified UnrealEngine 4.15 branch, and heavily modified v0.93 fastbuild. Neither of these things should affect usage of this script, however.
This has been tested on Windows builds with distribution enabled. We have not yet tested caching support.

# Setting up FASTBuild

We create a windows Environment variable, FASTBUILD_ROOT_DIR, for the fastbuild installation. This script reads the location from that environment variable.

* * * * FASTBuild
* * * * * FBuild.exe
* * * * * SDK 

Inside your SDK, setup junction links to your Windows SDK folder, as well as your Visual Studio 2015 folder. These junction links make sure that multiple developers will have the same path for their Visual Studio folder - which removes a bit reason for cache hash mismatches.

# Modifying the Engine

There are a few files in the UnrealBuildTool project we need to modify, these are:
- Configuration/BuildConfiguration.cs
- Configuration/UEBuildPlatform.cs
- System/ActionGraph.cs
- Windows/UEBuildWindows.cs

We also need to add a new file to generate a BFF file from the provided actions list. For this, I'm only going to focus on the Windows platform.

### Adding FASTBuild classes

First step is to add our FASTBuild C# class. Place the FastBuild.cs file into UnrealEngine/Engine/Source/Programs/UnrealBuildTool/System

### Configuration/BuildConfiguration.cs

- Add the following properties to the top of the file.

```
// --> FASTBuild

/// <summary>
/// Whether FASTBuild may be used.
/// </summary>
[XmlConfig]
public static bool bAllowFastbuild;

// <-- FASTBuild
```

- Add the following to the very bottom of the LoadDefaults method.

```
// --> FASTBuild

bAllowFastbuild = true;

// <-- FASTBuild
```

Finally, add this to the ValidateConfiguration method near similar lines for XGE/Distcc/SNDBS

```
// --> FASTBuild
if( !BuildPlatform.CanUseFastbuild() )
{
    bAllowFastbuild = false;
}

if ( bAllowFastBuild )
{
  // PCH Files are breaking the build in UE4.15 because they are not including the proper extra defines, so we have two options:
  // #1. Disable them here with bUsePCHFiles = false;
  // #2. Change line 1188 in UEBuildModule.cs from "CPPCompileEnvironment.Config.Definitions.Clear();" to the following:
  // if (!BuildPlatform.CanUseFastBuild()) // This breaks FASTBuild, so we're not going to execute it if we're using FASTBuild.
  // {
  //      CPPCompileEnvironment.Config.Definitions.Clear();
  // }
  bUsePCHFiles = false;	// Only required due to a bug in UE4.15
  bUsePDBFiles = false;	// Only required if you're using MSVC
}
// <-- FASTBuild
```

### Configuration/UEBuildPlatform.cs

Near similar lines for XGE/Distcc/SNDBS, add the following method.

```
// --> FASTBuild
public virtual bool CanUseFastbuild()
{
    return false;
}
// <-- FASTBuild
```

### System/ActionGraph.cs

Alter the ExecuteActions method to run this check after the SNDBS one, and before the else case:
```
else if (FASTBuild.IsAvailable() && BuildConfiguration.bAllowFastBuild && (ActionsToExecute.Count() >= FASTBuild.MinimumRequiredActionsToEnableThreshold) )
{
    Log.TraceInformation("ActionGraph.cs::Chose FastBuild as executor - FastBuild.IsAvailable() = {0}", FASTBuild.IsAvailable());
    Executor = new FASTBuild();
}
```

### Windows/UEBuildWindows

Near similar lines for SNDBS, add the following method:

```
/// <summary>
/// If this platform can be compiled with FastBuild
/// </summary>
public override bool CanUseFastBuild()
{
	// Check that FastBuild is available
	return FASTBuild.IsAvailable();
}
```
# Warnings as errors

Enabling warnings as errors will cause issues with certain things being falsely detected as digraphs, and will need to be fixed manually by adding a space to separate the "<::X"
