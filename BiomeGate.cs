using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using System.Collections.Generic;
using System.Reflection;
using System;
using System.Linq;
using static HitData;
using UnityEngine;

namespace BiomeGate
{
    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    public class BiomeGate : BaseUnityPlugin
    {
        const string pluginID = "shudnal.BiomeGate";
        const string pluginName = "Biome Gate";
        const string pluginVersion = "1.0.0";

        private readonly Harmony harmony = new Harmony(pluginID);

        internal static readonly ConfigSync configSync = new ConfigSync(pluginID) { DisplayName = pluginName, CurrentVersion = pluginVersion, MinimumRequiredVersion = pluginVersion };

        public static BiomeGate instance;

        public static ConfigEntry<bool> modEnabled;
        private static ConfigEntry<bool> configLocked;
        private static ConfigEntry<bool> loggingEnabled;

        private static ConfigEntry<Heightmap.Biome> gatedBiomes;
        private static ConfigEntry<bool> adminPermitted;

        internal static ConfigEntry<DamageModifier> damageReceivedModifier;
        internal static ConfigEntry<float> damageDoneModifier;
        internal static ConfigEntry<float> noiseModifier;
        internal static ConfigEntry<float> sneakModifier;
        internal static ConfigEntry<float> raiseSkillModifier;
        internal static ConfigEntry<bool> showRepeatMessage;
        internal static ConfigEntry<bool> showStartMessage;

        private void Awake()
        {
            harmony.PatchAll();

            instance = this;

            ConfigInit();
            _ = configSync.AddLockingConfigEntry(configLocked);

            Game.isModded = true;
        }

        private void OnDestroy()
        {
            Config.Save();
            harmony?.UnpatchSelf();
        }

        public static void LogInfo(object data)
        {
            if (loggingEnabled.Value)
                instance.Logger.LogInfo(data);
        }

        private void ConfigInit()
        {
            modEnabled = config("General", "Enabled", defaultValue: true, "Enable the mod.");
            configLocked = config("General", "Lock Configuration", defaultValue: true, "Configuration is locked and can be changed by server admins only.");
            loggingEnabled = config("General", "Logging enabled", defaultValue: false, "Enable logging. [Not Synced with Server]", false);

            gatedBiomes = config("Gating", "Biomes", defaultValue: Heightmap.Biome.None, "Biome list.");
            adminPermitted = config("Gating", "Ignore admins", defaultValue: true, "Admins and host will not be affected");
            damageReceivedModifier = config("Gating", "Damage received modifier", defaultValue: DamageModifier.VeryWeak, "Damage modifier for incoming damage");
            damageDoneModifier = config("Gating", "Damage dealt modifier", defaultValue: 0f, "Damage modifier for damage done by affected players");
            noiseModifier = config("Gating", "Noise modifier", defaultValue: 10f, "Noise modifier. How far away player will be heared.");
            sneakModifier = config("Gating", "Sneak modifier", defaultValue: -1f, "Sneak efficacy modifier");
            raiseSkillModifier = config("Gating", "Raise skill modifier", defaultValue: -100f, "Modifier for skill raise. 0 is normal skill raise. Other status effects can also modify skill raise.");
            showRepeatMessage = config("Gating", "Show repeat message", defaultValue: true, "Show repeated message every 20 seconds");
            showStartMessage = config("Gating", "Show start message", defaultValue: true, "Show message when debuff is applied");

            damageReceivedModifier.SettingChanged += (sender, args) => SE_BiomeGate.UpdateBiomeGateProperties();
            damageDoneModifier.SettingChanged += (sender, args) => SE_BiomeGate.UpdateBiomeGateProperties();
            noiseModifier.SettingChanged += (sender, args) => SE_BiomeGate.UpdateBiomeGateProperties();
            sneakModifier.SettingChanged += (sender, args) => SE_BiomeGate.UpdateBiomeGateProperties();
            raiseSkillModifier.SettingChanged += (sender, args) => SE_BiomeGate.UpdateBiomeGateProperties();
            showRepeatMessage.SettingChanged += (sender, args) => SE_BiomeGate.UpdateBiomeGateProperties();
            showStartMessage.SettingChanged += (sender, args) => SE_BiomeGate.UpdateBiomeGateProperties();
        }

        private static bool IsAdmin()
        {
            if (!adminPermitted.Value)
                return false;

            if (!ZNet.instance)
                return false;

            return ZNet.instance.LocalPlayerIsAdminOrHost();
        }

        ConfigEntry<T> config<T>(string group, string name, T defaultValue, ConfigDescription description, bool synchronizedSetting = true)
        {
            ConfigEntry<T> configEntry = Config.Bind(group, name, defaultValue, description);

            SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

            return configEntry;
        }

        ConfigEntry<T> config<T>(string group, string name, T defaultValue, string description, bool synchronizedSetting = true) => config(group, name, defaultValue, new ConfigDescription(description), synchronizedSetting);

        public void FixedUpdate()
        {
            if (Player.m_localPlayer == null)
                return;

            if (Player.m_localPlayer.GetSEMan().HaveStatusEffect(SE_BiomeGate.statusEffectBiomeGateHash))
            {
                if (gatedBiomes.Value == Heightmap.Biome.None || Player.m_localPlayer.m_currentBiome == Heightmap.Biome.None)
                    Player.m_localPlayer.GetSEMan().RemoveStatusEffect(SE_BiomeGate.statusEffectBiomeGateHash);
                else if (!gatedBiomes.Value.HasFlag(Player.m_localPlayer.m_currentBiome))
                    Player.m_localPlayer.GetSEMan().RemoveStatusEffect(SE_BiomeGate.statusEffectBiomeGateHash);
                else if (IsAdmin())
                    Player.m_localPlayer.GetSEMan().RemoveStatusEffect(SE_BiomeGate.statusEffectBiomeGateHash);
            }
            else
            {
                if (gatedBiomes.Value.HasFlag(Player.m_localPlayer.m_currentBiome) && !IsAdmin())
                    Player.m_localPlayer.GetSEMan().AddStatusEffect(SE_BiomeGate.statusEffectBiomeGateHash);
            }
            
        }

        [HarmonyPatch]
        public static class PreventInteractions
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                return AppDomain.CurrentDomain.GetAssemblies().Where(x => x.FullName.StartsWith("assembly_valheim"))
                                                                .SelectMany(s => s.GetTypes())
                                                                .Where(p => typeof(Interactable).IsAssignableFrom(p))
                                                                .Where(p => p.Name != "Interactable")
                                                                .SelectMany(t => new List<MethodBase>() { AccessTools.Method(t, "Interact"), AccessTools.Method(t, "UseItem") });
            }

            private static bool Prefix(object[] __args)
            {
                Humanoid user = __args.FirstOrDefault(arg => arg is Humanoid) as Humanoid;
                if (user != null && user.GetSEMan().HaveStatusEffect(SE_BiomeGate.statusEffectBiomeGateHash))
                {
                    user.Message(MessageHud.MessageType.Center, "$msg_nobuildzone");
                    return false;
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.SetPlaceMode))]
        public static class Player_SetPlaceMode_PreventBuilding
        {
            private static void Prefix(Player __instance, ref PieceTable buildPieces, ref PieceTable __state)
            {
                if (!__instance.GetSEMan().HaveStatusEffect(SE_BiomeGate.statusEffectBiomeGateHash))
                    return;

                __state = buildPieces;
                buildPieces = null;
            }

            private static void Postfix(ref PieceTable buildPieces, ref PieceTable __state)
            {
                if (__state == null)
                    return;

                buildPieces = __state;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.InPlaceMode))]
        public static class Player_InPlaceMode_PreventBuilding
        {
            private static bool Prefix(Player __instance, ref bool __result)
            {
                if (!__instance.GetSEMan().HaveStatusEffect(SE_BiomeGate.statusEffectBiomeGateHash))
                    return true;

                __result = false;
                return false;
            }
        }
        
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdateExplore))]
        public static class Minimap_UpdateExplore_PreventExplore
        {
            private static void Prefix(Minimap __instance, Player player)
            {
                if (!player.GetSEMan().HaveStatusEffect(SE_BiomeGate.statusEffectBiomeGateHash))
                    return;

                __instance.m_exploreTimer = 0;
            }
        }
    }
}
