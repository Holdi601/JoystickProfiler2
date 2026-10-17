-- Synthetic excerpt in the layout of X4 9.00's ui/addons/ego_gameoptions/gameoptions.lua (smoke test fixture).
local config = { input = {} }

config.input.controlFunctions = {
	[1] = {
		["name"] = ReadText(1001, 9001),	-- "Smoke Function"
		--["name"] = ReadText(1001, 9999),	-- "commented out"
		["definingcontrol"] = {"actions", 12},
		["actions"] = { 12, 6 },
		["states"] = {},
		["ranges"] = {},
		["contexts"]= { 1, 2 },
	},
}

config.input.controlsorder = {
	["space"] = {
		[1] = {
			["title"] = ReadText(1001, 9000),	-- "Smoke Group, {with} braces"
			["mappable"] = true,
			{ "ranges", 4, { 1, 2 } },
			{ "ranges", 1, { 1, 2 }, ReadText(1026, 2629) },
			{ "ranges", 10, { 1, 2 }, nil, true, mousewheelonly = true },
			{ "functions", 1, { 1, 2 } },
			{ "actions", 9999, { 1, 2 } },
		},
		[2] = {
			["title"] = ReadText(1001, 9002),	-- "Options Menu"
			["mappable"] = true,
			{ "actions", 30 },
		},
	},
	["menus"] = {
		[1] = {
			["title"] = ReadText(1001, 9003),
			["mappable"] = true,
			{ "states", 1, { 1, 2 } },
		},
	},
}
