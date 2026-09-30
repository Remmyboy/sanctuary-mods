-- Sanctuary Mod API: shared AI functions that AIs written for a newer game
-- call. Each goes in only when the game lacks it, so the game's own takes
-- over as soon as an update brings it.

if not IsPathMapPathableWithinReach then
    --- True when some cell of the path map within `reach` cells of `pos` is
    --- open ground, or when the map has no cells there (or no path map yet).
    function IsPathMapPathableWithinReach(pos, reach)
        if type(pos) ~= "table" or type(pos.x) ~= "number" or type(pos.z) ~= "number" then return false end
        if type(reach) ~= "number" or reach < 0 then return false end
        if not PathMap then return true end
        local cx, cz = mathFloor(pos.x), mathFloor(pos.z)
        local cells = mathFloor(reach)
        local limit = reach * reach
        local sawCell = false
        for dx = -cells, cells do
            local column = PathMap[cx + dx]
            if column then
                for dz = -cells, cells do
                    if dx * dx + dz * dz <= limit then
                        local cell = column[cz + dz]
                        if cell then
                            if not cell.blocked then return true end
                            sawCell = true
                        end
                    end
                end
            end
        end
        return not sawCell
    end
end
