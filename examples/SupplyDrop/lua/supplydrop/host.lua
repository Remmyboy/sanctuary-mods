-- The host's simulation imports this file because mod.json names it as the
-- mod's "hostScript". It runs once, while the host loads, before the match
-- starts: say what should happen and when, and the framework's match events
-- call it at the right moment. Only the host runs it; clients see the
-- result through the game's own updates.

local Events = Import("modapi/events.lua").Events
local Options = Import("modoptions/sanctuarymods.example.supplydrop.lua").Options

Events.Every(Options.interval * 60, function()
    local count = 0
    for _, army in pairs(Armies) do
        if not army.civilian and army:IsAlive() then
            if Options.alloys > 0 then army:GiveResources("alloys", Options.alloys) end
            if Options.energy > 0 then army:GiveResources("energy", Options.energy) end
            count = count + 1
        end
    end
    Log("Supply drop at " .. Events.GameTime() .. "s: " .. count .. " armies got " ..
        Options.alloys .. " alloys and " .. Options.energy .. " energy.")
end)

Events.OnArmyDefeated(function(army)
    Log("Supply drop: army " .. tostring(army.id) .. " is out, so no more drops for it.")
end)
