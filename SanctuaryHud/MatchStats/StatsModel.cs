using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace SanctuaryHud
{
    // What the Lua side has gathered, as parsed out of its pull: one record
    // per army with its whole-match totals, and a sample a second of the
    // figures the charts draw.

    internal struct Sample
    {
        public int Tick;
        public float AlloyIncome, EnergyIncome;   // per second
        public float AlloySpend, EnergySpend;     // per second
        public float AlloyStored, EnergyStored;
        public float ArmyValue;                   // alloy cost of finished mobile units alive
        public int Units;                         // finished mobile units alive
        public float Score;                       // FAF's formula, so far
    }

    internal sealed class ArmyStats
    {
        public int Id;
        public string Name = "";
        public int Faction;
        public bool Human;
        public int Team;
        public Color Colour = Color.grey;
        /// WinCondition: 0 undecided, 1 won, 2 lost.
        public int Condition;
        public int ConditionTick;

        public int Ticks;
        public float AlloyGathered, EnergyGathered, AlloySpent, EnergySpent;
        public float AlloyWasted, EnergyWasted;
        public int AlloyStallTicks, EnergyStallTicks;
        public float PeakAlloyIncome, PeakEnergyIncome;
        public float MaxStorage;

        public int BuiltLand, BuiltAir, BuiltNaval, BuiltEngineers, BuiltStructures;
        public float BuiltValue;
        public int LostMobile, LostStructures, LostCommander;
        public float LostValue;
        public float PeakArmyValue;
        public int PeakUnits;
        public float Score;
        /// Alloy value of enemy units destroyed, as this army's share: the
        /// game names no killer, so each loss is split among the loser's
        /// enemies.
        public float KilledValue;
        public float CommanderKills;

        public int BuiltTotal => BuiltLand + BuiltAir + BuiltNaval + BuiltEngineers + BuiltStructures;
        public int LostTotal => LostMobile + LostStructures + LostCommander;

        public readonly List<Sample> Samples = new List<Sample>();

        public string FactionName
        {
            get
            {
                switch (Faction)
                {
                    case 1: return "EDA";
                    case 2: return "Chosen";
                    case 3: return "Guard";
                    default: return "";
                }
            }
        }

        public string DisplayName => Name.Length > 0 ? Name : Human ? "Player " + Id : "AI " + Id;
    }

    internal sealed class MatchData
    {
        public string Session;
        public int Tick;
        public int TickRate = 10;
        public int Focus = int.MinValue;
        public string Map = "";
        /// How many sample lines have come across; the next pull asks for
        /// the ones after it.
        public int Cursor;
        public readonly Dictionary<int, ArmyStats> Armies = new Dictionary<int, ArmyStats>();

        /// The armies that played: a seat nobody took still gets an army
        /// (the host fills empty map slots), but it never has any storage.
        /// Allies together, the team with the best score first, and by
        /// score within a team, as FAF's score screen ranks them.
        public List<ArmyStats> Players() =>
            Armies.Values.Where(a => a.Human || a.MaxStorage > 0f)
                .GroupBy(a => a.Team)
                .OrderByDescending(g => g.Max(a => a.Score)).ThenBy(g => g.Key)
                .SelectMany(g => g.OrderByDescending(a => a.Score).ThenBy(a => a.Id))
                .ToList();

        public float Seconds(int tick) => tick / (float)Math.Max(1, TickRate);

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static float F(string[] f, int i) => i < f.Length && float.TryParse(f[i], NumberStyles.Float, Inv, out var v) ? v : 0f;
        private static int I(string[] f, int i) => i < f.Length && int.TryParse(f[i], NumberStyles.Integer, Inv, out var v) ? v : 0;

        private ArmyStats Army(int id)
        {
            if (!Armies.TryGetValue(id, out var a)) Armies[id] = a = new ArmyStats { Id = id };
            return a;
        }

        /// Reads one pull. Returns the session it came from, or null when the
        /// text is not a pull at all; the caller compares sessions to tell a
        /// new match's Lua VM from the one it has been reading.
        public static string SessionOf(string raw)
        {
            if (string.IsNullOrEmpty(raw) || !raw.StartsWith("V|", StringComparison.Ordinal)) return null;
            var end = raw.IndexOf('\n');
            var f = (end < 0 ? raw : raw.Substring(0, end)).Split('|');
            return f.Length > 1 ? f[1] : null;
        }

        public void Apply(string raw)
        {
            foreach (var line in raw.Split('\n'))
            {
                if (line.Length < 2 || line[1] != '|') continue;
                var f = line.Split('|');
                switch (line[0])
                {
                    case 'V':
                        Session = f.Length > 1 ? f[1] : Session;
                        Tick = I(f, 2);
                        TickRate = Math.Max(1, I(f, 3));
                        Focus = I(f, 4);
                        if (f.Length > 5 && f[5].Length > 0) Map = f[5];
                        break;
                    case 'P':
                    {
                        var a = Army(I(f, 1));
                        a.Name = f.Length > 2 ? f[2] : "";
                        a.Faction = I(f, 3);
                        a.Human = I(f, 4) == 1;
                        a.Team = I(f, 5);
                        a.Colour = new Color(F(f, 6), F(f, 7), F(f, 8));
                        a.Condition = I(f, 9);
                        a.ConditionTick = I(f, 10);
                        break;
                    }
                    case 'T':
                    {
                        var a = Army(I(f, 1));
                        a.Ticks = I(f, 2);
                        a.AlloyGathered = F(f, 3);
                        a.EnergyGathered = F(f, 4);
                        a.AlloySpent = F(f, 5);
                        a.EnergySpent = F(f, 6);
                        a.AlloyWasted = F(f, 7);
                        a.EnergyWasted = F(f, 8);
                        a.AlloyStallTicks = I(f, 9);
                        a.EnergyStallTicks = I(f, 10);
                        a.PeakAlloyIncome = F(f, 11);
                        a.PeakEnergyIncome = F(f, 12);
                        a.MaxStorage = F(f, 13);
                        a.BuiltLand = I(f, 14);
                        a.BuiltAir = I(f, 15);
                        a.BuiltNaval = I(f, 16);
                        a.BuiltEngineers = I(f, 17);
                        a.BuiltStructures = I(f, 18);
                        a.BuiltValue = F(f, 19);
                        a.LostMobile = I(f, 20);
                        a.LostStructures = I(f, 21);
                        a.LostCommander = I(f, 22);
                        a.LostValue = F(f, 23);
                        a.PeakArmyValue = F(f, 24);
                        a.PeakUnits = I(f, 25);
                        a.Score = F(f, 26);
                        a.KilledValue = F(f, 27);
                        a.CommanderKills = F(f, 28);
                        break;
                    }
                    case 'S':
                        Army(I(f, 1)).Samples.Add(new Sample
                        {
                            Tick = I(f, 2),
                            AlloyIncome = F(f, 3),
                            EnergyIncome = F(f, 4),
                            AlloySpend = F(f, 5),
                            EnergySpend = F(f, 6),
                            AlloyStored = F(f, 7),
                            EnergyStored = F(f, 8),
                            ArmyValue = F(f, 9),
                            Units = I(f, 10),
                            Score = F(f, 11),
                        });
                        break;
                    case 'C':
                        Cursor = I(f, 1);
                        break;
                }
            }
        }
    }
}
