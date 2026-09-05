using System.Collections.Generic;

namespace SanctuaryHud
{
    /// What a hotkey means, independent of faction.
    ///
    /// Every role is a tag expression rather than a list of template ids,
    /// because the three factions share the same tag vocabulary: the T1 point
    /// defence is ucs1001 / ues1001 / ugs1001, and all three carry
    /// DEFENCE + ANTI_SURFACE + STRUCTURE. So one expression resolves to the
    /// right template whichever faction is playing, and a faction that lacks a
    /// tier (only Chosen has a T3 point defence) simply has a shorter cycle.
    ///
    /// The expressions are evaluated inside the game's own Tags table, which
    /// overloads `+` as union, `*` as intersection and `-` as difference.
    internal enum RoleMode
    {
        /// Something an engineer, engineering station or the commander places.
        Structure,
        /// Something a factory queues.
        Unit,
    }

    internal sealed class Role
    {
        internal string Name;
        internal RoleMode Mode;
        /// A Lua expression over `Tags`, evaluated at press time.
        internal string Expression;
        internal string DefaultKey;
        internal string Description;
        /// Highest tech tier this role will offer. These roles stop at T3
        /// because the experimentals above them are faction-specific one-offs
        /// that want their own keys — without the cap, Guard's T4 Experimental
        /// Generator (ugs4621, tagged ENERGY_PRODUCTION) would sit at the top
        /// of the energy cycle and a tap of D would try to start one.
        internal int MaxTier;

        internal Role(string name, RoleMode mode, string expression, string defaultKey, string description,
            int maxTier = 3)
        {
            Name = name;
            Mode = mode;
            Expression = expression;
            DefaultKey = defaultKey;
            Description = description;
            MaxTier = maxTier;
        }
    }

    internal static class Roles
    {
        /// The shared T1-T3 roles — the ones every faction has, so one key
        /// means the same thing whoever you are playing.
        ///
        /// Order matters: roles sharing a key merge into one cycle, ranked by
        /// tier first and this order second. That does two jobs at once. Where
        /// the roles are mutually exclusive by context it reads as "first one
        /// that applies" — R is the land factory's tank and the naval
        /// factory's warship, and no factory builds both. Where they can
        /// coexist it reads as a round-robin: an engineer that can build all
        /// three factories gets land, air, then naval off repeated presses of
        /// W, each at its best tier, before the cycle drops a tier and comes
        /// round again. Splitting them back onto separate keys is just a
        /// config edit.
        ///
        /// Faction-unique units (Guard transmitters, Chosen shield boosters,
        /// EDA repair stations) and the T4/T5 experimentals are deliberately
        /// absent; they need their own generic keys and are a separate pass.
        internal static readonly List<Role> All = new List<Role>
        {
            // ---- Structures: what an engineer places ----
            new Role("LandFactory", RoleMode.Structure,
                "Tags.LAND_FACTORY", "W", "Land factory."),
            // Air and naval share W: one key cycles the three domains, each at
            // its best tier, rather than spending three keys on one decision.
            new Role("AirFactory", RoleMode.Structure,
                "Tags.AIR_FACTORY", "W", "Air factory — second press of the factory key."),
            new Role("NavalFactory", RoleMode.Structure,
                "Tags.NAVAL_FACTORY", "W", "Naval factory — third press of the factory key."),
            new Role("EngineeringStation", RoleMode.Structure,
                "Tags.ENGINEERING_STATION", "E", "Engineering station."),

            new Role("AlloyExtractor", RoleMode.Structure,
                "Tags.ALLOYS_EXTRACTION * Tags.STRUCTURE", "S", "Alloy extractor."),
            new Role("AlloyStorage", RoleMode.Structure,
                "Tags.ALLOYS_STORAGE * Tags.STRUCTURE", "Ctrl-S", "Alloy storage."),
            new Role("EnergyGenerator", RoleMode.Structure,
                "Tags.ENERGY_PRODUCTION * Tags.STRUCTURE", "D", "Energy generator."),
            // Factories carry ENERGY_STORAGE too, so they have to come out or
            // this key would offer a land factory as its top "storage" result.
            new Role("EnergyStorage", RoleMode.Structure,
                "Tags.ENERGY_STORAGE * Tags.STRUCTURE - Tags.FACTORY", "Ctrl-D", "Energy storage."),

            new Role("PointDefence", RoleMode.Structure,
                "Tags.DEFENCE * Tags.ANTI_SURFACE * Tags.STRUCTURE", "X", "Point defence."),
            new Role("AntiAir", RoleMode.Structure,
                "Tags.DEFENCE * Tags.ANTI_AIR * Tags.STRUCTURE", "C", "Anti-air turret."),
            new Role("TorpedoLauncher", RoleMode.Structure,
                "Tags.DEFENCE * Tags.ANTI_NAVAL * Tags.STRUCTURE", "Ctrl-X", "Torpedo launcher."),
            new Role("Shield", RoleMode.Structure,
                "Tags.SHIELD * Tags.STRUCTURE", "T", "Shield generator."),
            new Role("Artillery", RoleMode.Structure,
                "Tags.ARTILLERY * Tags.STRUCTURE", "K", "Artillery emplacement."),
            new Role("Wall", RoleMode.Structure,
                "Tags.WALL", "O", "Wall segment."),

            new Role("Radar", RoleMode.Structure,
                "Tags.RADAR * Tags.STRUCTURE", "R", "Radar."),
            new Role("Sonar", RoleMode.Structure,
                "Tags.SONAR * Tags.STRUCTURE", "Ctrl-R", "Sonar."),

            new Role("LandTechCentre", RoleMode.Structure,
                "Tags.LAND_TECH_CENTRE", "B", "Land tech centre."),
            new Role("AirTechCentre", RoleMode.Structure,
                "Tags.AIR_TECH_CENTRE", "Ctrl-B", "Air tech centre."),
            new Role("NavalTechCentre", RoleMode.Structure,
                "Tags.NAVAL_TECH_CENTRE", "Ctrl-N", "Naval tech centre."),

            // ---- Units: what a factory queues ----
            //
            // These follow the FAF hotbuild layout that grew out of Zulan's:
            // mnemonic, and the same letter reused across domains because a
            // factory only ever builds one of them. S is scout or submarine,
            // T is tank or transport, B is raider, bomber or battleship, F is
            // fighter or frigate — which is exactly what roles sharing a key
            // already do here, resolved by whichever factory is selected.
            new Role("Engineer", RoleMode.Unit,
                "Tags.ENGINEER * Tags.MOBILE", "E", "Engineer."),
            new Role("Scout", RoleMode.Unit,
                "Tags.SCOUT * Tags.MOBILE", "S", "Scout (land or air, whichever this factory builds)."),

            new Role("Tank", RoleMode.Unit,
                "Tags.TANK * Tags.MOBILE * Tags.LAND", "T", "Tank."),
            new Role("Transport", RoleMode.Unit,
                "Tags.TRANSPORT * Tags.MOBILE", "T", "Transport."),

            // Zulan's gives each warship class its own key rather than walking
            // a line of tiers, so frigate/destroyer/battleship are split.
            new Role("Raider", RoleMode.Unit,
                "Tags.RAIDER * Tags.MOBILE", "B", "Raider — the light, fast land unit."),
            new Role("Bomber", RoleMode.Unit,
                "Tags.BOMBER * Tags.MOBILE", "B", "Bomber."),
            new Role("Battleship", RoleMode.Unit,
                "Tags.BATTLESHIP", "B", "Battleship."),

            new Role("Fighter", RoleMode.Unit,
                "Tags.FIGHTER * Tags.MOBILE", "F", "Air-superiority fighter."),
            new Role("Frigate", RoleMode.Unit,
                "Tags.FRIGATE", "F", "Frigate."),

            new Role("Submarine", RoleMode.Unit,
                "Tags.SUBMARINE", "S", "Submarine."),
            new Role("Destroyer", RoleMode.Unit,
                "Tags.DESTROYER", "D", "Destroyer."),

            new Role("MobileAntiAir", RoleMode.Unit,
                "Tags.ANTI_AIR * Tags.MOBILE * Tags.LAND", "N", "Mobile anti-air."),
            new Role("MobileArtillery", RoleMode.Unit,
                "Tags.ARTILLERY * Tags.MOBILE", "R", "Mobile artillery."),
            // GUNSHIP on its own also covers transports and some scouts, which
            // both carry it; the combat ones are the ones that shoot ground.
            new Role("Gunship", RoleMode.Unit,
                "Tags.GUNSHIP * Tags.ANTI_SURFACE * Tags.MOBILE", "G", "Gunship."),
            new Role("TorpedoBomber", RoleMode.Unit,
                "Tags.TORPEDO_BOMBER", "O", "Torpedo bomber."),
            // Sanctuary-only: Zulan's has no sniper, and its V is the mobile
            // shield, which has no shared-tier equivalent here. Reclaim still
            // works on V for anything that is not a factory.
            new Role("Sniper", RoleMode.Unit,
                "Tags.SNIPER * Tags.MOBILE", "V", "Sniper."),
        };
    }
}
