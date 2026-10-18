using System;
using System.Diagnostics;
using System.Drawing;

namespace WUGrid.Gui
{
    static class AppInfo
    {
        public const string RepoUrl = "https://github.com/Lill82/WUGrid";

        /// <summary>"0.2.0", from Directory.Build.props.</summary>
        public static readonly string Version = typeof(AppInfo).Assembly.GetName().Version.ToString(3);

        static readonly Lazy<Icon> icon = new Lazy<Icon>(() =>
        {
            using (var stream = typeof(AppInfo).Assembly.GetManifestResourceStream("WUGrid.ico"))
                return new Icon(stream);   // keeps every frame: the title bar takes 16 px, Alt+Tab 32 px
        });

        public static Icon Icon => icon.Value;

        public static void OpenRepo() => Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
    }
}
