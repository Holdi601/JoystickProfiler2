// A stand-in for DCS's bin\luae.exe in the engine smoke test (tests\smoke\progress.ps1): it reads the Lua script the
// engine writes for a headless DCS refresh and answers with the lines the real script prints, slowly enough that the
// engine's live progress can be seen. Built with the .NET Framework csc.exe (C# 5, no newer syntax).
//   probe script ("JoyPro DCS input profile probe"): per module the InputProfiles of its entry.lua
//     (["<unit>"] = current_mod_path .. '/Input/<folder>'), JOYPRO_PROBE_PROGRESS, JOYPRO_PROBE_DONE;
//   ID refresh: per profile JOYPRO_PROGRESS|i|n|export|<unit>, an HTML file in <saved games>\InputLayoutsTxt\<unit>,
//     JOYPRO_PROFILE_OK; clean refresh: load/clear/save progress and the fake stick's diff in Config\Input\<unit>.
// FAKE_LUAE_CANCEL_AT=<n> with JOYPRO_CANCEL_FILE: creates the cancel file at profile n (the user presses Cancel),
// FAKE_LUAE_DELAY_MS: the time per profile (default 40).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static class FakeLuae
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.WriteLine("JOYPRO_FATAL|no script");
            return 1;
        }
        string script = File.ReadAllText(args[0], Encoding.Default);
        int delay = 40;
        int parsedDelay;
        if (int.TryParse(Environment.GetEnvironmentVariable("FAKE_LUAE_DELAY_MS") ?? string.Empty, out parsedDelay))
            delay = parsedDelay;
        if (script.Contains("JoyPro DCS input profile probe"))
            return Probe(script, delay);
        if (script.Contains("JoyPro headless DCS ID refresh"))
            return Refresh(script, true, delay);
        if (script.Contains("JoyPro headless DCS clean refresh"))
            return Refresh(script, false, delay);
        Console.WriteLine("JOYPRO_FATAL|unknown script");
        return 1;
    }

    private static int Probe(string script, int delay)
    {
        List<string> modules = new List<string>();
        foreach (Match m in Regex.Matches(script, @"^\s*\[==\[(.*)\]==\],\s*$", RegexOptions.Multiline))
            modules.Add(m.Groups[1].Value);
        Regex profile = new Regex(@"\[\s*""([^""]+)""\s*\]\s*=\s*current_mod_path\s*\.\.\s*'([^']+)'");
        for (int i = 0; i < modules.Count; i++)
        {
            string module = modules[i];
            string entry = Path.Combine(module, "entry.lua");
            MatchCollection found = File.Exists(entry) ? profile.Matches(File.ReadAllText(entry)) : null;
            Console.WriteLine("JOYPRO_MODULE|" + module + "|1");
            if (found != null)
            {
                foreach (Match m in found)
                    Console.WriteLine("JOYPRO_INPUTPROFILE|" + module + "|" + m.Groups[1].Value + "|" + module + m.Groups[2].Value.Replace('/', '\\'));
            }
            Console.WriteLine("JOYPRO_PROBE_PROGRESS|" + module + "|" + (i + 1) + "|" + modules.Count);
            Console.Out.Flush();
            Thread.Sleep(delay);
        }
        Console.WriteLine("JOYPRO_PROBE_DONE|" + modules.Count);
        return 0;
    }

    private static int Refresh(string script, bool exportHtml, int delay)
    {
        string savedGames = Regex.Match(script, @"^local savedGamesPath = '(.*)'\s*$", RegexOptions.Multiline).Groups[1].Value.Replace("\\\\", "\\");
        List<string> units = new List<string>();
        foreach (Match m in Regex.Matches(script, @"^\s*\{ unit = '(.*?)', folder = '(.*?)' \},\s*$", RegexOptions.Multiline))
            units.Add(m.Groups[1].Value);
        int cancelAt;
        if (!int.TryParse(Environment.GetEnvironmentVariable("FAKE_LUAE_CANCEL_AT") ?? string.Empty, out cancelAt))
            cancelAt = 0;
        string cancelFile = Environment.GetEnvironmentVariable("JOYPRO_CANCEL_FILE") ?? string.Empty;
        Console.WriteLine("JOYPRO_DEVICES|2");
        for (int i = 0; i < units.Count; i++)
        {
            string unit = units[i];
            if (cancelAt == i + 1 && cancelFile.Length > 0)
                File.WriteAllText(cancelFile, "cancel");
            Console.WriteLine("JOYPRO_PROGRESS|" + (i + 1) + "|" + units.Count + "|" + (exportHtml ? "export" : "clear") + "|" + unit);
            Console.Out.Flush();
            Thread.Sleep(cancelAt > 0 ? Math.Max(delay, 1500) : delay);
            if (exportHtml)
            {
                string folder = Path.Combine(savedGames, "InputLayoutsTxt", unit);
                Directory.CreateDirectory(folder);
                string html = Path.Combine(folder, "Fake Stick.html");
                File.WriteAllText(html, "<html><body><table><tr><td>JOY_BTN1</td><td>" + unit + " fire</td><td>Weapons</td></tr></table></body></html>");
                Console.WriteLine("JOYPRO_PROFILE_OK|" + unit + "|" + html);
            }
            else
            {
                string folder = Path.Combine(savedGames, "Config", "Input", unit, "joystick");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "JoyPro Headless Device {FAKE-JOYSTICK-0001}.diff.lua"), "local diff = {}\r\nreturn diff\r\n");
                Console.WriteLine("JOYPRO_PROFILE_OK|" + unit + "|cleared");
            }
            Console.Out.Flush();
        }
        if (!exportHtml)
            Console.WriteLine("JOYPRO_PROGRESS|" + units.Count + "|" + units.Count + "|save|");
        Console.WriteLine("JOYPRO_DONE|" + units.Count + "|" + units.Count + "|0");
        return 0;
    }
}
