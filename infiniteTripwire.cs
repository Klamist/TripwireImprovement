using BepInEx;
using BepInEx.Configuration;
using EFT.InventoryLogic;
using EFT.SynchronizableObjects;
using HarmonyLib;
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace infiniteTripwire
{
    [BepInPlugin("ciallo.inftripwire", "Tripwire Grenade Modify", "1.0.0")]
    public class NoTripwirePlugin : BaseUnityPlugin
    {
        private static ConfigEntry<bool> noConsumption;
        private static ConfigEntry<float> boomDelayReduce;
        private static ConfigEntry<int> effSecond;
        private static ConfigEntry<float> maxHeightDiff;
        private static ConfigEntry<float> maxTrip;

        private void Awake()
        {
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
        [HarmonyPatch(typeof(InteractionsHandlerClass), nameof(InteractionsHandlerClass.SimulatePlantTripwire))]
        class PatchSimulatePlantTripwire
        {
            static bool Prefix(InventoryController controller, ThrowWeapItemClass tripwire, [CanBeNull] PlantingKitsItemClass plantingKit, ref GStruct154<GClass3407> __result)
            {
                if (!noConsumption.Value)
                {
                    return true; // 不启用时走原始逻辑
                }
                var gstruct = InteractionsHandlerClass.Discard(tripwire, controller, true);
                if (gstruct.Failed)
                {
                    __result = gstruct.Error;
                    return false;
                }
                List<GInterface433> list = new List<GInterface433>(2)
                {
                    gstruct.Value
                };
                __result = new GClass3407(tripwire, plantingKit, new GClass3400(list.ToArray()));
                return false; // 阻止原方法执行
            }
        }

        // 修改绊线有效时间
        [HarmonyPatch(typeof(TripwireSynchronizableObject), "method_1")]
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
                    // 找到 ldarg.0 + call get_TripwiresGlobalSettings_0 + ldfld
                    if (codes[i].opcode == OpCodes.Ldarg_0 &&
                        codes[i + 1].opcode == OpCodes.Call &&
                        codes[i + 1].operand is MethodInfo mi &&
                        mi.Name == "get_TripwiresGlobalSettings_0" &&
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
        [HarmonyPatch(typeof(EFT.TripwireVisualPlacer), "method_2")]
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
                        mi.Name == "get_TripwiresGlobalSettings_0" &&
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
                        mi2.Name == "get_TripwiresGlobalSettings_0" &&
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
        [HarmonyPatch(typeof(EFT.TripwireVisualPlacer), "method_1")]
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
                        mi.Name == "get_TripwiresGlobalSettings_0" &&
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
        [HarmonyPatch(typeof(TripwireSynchronizableObject), "method_6")]
        class PatchMethod6
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (i == 23 && codes[i].opcode == OpCodes.Ldc_R4)
                    {
                        codes[i] = new CodeInstruction(OpCodes.Ldsfld,
                            AccessTools.Field(typeof(NoTripwirePlugin), "boomDelayReduce"));
                        codes.Insert(i + 1, new CodeInstruction(OpCodes.Callvirt,
                            AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value")));
                    }
                }
                return codes;
            }
        }
    }
}
