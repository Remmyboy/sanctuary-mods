-- Phantom-X numbers, from the Forged Alliance mod (faf-phantomx v268,
-- lua/PhantomSim.lua) unless a comment says otherwise.

-- Share of the innocents' combined income each live phantom gets every
-- tick, by how many phantoms are still alive. "ally": every phantom allied
-- with every innocent; "enemy": none are; "mix": some are. "war" multiplies
-- a phantom's share while it is at war with another live phantom (and still
-- allied with an innocent). "vampire": in the phantom war, the share of an
-- enemy unit's cost its killer gets back. The lobby's bonus option scales
-- all of these but "war".
Economy = {
    [1] = { ally = 0.20, mix = 0.24, enemy = 0.28, war = 1.0, vampire = 0.0 },
    [2] = { ally = 0.10, mix = 0.14, enemy = 0.18, war = 1.3, vampire = 0.3 },
    [3] = { ally = 0.08, mix = 0.10, enemy = 0.14, war = 1.2, vampire = 0.3 },
}

-- The original gave each phantom an off-map storage unit holding 50,000
-- mass and 300,000 energy, so the bonus never overflows. The commander's
-- own storage is about the same size in both games (FA 650 / 4,000,
-- Sanctuary 500 / 5,000), so the numbers carry over.
PhantomStorage = { alloys = 50000, energy = 300000 }

-- The vote opens this long before the assignment, the volunteer question
-- this long (FA: 6 and 7 minutes into an 8 minute wait).
VoteLead = 120
VolunteerLead = 60

-- A volunteer is this many extra tickets in the draw; everyone has one.
VolunteerTokens = 5

-- With no votes cast, a third of the players (rounded up) are phantoms.
PhantomShare = 1 / 3

-- A paladin mark costs alloys, taken from storage as it comes in:
-- (Base / p + Max * atan(Slope * minutes since the assignment)) * p, where
-- p is the chance a random innocent is a paladin. FA charged mass; alloys
-- are on the same scale.
MarkBaseCost = 4000
MarkMaxCost = 100000
MarkSlope = 0.05

-- "Same as the previous reveal" means this many seconds after it.
RevealGap = 12

-- A paladin's reveal follows its phantom's by this much.
PaladinRevealDelay = 6

-- How the volunteer-free balancer scores a player before the assignment:
-- alloys spent plus energy spent at this many energy per alloy. FA used 20
-- energy per mass; most Sanctuary units cost 10 energy per alloy.
EnergyPerAlloy = 10
