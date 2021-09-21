using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Preloader;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace ConfigWatcher
{
	public class ConfigWatcher
	{
		public static IEnumerable<string> TargetDLLs { get; } = Array.Empty<string>();
		public static void Patch(AssemblyDefinition assembly) { }

		private static readonly Dictionary<string, List<WeakReference<ConfigFile>>> watchedFileMap = new();
		private static readonly List<WeakReference<ConfigFile>> uninitializedConfigs = new();
		private static bool initialized = false;

		public static void Initialize()
		{
			Assembly assembly = Assembly.GetExecutingAssembly();
			Harmony harmony = new("org.bepinex.patchers.configwatcher");
			harmony.PatchAll(assembly);
		}

		[HarmonyPatch(typeof(ConfigFile), MethodType.Constructor, typeof(string), typeof(bool), typeof(BepInPlugin))]
		private static class Patch_ConfigFile_Constructor
		{
			private static void Postfix(ConfigFile __instance)
			{
				if (initialized)
				{
					initFileWatcher(new WeakReference<ConfigFile>(__instance));
				}
				else
				{
					uninitializedConfigs.Add(new WeakReference<ConfigFile>(__instance));
				}
			}
		}

		[HarmonyPatch]
		private static class Patch_Preloader_PatchEntrypoint
		{
			private static IEnumerable<MethodInfo> TargetMethods() => new [] { AccessTools.DeclaredMethod(typeof(EnvVars).Assembly.GetType("BepInEx.Preloader.Preloader"), "PatchEntrypoint") };

			private static void AddChainloaderFinishedCall(ILProcessor ilProcessor, Instruction instruction, AssemblyDefinition assembly) =>
				ilProcessor.InsertBefore(instruction, ilProcessor.Create(Mono.Cecil.Cil.OpCodes.Call, assembly.MainModule.ImportReference(ChainloaderFinished)));

			private static readonly MethodInfo ChainloaderFinishedCallInstructionAdder = AccessTools.DeclaredMethod(typeof(Patch_Preloader_PatchEntrypoint), nameof(AddChainloaderFinishedCall));
			private static readonly MethodInfo ChainloaderFinished = AccessTools.DeclaredMethod(typeof(Patch_Preloader_PatchEntrypoint), nameof(PostChainloader));
			private static readonly MethodInfo ILInstructionInserter = AccessTools.DeclaredMethod(typeof(ILProcessor), nameof(ILProcessor.InsertBefore));

			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				List<CodeInstruction> newInstr = new();
				bool first = true;
				foreach (CodeInstruction instruction in instructions.Reverse())
				{
					if (first && instruction.opcode == OpCodes.Callvirt && instruction.OperandIs(ILInstructionInserter))
					{
						newInstr.Add(new CodeInstruction(OpCodes.Call, ChainloaderFinishedCallInstructionAdder));
						newInstr.Add(new CodeInstruction(OpCodes.Ldind_Ref));
						newInstr.Add(new CodeInstruction(OpCodes.Ldarg_0)); // assembly
						newInstr.Add(new CodeInstruction(OpCodes.Ldloc_S, 12)); // target
						newInstr.Add(new CodeInstruction(OpCodes.Ldloc_S, 11)); // ilProcessor
						first = false;
					}
					newInstr.Add(instruction);
				}
				return ((IEnumerable<CodeInstruction>)newInstr).Reverse();
			}

			private static void PostChainloader()
			{
				initialized = true;
				foreach (WeakReference<ConfigFile> configRef in uninitializedConfigs)
				{
					initFileWatcher(configRef);
				}
				uninitializedConfigs.Clear();
			}
		}

		private static void initFileWatcher(WeakReference<ConfigFile> configFileRef)
		{
			if (!configFileRef.TryGetTarget(out ConfigFile configFile))
			{
				return;
			}

			FileSystemWatcher configFileWatcher = new(Path.GetDirectoryName(configFile.ConfigFilePath)!, Path.GetFileName(configFile.ConfigFilePath));
			configFileWatcher.Created += configFileEvent;
			configFileWatcher.Changed += configFileEvent;
			configFileWatcher.Renamed += configFileEvent;
			configFileWatcher.IncludeSubdirectories = true;
			configFileWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
			configFileWatcher.EnableRaisingEvents = true;

			if (!watchedFileMap.TryGetValue(configFile.ConfigFilePath, out List<WeakReference<ConfigFile>> configFileList))
			{
				configFileList = watchedFileMap[configFile.ConfigFilePath] = new List<WeakReference<ConfigFile>>();
			}
			configFileList.Add(configFileRef);
		}

		private static void configFileEvent(object sender, FileSystemEventArgs e)
		{
			List<WeakReference<ConfigFile>> configFileRefs = watchedFileMap[e.FullPath];
			foreach (WeakReference<ConfigFile> configFileRef in configFileRefs)
			{
				if (!configFileRef.TryGetTarget(out ConfigFile configFile))
				{
					configFileRefs.Remove(configFileRef);
					if (configFileRefs.Count == 0)
					{
						watchedFileMap.Remove(e.FullPath);
					}
					((FileSystemWatcher)sender).EnableRaisingEvents = false;
                }
                else if (File.Exists(configFile.ConfigFilePath))
                {
                	configFile.Reload();
                }
			}
		}
	}
}
