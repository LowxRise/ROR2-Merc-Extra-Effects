using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using RoR2;
using RoR2.Skills;
using UnityEngine;
using UnityEngine.AddressableAssets;
using EntityStates.Merc;

// RiskOfOptions (UI in r2modman)
using RiskOfOptions;
using RiskOfOptions.OptionConfigs;
using RiskOfOptions.Options;

namespace ExamplePlugin
{
    public sealed class ConfigValue<T>
    {
        public ConfigEntry<T> Entry;
        public readonly T DefaultValue;
        public readonly T MinValue;
        public readonly T MaxValue;
        public readonly T IncrementValue;

        public T Value => Entry.Value;

        public ConfigValue(T defaultValue)
        {
            DefaultValue = defaultValue;
            MinValue = default(T);
            MaxValue = default(T);
            IncrementValue = default(T);
        }

        public ConfigValue(T defaultValue, T minValue, T maxValue, T incrementValue)
        {
            DefaultValue = defaultValue;
            MinValue = minValue;
            MaxValue = maxValue;
            IncrementValue = incrementValue;
        }

        public string DefaultAsString()
        {
            return $"Default: {DefaultValue}";
        }
    }

    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency("com.rune580.riskofoptions", BepInDependency.DependencyFlags.SoftDependency)]
    public class MercExtraEffectsPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.fafnir62.mercenary_extra_effects";
        public const string PluginName = "Mercenary Extra Effects";
        public const string PluginVersion = "1.3.3";

        private static BodyIndex mercBodyIndex = BodyIndex.None;

        // ----------------------------
        // Config
        // ----------------------------
        private static ConfigValue<float> cfgSecondaryLifestealPercent = new ConfigValue<float>(15f, 0f, 200f, 1f);
        private static ConfigValue<float> cfgEvisBarrierPercent = new ConfigValue<float>(15f, 0f, 40f, 1f);

        // Speed fix taken from:
        // https://github.com/mcrutherford/MercenaryTweaks/blob/master/MercenaryTweaks/MercenaryTweaks.cs
        public static ConfigValue<bool> M1AttackSpeedFix { get; set; } = new ConfigValue<bool>(true);
        public static ConfigValue<bool> M2AttackSpeedIgnore { get; set; } = new ConfigValue<bool>(true);
        public static ConfigValue<bool> EvisIgnoreAllies { get; set; } = new ConfigValue<bool>(true);

        // Skill tuning configs
        private static ConfigValue<float> cfgFocusedAssaultDamagePercent = new ConfigValue<float>(700f, 350f, 1400f, 10f);
        private static ConfigValue<float> cfgFocusedAssaultCooldownSeconds = new ConfigValue<float>(8f, 4f, 16f, 1f);
        private static ConfigValue<float> cfgEvisDamagePercent = new ConfigValue<float>(110f, 50f, 220f, 10f);
        private static ConfigValue<float> cfgEvisCooldownSeconds = new ConfigValue<float>(6f, 3f, 16f, 1f);
        private static ConfigValue<float> cfgBlindingAssaultDamagePercent = new ConfigValue<float>(300f, 150f, 600f, 10f);
        private static ConfigValue<float> cfgBlindingAssaultCooldownSeconds = new ConfigValue<float>(5f, 3f, 16f, 1f);

        private const float UtilityCooldownSeconds = 6f;

        // Addressables (vanilla assets)
        private const string FocusedAssaultSkillDefPath = "RoR2/Base/Merc/MercBodyFocusedAssault.asset";
        private const string BlindingAssaultSkillDefPath = "RoR2/Base/Merc/MercBodyAssaulter.asset";

        // Eviscerate SkillDef path can vary by RoR2 version/modpack; we try several common ones.
        // If none load, we fall back to finding it on MercBody prefab by state machine.
        private static readonly string[] EvisSkillDefPaths =
        {
            "RoR2/Base/Merc/MercBodyEviscerate.asset",
            "RoR2/Base/Merc/MercBodyEvis.asset",
            "RoR2/Base/Merc/MercBodySpecialEviscerate.asset"
        };

        private void Awake()
        {
            Logger.LogInfo("[MercExtraEffects] Awake");

            InitConfig();

            RoR2Application.onLoad += OnGameLoaded;
        }

        private void OnGameLoaded()
        {
            mercBodyIndex = BodyCatalog.FindBodyIndex("MercBody");
            Logger.LogInfo("[MercExtraEffects] MercBodyIndex=" + mercBodyIndex);

            CharacterBody.onBodyStartGlobal += OnBodyStart;

            // Track M2 windows
            On.EntityStates.Merc.Uppercut.OnEnter += Uppercut_OnEnter;
            On.EntityStates.Merc.Uppercut.OnExit += Uppercut_OnExit;

            On.EntityStates.Merc.WhirlwindBase.OnEnter += WhirlwindBase_OnEnter;
            On.EntityStates.Merc.WhirlwindBase.OnExit += WhirlwindBase_OnExit;

            // Evis ignore allies
            On.EntityStates.Merc.EvisDash.FixedUpdate += Evis_FixedUpdate;

            // Skills speed fix
            On.EntityStates.Merc.Weapon.GroundLight2.OnEnter += M1_onEnter;
            On.EntityStates.Merc.Uppercut.PlayAnim += Uppercut_PlayAnim;
            On.EntityStates.Merc.WhirlwindAir.PlayAnim += WhirlwindAir_PlayAnim;
            On.EntityStates.Merc.WhirlwindGround.PlayAnim += WhirlwindGround_PlayAnim;

            // Add barrier on evis exit
            On.EntityStates.Merc.Evis.OnExit += Evis_OnExit;

            // Focused Assault damage override (vanilla default 700%)
            On.EntityStates.Merc.FocusedAssaultDash.AuthorityModifyOverlapAttack += FocusedAssaultDash_AuthorityModifyOverlapAttack;
            On.EntityStates.Merc.Assaulter2.AuthorityModifyOverlapAttack += Assaulter2_AuthorityModifyOverlapAttack;

            // Server-side: lifesteal
            GlobalEventManager.onServerDamageDealt += OnServerDamageDealt;

            ApplySkillTuning();
        }

        private static Sprite LoadEmbeddedSprite(string resourceName)
        {
            var assembly = typeof(MercExtraEffectsPlugin).Assembly;

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    Debug.LogError($"Failed to load resource: {resourceName}");
                    return null;
                }

                byte[] buffer = new byte[stream.Length];
                stream.Read(buffer, 0, buffer.Length);

                Texture2D tex = new Texture2D(2, 2);
                tex.LoadImage(buffer);

                return Sprite.Create(
                    tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f)
                );
            }
        }

        // ----------------------------
        // Config + RiskOfOptions UI
        // ----------------------------
        private void InitConfig()
        {
            cfgSecondaryLifestealPercent.Entry = Config.Bind(
                "Skills effects",
                "Secondary Lifesteal (%)",
                cfgSecondaryLifestealPercent.DefaultValue,
                new ConfigDescription(
                    "How much of damage dealt is healed while Merc M2 " +
                    "(Uppercut/Whirlwind) is active. " +
                    cfgSecondaryLifestealPercent.DefaultAsString(),
                    new AcceptableValueRange<float>(
                        cfgSecondaryLifestealPercent.MinValue,
                        cfgSecondaryLifestealPercent.MaxValue)
                )
            );

            cfgEvisBarrierPercent.Entry = Config.Bind(
                "Skills effects",
                "Evis Barrier HP (%)",
                cfgEvisBarrierPercent.DefaultValue,
                new ConfigDescription(
                    "Barrier gained after Eviscerate as % of max mercenary health. " +
                    cfgEvisBarrierPercent.DefaultAsString(),
                    new AcceptableValueRange<float>(
                        cfgEvisBarrierPercent.MinValue,
                        cfgEvisBarrierPercent.MaxValue)
                )
            );

            // ----------------------------
            //  Speed fix
            // ----------------------------
            M1AttackSpeedFix.Entry = Config.Bind(
                "Skills effects",
                "M1 attack speed fix",
                M1AttackSpeedFix.DefaultValue,
                "When enabled, gives Merc's 3rd m1 a fixed duration, allowing for consistent m1 extends. " +
                M1AttackSpeedFix.DefaultAsString()
            );

            M2AttackSpeedIgnore.Entry = Config.Bind(
                "Skills effects",
                "M2 Attack Speed Ignore",
                M2AttackSpeedIgnore.DefaultValue,
                "When enabled, Whirlwind and Rising Thunder ignore attack speed, making their utility consistent throughout the run. " +
                M2AttackSpeedIgnore.DefaultAsString()
            );

            // ----------------------------
            //  Evis Ignore Allies
            // ----------------------------
            EvisIgnoreAllies.Entry = Config.Bind(
                "Skills effects",
                "Eviscerate ignore allies",
                EvisIgnoreAllies.DefaultValue,
                "When enabled, Eviscerate will not target drones and other allies. " +
                EvisIgnoreAllies.DefaultAsString()
            );

            // ----------------------------
            // Vanilla defaults requested
            // ----------------------------
            cfgFocusedAssaultDamagePercent.Entry = Config.Bind(
                "Damage and cooldown",
                "Focused Assault Damage (%)",
                cfgFocusedAssaultDamagePercent.DefaultValue,
                new ConfigDescription(
                    "Focused Assault damage per dash hit (vanilla 700%). " +
                    cfgFocusedAssaultDamagePercent.DefaultAsString(),
                    new AcceptableValueRange<float>(
                        cfgFocusedAssaultDamagePercent.MinValue,
                        cfgFocusedAssaultDamagePercent.MaxValue)
                )
            );

            cfgFocusedAssaultCooldownSeconds.Entry = Config.Bind(
                "Damage and cooldown",
                "Focused Assault Cooldown (s)",
                cfgFocusedAssaultCooldownSeconds.DefaultValue,
                new ConfigDescription(
                    "Focused Assault base cooldown in seconds (vanilla 8). " + cfgFocusedAssaultCooldownSeconds.DefaultAsString(),
                    new AcceptableValueRange<float>(
                        cfgFocusedAssaultCooldownSeconds.MinValue,
                        cfgFocusedAssaultCooldownSeconds.MaxValue)
                )
            );

            cfgEvisDamagePercent.Entry = Config.Bind(
                "Damage and cooldown",
                "Eviscerate Damage (%)",
                cfgEvisDamagePercent.DefaultValue,
                new ConfigDescription(
                     "Eviscerate damage per hit (vanilla 110%). Example: 110 = 110%. " + cfgEvisDamagePercent.DefaultAsString(),
                    new AcceptableValueRange<float>(
                        cfgEvisDamagePercent.MinValue,
                        cfgEvisDamagePercent.MaxValue)
                )
            );

            cfgEvisCooldownSeconds.Entry = Config.Bind(
                "Damage and cooldown",
                "Eviscerate Cooldown (s)",
                cfgEvisCooldownSeconds.DefaultValue,
                new ConfigDescription(
                    "Eviscerate base cooldown in seconds (vanilla 6). " + cfgEvisCooldownSeconds.DefaultAsString(),
                    new AcceptableValueRange<float>(
                        cfgEvisCooldownSeconds.MinValue,
                        cfgEvisCooldownSeconds.MaxValue)
                )
            );

            cfgBlindingAssaultDamagePercent.Entry = Config.Bind(
                "Damage and cooldown",
                "Blinding Assault Damage (%)",
                cfgBlindingAssaultDamagePercent.DefaultValue,
                new ConfigDescription(
                    "Blinding Assault damage per dash hit (vanilla 300%). Example: 300 = 300%. " + cfgBlindingAssaultDamagePercent.DefaultAsString(),
                    new AcceptableValueRange<float>(
                        cfgBlindingAssaultDamagePercent.MinValue,
                        cfgBlindingAssaultDamagePercent.MaxValue)
                )
            );

            cfgBlindingAssaultCooldownSeconds.Entry = Config.Bind(
                "Damage and cooldown",
                "Blinding Assault Cooldown (s)",
                cfgBlindingAssaultCooldownSeconds.DefaultValue,
                new ConfigDescription(
                    "Blinding Assault base cooldown in seconds (vanilla 8). " + cfgBlindingAssaultCooldownSeconds.DefaultAsString(),
                    new AcceptableValueRange<float>(
                        cfgBlindingAssaultCooldownSeconds.MinValue,
                        cfgBlindingAssaultCooldownSeconds.MaxValue)
                )
            );
            
            // Enforce "config-file-only" editing:
            // If something changes the value at runtime (RiskOfOptions UI), we call this
            Config.SettingChanged += OnAnyConfigSettingChanged;

            // RiskOfOptions UI (only works if RiskOfOptions is installed)
            // We show values, but we DO NOT allow changing them from inside the game.
            try
            {

                ModSettingsManager.SetModDescription(
                    "Mercenary skill effects.\n\n" +
                    "This mod is configured via the BepInEx config file and RiskUI."
                );

                Sprite icon = LoadEmbeddedSprite("ExamplePlugin.Assets.MercExtraEffectIcon128.png");
                ModSettingsManager.SetModIcon(icon);
                AddConfigSlider(cfgSecondaryLifestealPercent);
                AddConfigSlider(cfgEvisBarrierPercent);

                ModSettingsManager.AddOption(new CheckBoxOption(M1AttackSpeedFix.Entry));
                ModSettingsManager.AddOption(new CheckBoxOption(M2AttackSpeedIgnore.Entry));
                ModSettingsManager.AddOption(new CheckBoxOption(EvisIgnoreAllies.Entry));

                AddConfigSlider(cfgFocusedAssaultDamagePercent);
                AddConfigSlider(cfgFocusedAssaultCooldownSeconds);
                AddConfigSlider(cfgEvisDamagePercent);
                AddConfigSlider(cfgEvisCooldownSeconds);
                AddConfigSlider(cfgBlindingAssaultDamagePercent);
                AddConfigSlider(cfgBlindingAssaultCooldownSeconds);
            }
            catch (Exception e)
            {
                Logger.LogInfo("[MercExtraEffects] RiskOfOptions UI not available: " + e.Message);
            }

        }

        private void AddConfigSlider(ConfigValue<float> entry)
        {
            // RiskOfOptions doesn't expose a true "disabled slider" for ConfigEntry-based options.
            // We show the slider, but any attempted change gets reverted immediately by SettingChanged handler.
            ModSettingsManager.AddOption(new StepSliderOption(
                entry.Entry,
                new StepSliderConfig
                {
                    min = entry.MinValue,
                    max = entry.MaxValue,
                    increment = entry.IncrementValue
                }
            ));
        }

        private void OnAnyConfigSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (e == null || e.ChangedSetting == null) return;

            try
            {
                if (ReferenceEquals(e.ChangedSetting, cfgSecondaryLifestealPercent.Entry) ||
                    ReferenceEquals(e.ChangedSetting, cfgEvisBarrierPercent.Entry) ||
                    ReferenceEquals(e.ChangedSetting, cfgFocusedAssaultDamagePercent.Entry) ||
                    ReferenceEquals(e.ChangedSetting, cfgFocusedAssaultCooldownSeconds.Entry) ||
                    ReferenceEquals(e.ChangedSetting, cfgEvisDamagePercent.Entry) ||
                    ReferenceEquals(e.ChangedSetting, cfgEvisCooldownSeconds.Entry) ||
                    ReferenceEquals(e.ChangedSetting, cfgBlindingAssaultDamagePercent.Entry) ||
                    ReferenceEquals(e.ChangedSetting, cfgBlindingAssaultCooldownSeconds.Entry) ||
                    ReferenceEquals(e.ChangedSetting, M1AttackSpeedFix.Entry) ||
                    ReferenceEquals(e.ChangedSetting, M2AttackSpeedIgnore.Entry) ||
                    ReferenceEquals(e.ChangedSetting, EvisIgnoreAllies.Entry))
                {
                    // damage modification does not need update, the values are retrieved each time an attack is launched
                ApplySkillTuning();

                Logger.LogInfo("[MercExtraEffects] In-game config change detected");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[MercExtraEffects] Failed config update: " + ex);
            }
        }

        // ----------------------------
        // Helpers / tracker
        // ----------------------------
        private static bool IsMerc(CharacterBody body)
        {
            return body != null && body.bodyIndex == mercBodyIndex;
        }

        private void OnBodyStart(CharacterBody body)
        {
            if (body == null) return;
            if (body.GetComponent<MercTracker>() == null)
                body.gameObject.AddComponent<MercTracker>();
        }

        private static MercTracker GetTracker(CharacterBody body)
        {
            return body != null ? body.GetComponent<MercTracker>() : null;
        }

        private class MercTracker : MonoBehaviour
        {
            public bool uppercutActive;
            public bool whirlwindActive;
        }

        // ----------------------------
        // Uppercut active window
        // ----------------------------
        private void Uppercut_OnEnter(On.EntityStates.Merc.Uppercut.orig_OnEnter orig, Uppercut self)
        {
            orig(self);

            var body = self.characterBody;
            if (!IsMerc(body)) return;

            var t = GetTracker(body);
            if (t != null) t.uppercutActive = true;
        }

        private void Uppercut_OnExit(On.EntityStates.Merc.Uppercut.orig_OnExit orig, Uppercut self)
        {
            var body = self.characterBody;
            if (IsMerc(body))
            {
                var t = GetTracker(body);
                if (t != null) t.uppercutActive = false;
            }

            orig(self);
        }

        // ----------------------------
        // Whirlwind active window
        // ----------------------------
        private void WhirlwindBase_OnEnter(On.EntityStates.Merc.WhirlwindBase.orig_OnEnter orig, WhirlwindBase self)
        {
            orig(self);

            var body = self.characterBody;
            if (!IsMerc(body)) return;

            var t = GetTracker(body);
            if (t != null) t.whirlwindActive = true;
        }

        private void WhirlwindBase_OnExit(On.EntityStates.Merc.WhirlwindBase.orig_OnExit orig, WhirlwindBase self)
        {
            var body = self.characterBody;
            if (IsMerc(body))
            {
                var t = GetTracker(body);
                if (t != null) t.whirlwindActive = false;
            }

            orig(self);
        }

        private void Evis_FixedUpdate(On.EntityStates.Merc.EvisDash.orig_FixedUpdate orig, EntityStates.Merc.EvisDash self)
        {
            if (!EvisIgnoreAllies.Value)
            {
                orig(self);
                return;
            }
            // this code prevents eviscerate from targeting allies
            self.stopwatch += Time.fixedDeltaTime;
            if (self.stopwatch > EvisDash.dashPrepDuration && !self.isDashing)
            {
                self.isDashing = true;
                self.dashVector = self.inputBank.aimDirection;
                self.CreateBlinkEffect(Util.GetCorePosition(self.gameObject));
                self.PlayCrossfade("FullBody, Override", "EvisLoop", 0.1f);
                if (self.modelTransform)
                {
                    TemporaryOverlay temporaryOverlay = self.modelTransform.gameObject.AddComponent<TemporaryOverlay>();
                    temporaryOverlay.duration = 0.6f;
                    temporaryOverlay.animateShaderAlpha = true;
                    temporaryOverlay.alphaCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
                    temporaryOverlay.destroyComponentOnEnd = true;
                    temporaryOverlay.originalMaterial = Resources.Load<Material>("Materials/matHuntressFlashBright");
                    // In many RoR2 builds the method is misspelled internally as "AddToCharacerModel" instead "AddToCharacterModel"
                    temporaryOverlay.AddToCharacerModel(self.modelTransform.GetComponent<CharacterModel>());
                    TemporaryOverlay temporaryOverlay2 = self.modelTransform.gameObject.AddComponent<TemporaryOverlay>();
                    temporaryOverlay2.duration = 0.7f;
                    temporaryOverlay2.animateShaderAlpha = true;
                    temporaryOverlay2.alphaCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
                    temporaryOverlay2.destroyComponentOnEnd = true;
                    temporaryOverlay2.originalMaterial = Resources.Load<Material>("Materials/matHuntressFlashExpanded");
                    temporaryOverlay2.AddToCharacerModel(self.modelTransform.GetComponent<CharacterModel>());
                }
            }
            bool flag = self.stopwatch >= EvisDash.dashDuration + EvisDash.dashPrepDuration;
            if (self.isDashing)
            {
                if (self.characterMotor && self.characterDirection)
                {
                    self.characterMotor.rootMotion += self.dashVector * (self.moveSpeedStat * EvisDash.speedCoefficient * Time.fixedDeltaTime);
                }
                if (self.isAuthority)
                {
                    Collider[] array = Physics.OverlapSphere(self.transform.position, self.characterBody.radius + EvisDash.overlapSphereRadius * (flag ? EvisDash.lollypopFactor : 1f), LayerIndex.entityPrecise.mask);
                    for (int i = 0; i < array.Length; i++)
                    {
                        HurtBox component = array[i].GetComponent<HurtBox>();
                        if (component && component.healthComponent != self.healthComponent && !(component.teamIndex == self.teamComponent.teamIndex))
                        {
                            Evis nextState = new Evis();
                            self.outer.SetNextState(nextState);
                            return;
                        }
                    }
                }
            }
            if (flag && self.isAuthority)
            {
                self.outer.SetNextStateToMain();
            }          
        }

        // ----------------------------
        // Skills speed fix
        // ----------------------------
        private void M1_onEnter(On.EntityStates.Merc.Weapon.GroundLight2.orig_OnEnter orig, EntityStates.Merc.Weapon.GroundLight2 self)
        {
            if (M1AttackSpeedFix.Value)
            {
                if (self.step == 2)
                {
                    self.ignoreAttackSpeed = true;
                }
                orig(self);
                if (self.ignoreAttackSpeed && self.isComboFinisher)
                {
                    self.durationBeforeInterruptable = EntityStates.Merc.Weapon.GroundLight2.comboFinisherBaseDurationBeforeInterruptable;
                }
            }
            else
            {
                orig(self);
            }
        }

        private void Uppercut_PlayAnim(On.EntityStates.Merc.Uppercut.orig_PlayAnim orig, EntityStates.Merc.Uppercut self)
        {
            if (M2AttackSpeedIgnore.Value)
            {
                self.duration = EntityStates.Merc.Uppercut.baseDuration;
            }
            orig(self);
        }

        private void WhirlwindAir_PlayAnim(On.EntityStates.Merc.WhirlwindAir.orig_PlayAnim orig, EntityStates.Merc.WhirlwindAir self)
        {
            if (M2AttackSpeedIgnore.Value)
            {
                self.duration = self.baseDuration;
            }
            orig(self);
        }

        private void WhirlwindGround_PlayAnim(On.EntityStates.Merc.WhirlwindGround.orig_PlayAnim orig, EntityStates.Merc.WhirlwindGround self)
        {
            if (M2AttackSpeedIgnore.Value)
            {
                self.duration = self.baseDuration;
            }
            orig(self);
        }

        // ----------------------------
        // Evis barrier effect
        // ----------------------------
        private void Evis_OnExit(On.EntityStates.Merc.Evis.orig_OnExit orig, Evis self)
        {
            var body = self.characterBody;
            if (IsMerc(body))
            {
                var hc = body.healthComponent;
                if (hc)
                {
                    float barrierFrac = Mathf.Clamp(cfgEvisBarrierPercent.Value, 0f, 100f) / 100f;
                    float barrierAmount = hc.fullCombinedHealth * barrierFrac;

                    if (barrierAmount > 0f)
                    {
                        hc.AddBarrier(barrierAmount);
                        // Logger.LogInfo("Barrier granted on Evis exit");
                    }
                }
            }

            orig(self);
        }

        // ----------------------------
        // Focused Assault damage override
        // ----------------------------
        private void FocusedAssaultDash_AuthorityModifyOverlapAttack(
            On.EntityStates.Merc.FocusedAssaultDash.orig_AuthorityModifyOverlapAttack orig,
            FocusedAssaultDash self,
            OverlapAttack overlapAttack)
        {
            orig(self, overlapAttack);

            try
            {
                if (self == null || overlapAttack == null) return;

                float coeff = Mathf.Clamp(cfgFocusedAssaultDamagePercent.Value, 0f, 5000f) / 100f;
                overlapAttack.damage = self.damageStat * coeff;
                // Logger.LogInfo("[MercExtraEffects] Focused assault coeff: " + coeff);
            }
            catch (Exception e)
            {
                Logger.LogError("[MercExtraEffects] FocusedAssaultDash_AuthorityModifyOverlapAttack error: " + e);
            }
        }

        // ----------------------------
        // Blinding Assault damage override
        // ----------------------------
        private void Assaulter2_AuthorityModifyOverlapAttack(
            On.EntityStates.Merc.Assaulter2.orig_AuthorityModifyOverlapAttack orig,
            EntityStates.Merc.Assaulter2 self,
            OverlapAttack attack)
        {
            // Let the game set up the attack first
            orig(self, attack);

            try
            {
                if (self == null || attack == null) return;

                float coeff = Mathf.Clamp(cfgBlindingAssaultDamagePercent.Value, 0f, 5000f) / 100f;
                attack.damage = self.damageStat * coeff;
                // Logger.LogInfo("[MercExtraEffects] Blinding assault coeff: " + coeff);
            }
            catch (Exception e)
            {
                Logger.LogError("[MercExtraEffects] Assaulter2_AuthorityModifyOverlapAttack error: " + e);
            }
        }

        // ----------------------------
        // Server-side: lifesteal
        // ----------------------------
        private void OnServerDamageDealt(DamageReport report)
        {
            try
            {
                if (report == null) return;

                var attackerBody = report.attackerBody;
                if (!IsMerc(attackerBody)) return;

                var t = GetTracker(attackerBody);
                if (t == null) return;

                var hc = attackerBody.healthComponent;
                if (!hc) return;

                float damage = report.damageDealt;
                if (damage <= 0f) return;

                // Secondary lifesteal while Uppercut OR Whirlwind active
                if (t.uppercutActive || t.whirlwindActive)
                {
                    float lifestealFrac = Mathf.Clamp(cfgSecondaryLifestealPercent.Value, 0f, 200f) / 100f;
                    float healAmount = damage * lifestealFrac;
                    if (healAmount > 0f)
                        hc.Heal(healAmount, default(ProcChainMask), true);
                }
            }
            catch (Exception e)
            {
                Logger.LogError("[MercExtraEffects] OnServerDamageDealt error: " + e);
            }
        }

        // ----------------------------
        // Apply skills tuning
        // ----------------------------
        private void ApplySkillTuning()
        {
            ApplyFocusedAssaultCooldown();
            ApplyBlindingAssaultCooldown();
            ApplyEvisDamageCoefficient();
            ApplyEvisCooldown();
        }

        private void ApplyFocusedAssaultCooldown()
        {
            try
            {
                float cd = Mathf.Clamp(cfgFocusedAssaultCooldownSeconds.Value, 0f, 60f);

                SkillDef focusedAssaultDef = Addressables.LoadAssetAsync<SkillDef>(FocusedAssaultSkillDefPath).WaitForCompletion();
                if (focusedAssaultDef == null)
                {
                    Logger.LogWarning("[MercExtraEffects] Focused Assault SkillDef not found at: " + FocusedAssaultSkillDefPath);
                    return;
                }

                float old = focusedAssaultDef.baseRechargeInterval;
                focusedAssaultDef.baseRechargeInterval = cd;

                Logger.LogInfo("[MercExtraEffects] Focused Assault cooldown changed: " + old + " -> " + focusedAssaultDef.baseRechargeInterval);
            }
            catch (Exception e)
            {
                Logger.LogError("[MercExtraEffects] Failed to set Focused Assault cooldown: " + e);
            }
        }

        private void ApplyBlindingAssaultCooldown()
        {
            try
            {
                float cd = Mathf.Clamp(cfgBlindingAssaultCooldownSeconds.Value, 0f, 60f);

                SkillDef BlindingAssaultDef = Addressables.LoadAssetAsync<SkillDef>(BlindingAssaultSkillDefPath).WaitForCompletion();
                if (BlindingAssaultDef == null)
                {
                    Logger.LogWarning("[MercExtraEffects] Blinding Assault SkillDef not found at: " + BlindingAssaultSkillDefPath);
                    return;
                }

                float old = BlindingAssaultDef.baseRechargeInterval;
                BlindingAssaultDef.baseRechargeInterval = cd;

                Logger.LogInfo("[MercExtraEffects] Blinding Assault changed: " + old + " -> " + BlindingAssaultDef.baseRechargeInterval);
            }
            catch (Exception e)
            {
                Logger.LogError("[MercExtraEffects] Failed to set Blinding Assault cooldown: " + e);
            }
        }

        private void ApplyEvisDamageCoefficient()
        {
            try
            {
                float coeff = Mathf.Clamp(cfgEvisDamagePercent.Value, 0f, 5000f) / 100f;

                if (!TrySetStaticFloat(typeof(Evis), "damageCoefficient", coeff))
                {
                    TrySetStaticFloat(typeof(Evis), "baseDamageCoefficient", coeff);
                    TrySetStaticFloat(typeof(Evis), "damageCoefficientPerHit", coeff);
                }
            }
            catch (Exception e)
            {
                Logger.LogError("[MercExtraEffects] Failed to set Evis damage coefficient: " + e);
            }
        }

        private void ApplyEvisCooldown()
        {
            try
            {
                float cd = Mathf.Clamp(cfgEvisCooldownSeconds.Value, 0f, 60f);

                SkillDef evisDef = null;
                foreach (var path in EvisSkillDefPaths)
                {
                    try
                    {
                        evisDef = Addressables.LoadAssetAsync<SkillDef>(path).WaitForCompletion();
                        if (evisDef != null)
                        {
                            // Logger.LogInfo("[MercExtraEffects] Found Evis SkillDef at: " + path);
                            break;
                        }
                    }
                    catch { }
                }

                if (evisDef == null)
                {
                    evisDef = FindMercSpecialSkillDefByState(typeof(EvisDash)) ?? FindMercSpecialSkillDefByState(typeof(Evis));
        }

                if (evisDef == null)
                {
                    Logger.LogWarning("[MercExtraEffects] Could not find Eviscerate SkillDef to set cooldown. (Tried addressables + MercBody scan)");
                    return;
                }

                float old = evisDef.baseRechargeInterval;
                evisDef.baseRechargeInterval = cd;

                Logger.LogInfo("[MercExtraEffects] Eviscerate cooldown changed: " + old + " -> " + evisDef.baseRechargeInterval);
            }
            catch (Exception e)
            {
                Logger.LogError("[MercExtraEffects] Failed to set Eviscerate cooldown: " + e);
            }
        }

        private SkillDef FindMercSpecialSkillDefByState(Type targetStateType)
        {
            try
            {
                GameObject mercPrefab = BodyCatalog.FindBodyPrefab("MercBody");
                if (mercPrefab == null) return null;

                SkillLocator locator = mercPrefab.GetComponent<SkillLocator>();
                if (locator == null || locator.special == null) return null;

                SkillDef def = locator.special.skillDef;
                if (def == null) return null;

                if (def.activationState.stateType == targetStateType)
                    return def;

                return null;
            }
            catch (Exception e)
            {
                Logger.LogWarning("[MercExtraEffects] FindMercSpecialSkillDefByState error: " + e.Message);
                return null;
            }
        }

        private bool TrySetStaticFloat(Type type, string fieldName, float value)
        {
            try
            {
                FieldInfo fi = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (fi == null) return false;
                if (fi.FieldType != typeof(float)) return false;

                float old = (float)fi.GetValue(null);
                fi.SetValue(null, value);
                Logger.LogInfo($"[MercExtraEffects] {type.Name}.{fieldName} changed: {old} -> {value}");
                return true;
            }
            catch (Exception e)
        {
                Logger.LogWarning($"[MercExtraEffects] Failed setting {type.Name}.{fieldName}: {e.Message}");
                return false;
            }
        }
    }
}
