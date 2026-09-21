// SPDX-License-Identifier: GPL-3.0-only
// Entry point. Double click opens the window. The command line form exists for the test script:
//   SnowRunnerGTAO.exe --cli status|install|remove <shader.pak> [--state <folder>] [--log <file>]
//   SnowRunnerGTAO.exe --cli find x --log <file> lists every shader.pak the game search finds
//   exit code 0 = done, 1 = refused with a reason (nothing changed), 2 = unexpected error
//   the log gets one line: state=..., result=... or error=...
//   SnowRunnerGTAO.exe --cli shot <shader.pak> --png <file> draws the window into a picture (layout check, no screen needed)
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SnowRunnerGtao
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "--cli") return Cli(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Length == 1 ? args[0] : null));
            return 0;
        }

        static int Cli(string[] args)
        {
            string logFile = null, png = null;
            for (int i = 3; i + 1 < args.Length; i += 2)
            {
                if (args[i] == "--state") Installer.StateDir = args[i + 1];
                else if (args[i] == "--log") logFile = args[i + 1];
                else if (args[i] == "--png") png = args[i + 1];
            }
            string line;
            int code = 0;
            try
            {
                Action<string> quiet = delegate(string s) { };
                if (args[1] == "status") line = "state=" + Installer.Check(args[2]).State;
                else if (args[1] == "install") line = "result=" + Installer.Install(args[2], quiet);
                else if (args[1] == "remove") line = "result=" + Installer.Remove(args[2], quiet);
                else if (args[1] == "find") line = "found=" + string.Join("|", GameFinder.FindPaks().ToArray());
                else if (args[1] == "shot" && png != null)
                {
                    Application.EnableVisualStyles();
                    using (MainForm form = new MainForm(args[2]))
                    {
                        form.Show();
                        DateTime end = DateTime.Now.AddSeconds(8);
                        while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(30); }
                        using (Bitmap picture = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(picture, new Rectangle(0, 0, form.Width, form.Height));
                            picture.Save(png, ImageFormat.Png);
                        }
                    }
                    line = "result=shot";
                }
                else { line = "error=unknown command"; code = 2; }
            }
            catch (PatchException e) { line = "error=" + e.Message; code = 1; }
            catch (Exception e) { line = "error=" + e.GetType().Name + ": " + e.Message; code = 2; }
            if (logFile != null) File.WriteAllText(logFile, line + Environment.NewLine);
            return code;
        }
    }
}
