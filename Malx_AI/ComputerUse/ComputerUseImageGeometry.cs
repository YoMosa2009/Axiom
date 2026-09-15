using System;
using System.Collections.Generic;
using System.Linq;

namespace Malx_AI.ComputerUse;

internal static class ComputerUseImageGeometry
{
    // A screenshot is the single largest thing Computer Use sends a model, and an oversized one
    // is the usual cause of a provider dying mid-turn ("model runner has unexpectedly stopped",
    // context overflow, request-size rejection). The ladder lets the controller retry an
    // interrupted turn with a materially cheaper image instead of resending what just failed.
    // This is deliberately provider-agnostic: any backend that cannot afford the current payload
    // gets a smaller one, no per-model rules involved.
    private static readonly (int MaxEdge, long JpegQuality)[] DetailLadder =
    [
        (1280, 82L),
        (1024, 76L),
        (800, 70L),
        (640, 64L)
    ];

    public static int MaxDetailLevel => DetailLadder.Length - 1;

    public static int ClampDetailLevel(int level) => Math.Clamp(level, 0, MaxDetailLevel);

    public static bool CanReduceDetail(int level) => level < MaxDetailLevel;

    public static int MaxEdgeForDetailLevel(int level) => DetailLadder[ClampDetailLevel(level)].MaxEdge;

    public static long JpegQualityForDetailLevel(int level) => DetailLadder[ClampDetailLevel(level)].JpegQuality;

    public static (int Width, int Height) GetSize(int width, int height, int maxEdge = 1280)
    {
        double scale = Math.Min(1d, maxEdge / (double)Math.Max(1, Math.Max(width, height)));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public static (int Width, int Height) GetSizeForDetailLevel(int width, int height, int level)
        => GetSize(width, height, MaxEdgeForDetailLevel(level));

    public static IReadOnlyList<ComputerUseUiTarget> ScaleTargets(IReadOnlyList<ComputerUseUiTarget> targets,
        int screenWidth, int screenHeight, int imageWidth, int imageHeight)
        => targets.Select(target => new ComputerUseUiTarget
        {
            Id = target.Id, Name = target.Name, Role = target.Role, Value = target.Value,
            ImageX = Math.Clamp((int)Math.Floor(target.ImageX * imageWidth / (double)Math.Max(1, screenWidth)), 0, imageWidth - 1),
            ImageY = Math.Clamp((int)Math.Floor(target.ImageY * imageHeight / (double)Math.Max(1, screenHeight)), 0, imageHeight - 1)
        }).ToArray();
}
