using BepInEx;
using BepInEx.Configuration;
using Diz.LanguageExtensions;
using Comfort.Common;
using EFT;
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
            noConsumption = Config.Bind("General", "Infinite Tripwire", true);
            boomDelayReduce = Config.Bind("General", "Reduce Explosion Delay (?)", 0f,
                new ConfigDescription("Decrease time (in second) before boom. Higher than grenade's fuse value will make instant explosion." +
                                        "\nWarning: Partisan's F-1 trap is also changed (=3.5s - this)",
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
            static bool Prefix(InventoryController controller, ThrowWeap tripwire, PlantingKit plantingKit, ref OperationResult<PlantTripwireResult> __result)
            {
                if (!noConsumption.Value)
                    return true;

                var discardResult = ItemManipulator.Discard(tripwire, controller, true);

                if (discardResult.Failed)
                {
                    __result = discardResult.Error;
                    return false;
                }

                List<ISyncOperationResult> operations = new List<ISyncOperationResult>() { discardResult.Value };

                __result = new PlantTripwireResult(tripwire, plantingKit, new CombinedOperationResult(operations.ToArray()));

                return false;
            }
        }

        // 修改绊线有效时间
        [HarmonyPatch(typeof(TripwireSynchronizableObject), nameof(TripwireSynchronizableObject.SetupStakes))]
        class PatchTripwireInertSeconds
        {
            static void Prefix(
                TripwireSynchronizableObject __instance)
            {
                __instance.TripwiresSettings.InertSeconds = effSecond.Value;
            }
        }

        // 修改绊线距离与高度差
        [HarmonyPatch(typeof(TripwireSynchronizableObject), nameof(TripwireSynchronizableObject.SetupStakes))]
        class PatchTripwireSettings
        {
            static void Prefix()
            {
                var settings = Singleton<GlobalConfiguration>.Instance.TripwiresSettings;
                settings.MaxLength = maxTrip.Value;
                settings.MinLength = 0.1f;
                settings.MaxHeightDifference = maxHeightDiff.Value;
            }
        }

        // 解除绊线高低差0.2m
        [HarmonyPatch(typeof(TripwireSynchronizableObject), nameof(TripwireSynchronizableObject.ValidateTripwirePositionAI))]
        class PatchValidateTripwirePositionAI
        {
            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);

                for (int i = 0; i < codes.Count; i++)
                {
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
        [HarmonyPatch(typeof(TripwireVisualPlacer), nameof(TripwireVisualPlacer.Init))]
        class PatchTripwireVisualPlacerInit
        {
            static void Prefix()
            {
                var settings = Singleton<GlobalConfiguration>.Instance.TripwiresSettings;
                settings.MaxPreviewLength = maxTrip.Value;
                settings.MaxLength = maxTrip.Value;
                settings.MinLength = 0.1f;
                settings.MaxHeightDifference = maxHeightDiff.Value;
                settings.GroundDotProductTolerance = 0.1f;
            }
        }

        // 修改绊雷预爆时间
        [HarmonyPatch(typeof(TripwireSynchronizableObject), "ActivateGrenade")]
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
