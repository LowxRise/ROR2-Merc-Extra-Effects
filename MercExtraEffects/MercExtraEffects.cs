using System;
using BepInEx;
using BepInEx.Configuration;
using RoR2;

namespace CurrentlyMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class CurrentlyModPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "youss.CurrentlyMod.RedLegendaryStatBoost";
        public const string PluginName = "Red Legendary Stat Boost";
        public const string PluginVersion = "1.0.0";

        private const string ConfigSectionGeneral = "General";
        private const string ConfigKeyPercentPerLegendary = "Percent bonus per legendary item";
        private static ConfigEntry<float> percentBonusPerLegendary;

        private void Awake()
        {
            BindConfig();
            On.RoR2.CharacterBody.RecalculateStats += CharacterBody_RecalculateStats;
            Logger.LogInfo("Red Legendary Stat Boost loaded.");
        }

        private void OnDestroy()
        {
            On.RoR2.CharacterBody.RecalculateStats -= CharacterBody_RecalculateStats;
        }

        private void BindConfig()
        {
            percentBonusPerLegendary = Config.Bind(
                ConfigSectionGeneral,
                ConfigKeyPercentPerLegendary,
                5f,
                new ConfigDescription(
                    "Percent bonus applied to health, shield, move speed, damage, attack speed, crit, regen, and armor for each non-scrap legendary item stack. Default: 5.",
                    new AcceptableValueRange<float>(0f, 100f),
                    new object[0]));
        }

        private static void CharacterBody_RecalculateStats(On.RoR2.CharacterBody.orig_RecalculateStats orig, RoR2.CharacterBody self)
        {
            orig(self);

            if (self == null || self.inventory == null)
            {
                return;
            }

            int legendaryCount = GetLegendaryItemCount(self.inventory);
            if (legendaryCount <= 0)
            {
                return;
            }

            float statBoostMultiplier = legendaryCount * Math.Max(0f, percentBonusPerLegendary.Value) / 100f;
            if (statBoostMultiplier <= 0f)
            {
                return;
            }

            ApplyAllStatBoost(self, statBoostMultiplier);
        }

        private static int GetLegendaryItemCount(RoR2.Inventory inventory)
        {
            int total = 0;
            int itemCount = ItemCatalog.itemCount;

            for (ItemIndex itemIndex = (ItemIndex)0; (int)itemIndex < itemCount; itemIndex++)
            {
                ItemDef itemDef = ItemCatalog.GetItemDef(itemIndex);
                if (itemDef == null)
                {
                    continue;
                }

                if (itemDef.tier != ItemTier.Tier3)
                {
                    continue;
                }

                if (itemDef.ContainsTag(ItemTag.Scrap))
                {
                    continue;
                }

                total += inventory.GetItemCount(itemIndex);
            }

            return total;
        }

        private static void ApplyAllStatBoost(RoR2.CharacterBody body, float boostFraction)
        {
            body.maxHealth += body.maxHealth * boostFraction;
            body.maxShield += body.maxShield * boostFraction;
            body.moveSpeed += body.moveSpeed * boostFraction;
            body.damage += body.damage * boostFraction;
            body.attackSpeed += body.attackSpeed * boostFraction;
            body.crit += body.crit * boostFraction;
            body.regen += body.regen * boostFraction;
            body.armor += body.armor * boostFraction;
        }
    }
}
