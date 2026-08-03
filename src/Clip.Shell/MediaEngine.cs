using System;
using System.IO;
using FlyleafLib;

namespace Clip.Shell;

/// <summary>
/// Brings up the video decoder the picture-in-picture player uses, once, on first need.
///
/// Windows' own playback was tried here first and was not good enough to keep: soft next to what
/// the browser managed, and often running the clock without ever showing a picture. This decodes on
/// the graphics card the way a browser does, so the picture is as good, while still being drawn
/// into Clip's own window so it cannot fall behind the frame.
///
/// The decoder's libraries are fetched by Get-FFmpeg.ps1 rather than kept in the repository. If
/// they are missing this reports as much and the caller falls back to the browser player, so a
/// checkout that has not run the script still has working picture-in-picture.
/// </summary>
internal static class MediaEngine
{
    private static readonly object Gate = new();
    private static bool _tried;
    private static bool _ready;

    /// <summary>The folder the decoder's libraries are copied to beside the executable.</summary>
    public static string DecoderPath => Path.Combine(AppContext.BaseDirectory, "FFmpeg");

    public static bool EnsureStarted()
    {
        lock (Gate)
        {
            if (_tried)
            {
                return _ready;
            }

            _tried = true;

            try
            {
                if (!Directory.Exists(DecoderPath))
                {
                    ShellLog.Info($"video decoder not installed at {DecoderPath}; using the browser player");
                    return false;
                }

                Engine.Start(new EngineConfig
                {
                    FFmpegPath = DecoderPath,
                    FFmpegLoadProfile = Flyleaf.FFmpeg.LoadProfile.Main,
                    UIRefresh = false,
                    LogOutput = null,
                });

                _ready = true;
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, "video decoder failed to start; using the browser player");
            }

            return _ready;
        }
    }
}

