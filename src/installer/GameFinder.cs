// SPDX-License-Identifier: GPL-3.0-only
// Finds shader.pak of every SnowRunner install it can: Steam (all library folders), Epic Games, Microsoft Store.
// Every source is tried on its own, a failure in one never hides the others.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SnowRunnerGtao
{
    static class GameFinder
    {
        const string SteamAppId = "1465360";
        static readonly string[] PakTails = { @"preload\paks\client\shader.pak", @"en_us\preload\paks\client\shader.pak" };

        public static List<string> FindPaks()
        {
            List<string> roots = new List<string>();
            Try(delegate { Steam(roots); });
            Try(delegate { Epic(roots); });
            Try(delegate { MicrosoftStore(roots); });
            List<string> paks = new List<string>();
            foreach (string root in roots)
                foreach (string tail in PakTails)
                {
                    string p = Path.Combine(root, tail);
                    if (File.Exists(p) && !paks.Exists(delegate(string x) { return string.Equals(x, p, StringComparison.OrdinalIgnoreCase); })) paks.Add(p);
                }
            return paks;
        }

        static void Try(Action source)
        {
            try { source(); } catch (Exception) { }
        }

        static string RegistryString(RegistryKey hive, string key, string name)
        {
            using (RegistryKey k = hive.OpenSubKey(key)) return k == null ? null : k.GetValue(name) as string;
        }

        static void Steam(List<string> roots)
        {
            string steam = RegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                ?? RegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                ?? RegistryString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            if (steam == null) return;
            steam = steam.Replace('/', '\\');
            List<string> libraries = new List<string>();
            libraries.Add(steam);
            string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            foreach (string lib in libraries)
            {
                string manifest = Path.Combine(lib, @"steamapps\appmanifest_" + SteamAppId + ".acf");
                if (File.Exists(manifest))
                {
                    Match m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                    if (m.Success) roots.Add(Path.Combine(lib, @"steamapps\common", m.Groups[1].Value));
                }
                roots.Add(Path.Combine(lib, @"steamapps\common\SnowRunner"));
            }
        }

        static void Epic(List<string> roots)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) return;
            foreach (string item in Directory.GetFiles(dir, "*.item"))
            {
                string text = File.ReadAllText(item);
                if (text.IndexOf("SnowRunner", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Match m = Regex.Match(text, "\"InstallLocation\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) roots.Add(m.Groups[1].Value.Replace(@"\\", @"\").Replace('/', '\\'));
            }
        }

        static void MicrosoftStore(List<string> roots)
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                roots.Add(Path.Combine(d.RootDirectory.FullName, @"XboxGames\SnowRunner\Content"));
            }
        }
    }
}
