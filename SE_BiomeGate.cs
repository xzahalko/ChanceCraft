using HarmonyLib;
using System;
using System.Linq;
using UnityEngine;
using static HitData;
using static BiomeGate.BiomeGate;

namespace BiomeGate
{
    public class SE_BiomeGate : SE_Stats
    {
        public const string statusEffectBiomeGateName = "BiomeGate";
        public static readonly int statusEffectBiomeGateHash = statusEffectBiomeGateName.GetStableHashCode();

        public const string statusEffectName = "$menu_server_warning";
        public const string statusEffectTooltip = "$tutorial_blackforest_topic";

        public override string GetIconText()
        {
            return Localization.instance.Localize(statusEffectTooltip);
        }

        public static void UpdateBiomeGateProperties()
        {
            if (ObjectDB.instance)
                UpdateStatusEffectProperties(ObjectDB.instance.GetStatusEffect(statusEffectBiomeGateHash) as SE_BiomeGate);

            if (Player.m_localPlayer != null && Player.m_localPlayer.GetSEMan().HaveStatusEffect(statusEffectBiomeGateHash))
                UpdateStatusEffectProperties(Player.m_localPlayer.GetSEMan().GetStatusEffect(statusEffectBiomeGateHash) as SE_BiomeGate);
        }

        private static void UpdateStatusEffectProperties(SE_BiomeGate statusEffect)
        {
            if (statusEffect == null)
                return;

            statusEffect.m_noiseModifier = noiseModifier.Value;
            statusEffect.m_stealthModifier = sneakModifier.Value;
            statusEffect.m_damageModifier = damageDoneModifier.Value;
            statusEffect.m_raiseSkillModifier = raiseSkillModifier.Value;

            statusEffect.m_repeatMessage = showRepeatMessage.Value ? "$npc_dvergrrogue_random_private_area_alarm8" : "";
            statusEffect.m_startMessage = showStartMessage.Value ? "$npc_dvergr_ashlands_random_private_area_alarm5" : "";

            statusEffect.m_mods.Clear();
            foreach (DamageType damageType in Enum.GetValues(typeof(DamageType)))
                if (damageType != DamageType.Damage && damageType != DamageType.Physical && damageType != DamageType.Elemental)
                    statusEffect.m_mods.Add(new DamageModPair() { m_modifier = damageReceivedModifier.Value, m_type = damageType });
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
        public static class ObjectDB_Awake_AddStatusEffects
        {
            public static void AddCustomStatusEffects(ObjectDB odb)
            {
                if (odb.m_StatusEffects.Count > 0)
                {
                    if (!odb.m_StatusEffects.Any(se => se.name == statusEffectBiomeGateName))
                    {
                        SE_BiomeGate statusEffect = ScriptableObject.CreateInstance<SE_BiomeGate>();
                        statusEffect.name = statusEffectBiomeGateName;
                        statusEffect.m_nameHash = statusEffectBiomeGateHash;
                        statusEffect.m_icon = odb.m_StatusEffects.Find(se => se.m_icon != null && se.m_icon.name.IndexOf("immobilized") != -1)?.m_icon;
                        statusEffect.m_flashIcon = true;

                        statusEffect.m_modifyAttackSkill = Skills.SkillType.All;
                        statusEffect.m_raiseSkill = Skills.SkillType.All;

                        statusEffect.m_startMessageType = MessageHud.MessageType.Center;
                        statusEffect.m_repeatMessageType = MessageHud.MessageType.Center;
                        statusEffect.m_repeatInterval = 20f;

                        statusEffect.m_name = statusEffectName;
                        statusEffect.m_tooltip = statusEffectTooltip;

                        UpdateStatusEffectProperties(statusEffect);

                        odb.m_StatusEffects.Add(statusEffect);
                    }
                }
            }

            private static void Postfix(ObjectDB __instance)
            {
                AddCustomStatusEffects(__instance);
            }
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
        public static class ObjectDB_CopyOtherDB_AddStatusEffects
        {
            private static void Postfix(ObjectDB __instance)
            {
                ObjectDB_Awake_AddStatusEffects.AddCustomStatusEffects(__instance);
            }
        }
    }
}
