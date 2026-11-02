-- JoyPro smoke test: a module whose input files have no profile folder (Mods\aircraft\AJS37\Input\joystick).
return {
	keyCommands = {
		{down = 3402, up = 3402, cockpit_device_id = devices.FLIGHTDATAUNIT, value_down = 1.0, value_up = 0.0, name = _('JoyPro test autothrottle (hold)'), category = {_('JoyPro test')}},
	},
}