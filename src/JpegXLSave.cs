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
using PaintDotNet.Rendering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using ExifColorSpace = JpegXLFileTypePlugin.Exif.ExifColorSpace;

namespace JpegXLFileTypePlugin
{
    internal static class JpegXLSave
    {
        public static unsafe void Save(IReadOnlyFileTypeDocument input,
                                       Stream output,
                                       ProgressEventHandler progressEventHandler,
                                       int quality,
                                       bool lossless,
                                       int effort,
                                       IImagingFactory imagingFactory)
        {
            // TODO: support more pixel formats
            using IFileTypeCompositeBitmap<ColorBgra32> compositeBitmap = input.GetCompositeBitmap<ColorBgra32>();
            using IFileTypeBitmapLock<ColorBgra32> compositeLock = compositeBitmap.Lock();

            ProgressCallback? progressCallback = null;

            if (progressEventHandler != null)
            {
                progressCallback = new ProgressCallback(delegate (int progress)
                {
                    try
                    {
                        progressEventHandler.Invoke(null, new ProgressEventArgs(progress, true));
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                });
            }

            EncoderOptions options = new(quality, lossless, effort);
            EncoderImageMetadata metadata = CreateImageMetadata(input, imagingFactory);

            JpegXLNative.SaveImage(compositeLock.AsRegionPtr(), options, metadata, progressCallback, output);
        }

        private static EncoderImageMetadata CreateImageMetadata(IReadOnlyFileTypeDocument input, IImagingFactory imagingFactory)
        {
            byte[]? exifBytes = null;
            byte[]? iccProfileBytes = null;
            byte[]? xmpBytes = null;
            CicpColorSpace? cicpColorSpace = null;

            ExifColorSpace exifColorSpace = ExifColorSpace.Srgb;

            IColorContext? colorContext = input.GetColorContext();

            if (colorContext != null)
            {
                // Prefer compact CICP code points, but only when they reproduce the color context exactly:
                // build a color context back from the CICP and require it to compare equal to the original.
                // This keeps standard wide-gamut spaces losslessly tagged while preserving the exact ICC
                // profile for anything that does not round-trip (e.g. Adobe RGB, or an off-standard variant of
                // a standard space, where writing CICP could shift some pixels).
                if (colorContext.TryGetCicpColorSpace(out CicpColorSpace cicp) &&
                    cicp.CanCreateColorContext &&
                    IsNativeExpressible(cicp))
                {
                    using IColorContext roundTrippedColorContext = imagingFactory.CreateColorContext(cicp);

                    if (colorContext.Equals(roundTrippedColorContext))
                    {
                        cicpColorSpace = cicp;

                        // The EXIF color space tag is only sRGB when the color space is sRGB.
                        if (cicp.ColorPrimaries != CicpColorPrimaries.Bt709 || 
                            cicp.TransferCharacteristics != CicpTransferCharacteristics.Srgb)
                        {
                            exifColorSpace = ExifColorSpace.Uncalibrated;
                        }
                    }
                }

                if (cicpColorSpace is null)
                {
                    // We do not set an ICC profile for sRGB images as JpegXL can signal that
                    // using its built-in color space encoding, and sRGB is the default for
                    // images without an ICC profile.
                    if (colorContext.Type != ColorContextType.ExifColorSpace || 
                        colorContext.ExifColorSpace != PaintDotNet.Imaging.ExifColorSpace.Srgb)
                    {
                        iccProfileBytes = colorContext.GetProfileBytes().ToArray();

                        if (iccProfileBytes.Length > 0)
                        {
                            exifColorSpace = ExifColorSpace.Uncalibrated;
                        }
                    }
                }
            }

            Dictionary<ExifPropertyPath, ExifValue>? propertyItems = GetExifMetadataFromDocument(input);

            if (propertyItems != null)
            {
                propertyItems.Remove(ExifPropertyKeys.Image.InterColorProfile.Path);

                if (exifColorSpace == ExifColorSpace.Uncalibrated)
                {
                    // Remove the InteroperabilityIndex and related tags, these tags should
                    // not be written if the image has a non-sRGB color space (ICC or CICP).
                    propertyItems.Remove(ExifPropertyKeys.Interop.InteroperabilityIndex.Path);
                    propertyItems.Remove(ExifPropertyKeys.Interop.InteroperabilityVersion.Path);
                }

                exifBytes = new ExifWriter(input.Size, propertyItems, exifColorSpace).CreateExifBlob();
            }

            XmpPacket? xmpPacket = input.Metadata.Xmp.XmpPacket;

            if (xmpPacket != null)
            {
                string xmpPacketAsString = xmpPacket.ToString(XmpPacketWrapperType.ReadOnly);

                xmpBytes = Encoding.UTF8.GetBytes(xmpPacketAsString);
            }

            return new EncoderImageMetadata(exifBytes, iccProfileBytes, xmpBytes, cicpColorSpace);
        }

        // Whether the native encoder can build a JxlColorEncoding for this CICP color space. Kept in sync with
        // BuildColorEncodingFromCicp in the native JxlEncoder.
        private static bool IsNativeExpressible(CicpColorSpace cicp)
        {
            bool primariesExpressible = cicp.ColorPrimaries is CicpColorPrimaries.Bt709
                or CicpColorPrimaries.Bt2020
                or CicpColorPrimaries.Smpte431
                or CicpColorPrimaries.Smpte432;

            bool transferExpressible = cicp.TransferCharacteristics is CicpTransferCharacteristics.Bt709
                or CicpTransferCharacteristics.Linear
                or CicpTransferCharacteristics.Srgb
                or CicpTransferCharacteristics.SmpteSt2084PQ
                or CicpTransferCharacteristics.AribStdB67Hlg;

            return primariesExpressible && transferExpressible;
        }

        private static Dictionary<ExifPropertyPath, ExifValue>? GetExifMetadataFromDocument(IReadOnlyFileTypeDocument doc)
        {
            Dictionary<ExifPropertyPath, ExifValue> items = new();

            foreach (ExifPropertyItem property in doc.Metadata.Exif.Items)
            {
                items.TryAdd(property.Path, property.Value);
            }

            return items.Count == 0 ? null : items;
        }
    }
}
