-- JoyPro2 telemetry force feedback: sends flight data to JoyPro's force feedback and telemetry recording (UDP 127.0.0.1:41992).
-- Installed by JoyPro (engine ffb telemetry setup dcs); remove it there or delete this file.
-- It only reads (Export.Lo*), never changes the simulation, and never blocks: UDP, no reply expected.
-- Hook version 2. The line protocol is described in JoyPro's DcsTelemetryProtocol (docs/ffb-telemetry.md):
--   t=..;k=v;...        flight values, at most 100 lines per second of model time
--   slow t=..;k=v;...   slower values (10 per second; route and wingmen once a second)
--   plane <type>        the aircraft (on change and every 5 s)    start / exit   simulation start / stop
--   info <key>=<text>   session facts (map, mission, pilot, ...; at start and every 10 s)
--   objs / obj / objend the other objects once a second, when the server allows object export
--   ev <name> <text>    game events (takeoff, landing, kill, pause, mode changes ...)   msg <from>|<text>  chat
--   noexport            the multiplayer server forbids ownship export
local ok, err = pcall(function()
    local api = Sim or DCS
    package.path = package.path .. ';.\\LuaSocket\\?.lua'
    package.cpath = package.cpath .. ';.\\LuaSocket\\?.dll'
    local socket = require('socket')
    local udp = socket.udp()
    udp:settimeout(0)
    local PORT = 41992
    local E = Export
    local fmt, concat, gsub, sqrt, floor = string.format, table.concat, string.gsub, math.sqrt, math.floor
    local huge = math.huge

    local function send(text)
        pcall(udp.sendto, udp, text, '127.0.0.1', PORT)
    end

    -- A bug in this file is written to dcs.log once per place and mission; a missing or failing Lo* function is
    -- not a bug (modules export different things) and stays silent.
    local reported = {}
    local function report(where, e)
        if reported[where] then return end
        reported[where] = true
        if log and log.write then
            pcall(log.write, 'JoyPro', log.WARNING, 'JoyPro telemetry hook (' .. where .. '): ' .. tostring(e))
        end
    end

    local function get(name, ...)
        local f = E and E[name]
        if type(f) ~= 'function' then return nil end
        local okCall, a, b, c = pcall(f, ...)
        if okCall then return a, b, c end
        return nil
    end

    local function finite(v)
        return type(v) == 'number' and v == v and v ~= huge and v ~= -huge
    end

    local function clean(s)
        if s == nil then return '' end
        return (gsub(tostring(s), '[|\r\n]', '/'))
    end

    local function short(s)
        return string.sub(clean(s), 1, 64)
    end

    local parts = {}
    local function add(key, value, f)
        if finite(value) then parts[#parts + 1] = key .. '=' .. fmt(f or '%.5g', value) end
    end
    local function flag(key, value)
        if type(value) == 'boolean' then parts[#parts + 1] = key .. (value and '=1' or '=0')
        elseif finite(value) then parts[#parts + 1] = key .. '=' .. fmt('%.5g', value) end
    end
    local function lr(prefix, t)
        if type(t) == 'table' then add(prefix .. '_l', t.left); add(prefix .. '_r', t.right) end
    end
    local function value(t)
        if type(t) == 'table' then return t.value end
        return nil
    end
    local function count(t)
        if type(t) ~= 'table' then return nil end
        local n = 0
        for _ in pairs(t) do n = n + 1 end
        return n
    end

    -- LoGetMCPState() flags with short keys ("stall" and "fail" are in the fast line). Unknown flags: mcp_<name>.
    local MCP = {
        LeftEngineFailure = 'fail_eng_l', RightEngineFailure = 'fail_eng_r', HydraulicsFailure = 'fail_hyd',
        ACSFailure = 'fail_acs', AutopilotFailure = 'fail_ap', AutopilotOn = 'ap_on', MasterWarning = 'master_warn',
        LeftTailPlaneFailure = 'fail_tail_l', RightTailPlaneFailure = 'fail_tail_r',
        LeftAileronFailure = 'fail_ail_l', RightAileronFailure = 'fail_ail_r', CanopyOpen = 'canopy_open',
        CannonFailure = 'fail_gun', LeftMainPumpFailure = 'fail_pump_l', RightMainPumpFailure = 'fail_pump_r',
        LeftWingPumpFailure = 'fail_wpump_l', RightWingPumpFailure = 'fail_wpump_r', RadarFailure = 'fail_radar',
        EOSFailure = 'fail_eos', MLWSFailure = 'fail_mlws', RWSFailure = 'fail_rws', ECMFailure = 'fail_ecm',
        GearFailure = 'fail_gear', MFDFailure = 'fail_mfd', HUDFailure = 'fail_hud', HelmetFailure = 'fail_hmd',
        FuelTankDamage = 'fuel_tank_dmg', StallSignalization = false
    }

    local lastSend, lastPlane, lastNotice, lastSlow, lastSlow1, lastFacts, lastObj = -1, nil, -100, -100, -100, -100, -100
    local lastMode, objAllowed, sensorAllowed = nil, nil, nil
    local mission = {}
    local batch, objSeq = nil, 0

    local function reset()
        lastSend, lastPlane, lastNotice, lastSlow, lastSlow1, lastFacts, lastObj = -1, nil, -100, -100, -100, -100, -100
        lastMode, objAllowed, sensorAllowed = nil, nil, nil
        batch = nil
        reported = {}
    end

    -- Mission facts, read once at simulation start (the mission table is big; it is not read again).
    local function readMission()
        mission = {}
        pcall(function() mission.mission = api.getMissionName() end)
        pcall(function()
            local m = api.getCurrentMission()
            local mm = type(m) == 'table' and (m.mission or m) or nil
            if type(mm) ~= 'table' then return end
            if type(mm.theatre) == 'string' then mission.map = mm.theatre end
            local d = mm.date
            if type(d) == 'table' and finite(d.Year) and finite(d.Month) and finite(d.Day) then
                mission.date = fmt('%04d-%02d-%02d', d.Year, d.Month, d.Day)
            end
            if finite(mm.start_time) then mission.start = fmt('%d', floor(mm.start_time)) end
        end)
        pcall(function()
            local v = get('LoGetVersionInfo')
            local p = type(v) == 'table' and v.ProductVersion or nil
            if type(p) == 'table' and p[1] then mission.version = fmt('%d.%d.%d.%d', p[1], p[2] or 0, p[3] or 0, p[4] or 0) end
        end)
        pcall(function() mission.mp = api.isMultiplayer() and '1' or '0' end)
    end

    local function pilotName()
        local name
        pcall(function()
            if net and net.get_my_player_id and api.isMultiplayer and api.isMultiplayer() then
                name = net.get_player_info(net.get_my_player_id(), 'name')
            end
        end)
        if name == nil or name == '' then name = get('LoGetPilotName') end
        return name
    end

    local function facts(me)
        local lines = {}
        local function info(key, text)
            if text ~= nil and text ~= '' then lines[#lines + 1] = 'info ' .. key .. '=' .. clean(text) end
        end
        info('map', mission.map)
        info('mission', mission.mission)
        info('mission_date', mission.date)
        info('mission_time', mission.start)
        info('game_version', mission.version)
        info('multiplayer', mission.mp)
        info('pilot', pilotName())
        if type(me) == 'table' then
            info('unit', me.UnitName)
            info('group', me.GroupName)
            local c = tonumber(me.CoalitionID)
            info('coalition', c == 1 and 'Red' or c == 2 and 'Blue' or c == 0 and 'Neutral' or me.Coalition)
            if finite(me.Country) then info('country', fmt('%d', me.Country)) end
        end
        if objAllowed ~= nil then info('objects', objAllowed and 'on' or 'off') end
        if sensorAllowed ~= nil then info('sensors', sensorAllowed and 'on' or 'off') end
        if #lines > 0 then send(concat(lines, '\n')) end
    end

    -- 10 per second: values from the tables the fast line already read, plus a few cheap calls.
    local function slow(t, me, mech, engine, payload, mcp, oncePerSecond)
        parts = {}
        if type(me) == 'table' then add('coalition', tonumber(me.CoalitionID)); add('country', me.Country) end
        if type(engine) == 'table' then
            lr('temp', engine.Temperature)
            lr('fuel_flow', engine.FuelConsumption)
            lr('hyd', engine.HydraulicPressure)
            add('fuel_int', engine.fuel_internal)
            add('fuel_ext', engine.fuel_external)
            if finite(engine.fuel_internal) then add('fuel', engine.fuel_internal + (finite(engine.fuel_external) and engine.fuel_external or 0)) end
        end
        if type(mech) == 'table' then
            local cs = mech.controlsurfaces
            if type(cs) == 'table' then lr('elev', cs.elevator); lr('ail', cs.eleron); lr('rud', cs.rudder) end
            add('wheelbrake', value(mech.wheelbrakes))
            add('canopy', value(mech.canopy))
            add('refuel_boom', value(mech.refuelingboom))
            add('chute', value(mech.parachute))
            add('hook', value(mech.hook))
            add('wing_fold', value(mech.wing))
            add('air_intake', value(mech.airintake))
            add('nose_flap', value(mech.noseflap))
        end
        if type(payload) == 'table' then
            add('station', payload.CurrentStation)
            local stations = payload.Stations
            if type(stations) == 'table' then
                local n, stores = 0, 0
                for i, st in pairs(stations) do
                    n = n + 1
                    if type(st) == 'table' and finite(st.count) then
                        stores = stores + st.count
                        if i == payload.CurrentStation then add('station_count', st.count) end
                    end
                end
                add('stations', n)
                add('stores', stores)
            end
        end
        if type(mcp) == 'table' then
            for name, v in pairs(mcp) do
                if type(v) == 'boolean' and type(name) == 'string' then
                    local key = MCP[name]
                    if key == nil then key = 'mcp_' .. string.sub(gsub(string.lower(name), '[^a-z0-9_]', ''), 1, 28) end
                    if key then flag(key, v) end
                end
            end
        end
        local snares = get('LoGetSnares')
        if type(snares) == 'table' then add('chaff', snares.chaff); add('flare', snares.flare) end
        add('ralt', get('LoGetRadarAltimeter'))
        add('mag_heading', get('LoGetMagneticYaw'))
        add('gs_dev', get('LoGetGlideDeviation'))
        add('loc_dev', get('LoGetSideDeviation'))
        add('slip_ball', get('LoGetSlipBallPosition'))
        add('baro', get('LoGetBasicAtmospherePressure'))
        add('shake', get('LoGetShakeAmplitude'))
        add('time_accel', get('LoGetModelTimeAcceleration'))
        flag('in_air', get('LoGetInAir'))
        local wind = get('LoGetVectorWindVelocity')
        if type(wind) == 'table' then add('wind_x', wind.x); add('wind_y', wind.y); add('wind_z', wind.z) end
        local nav = get('LoGetNavigationInfo')
        if type(nav) == 'table' then
            local req = nav.Requirements
            if type(req) == 'table' then
                add('req_roll', req.roll); add('req_pitch', req.pitch); add('req_speed', req.speed)
                add('req_vs', req.vertical_speed); add('req_alt', req.altitude)
            end
            local acs = type(nav.ACS) == 'table' and nav.ACS or {}
            flag('autothrust', acs.autothrust)
            local sm = type(nav.SystemMode) == 'table' and nav.SystemMode or {}
            local mode = clean(sm.master) .. '/' .. clean(sm.submode) .. ' ' .. clean(acs.mode)
            if mode ~= lastMode then
                if lastMode ~= nil or mode ~= '/ ' then send('ev mode ' .. mode) end
                lastMode = mode
            end
        end
        -- Only an explicit false forbids (a missing or failing check function does not).
        local sensors = get('LoIsSensorExportAllowed') ~= false
        sensorAllowed = sensors
        if sensors then
            add('targets', count(get('LoGetTargetInformation')))
            local locked = get('LoGetLockedTargetInformation')
            add('locked', count(locked))
            if type(locked) == 'table' then
                local _, first = next(locked)
                if type(first) == 'table' then
                    add('lock_id', first.ID, '%.10g'); add('lock_dist', first.distance)
                    add('lock_vc', first.convergence_velocity); add('lock_mach', first.mach)
                    add('lock_aspect', first.delta_psi)
                end
            end
        end
        if oncePerSecond then
            local route = get('LoGetRoute')
            if type(route) == 'table' then
                local g = route.goto_point
                if type(g) == 'table' then
                    add('wpt', g.this_point_num)
                    local wp, pos = g.world_point, type(me) == 'table' and me.Position or nil
                    if type(wp) == 'table' and type(pos) == 'table' and finite(wp.x) and finite(wp.z) and finite(pos.x) and finite(pos.z) then
                        add('wpt_dist', sqrt((wp.x - pos.x) ^ 2 + (wp.z - pos.z) ^ 2))
                    end
                end
                add('wpts', count(route.route))
            end
            add('wingmen', count(get('LoGetWingInfo')))
        end
        if #parts == 0 then return nil end
        return 'slow t=' .. fmt('%.3f', t) .. ';' .. concat(parts, ';')
    end

    local function ownship(t)
        if get('LoIsOwnshipExportAllowed') == false then
            if t - lastNotice > 5 then send('noexport'); lastNotice = t end
            if t - lastFacts >= 10 then lastFacts = t; facts(nil) end
            return
        end
        local me = get('LoGetSelfData')
        if type(me) ~= 'table' then return end
        if me.Name ~= lastPlane or t - lastNotice > 5 then
            send('plane ' .. clean(me.Name))
            lastPlane = me.Name
            lastNotice = t
        end
        if t - lastFacts >= 10 then
            lastFacts = t
            facts(me)
        end
        parts = {}
        add('t', t, '%.3f')
        add('ias', get('LoGetIndicatedAirSpeed'))
        add('tas', get('LoGetTrueAirSpeed'))
        add('mach', get('LoGetMachNumber'))
        add('aoa', get('LoGetAngleOfAttack'))
        add('aos', get('LoGetAngleOfSideSlip'))
        add('vv', get('LoGetVerticalVelocity'))
        add('agl', get('LoGetAltitudeAboveGroundLevel'))
        add('alt', get('LoGetAltitudeAboveSeaLevel'))
        local acc = get('LoGetAccelerationUnits')
        if type(acc) == 'table' then add('g', acc.y); add('gx', acc.x); add('gz', acc.z) end
        local w = get('LoGetAngularVelocity')
        if type(w) == 'table' then add('wx', w.x); add('wy', w.y); add('wz', w.z) end
        local v = get('LoGetVectorVelocity')
        if type(v) == 'table' then
            if finite(v.x) and finite(v.z) then add('gs', sqrt(v.x * v.x + v.z * v.z)) end
            add('vx', v.x); add('vy', v.y); add('vz', v.z)
        end
        local mech = get('LoGetMechInfo')
        if type(mech) == 'table' then
            if type(mech.gear) == 'table' then
                add('gear', mech.gear.value)
                local main = mech.gear.main
                if type(main) == 'table' then
                    if type(main.left) == 'table' then add('rod_l', main.left.rod) end
                    if type(main.right) == 'table' then add('rod_r', main.right.rod) end
                    if type(main.nose) == 'table' then add('rod_n', main.nose.rod) end
                end
            end
            add('flaps', value(mech.flaps))
            add('speedbrake', value(mech.speedbrakes))
        end
        local payload = get('LoGetPayloadInfo')
        if type(payload) == 'table' and type(payload.Cannon) == 'table' then add('shells', payload.Cannon.shells) end
        local mcp = get('LoGetMCPState')
        if type(mcp) == 'table' then
            add('stall', mcp.StallSignalization and 1 or 0)
            local failed = 0
            for key, val in pairs(mcp) do
                if val == true and type(key) == 'string' and string.find(key, 'Failure') then failed = failed + 1 end
            end
            if mcp.FuelTankDamage then failed = failed + 1 end
            add('fail', failed)
        end
        local engine = get('LoGetEngineInfo')
        if type(engine) == 'table' and type(engine.RPM) == 'table' then add('rpm_l', engine.RPM.left); add('rpm_r', engine.RPM.right) end
        local p, b, y = get('LoGetADIPitchBankYaw')
        add('pitch', p); add('bank', b); add('yaw', y)
        local lla = me.LatLongAlt
        if type(lla) == 'table' then add('lat', lla.Lat, '%.7f'); add('lon', lla.Long, '%.7f'); add('alt_msl', lla.Alt, '%.2f') end
        add('heading', me.Heading, '%.6g')
        add('true_pitch', me.Pitch, '%.6g')
        add('true_bank', me.Bank, '%.6g')
        local pos = me.Position
        if type(pos) == 'table' then add('pos_x', pos.x, '%.2f'); add('pos_y', pos.y, '%.2f'); add('pos_z', pos.z, '%.2f') end
        local line = concat(parts, ';')
        if t - lastSlow >= 0.1 then
            local once = t - lastSlow1 >= 1
            lastSlow = t
            if once then lastSlow1 = t end
            local okSlow, s = pcall(slow, t, me, mech, engine, payload, mcp, once)
            if not okSlow then report('slow', s)
            elseif s then line = s .. '\n' .. line end
        end
        send(line)
    end

    -- The other objects: one snapshot a second (at most 600, the player's own aircraft left out), formatted over the
    -- next frames (100 per frame) and sent in datagrams below 8 KB:
    --   objs <seq> <part> <t>  then  obj id|level1.level2.level3.level4|name|coalition id|lat|lon|alt|bank|pitch|heading|unit|group|country|flags
    --   objend <seq> <parts> <count>  (in the last datagram)
    local OBJ_CAP, OBJ_PER_FRAME, CHUNK = 600, 100, 7000

    local function num(v, f)
        if finite(v) then return fmt(f, v) end
        return ''
    end

    local function objLine(id, o)
        local ty = o.Type
        local tys = ''
        if type(ty) == 'table' then
            tys = fmt('%d.%d.%d.%d', tonumber(ty.level1) or 0, tonumber(ty.level2) or 0, tonumber(ty.level3) or 0, tonumber(ty.level4) or 0)
        end
        local lla = type(o.LatLongAlt) == 'table' and o.LatLongAlt or {}
        local fl, flags = o.Flags, ''
        if type(fl) == 'table' then
            if fl.Human then flags = flags .. 'H' end
            if fl.RadarActive then flags = flags .. 'R' end
            if fl.Jamming then flags = flags .. 'J' end
            if fl.IRJamming then flags = flags .. 'K' end
            if fl.Static then flags = flags .. 'S' end
            if fl.Invisible then flags = flags .. 'I' end
            if fl.AI_ON then flags = flags .. 'A' end
        end
        local c = tonumber(o.CoalitionID)
        return 'obj ' .. concat({
            clean(id), tys, short(o.Name), c and fmt('%d', c) or short(o.Coalition),
            num(lla.Lat, '%.7f'), num(lla.Long, '%.7f'), num(lla.Alt, '%.1f'),
            num(o.Bank, '%.4f'), num(o.Pitch, '%.4f'), num(o.Heading, '%.4f'),
            short(o.UnitName), short(o.GroupName), num(o.Country, '%d'), flags }, '|')
    end

    local function objFlush(final)
        local b = batch
        b.part = b.part + 1
        local text = 'objs ' .. b.seq .. ' ' .. b.part .. ' ' .. fmt('%.3f', b.t)
        if #b.lines > 0 then text = text .. '\n' .. concat(b.lines, '\n') end
        if final then text = text .. '\nobjend ' .. b.seq .. ' ' .. b.part .. ' ' .. b.count end
        send(text)
        b.lines, b.bytes = {}, 0
    end

    local function objStep()
        local b = batch
        local n = 0
        while b.i <= #b.list and n < OBJ_PER_FRAME do
            local id, o = b.list[b.i], b.list[b.i + 1]
            b.i, n = b.i + 2, n + 1
            local okLine, line = pcall(objLine, id, o)
            if okLine and line then
                b.lines[#b.lines + 1] = line
                b.bytes = b.bytes + #line + 1
                b.count = b.count + 1
                if b.bytes > CHUNK then objFlush(false) end
            elseif not okLine then
                report('object', line)
            end
        end
        if b.i > #b.list then
            objFlush(true)
            batch = nil
        end
    end

    local function objects(t)
        if batch then objStep(); return end
        if t - lastObj < 1 then return end
        lastObj = t
        local allowed = get('LoIsObjectExportAllowed') ~= false
        if allowed ~= objAllowed then
            objAllowed = allowed
            send('info objects=' .. (allowed and 'on' or 'off'))
        end
        if not allowed then return end
        local all = get('LoGetWorldObjects')
        if type(all) ~= 'table' then return end
        local own = get('LoGetPlayerPlaneId')
        local list, n = {}, 0
        for id, o in pairs(all) do
            if id ~= own and type(o) == 'table' then
                list[#list + 1] = id
                list[#list + 1] = o
                n = n + 1
                if n >= OBJ_CAP then break end
            end
        end
        objSeq = objSeq + 1
        batch = { seq = objSeq, t = t, list = list, i = 1, part = 0, lines = {}, bytes = 0, count = 0 }
        objStep()
    end

    local function frame(t)
        local okOwn, e1 = pcall(ownship, t)
        if not okOwn then report('frame', e1) end
        local okObj, e2 = pcall(objects, t)
        if not okObj then report('objects', e2); batch = nil end
    end

    local function event(name, ...)
        local args = {}
        for i = 1, select('#', ...) do args[#args + 1] = clean(select(i, ...)) end
        send(string.sub('ev ' .. clean(name) .. ' ' .. concat(args, '|'), 1, 500))
    end

    local callbacks = {}
    function callbacks.onSimulationStart()
        reset()
        readMission()
        send('start')
        facts(get('LoGetSelfData'))
    end
    function callbacks.onSimulationFrame()
        local t = api.getModelTime()
        if t < lastSend then lastSend = -1 end
        if t - lastSend < 0.01 then return end
        lastSend = t
        pcall(frame, t)
    end
    function callbacks.onSimulationStop()
        send('exit')
        reset()
    end
    function callbacks.onSimulationPause() send('ev pause') end
    function callbacks.onSimulationResume() send('ev resume') end
    function callbacks.onGameEvent(name, ...)
        pcall(event, name, ...)
    end
    function callbacks.onChatMessage(message, from)
        pcall(function()
            local who = from
            if net and net.get_name then
                local okName, n = pcall(net.get_name, from)
                if okName and n then who = n end
            end
            send(string.sub('msg ' .. clean(who) .. '|' .. clean(message), 1, 500))
        end)
    end
    api.setUserCallbacks(callbacks)
end)
if not ok and log and log.write then
    pcall(log.write, 'JoyPro', log.WARNING, 'JoyPro force feedback hook not loaded: ' .. tostring(err))
end
