using System;
using BepInEx.Logging;
using EM.Lua;
using EM.Prefabs;
using HarmonyLib;

namespace SanctuaryHud.CameraUtils
{
    // Stops units and structures vanishing when the camera pulls back.
    //
    // Every renderable entity carries an LODDataComponent: up to six levels,
    // each with a render distance, and the culling system picks the first
    // level whose distance the camera is still inside. Run past the last
    // one and the entity is simply not drawn — that is the disappearance.
    // Units and structures get exactly one level, built in Lua's
    // unitTemplateLoader at 100 world units for anything that moves and 160
    // for anything that doesn't, so past that they are gone and only the
    // strategic icon is left.
    //
    // Nothing exposes those numbers at runtime: the culling job is a Burst
    // job, and the Lua side has no setter for them. They exist in a
    // patchable, managed form in exactly one place — the point where the Lua
    // templates for a match are turned into prefab templates — so that is
    // where this raises them, once per match as the templates load.
    //
    // That point is reachable because prefab creation is one of the Lua
    // entry points the generator leaves managed: ClientLuaInterface.CreatePrefab
    // is bound as a plain delegate rather than through
    // BurstCompiler.CompileFunctionPointer (most of its neighbours in
    // GeneratedDelegates are), so the managed method really does run and a
    // Harmony patch on it really does fire.
    //
    // Only chains with a single level are touched. That is what a unit or
    // structure has, and its one distance is purely a cull distance with
    // nothing cheaper to fall back to. Props keep a real chain with an
    // impostor at the end, so raising theirs would hold full-detail meshes on
    // screen at range; those are left exactly as the game built them.
    //
    // Render prefabs are client-side, so this changes no simulation state and
    // no hashed file: a client running it stays lobby-compatible.
    internal static class DrawDistance
    {
        /// Smallest distance at which a unit may stop being drawn, in world
        /// units. 0 leaves the game's own values alone.
        internal static float Wanted;

        /// What the templates loaded for the current match actually got, so
        /// the panel can say when a change is still waiting for the next one.
        internal static float Applied;

        private static ManualLogSource _log;
        private static bool _loggedFailure;

        internal static void ApplyPatch(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            harmony.Patch(
                AccessTools.Method(typeof(LuaInterface), nameof(LuaInterface.CreateLocalPrefabTemplates)),
                postfix: new HarmonyMethod(typeof(DrawDistance), nameof(TemplatesPostfix)));
        }

        /// Called when the client VM goes away: the next match rebuilds its
        /// templates from scratch, so nothing is applied any more.
        internal static void Forget()
        {
            Applied = 0f;
            _loggedFailure = false;
        }

        private static void TemplatesPostfix(ref LocalPrefabTemplates localPrefabTemplates)
        {
            try
            {
                var floor = Wanted;
                if (floor <= 0f) return;

                var levels = localPrefabTemplates.lodPrefabTemplates;
                if (!levels.IsCreated) return;

                for (var i = 0; i < levels.Length; i++)
                {
                    // ElementAt hands back a reference into the list's own
                    // memory, so this edits the template the game is about to
                    // build its prefab from.
                    ref var template = ref levels.ElementAt(i);
                    if (template.lodDataComponent.lodCount != 1) continue;
                    if (template.lodDataComponent.renderDistance0 >= floor) continue;
                    template.lodDataComponent.renderDistance0 = floor;
                }

                if (Applied != floor)
                {
                    Applied = floor;
                    _log?.LogInfo($"Camera Utilities: unit draw distance raised to {floor:0} world units.");
                }
            }
            catch (Exception e)
            {
                // A template that cannot be raised is a unit that fades early,
                // not a broken match — say so once and leave the rest alone.
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    _log?.LogWarning($"Camera Utilities: raising the draw distance failed, units will fade as usual: {e}");
                }
            }
        }
    }
}
