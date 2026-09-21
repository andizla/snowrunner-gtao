// SPDX-License-Identifier: GPL-3.0-only
// The window: one game file, one status line, Install and Remove. All work runs on a background thread.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SnowRunnerGtao
{
    class MainForm : Form
    {
        readonly ComboBox games = new ComboBox();
        readonly Button browse = new Button();
        readonly Label status = new Label();
        readonly Button install = new Button();
        readonly Button remove = new Button();
        readonly Button close = new Button();
        readonly ProgressBar progress = new ProgressBar();
        readonly TextBox log = new TextBox();
        bool busy;
        float scale = 1f;

        int S(int pixels) { return (int)Math.Round(pixels * scale); }

        public MainForm(string presetPak)
        {
            Text = SnowRunnerGtao.Text.Title;
            // sizes in this file are for 96 dpi, S() scales them to the screen
            AutoScaleMode = AutoScaleMode.None;
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(700), S(380));

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(S(14));
            grid.ColumnCount = 3;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(grid);

            Label intro = new Label();
            intro.Text = SnowRunnerGtao.Text.Intro;
            intro.AutoSize = true;
            intro.MaximumSize = new Size(S(670), 0);
            intro.Margin = new Padding(S(3), S(3), S(3), S(12));
            grid.Controls.Add(intro, 0, 0);
            grid.SetColumnSpan(intro, 3);

            Label gameLabel = new Label();
            gameLabel.Text = SnowRunnerGtao.Text.GameFile;
            gameLabel.AutoSize = true;
            gameLabel.Anchor = AnchorStyles.Left;
            grid.Controls.Add(gameLabel, 0, 1);
            games.DropDownStyle = ComboBoxStyle.DropDownList;
            games.Dock = DockStyle.Fill;
            games.SelectedIndexChanged += delegate { CheckSelected(null); };
            grid.Controls.Add(games, 1, 1);
            browse.Text = SnowRunnerGtao.Text.Browse;
            browse.AutoSize = true;
            browse.Click += delegate { Browse(); };
            grid.Controls.Add(browse, 2, 1);

            status.AutoSize = true;
            status.MaximumSize = new Size(S(670), 0);
            status.Font = new Font(Font, FontStyle.Bold);
            status.Margin = new Padding(S(3), S(14), S(3), S(10));
            grid.Controls.Add(status, 0, 2);
            grid.SetColumnSpan(status, 3);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            install.Text = SnowRunnerGtao.Text.Install;
            install.Size = new Size(S(150), S(38));
            install.Click += delegate { Run(true); };
            remove.Text = SnowRunnerGtao.Text.Remove;
            remove.Size = new Size(S(150), S(38));
            remove.Click += delegate { Run(false); };
            close.Text = SnowRunnerGtao.Text.Close;
            close.Size = new Size(S(110), S(38));
            close.Click += delegate { Close(); };
            buttons.Controls.Add(install);
            buttons.Controls.Add(remove);
            buttons.Controls.Add(close);
            grid.Controls.Add(buttons, 0, 3);
            grid.SetColumnSpan(buttons, 3);

            progress.Dock = DockStyle.Fill;
            progress.Height = S(10);
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 0;
            grid.Controls.Add(progress, 0, 4);
            grid.SetColumnSpan(progress, 3);

            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Dock = DockStyle.Fill;
            log.BackColor = SystemColors.Window;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(log, 0, 5);
            grid.SetColumnSpan(log, 3);

            FormClosing += delegate(object s, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            Shown += delegate { FindGames(presetPak); };
            SetButtons(false, false, SnowRunnerGtao.Text.Install);
        }

        void Say(string line)
        {
            log.AppendText(line + Environment.NewLine);
        }

        void SetButtons(bool canInstall, bool canRemove, string installText)
        {
            install.Text = installText;
            install.Enabled = canInstall && !busy;
            remove.Enabled = canRemove && !busy;
            games.Enabled = browse.Enabled = close.Enabled = !busy;
        }

        void FindGames(string presetPak)
        {
            List<string> paks = GameFinder.FindPaks();
            if (!string.IsNullOrEmpty(presetPak) && File.Exists(presetPak)) paks.Insert(0, presetPak);
            foreach (string p in paks) AddGame(p, false);
            if (games.Items.Count > 0) games.SelectedIndex = 0;
            else status.Text = SnowRunnerGtao.Text.NotFound;
        }

        void Browse()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = SnowRunnerGtao.Text.BrowseTitle;
                d.Filter = "shader.pak|shader.pak";
                d.CheckFileExists = true;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                AddGame(d.FileName, true);
                foreach (Game g in games.Items) if (string.Equals(g.Pak, d.FileName, StringComparison.OrdinalIgnoreCase)) games.SelectedItem = g;
            }
        }

        // The list shows the game folder; the pak path behind it is what the work uses
        class Game
        {
            public readonly string Pak;
            public Game(string pak) { Pak = pak; }
            public override string ToString()
            {
                string tail = @"preloadpaksclientshader.pak";
                return Pak.EndsWith(tail, StringComparison.OrdinalIgnoreCase) ? Pak.Substring(0, Pak.Length - tail.Length) : Pak;
            }
        }

        string SelectedPak { get { Game g = games.SelectedItem as Game; return g == null ? null : g.Pak; } }

        void AddGame(string pak, bool first)
        {
            pak = Path.GetFullPath(pak);   // one spelling per file, backslashes
            foreach (Game g in games.Items) if (string.Equals(g.Pak, pak, StringComparison.OrdinalIgnoreCase)) { games.SelectedItem = g; return; }
            Game added = new Game(pak);
            if (first) games.Items.Insert(0, added); else games.Items.Add(added);
        }

        void Work(DoWorkEventHandler job, RunWorkerCompletedEventHandler done)
        {
            busy = true;
            SetButtons(false, false, install.Text);
            progress.MarqueeAnimationSpeed = 30;
            BackgroundWorker w = new BackgroundWorker();
            w.DoWork += job;
            w.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                busy = false;
                progress.MarqueeAnimationSpeed = 0;
                progress.Refresh();
                done(s, e);
            };
            w.RunWorkerAsync();
        }

        // keepStatus: the line that stays on screen after the buttons were refreshed (the result of an install or a failure)
        void CheckSelected(string keepStatus)
        {
            string pak = SelectedPak;
            if (pak == null || busy) return;
            if (keepStatus == null) status.Text = SnowRunnerGtao.Text.Checking;
            Work(delegate(object s, DoWorkEventArgs e) { e.Result = Installer.Check(pak); },
                 delegate(object s, RunWorkerCompletedEventArgs e)
                 {
                     if (e.Error != null) { ShowFailure(e.Error, false); SetButtons(false, false, SnowRunnerGtao.Text.Install); return; }
                     ShowState(((PakAnalysis)e.Result).State);
                     if (keepStatus != null) status.Text = keepStatus;
                 });
        }

        void ShowState(PakState state)
        {
            switch (state)
            {
                case PakState.NotInstalled: status.Text = SnowRunnerGtao.Text.NotInstalled; SetButtons(true, false, SnowRunnerGtao.Text.Install); break;
                case PakState.Installed: status.Text = SnowRunnerGtao.Text.Installed; SetButtons(false, true, SnowRunnerGtao.Text.Install); break;
                case PakState.InstalledOther: status.Text = SnowRunnerGtao.Text.InstalledOther; SetButtons(true, true, SnowRunnerGtao.Text.Update); break;
                default: status.Text = SnowRunnerGtao.Text.UnknownShader; SetButtons(false, false, SnowRunnerGtao.Text.Install); break;
            }
        }

        void Run(bool doInstall)
        {
            string pak = SelectedPak;
            if (pak == null || busy) return;
            Action<string> step = delegate(string line) { BeginInvoke((MethodInvoker)delegate { status.Text = line; Say(line); }); };
            Work(delegate(object s, DoWorkEventArgs e) { e.Result = doInstall ? Installer.Install(pak, step) : Installer.Remove(pak, step); },
                 delegate(object s, RunWorkerCompletedEventArgs e)
                 {
                     if (e.Error != null) { CheckSelected(ShowFailure(e.Error, true)); return; }
                     string message = (string)e.Result;
                     Say(message);
                     if (message == SnowRunnerGtao.Text.DoneInstall) Say(SnowRunnerGtao.Text.AfterUpdateHint);
                     CheckSelected(message);
                 });
        }

        string ShowFailure(Exception error, bool offerElevation)
        {
            string message;
            if (error is PatchException) message = error.Message;
            else if (error is UnauthorizedAccessException) message = SnowRunnerGtao.Text.AccessDenied;
            else if (error is OutOfMemoryException) message = SnowRunnerGtao.Text.LowMemory;
            else if (error is IOException && Installer.GameRunning()) message = SnowRunnerGtao.Text.GameRunning;
            else message = string.Format(SnowRunnerGtao.Text.Failed, error.Message);
            status.Text = message;
            Say(message);
            if (offerElevation && error is UnauthorizedAccessException &&
                MessageBox.Show(this, message + Environment.NewLine + Environment.NewLine + SnowRunnerGtao.Text.AskElevate, SnowRunnerGtao.Text.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                try
                {
                    ProcessStartInfo again = new ProcessStartInfo(Application.ExecutablePath, "\"" + SelectedPak + "\"");
                    again.Verb = "runas";
                    Process.Start(again);
                    Close();
                }
                catch (Win32Exception) { }   // the user said no at the Windows prompt
            }
            return message;
        }
    }
}
