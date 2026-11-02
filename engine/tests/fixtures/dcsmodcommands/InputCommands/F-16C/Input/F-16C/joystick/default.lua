-- JoyPro smoke test (tests/smoke/dcsmodcommands.ps1): a command pack in the Input Command Injector's format
-- (Saved Games\<DCS>\InputCommands\<module folder>\Input\<profile>\joystick\default.lua), run in the F-16C's own
-- input environment (devices, iff_commands come from the module's default.lua).
return {
	keyCommands = {
		{cockpit_device_id = devices.IFF_CONTROL_PANEL, down = iff_commands.CNI_Knob, up = iff_commands.CNI_Knob, value_down = 1, value_up = 0, name = _('JoyPro test C & I Knob - UFC else BACKUP (2-way Switch)'), category = {_('Special For Joystick'), _('JoyPro test')}},
		{cockpit_device_id = devices.IFF_CONTROL_PANEL, down = iff_commands.CNI_Knob, up = iff_commands.CNI_Knob, value_down = 0, value_up = 1, name = _('JoyPro test C & I Knob - BACKUP else UFC (2-way Switch)'), category = {_('Special For Joystick'), _('JoyPro test')}},
	},
}