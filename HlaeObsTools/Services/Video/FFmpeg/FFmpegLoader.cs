using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace HlaeObsTools.Services.Video.FFmpeg;

/// <summary>
/// Handles loading FFmpeg native libraries
/// </summary>
public static class FFmpegLoader
{
    private static bool _isInitialized = false;

    /// <summary>
    /// Initialize FFmpeg and load libraries from the FFmpeg folder
    /// </summary>
    public static void Initialize()
    {
        if (_isInitialized)
            return;

        // Determine FFmpeg library path
        string ffmpegPath = GetFFmpegPath();

        Console.WriteLine($"Looking for FFmpeg binaries at: {ffmpegPath}");
        Console.WriteLine($"Directory exists: {Directory.Exists(ffmpegPath)}");

        if (!Directory.Exists(ffmpegPath))
        {
            throw new DirectoryNotFoundException(
                $"FFmpeg binaries not found at: {ffmpegPath}\n" +
                $"Please download FFmpeg shared libraries and place them in the FFmpeg folder.");
        }

        // List files in directory for debugging
        var files = Directory.GetFiles(ffmpegPath, "*.dll");
        Console.WriteLine($"Found {files.Length} DLL files:");
        foreach (var file in files)
        {
            Console.WriteLine($"  - {Path.GetFileName(file)}");
        }

        // Set FFmpeg binary path
        ffmpeg.RootPath = ffmpegPath;

        // Also set up custom resolver to help with loading
        SetupLibraryResolver(ffmpegPath);

        try
        {
            // Test that libraries loaded correctly
            var version = ffmpeg.av_version_info();
            Console.WriteLine($"FFmpeg version: {version}");
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to load FFmpeg libraries from {ffmpegPath}\n" +
                              $"Error: {ex.Message}\n" +
                              $"Make sure all required DLLs are present (avcodec, avformat, avutil, swscale, swresample)", ex);
        }
    }

    /// <summary>
    /// Setup custom library resolver to help load FFmpeg DLLs
    /// </summary>
    private static void SetupLibraryResolver(string ffmpegPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Add FFmpeg directory to DLL search path on Windows
            SetDllDirectory(ffmpegPath);

            // Also add to PATH environment variable for the process
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!path.Contains(ffmpegPath))
            {
                Environment.SetEnvironmentVariable("PATH", $"{ffmpegPath};{path}");
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);

    /// <summary>
    /// Get the path to the FFmpeg binaries
    /// </summary>
    private static string GetFFmpegPath()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string platform;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            platform = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "win-x64" : "win-x86";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            platform = "linux-x64";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            platform = "osx-x64";
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported platform");
        }

        return Path.Combine(baseDirectory, "FFmpeg", platform);
    }

    /// <summary>
    /// Get FFmpeg version information
    /// </summary>
    public static string GetVersionInfo()
    {
        if (!_isInitialized)
            Initialize();

        return ffmpeg.av_version_info();
    }
}
