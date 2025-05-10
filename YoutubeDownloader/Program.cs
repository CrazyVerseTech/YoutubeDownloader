using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.WebView.Desktop;
using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloader.Core.Downloading;
using YoutubeDownloader.Core.Resolving;
using YoutubeDownloader.Services;
using YoutubeDownloader.Utils;
using YoutubeExplode.Videos.Streams;

namespace YoutubeDownloader;

public static class Program
{
    private static Assembly Assembly { get; } = Assembly.GetExecutingAssembly();

    public static string Name { get; } = Assembly.GetName().Name ?? "YoutubeDownloader";

    public static Version Version { get; } = Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string VersionString { get; } = Version.ToString(3);

    public static bool IsDevelopmentBuild { get; } = Version.Major is <= 0 or >= 999;

    public static string ProjectUrl { get; } = "https://github.com/Tyrrrz/YoutubeDownloader";

    public static string ProjectReleasesUrl { get; } = $"{ProjectUrl}/releases";

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().UseDesktopWebView();

    [STAThread]
    public static int Main(string[] args)
    {
        // Check if running in CLI mode
        bool cliMode = args.Length > 0 && !args[0].StartsWith("-");

        if (cliMode)
        {
            return MainCli(args).GetAwaiter().GetResult();
        }

        // Build and run the app
        var builder = BuildAvaloniaApp();

        try
        {
            return builder.StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            if (OperatingSystem.IsWindows())
                _ = NativeMethods.Windows.MessageBox(0, ex.ToString(), "Fatal Error", 0x10);

            throw;
        }
        finally
        {
            // Clean up after application shutdown
            if (builder.Instance is IDisposable disposableApp)
                disposableApp.Dispose();
        }
    }

    // Add a new command line entry point
    [STAThread]
    public static async Task<int> MainCli(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine($"Usage: {Name} <youtube-url> [output-directory] [format] [quality]");
            Console.WriteLine("Formats: mp4, webm, mp3, ogg (default: mp4)");
            Console.WriteLine(
                "Quality: highest, lowest, or a specific resolution (e.g., 1080p, 720p, 480p, 360p) (default: highest)"
            );
            return 1;
        }

        try
        {
            // Set up services without UI components
            var services = new ServiceCollection();
            services.AddSingleton<SettingsService>();
            var serviceProvider = services.BuildServiceProvider(true);
            var settingsService = serviceProvider.GetRequiredService<SettingsService>();
            settingsService.Load();

            // Parse command line arguments
            var query = args[0];
            var outputDir = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();
            var formatStr = args.Length > 2 ? args[2] : "mp4";
            var qualityStr = args.Length > 3 ? args[3].ToLower() : "highest";

            // Determine container format
            var container = formatStr.ToLower() switch
            {
                "mp4" => Container.Mp4,
                "webm" => Container.WebM,
                "mp3" => Container.Mp3,
                "ogg" => new Container("ogg"),
                _ => Container.Mp4,
            };

            // Determine quality preference
            var qualityPreference = qualityStr switch
            {
                "highest" => VideoQualityPreference.Highest,
                "lowest" => VideoQualityPreference.Lowest,
                _ => ParseResolution(qualityStr),
            };

            // Check if FFmpeg is installed
            if (!FFmpeg.IsAvailable())
            {
                Console.WriteLine("Error: FFmpeg is required but not found.");
                Console.WriteLine("Please install FFmpeg and make it available in PATH.");
                return 2;
            }

            Console.WriteLine($"Resolving video from: {query}");

            // Resolve video
            var resolver = new QueryResolver(settingsService.LastAuthCookies);
            var result = await resolver.ResolveAsync(new[] { query });

            if (result.Videos.Count == 0)
            {
                Console.WriteLine("No videos found for the given URL.");
                return 3;
            }

            var video = result.Videos[0];
            Console.WriteLine($"Found video: {video.Title}");

            // Create file path
            var filePath = Path.Combine(
                outputDir,
                FileNameTemplate.Apply(settingsService.FileNameTemplate, video, container)
            );

            // Make sure directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            // Download video
            Console.WriteLine($"Downloading to: {filePath}");
            Console.WriteLine("Format: " + container.Name);

            // Update this part to use the selected quality preference
            var downloader = new VideoDownloader(settingsService.LastAuthCookies);
            var downloadOption = await downloader.GetBestDownloadOptionAsync(
                video.Id,
                new VideoDownloadPreference(container, qualityPreference),
                settingsService.ShouldInjectLanguageSpecificAudioStreams
            );

            // Show progress in console
            var progress = new PercentageProgress(p =>
            {
                Console.Write($"\rProgress: {p:P0}");
            });

            await downloader.DownloadVideoAsync(
                filePath,
                video,
                downloadOption,
                settingsService.ShouldInjectSubtitles,
                progress
            );

            Console.WriteLine("\nDownload complete!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 4;
        }
    }

    private static VideoQualityPreference ParseResolution(string resolution)
    {
        // Try to parse resolution like "1080p", "720p", etc.
        if (resolution.EndsWith("p") && int.TryParse(resolution.TrimEnd('p'), out int height))
        {
            return height switch
            {
                0 => VideoQualityPreference.Lowest,
                <= 360 => VideoQualityPreference.UpTo360p,
                <= 480 => VideoQualityPreference.UpTo480p,
                <= 720 => VideoQualityPreference.UpTo720p,
                <= 1080 => VideoQualityPreference.UpTo1080p,
                _ => VideoQualityPreference.Highest,
            };
        }

        // Try to parse just a number
        if (int.TryParse(resolution, out int directHeight))
        {
            return directHeight switch
            {
                0 => VideoQualityPreference.Lowest,
                <= 360 => VideoQualityPreference.UpTo360p,
                <= 480 => VideoQualityPreference.UpTo480p,
                <= 720 => VideoQualityPreference.UpTo720p,
                <= 1080 => VideoQualityPreference.UpTo1080p,
                _ => VideoQualityPreference.Highest,
            };
        }

        // Default to highest if parsing fails
        Console.WriteLine(
            $"Could not parse resolution '{resolution}', using highest quality instead."
        );
        return VideoQualityPreference.Highest;
    }
}
