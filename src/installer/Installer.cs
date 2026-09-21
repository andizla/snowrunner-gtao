// SPDX-License-Identifier: GPL-3.0-only
// File level work: backup, install, remove. No user interface in here, the window and the command line both call it.
//   State lives in %LOCALAPPDATA%\SnowRunnerGTAO: one backup of the original shader.pak per game file and installs.txt,
//   a tab separated list (pak path, backup file, SHA-256 of the patched pak, mod version, date).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace SnowRunnerGtao
{
    static class Installer
    {
        public static string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowRunnerGTAO");
        const long NeededBytes = 120L * 1024 * 1024;

        public static byte[] LoadShader()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("gtao.cso"))
            {
                byte[] b = new byte[s.Length];
                int read = 0;
                while (read < b.Length) read += s.Read(b, read, b.Length - read);
                return b;
            }
        }

        public static bool GameRunning()
        {
            return Process.GetProcessesByName("SnowRunner").Length > 0;
        }

        public static PakAnalysis Check(string pakPath)
        {
            return PakPatcher.Analyze(File.ReadAllBytes(pakPath), LoadShader());
        }

        // ---- installs.txt
        class Record { public string Pak, Backup, PatchedSha, Version, Date; }

        static string ListPath { get { return Path.Combine(StateDir, "installs.txt"); } }

        static List<Record> ReadRecords()
        {
            List<Record> list = new List<Record>();
            if (!File.Exists(ListPath)) return list;
            foreach (string line in File.ReadAllLines(ListPath))
            {
                string[] f = line.Split('\t');
                if (f.Length < 5) continue;
                Record r = new Record(); r.Pak = f[0]; r.Backup = f[1]; r.PatchedSha = f[2]; r.Version = f[3]; r.Date = f[4];
                list.Add(r);
            }
            return list;
        }

        static void WriteRecords(List<Record> list)
        {
            Directory.CreateDirectory(StateDir);
            List<string> lines = new List<string>();
            foreach (Record r in list) lines.Add(string.Join("\t", new string[] { r.Pak, r.Backup, r.PatchedSha, r.Version, r.Date }));
            File.WriteAllLines(ListPath, lines.ToArray());
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        static void RequireSpace(string path)
        {
            DriveInfo d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)));
            if (d.IsReady && d.AvailableFreeSpace < NeededBytes) throw new PatchException(Text.LowDisk);
        }

        // Writes next to the target first, reads it back, then swaps it in. The target is untouched until the copy is proven.
        static void ReplaceFile(string target, byte[] content)
        {
            string tmp = target + ".gtao-tmp";
            File.WriteAllBytes(tmp, content);
            if (PakPatcher.Sha256Hex(File.ReadAllBytes(tmp)) != PakPatcher.Sha256Hex(content)) { File.Delete(tmp); throw new PatchException(Text.WriteFailed); }
            try
            {
                try { File.Replace(tmp, target, null, true); }
                catch (IOException) { File.Copy(tmp, target, true); }
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }   // never leave a stray file next to the game's paks
        }

        public static string Install(string pakPath, Action<string> progress)
        {
            if (GameRunning()) throw new PatchException(Text.GameRunning);
            byte[] shader = LoadShader();
            byte[] pak = File.ReadAllBytes(pakPath);
            PakAnalysis a = PakPatcher.Analyze(pak, shader);
            if (a.State == PakState.Installed) return Text.Installed;
            if (a.State == PakState.UnknownShader) throw new PatchException(Text.UnknownShader);

            List<Record> records = ReadRecords();
            Record mine = records.Find(delegate(Record r) { return SamePath(r.Pak, pakPath); });
            string backup = mine != null ? mine.Backup : null;
            string createdNow = null;
            if (a.State == PakState.NotInstalled)
            {
                progress(Text.StepBackup);
                Directory.CreateDirectory(StateDir);
                RequireSpace(StateDir);
                string fresh = Path.Combine(StateDir, "backup-" + a.Sha256.Substring(0, 16) + ".pak");
                if (!File.Exists(fresh) || PakPatcher.Sha256Hex(File.ReadAllBytes(fresh)) != a.Sha256)
                {
                    File.WriteAllBytes(fresh + ".tmp", pak);
                    if (File.Exists(fresh)) File.Delete(fresh);
                    File.Move(fresh + ".tmp", fresh);
                    createdNow = fresh;
                }
                // an older backup of this game file belongs to a game version that is gone
                if (!string.IsNullOrEmpty(backup) && !SamePath(backup, fresh) && File.Exists(backup)) File.Delete(backup);
                backup = fresh;
            }

            byte[] patched;
            try
            {
                progress(Text.StepPatch);
                RequireSpace(pakPath);
                patched = PakPatcher.Patch(pak, shader);
                pak = null;
                progress(Text.StepVerify);
                ReplaceFile(pakPath, patched);
            }
            catch
            {
                // the game file was not changed, so a backup made a moment ago has nothing to protect
                if (createdNow != null && File.Exists(createdNow)) File.Delete(createdNow);
                throw;
            }

            records.RemoveAll(delegate(Record r) { return SamePath(r.Pak, pakPath); });
            Record rec = new Record();
            rec.Pak = Path.GetFullPath(pakPath); rec.Backup = backup ?? ""; rec.PatchedSha = PakPatcher.Sha256Hex(patched);
            rec.Version = Text.Version; rec.Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            records.Add(rec);
            WriteRecords(records);
            return Text.DoneInstall;
        }

        public static string Remove(string pakPath, Action<string> progress)
        {
            if (GameRunning()) throw new PatchException(Text.GameRunning);
            byte[] shader = LoadShader();
            byte[] pak = File.ReadAllBytes(pakPath);
            PakAnalysis a = PakPatcher.Analyze(pak, shader);
            if (a.State == PakState.NotInstalled || a.State == PakState.UnknownShader) return Text.NothingToRemove;
            pak = null;

            List<Record> records = ReadRecords();
            Record mine = records.Find(delegate(Record r) { return SamePath(r.Pak, pakPath) && r.PatchedSha == a.Sha256 && r.Backup.Length > 0 && File.Exists(r.Backup); });
            if (mine == null) throw new PatchException(Text.NoBackup);

            progress(Text.StepRestore);
            byte[] original = File.ReadAllBytes(mine.Backup);
            PakAnalysis b = PakPatcher.Analyze(original, shader);
            if (b.State != PakState.NotInstalled || !Path.GetFileName(mine.Backup).Contains(b.Sha256.Substring(0, 16))) throw new PatchException(Text.BackupDamaged);
            RequireSpace(pakPath);
            ReplaceFile(pakPath, original);

            // the game file now equals the backup byte for byte, so the backup has done its job
            File.Delete(mine.Backup);
            records.Remove(mine);
            WriteRecords(records);
            return Text.DoneRemove;
        }
    }
}
