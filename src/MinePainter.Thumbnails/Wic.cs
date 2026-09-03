using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace MinePainter.Thumbnails;

// Windows Imaging Component：用它解 PNG，不必帶任何影像函式庫進來。
// 注意：介面裡的方法「順序就是 vtable 順序」，用不到的欄位也得佔位，不能省。

[GeneratedComInterface, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
internal partial interface IWICImagingFactory
{
    void CreateDecoderFromFilename_Unused();                                      // 1
    void CreateDecoderFromStream(                                                 // 2
        nint stream, nint vendor, int metadataOptions, out IWICBitmapDecoder decoder);
    void CreateDecoderFromFileHandle_Unused();                                    // 3
    void CreateComponentInfo_Unused();                                            // 4
    void CreateDecoder_Unused();                                                  // 5
    void CreateEncoder_Unused();                                                  // 6
    void CreatePalette_Unused();                                                  // 7
    void CreateFormatConverter(out IWICFormatConverter converter);                // 8
    void CreateBitmapScaler(out IWICBitmapScaler scaler);                         // 9
}

[GeneratedComInterface, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf")]
internal partial interface IWICBitmapDecoder
{
    void QueryCapability_Unused();                                                // 1
    void Initialize_Unused();                                                     // 2
    void GetContainerFormat_Unused();                                             // 3
    void GetDecoderInfo_Unused();                                                 // 4
    void CopyPalette_Unused();                                                    // 5
    void GetMetadataQueryReader_Unused();                                         // 6
    void GetPreview_Unused();                                                     // 7
    void GetColorContexts_Unused();                                               // 8
    void GetThumbnail_Unused();                                                   // 9
    void GetFrameCount(out uint count);                                           // 10
    void GetFrame(uint index, out IWICBitmapSource frame);                        // 11
}

[GeneratedComInterface, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94")]
internal partial interface IWICBitmapSource
{
    void GetSize(out uint width, out uint height);                                // 1
    void GetPixelFormat_Unused();                                                 // 2
    void GetResolution_Unused();                                                  // 3
    void CopyPalette_Unused();                                                    // 4
    void CopyPixels(nint rect, uint stride, uint bufferSize, nint buffer);        // 5
}

[GeneratedComInterface, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
internal partial interface IWICFormatConverter
{
    void GetSize(out uint width, out uint height);                                // 1
    void GetPixelFormat_Unused();                                                 // 2
    void GetResolution_Unused();                                                  // 3
    void CopyPalette_Unused();                                                    // 4
    void CopyPixels(nint rect, uint stride, uint bufferSize, nint buffer);        // 5
    void Initialize(                                                              // 6
        IWICBitmapSource source, in Guid destinationFormat, int dither,
        nint palette, double alphaThreshold, int paletteTranslate);
    void CanConvert_Unused();                                                     // 7
}

[GeneratedComInterface, Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94")]
internal partial interface IWICBitmapScaler
{
    void GetSize(out uint width, out uint height);                                // 1
    void GetPixelFormat_Unused();                                                 // 2
    void GetResolution_Unused();                                                  // 3
    void CopyPalette_Unused();                                                    // 4
    void CopyPixels(nint rect, uint stride, uint bufferSize, nint buffer);        // 5
    void Initialize(IWICBitmapSource source, uint width, uint height, int mode);  // 6
}

internal static class Wic
{
    internal static readonly Guid ClsidImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    internal static readonly Guid IidImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    internal static readonly Guid Format32bppBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    internal const int DecodeMetadataCacheOnDemand = 0;
    internal const int DitherTypeNone = 0;
    internal const int PaletteTypeCustom = 0;
    internal const int InterpolationFant = 3;
}
