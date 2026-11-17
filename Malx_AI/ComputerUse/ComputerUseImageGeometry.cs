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

    /// <summary>
    /// Keeps the few regions worth telling a model about and converts them to screenshot pixels.
    /// </summary>
    /// <remarks>
    /// A region earns its place by being named and large: at least an eighth of the monitor.
    /// Small named groups ("Tools", "Colors") are toolbars, already covered by their buttons.
    /// When one region sits inside another of nearly the same size, the inner one is the real
    /// work surface and the wrapper is dropped.
    /// </remarks>
    public static IReadOnlyList<ComputerUseUiRegion> SelectRegions(IReadOnlyList<ComputerUseUiRegion> regions,
        int screenWidth, int screenHeight, int imageWidth, int imageHeight, int max = 3)
    {
        double screenArea = Math.Max(1, screenWidth) * (double)Math.Max(1, screenHeight);
        double scaleX = imageWidth / (double)Math.Max(1, screenWidth);
        double scaleY = imageHeight / (double)Math.Max(1, screenHeight);
        return (regions ?? [])
            .Where(region => !string.IsNullOrWhiteSpace(region.Name))
            .Select(region => (Region: region, Area: Math.Max(0, region.Right - region.Left) * (double)Math.Max(0, region.Bottom - region.Top)))
            .Where(candidate => candidate.Area >= screenArea * 0.125)
            .OrderBy(candidate => candidate.Area)
            .GroupBy(candidate => candidate.Region.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(max)
            .Select(candidate => new ComputerUseUiRegion
            {
                Name = candidate.Region.Name.Length <= 96 ? candidate.Region.Name : candidate.Region.Name[..96],
                Role = candidate.Region.Role,
                Left = Math.Clamp((int)Math.Ceiling(candidate.Region.Left * scaleX), 0, Math.Max(0, imageWidth - 1)),
                Top = Math.Clamp((int)Math.Ceiling(candidate.Region.Top * scaleY), 0, Math.Max(0, imageHeight - 1)),
                Right = Math.Clamp((int)Math.Floor(candidate.Region.Right * scaleX), 0, Math.Max(0, imageWidth - 1)),
                Bottom = Math.Clamp((int)Math.Floor(candidate.Region.Bottom * scaleY), 0, Math.Max(0, imageHeight - 1))
            })
            .ToArray();
    }

    /// <summary>
    /// Cuts a region down to the part that lies inside its window, or returns null when none does.
    /// </summary>
    /// <remarks>
    /// A scrollable surface reports its whole extent, not the part on screen: Paint's canvas
    /// reached 150 px past the right and bottom of its window on a real run. Passed on as the
    /// "work area", that told the model it could draw over whatever window sat behind Paint.
    /// </remarks>
    public static ComputerUseUiRegion? ClipToWindow(ComputerUseUiRegion region,
        int windowLeft, int windowTop, int windowRight, int windowBottom)
    {
        int left = Math.Max(region.Left, windowLeft);
        int top = Math.Max(region.Top, windowTop);
        int right = Math.Min(region.Right, windowRight);
        int bottom = Math.Min(region.Bottom, windowBottom);
        if (right <= left || bottom <= top)
            return null;
        return new ComputerUseUiRegion
        {
            Name = region.Name,
            Role = region.Role,
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };
    }

    public static IReadOnlyList<ComputerUseUiTarget> ScaleTargets(IReadOnlyList<ComputerUseUiTarget> targets,
        int screenWidth, int screenHeight, int imageWidth, int imageHeight)
        => targets.Select(target => new ComputerUseUiTarget
        {
            Id = target.Id, Name = target.Name, Role = target.Role, Value = target.Value, State = target.State,
            ImageWidth = (int)Math.Round(target.ImageWidth * imageWidth / (double)Math.Max(1, screenWidth)),
            ImageHeight = (int)Math.Round(target.ImageHeight * imageHeight / (double)Math.Max(1, screenHeight)),
            ImageX = Math.Clamp((int)Math.Floor(target.ImageX * imageWidth / (double)Math.Max(1, screenWidth)), 0, imageWidth - 1),
            ImageY = Math.Clamp((int)Math.Floor(target.ImageY * imageHeight / (double)Math.Max(1, screenHeight)), 0, imageHeight - 1)
        }).ToArray();
}
