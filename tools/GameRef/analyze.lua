-- Bytecode lint for tools/GameRef/LuaCheck.cs, run inside the same LuaJIT
-- the syntax check uses. __lc_analyze(code, name) compiles (never runs) the
-- chunk and returns one line per finding:
--   G <line> <name> <kind> <top>
--                            a global assignment; kind is func (a function
--                            stored straight into it), bool (literal
--                            true/false) or var; top is 1 in the main chunk
--                            (not inside any function), else 0
--   P <line>                 pcall(...) as a statement, results discarded
-- Opcode numbers differ between LuaJIT 2.0 and 2.1, so they are measured by
-- compiling tiny probe chunks rather than hard-coded.

local jutil = require("jit.util")
local bit = require("bit")
local funcbc, funck, funcinfo = jutil.funcbc, jutil.funck, jutil.funcinfo
local band, shr = bit.band, bit.rshift

local function decode(f)
    local t = {}
    for pc = 1, 1e9 do
        local ins = funcbc(f, pc)
        if not ins then break end
        t[#t + 1] = {
            op = band(ins, 0xff), a = band(shr(ins, 8), 0xff),
            b = shr(ins, 24), d = shr(ins, 16),
            line = funcinfo(f, pc).currentline,
        }
    end
    return t
end

local function kstr(f, d)
    local k = funck(f, -d - 1)
    if type(k) == "string" then return k end
end

local function find(code, name)
    local f = assert(loadstring(code))
    local t = decode(f)
    for i, x in ipairs(t) do
        if kstr(f, x.d) == name then return t, i end
    end
    error("probe failed: " .. code)
end

local GSET, GGET, KPRI, FNEW, CALL, CALLM
do
    local t, i = find("__p = true", "__p")
    GSET, KPRI = t[i].op, t[i - 1].op
    t, i = find("function __p() end", "__p")
    FNEW = t[i - 1].op
    t, i = find("local a = __p", "__p")
    GGET = t[i].op
    t, i = find("__p()", "__p")
    CALL = t[i + 1].op
    t, i = find("__p(...)", "__p")
    CALLM = t[i + 2].op
end

local function walk(f, out, top)
    local t = decode(f)
    local pending = {}   -- register -> line of a GGET pcall waiting for its call
    for i, x in ipairs(t) do
        local op = x.op
        if op == GSET then
            local prev = t[i - 1]
            local kind = "var"
            if prev and prev.a == x.a and prev.op == FNEW then
                kind = "func"
            elseif prev and prev.a == x.a and prev.op == KPRI and (prev.d == 1 or prev.d == 2) then
                kind = "bool"
            end
            out[#out + 1] = "G\t" .. x.line .. "\t" .. tostring(kstr(f, x.d)) .. "\t" .. kind .. "\t" .. (top and 1 or 0)
        elseif op == GGET then
            pending[x.a] = kstr(f, x.d) == "pcall" and x.line or nil
        elseif (op == CALL or op == CALLM) and pending[x.a] then
            -- B = results + 1: 1 means none kept.
            if x.b == 1 then out[#out + 1] = "P\t" .. pending[x.a] end
            pending[x.a] = nil
        end
    end
    for n = -1, -1e9, -1 do
        local k = funck(f, n)
        if k == nil then break end
        if type(k) == "proto" then walk(k, out, false) end
    end
end

function __lc_analyze(code, name)
    local f, err = loadstring(code, name)
    if not f then return "E\t" .. tostring(err) end
    local out = {}
    walk(f, out, true)
    return table.concat(out, "\n")
end
