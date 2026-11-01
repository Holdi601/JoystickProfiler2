---
title: About, licence and community
status: full
order: 2
summary: Who makes JoyPro, the licence, privacy in one paragraph, how to get help and how to contribute.
---
JoyPro is a hobby project, started for one pilot's own sticks and shared because it helps other pilots too. JoyPro 2
continues [JoyPro 1](https://github.com/Holdi601/JoystickProfiler) with the same idea and a new program around it
([what improved](jp1-vs-jp2.html)).

## Licence

JoyPro is free software under the **GNU General Public License, version 3 or later** ([LICENSE](https://github.com/Holdi601/JoystickProfiler2/blob/main/LICENSE)).
You may use, study, share and change it. It comes with **no warranty**: JoyPro backs up every game file before it
writes one, but keep your own backups anyway. The third-party components and their licences are listed in
[THIRD_PARTY_NOTICES.md](https://github.com/Holdi601/JoystickProfiler2/blob/main/THIRD_PARTY_NOTICES.md).

## Privacy in one paragraph

JoyPro has no account and no telemetry. It goes online only for update checks (GitHub), the makers' product photos of
your devices (only after you said yes per device), game covers missing on your PC (switchable), the optional AI model,
the optional drivers from Settings > Components and a missing .NET Framework installer. A problem report is a file on
your PC; nothing is uploaded unless you attach it somewhere yourself. Details: [[doc:privacy|Privacy]].

## Help and community

- **Discord**: [discord.gg/yK4gQA9xgk](https://discord.gg/yK4gQA9xgk) for questions, ideas and testing.
- **GitHub issues**: [JoystickProfiler2/issues](https://github.com/Holdi601/JoystickProfiler2/issues). Attach the
  support file ([how](../faq.html)).
- This is a spare-time project: help is given when possible, without promises about fixes or dates.

## Contributing

The source is on [GitHub](https://github.com/Holdi601/JoystickProfiler2). Build with
`dotnet build gui/JoyPro.Gui/JoyPro.Gui.csproj`; tests: `dotnet test gui/JoyPro.Gui.Tests` and
`engine/tests/engine_smoke_test.ps1`. The architecture map is
[ARCHITECTURE.md](https://github.com/Holdi601/JoystickProfiler2/blob/main/ARCHITECTURE.md). This website is generated
from the source by `website/build.py`; a new button or engine command needs a description, or the site's completeness
check fails.
