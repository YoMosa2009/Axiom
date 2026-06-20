using Microsoft.Web.WebView2.Core;

namespace Malx_AI
{
    /// <summary>
    /// Central policy for the WebView2 (Chromium) panes used to render chat messages and the
    /// Project Canvas. These render STATIC HTML (markdown + KaTeX) and do not need GPU
    /// acceleration.
    ///
    /// On a single-GPU machine the embedded Chromium otherwise competes with llama.cpp for the
    /// SAME VRAM. When a local model is partially offloaded onto a memory-constrained card
    /// (e.g. a 9B model on an 8GB GTX 1080), the GPU is already near-saturated by the weights +
    /// KV cache. A long streaming reply then drives sustained WebView2 GPU compositing
    /// concurrently with the CUDA decode, and the contention surfaces as a CUDA
    /// "illegal memory access" inside ggml that aborts the whole process
    /// (ucrtbase 0xc0000409) — reproducibly on the second turn, where the answer is long enough
    /// to keep the renderer busy. Forcing software rendering for these panes hands the GPU back
    /// to inference; the text/math rendering is visually identical.
    /// </summary>
    public static class WebView2GpuPolicy
    {
        public static CoreWebView2EnvironmentOptions CreateEnvironmentOptions()
        {
            return new CoreWebView2EnvironmentOptions
            {
                // --disable-gpu          : no GPU process / hardware acceleration.
                // --disable-gpu-compositing: composite on the CPU (no shared D3D surfaces).
                // --disable-software-rasterizer is intentionally NOT set so Chromium still
                //   rasterizes (on the CPU) rather than failing to draw.
                AdditionalBrowserArguments = "--disable-gpu --disable-gpu-compositing"
            };
        }
    }
}
