-- Balance Patch, "economy" section: wrecks last 6 minutes (the game deletes them after 3).
-- HostWreckage:__init starts one thread, WaitSeconds(180) then Delete; while the original
-- __init runs, that thread is swapped for one that waits 360.
if Import("modoptions/sanctuarymods.balancepatch.lua").Options.economy ~= false then
    local wreckLifetime = 360
    local balancePatchWreckInit = HostWreckage.__init

    local function isTimeoutThread(fn)
        local info = debug and debug.getinfo and debug.getinfo(fn, "S")
        return not info or not info.source or string.find(info.source, "wreckageClass", 1, true) ~= nil
    end

    function HostWreckage:__init(...)
        self.NewThread = function(wreck, fn, ...)
            wreck.NewThread = nil
            if isTimeoutThread(fn) then
                return wreck:NewThread(function(w)
                    WaitSeconds(wreckLifetime)
                    w:Delete()
                end)
            end
            return wreck:NewThread(fn, ...)
        end
        balancePatchWreckInit(self, ...)
        self.NewThread = nil
    end
end
