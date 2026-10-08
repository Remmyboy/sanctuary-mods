-- Balance Patch: an aircraft's shot from inside an enemy shield's bubble that lands inside it
-- hits the shield (balancepatch/shields.lua). CollisionUpdate calls it by name.
-- lua-check: globals ProcessRayCollisionEvent
if Import("modoptions/sanctuarymods.balancepatch.lua").Options.fixes ~= false then
    ProcessRayCollisionEvent = Import("balancepatch/shields.lua").WrapRayCollision(ProcessRayCollisionEvent, Constants.TickTimeStep)
end
