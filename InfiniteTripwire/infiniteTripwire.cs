using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using EFT.InventoryLogic;
using EFT.SynchronizableObjects;
using HarmonyLib;
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Diz.LanguageExtensions;

namespace infiniteTripwire
{
    [BepInPlugin("ciallo.inftripwire", "Tripwire Grenade Modify", PluginInfo.Version)]
    [BepInDependency("com.SPT.core", "4.1.0")]
    public class NoTripwirePlugin : BaseUnityPlugin
    {
        private static ConfigEntry<bool> noConsumption;
        private static ConfigEntry<float> boomDelayReduce;
        private static ConfigEntry<int> effSecond;
        private static ConfigEntry<float> maxHeightDiff;
        private static ConfigEntry<float> maxTrip;

        private void Awake()
        {
            // 补丁针对 SPT 4.1.x 的反混淆名称；更高版本会让补丁静默失效
            if (!Chainloader.PluginInfos.TryGetValue("com.SPT.core", out var sptCore))
            {
                Logger.LogError("SPT core plugin not found - plugin disabled.");
                enabled = false;
                return;
            }
            var sptVersion = sptCore.Metadata.Version;
            if (sptVersion.Major > 4 || (sptVersion.Major == 4 && sptVersion.Minor > 1))
            {
                Logger.LogError($"SPT {sptVersion} is newer than this build targets (4.1.x); the Harmony patches would silently do nothing. Plugin disabled.");
                enabled = false;
                return;
            }

            noConsumption = Config.Bind("General", "No Tripwire Consumption", true);
            boomDelayReduce = Config.Bind("General", "Reduce Explosion Delay (?)", 0f,
                new ConfigDescription("Decrease time (in second) before boom. Higher than grenade's fuse value will make instant explosion.\nWarning: Partisan's F-1 trap is also changed (=3.5s - this)",
                new AcceptableValueRange<float>(0f, 5f)));
            effSecond = Config.Bind("General", "Effective Seconds", 3600);
            maxHeightDiff = Config.Bind("General", "Max Height Diff", 1.5f, new ConfigDescription("", new AcceptableValueRange<float>(0.2f, 3f)));
            maxTrip = Config.Bind("General", "Max Length", 5f, new ConfigDescription("", new AcceptableValueRange<float>(3f, 10f)));

            var harmony = new Harmony("ciallo.inftripwire");
            harmony.PatchAll();
        }

        // 跳过绊线消耗
        [HarmonyPatch(typeof(ItemManipulator), nameof(ItemManipulator.SimulatePlantTripwire))]
        class PatchSimulatePlantTripwire
        {
            static bool Prefix(InventoryController controller, ThrowWeap tripwire, [CanBeNull] PlantingKit plantingKit, ref OperationResult<PlantTripwireResult> __result)
            {
                if (!noConsumption.Value)
                {
                    return true; // 不启用时走原始逻辑
                }
                var gstruct = ItemManipulator.Discard(tripwire, controller, true);
                if (gstruct.Failed)
                {
                    __result = gstruct.Error;
                    return false;
                }
                List<ISyncOperationResult> list = new List<ISyncOperationResult>(2)
                {
                    gstruct.Value
                };
                __result = new PlantTripwireResult(tripwire, plantingKit, new CombinedOperationResult(list.ToArray()));
                return false; // 阻止原方法执行
            }
        }

        // 修改绊线有效时间
        [HarmonyPatch(typeof(TripwireSynchronizableObject), nameof(TripwireSynchronizableObject.SetupStakes))]
        class PatchTripwireInertSeconds
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count - 3; i++)
                {
                    if (codes[i].opcode == OpCodes.Ldarg_0 &&
                        codes[i + 1].opcode == OpCodes.Call &&
                        codes[i + 2].opcode == OpCodes.Ldfld &&
                        codes[i + 2].operand is FieldInfo fi && fi.Name == "InertSeconds" &&
                        codes[i + 3].opcode == OpCodes.Stloc_S)
                    {
                        // 替换成加载 effSecond.Value
                        codes[i] = new CodeInstruction(OpCodes.Ldsfld,
                            AccessTools.Field(typeof(NoTripwirePlugin), "effSecond"));
                        codes[i + 1] = new CodeInstruction(OpCodes.Callvirt,
                            AccessTools.PropertyGetter(typeof(ConfigEntry<int>), "Value"));
                        // 把 i+2 位置的 ldfld 清掉（用 Nop）
                        codes[i + 2] = new CodeInstruction(OpCodes.Nop);
                        // i+3 的 stloc.s 保留
                    }
                }
                return codes;
            }
        }

        // 修改绊线距离与高度差
        [HarmonyPatch(typeof(EFT.SynchronizableObjects.TripwireSynchronizableObject), "ValidateTripwirePositionPlayer")]
        class PatchValidateTripwirePositionPlayer
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count - 1; i++)
                {
                    if (codes[i].opcode == OpCodes.Ldloc_0 &&
                        codes[i + 1].opcode == OpCodes.Ldfld && codes[i + 1].operand is FieldInfo fi)
                    {
                        if (fi.Name == "MaxLength")
                        {
                            codes[i] = new CodeInstruction(OpCodes.Ldsfld,
                                AccessTools.Field(typeof(NoTripwirePlugin), "maxTrip"));
                            codes[i + 1] = new CodeInstruction(OpCodes.Callvirt,
                                AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value"));
                        }
                        else if (fi.Name == "MinLength")
                        {
                            codes[i] = new CodeInstruction(OpCodes.Ldc_R4, 0.2f);
                            codes[i + 1] = new CodeInstruction(OpCodes.Nop); // 去掉原来的 ldfld
                        }
                        else if (fi.Name == "MaxHeightDifference")
                        {
                            codes[i] = new CodeInstruction(OpCodes.Ldsfld,
                                AccessTools.Field(typeof(NoTripwirePlugin), "maxHeightDiff"));
                            codes[i + 1] = new CodeInstruction(OpCodes.Callvirt,
                                AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value"));
                        }
                    }
                }
                return codes;
            }
        }

        // 修改AI绊线高度差
        [HarmonyPatch(typeof(EFT.SynchronizableObjects.TripwireSynchronizableObject), "ValidateTripwirePositionAI")]
        class PatchValidateTripwirePositionAI
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);

                for (int i = 0; i < codes.Count; i++)
                {
                    // 找到原始的 ldc.r4 0.2
                    if (codes[i].opcode == OpCodes.Ldc_R4 && codes[i].operand is float f && Math.Abs(f - 0.2f) < 0.0001f)
                    {
                        // 替换成 maxHeightDiff.Value
                        codes[i] = new CodeInstruction(OpCodes.Ldsfld,
                            AccessTools.Field(typeof(NoTripwirePlugin), "maxHeightDiff"));
                        codes.Insert(i + 1, new CodeInstruction(OpCodes.Callvirt,
                            AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value")));
                    }
                }
                return codes;
            }
        }

        // 修改绊线预览划线距离
        [HarmonyPatch(typeof(EFT.TripwireVisualPlacer), "Init")]
        class PatchTripwireVisualPlacerInit
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);

                for (int i = 0; i < codes.Count - 2; i++)
                {
                    // 找到 ldarg.0 + call get_TripwiresSettings + ldfld
                    if (codes[i].opcode == OpCodes.Ldarg_0 &&
                        codes[i + 1].opcode == OpCodes.Call &&
                        codes[i + 1].operand is MethodInfo mi &&
                        mi.Name == "get_TripwiresSettings" &&
                        codes[i + 2].opcode == OpCodes.Ldfld &&
                        codes[i + 2].operand is FieldInfo fi)
                    {
                        if (fi.Name == "MaxPreviewLength" || fi.Name == "MaxLength")
                        {
                            codes[i] = new CodeInstruction(OpCodes.Ldsfld,
                                AccessTools.Field(typeof(NoTripwirePlugin), "maxTrip"));
                            codes[i + 1] = new CodeInstruction(OpCodes.Callvirt,
                                AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value"));
                            codes[i + 2] = new CodeInstruction(OpCodes.Nop);
                        }
                        else if (fi.Name == "MinLength")
                        {
                            codes[i] = new CodeInstruction(OpCodes.Ldc_R4, 0.2f);
                            codes[i + 1] = new CodeInstruction(OpCodes.Nop);
                            codes[i + 2] = new CodeInstruction(OpCodes.Nop);
                        }
                    }
                }
                return codes;
            }
        }

        // 修改绊线预览划线距离（另一处）
        [HarmonyPatch(typeof(EFT.TripwireVisualPlacer), nameof(EFT.TripwireVisualPlacer.ValidateSecondState))]
        class PatchTripwireVisualPlacerMethod2
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count - 2; i++)
                {
                    // 替换 MaxPreviewLength
                    if (codes[i].opcode == OpCodes.Ldarg_0 &&
                        codes[i + 1].opcode == OpCodes.Call &&
                        codes[i + 1].operand is MethodInfo mi &&
                        mi.Name == "get_TripwiresSettings" &&
                        codes[i + 2].opcode == OpCodes.Ldfld &&
                        codes[i + 2].operand is FieldInfo fi &&
                        fi.Name == "MaxPreviewLength")
                    {
                        codes[i] = new CodeInstruction(OpCodes.Ldsfld,
                            AccessTools.Field(typeof(NoTripwirePlugin), "maxTrip"));
                        codes[i + 1] = new CodeInstruction(OpCodes.Callvirt,
                            AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value"));
                        codes[i + 2] = new CodeInstruction(OpCodes.Nop);
                    }

                    // 替换 MaxHeightDifference
                    if (codes[i].opcode == OpCodes.Ldarg_0 &&
                        codes[i + 1].opcode == OpCodes.Call &&
                        codes[i + 1].operand is MethodInfo mi2 &&
                        mi2.Name == "get_TripwiresSettings" &&
                        codes[i + 2].opcode == OpCodes.Ldfld &&
                        codes[i + 2].operand is FieldInfo fi2 &&
                        fi2.Name == "MaxHeightDifference")
                    {
                        codes[i] = new CodeInstruction(OpCodes.Ldsfld,
                            AccessTools.Field(typeof(NoTripwirePlugin), "maxHeightDiff"));
                        codes[i + 1] = new CodeInstruction(OpCodes.Callvirt,
                            AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value"));
                        codes[i + 2] = new CodeInstruction(OpCodes.Nop);
                    }
                }
                return codes;
            }
        }

        // 修改地面坡度兼容
        [HarmonyPatch(typeof(EFT.TripwireVisualPlacer), nameof(EFT.TripwireVisualPlacer.SphereCastPosition))]
        class PatchTripwireVisualPlacerGroundTolerance
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count - 2; i++)
                {
                    if (codes[i].opcode == OpCodes.Ldarg_0 &&
                        codes[i + 1].opcode == OpCodes.Call &&
                        codes[i + 1].operand is MethodInfo mi &&
                        mi.Name == "get_TripwiresSettings" &&
                        codes[i + 2].opcode == OpCodes.Ldfld &&
                        codes[i + 2].operand is FieldInfo fi &&
                        fi.Name == "GroundDotProductTolerance")
                    {
                        codes[i] = new CodeInstruction(OpCodes.Ldc_R4, 0.3f);
                        codes[i + 1] = new CodeInstruction(OpCodes.Conv_R8);
                        codes[i + 2] = new CodeInstruction(OpCodes.Nop);
                    }
                }
                return codes;
            }
        }

        // 修改绊雷预爆时间
        [HarmonyPatch(typeof(TripwireSynchronizableObject), nameof(TripwireSynchronizableObject.ActivateGrenade))]
        class PatchMethod6
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    // 找到 GrenadeFactory.Create 调用，往回找它的 prewarm 参数 (ldc.r4 0)
                    if (!(codes[i].operand is MethodInfo mi) || mi.Name != "Create" || mi.DeclaringType != typeof(GrenadeFactory))
                    {
                        continue;
                    }
                    for (int j = i - 1; j >= 0 && j > i - 6; j--)
                    {
                        if (codes[j].opcode != OpCodes.Ldc_R4)
                        {
                            continue;
                        }
                        // 替换成 boomDelayReduce.Value（预热时间越长，爆炸越快）
                        codes[j] = new CodeInstruction(OpCodes.Ldsfld,
                            AccessTools.Field(typeof(NoTripwirePlugin), "boomDelayReduce"));
                        codes.Insert(j + 1, new CodeInstruction(OpCodes.Callvirt,
                            AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value")));
                        break;
                    }
                    break;
                }
                return codes;
            }
        }
    }
}
