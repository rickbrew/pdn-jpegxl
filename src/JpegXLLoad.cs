////////////////////////////////////////////////////////////////////////
//
// This file is part of pdn-jpegxl, a FileType plugin for Paint.NET
// that loads and saves JPEG XL images.
//
// Copyright (c) 2022, 2023, 2024, 2025, 2026 Nicholas Hayes
//
// This file is licensed under the MIT License.
// See LICENSE.txt for complete licensing and attribution information.
//
////////////////////////////////////////////////////////////////////////

using JpegXLFileTypePlugin.Exif;
using JpegXLFileTypePlugin.Interop;
using PaintDotNet;
using PaintDotNet.FileTypes;
using PaintDotNet.Imaging;
using System;
using System.IO;
using System.Linq;

namespace JpegXLFileTypePlugin
{
    internal static class JpegXLLoad
    {
        public static IFileTypeDocument Load(IFileTypeDocumentFactory factory, Stream input, IImagingFactory imagingFactory)
        {
            byte[] data = new byte[input.Length];
            input.ReadExactly(data, 0, data.Length);

            using (DecoderImage decoderImage = new(imagingFactory))
            {
                JpegXLNative.LoadImage(data, decoderImage);

                DecoderLayerData decoderLayerData = decoderImage.LayerData ?? throw new FormatException("The layer data was null.");
                IBitmap decoderLayerBitmap = decoderLayerData.Color;

                IColorContext? documentColorContext;
                IBitmapSource bitmapLayerSource;
                bool isHdrDocument = false;
                if (decoderImage.ColorSpace == JpegXLColorSpace.Rgb && decoderImage.HdrFormat == HdrFormat.PQ)
                {
                    if (decoderImage.ChannelRepresentation == JpegXLImageChannelRepresentation.Uint8)
                    {
                        throw new FormatException("PQ HDR images with 8-bit color channels are not supported.");
                    }

                    // Decode the BT.2020 PQ content to linear-light HDR and keep it as an HDR document, instead of
                    // flattening it to SDR at load. The output is tagged with the linearized form of the gamut-
                    // appropriate working space (BT.2020 -> BT.2020 linear), and the document is flagged as HDR so
                    // Paint.NET tone-maps it when displaying or exporting to SDR.
                    CicpColorSpace cicpColorSpace = new(CicpColorPrimaries.Bt2020,
                                                        CicpTransferCharacteristics.SmpteSt2084PQ,
                                                        CicpMatrixCoefficients.Identity,
                                                        CicpVideoFullRangeFlag.Full);

                    using IColorContext recommendedColorContext = imagingFactory.CreateColorContext(cicpColorSpace.RecommendedColorSpace);
                    documentColorContext = imagingFactory.CreateLinearizedColorContextOrScRgb(recommendedColorContext);

                    bitmapLayerSource = decoderLayerBitmap.CreateColorTransformer<ColorRgba128Float>(cicpColorSpace, documentColorContext);
                    isHdrDocument = true;
                }
                else if (factory.SupportedPixelFormats.Contains(decoderLayerBitmap.PixelFormat))
                {
                    // This covers RGB, CMYK, and Gray (already converted to RGB by DecoderLayerData)
                    documentColorContext = decoderImage.TryGetColorContext();
                    bitmapLayerSource = decoderLayerBitmap.CreateRef();
                }
                else
                {
                    throw new FormatException($"Unsupported format: {decoderImage.ColorSpace}, {decoderImage.ChannelRepresentation}, {decoderImage.HdrFormat}");
                }

                IFileTypeDocument document = factory.CreateDocument(bitmapLayerSource.Size, bitmapLayerSource.PixelFormat);

                ExifValueCollection? exifValues = decoderImage.TryGetExif();
                if (exifValues != null)
                {
                    using (var exifTx = document.Metadata.Exif.CreateTransaction())
                    {
                        exifTx.SetItems(exifValues);
                    }
                }

                XmpPacket? xmpPacket = decoderImage.GetXmp();
                using (var xmpTx = document.Metadata.Xmp.CreateTransaction())
                {
                    xmpTx.XmpPacket = xmpPacket;
                }

                if (documentColorContext is not null)
                {
                    document.SetColorContext(documentColorContext);
                }

                if (isHdrDocument)
                {
                    using (var hdrTx = document.Metadata.Hdr.CreateTransaction())
                    {
                        hdrTx.IsHdrDocument = true;

                        // ContentMaxLuminanceNits is left null: the JPEG XL intensity_target is not yet plumbed
                        // through the native decoder, so Paint.NET measures the content peak itself when tone-
                        // mapping. SceneReferredSdrWhiteLevelNits keeps its default (80 nits).
                    }
                }

                using IFileTypeBitmapLayer bitmapLayer = document.CreateBitmapLayer();
                document.Layers.Add(bitmapLayer);

                if (!string.IsNullOrWhiteSpace(decoderLayerData.Name))
                {
                    bitmapLayer.Name = decoderLayerData.Name;
                }

                using IFileTypeBitmapSink bitmapLayerSink = bitmapLayer.GetBitmap();
                bitmapLayerSink.WriteSource(bitmapLayerSource);

                documentColorContext?.Dispose();
                bitmapLayerSource.Dispose();

                return document;
            }
        }
    }
}
