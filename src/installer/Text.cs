// SPDX-License-Identifier: GPL-3.0-only
// Every sentence the user can see, in one place.
namespace SnowRunnerGtao
{
    static class Text
    {
        public const string Version = "1.0.0";
        public const string Title = "SnowRunner GTAO " + Version;

        public const string Intro = "Replaces the game's ambient occlusion shader with a better one. It changes one game file, shader.pak, and keeps a backup so Remove can put the original back.";
        public const string GameFile = "Game folder:";
        public const string Browse = "Browse...";
        public const string BrowseTitle = "Pick shader.pak in the game folder: preload\\paks\\client";
        public const string Install = "Install";
        public const string Update = "Update";
        public const string Remove = "Remove";
        public const string Close = "Close";

        public const string Checking = "Checking the game file...";
        public const string NotFound = "SnowRunner was not found. Click Browse and pick shader.pak. It is in the game folder under preload\\paks\\client.";
        public const string NotInstalled = "Not installed.";
        public const string Installed = "Installed.";
        public const string InstalledOther = "Another version of this mod is installed. Update replaces it with this one.";
        public const string UnknownShader = "This game version has an ambient occlusion shader this mod does not know. Nothing was changed. Look for a newer version of the mod.";
        public const string NotShaderPak = "This file is not a SnowRunner shader.pak, or it is damaged. Nothing was changed.";

        public const string StepBackup = "Making a backup...";
        public const string StepPatch = "Putting the new shader in...";
        public const string StepVerify = "Checking the result...";
        public const string StepRestore = "Putting the original file back...";
        public const string DoneInstall = "Done. Start the game.";
        public const string DoneRemove = "Removed. The original file is back.";
        public const string NothingToRemove = "The mod is not installed in this file. Nothing to remove.";

        public const string GameRunning = "SnowRunner is running. Close the game and try again.";
        public const string NoBackup = "There is no backup for this file, so Remove cannot put the original back. The store can: in Steam, right click SnowRunner, Properties, Installed Files, Verify integrity of game files. In Epic, open the Library, click the three dots on SnowRunner, Manage, Verify.";
        public const string BackupDamaged = "The backup does not match this file any more. Nothing was changed. Use the store's file check instead: in Steam, right click SnowRunner, Properties, Installed Files, Verify integrity of game files.";
        public const string AccessDenied = "Windows did not allow writing to the game folder.";
        public const string AskElevate = "Restart this program as administrator and try again?";
        public const string LowDisk = "Not enough free disk space. About 120 MB are needed on the game drive and on the Windows drive.";
        public const string LowMemory = "Not enough free memory. Close other programs and try again.";
        public const string WriteFailed = "The new file did not pass its check, so the game file was left as it was.";
        public const string Failed = "Something went wrong and nothing was changed: {0}";

        public const string AfterUpdateHint = "A game update or the store's file check puts the original shader back. Run this program again afterwards.";
    }
}
