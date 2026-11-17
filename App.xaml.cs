using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using PcrBattleAssistant.Services;

namespace PcrBattleAssistant;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 支持命令行批处理自检模式：
        // PcrBattleAssistant.exe --test <inputImage> <outputDebugImage> <outputLogFile>
        // PcrBattleAssistant.exe --test-upload <inputImage> <outputDebugImage> <outputLogFile> [commaSeparatedDefIds]
        if (e.Args.Length >= 4 && e.Args[0] == "--test")
        {
            RunCliSelfTest(e.Args[1], e.Args[2], e.Args[3]);
            Shutdown(0);
            return;
        }

        if (e.Args.Length >= 4 && e.Args[0] == "--test-upload")
        {
            string? defCsv = e.Args.Length >= 5 ? e.Args[4] : null;
            RunCliUploadSelfTest(e.Args[1], e.Args[2], e.Args[3], defCsv);
            Shutdown(0);
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static void RunCliSelfTest(string inputPath, string debugOutPath, string logPath)
    {
        using var service = new AvatarRecognitionService();
        int tplCount = service.LoadLocalLibrary();

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(Path.GetFullPath(inputPath), UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        MultiTeamRecognitionResult result = service.RecognizeRightSideThreeTeams(bmp);

        // 保存调试模式标注图
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result.DebugAnnotatedImage));
        using (FileStream fs = File.Create(debugOutPath))
        {
            encoder.Save(fs);
        }

        var lines = new List<string>
        {
            $"LoadedTemplates: {tplCount}",
            $"SceneType: {result.SceneType}",
            $"SceneDesc: {result.SceneDescription}"
        };

        foreach (var team in result.Teams)
        {
            string slots = string.Join(" | ", team.Slots.Select(s => $"{s.UnitName}({s.UnitId}, {s.Confidence:P0})"));
            lines.Add($"{team.Title}: {slots}");
        }

        File.WriteAllLines(logPath, lines);
    }

    private static void RunCliUploadSelfTest(string inputPath, string debugOutPath, string logPath, string? defCsv)
    {
        using var service = new AvatarRecognitionService();
        int tplCount = service.LoadLocalLibrary();

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(Path.GetFullPath(inputPath), UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        var webDefIds = new List<int>();
        if (!string.IsNullOrWhiteSpace(defCsv))
        {
            foreach (string part in defCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out int id) && id > 0)
                {
                    webDefIds.Add(id);
                }
            }
        }

        UploadRecognitionResult result = service.RecognizeUploadTeams(bmp, webDefIds);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result.DebugAnnotatedImage));
        using (FileStream fs = File.Create(debugOutPath))
        {
            encoder.Save(fs);
        }

        var lines = new List<string>
        {
            $"LoadedTemplates: {tplCount}",
            $"SceneType: {result.SceneType}",
            $"SceneDesc: {result.SceneDescription}",
            $"MatchedTeamIndex: {result.MatchedTeamIndex}"
        };

        for (int i = 0; i < result.AttackTeams.Count; i++)
        {
            var atk = result.AttackTeams[i];
            string slots = string.Join(" | ", atk.Slots.Select(s => $"{s.UnitName}({s.UnitId}, {s.Confidence:P0})"));
            lines.Add($"Attack-{atk.Title}: {slots}");
        }

        for (int i = 0; i < result.DefenseTeams.Count; i++)
        {
            var def = result.DefenseTeams[i];
            string slots = string.Join(" | ", def.Slots.Select(s => $"{s.UnitName}({s.UnitId}, {s.Confidence:P0})"));
            lines.Add($"Defense-{def.Title}: {slots}");
        }

        File.WriteAllLines(logPath, lines);
    }
}
