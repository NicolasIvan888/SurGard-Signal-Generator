namespace SurGardSignalGenerator;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var snapshotArgument = args.FirstOrDefault(argument =>
            argument.StartsWith("--snapshot=", StringComparison.OrdinalIgnoreCase));
        if (snapshotArgument is not null)
        {
            var snapshotPath = Path.GetFullPath(snapshotArgument["--snapshot=".Length..].Trim('"'));
            using var form = new MainForm
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                ShowInTaskbar = false
            };
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            bitmap.Save(snapshotPath, System.Drawing.Imaging.ImageFormat.Png);
            form.Close();
            Application.ExitThread();
            Environment.Exit(0);
            return;
        }

        Application.Run(new MainForm());
    }
}
