-- Appended to the game's client/ui/uiUtils.lua.
--
-- Build buttons and unit cards show a portrait loaded by unit id
-- (UI/Sprites/Icons/Units/<id>.sansprite). A unit that borrows another's
-- model with general.modelTpId has no portrait of its own, so it would wear
-- the default "coming soon" picture, and UI mods that hide those (SanctuaryHud
-- does) would hide the unit's build button. Borrow the portrait too.

local originalInitializeTextures = InitializeTextures

function InitializeTextures()
    originalInitializeTextures()
    for tpID, tp in pairs(__Templates.Units) do
        local modelTpId = tp.general and tp.general.modelTpId
        if modelTpId and UnitTemplateIDToIconID[modelTpId] then
            UnitTemplateIDToIconID[tpID] = UnitTemplateIDToIconID[modelTpId]
        end
    end
end
