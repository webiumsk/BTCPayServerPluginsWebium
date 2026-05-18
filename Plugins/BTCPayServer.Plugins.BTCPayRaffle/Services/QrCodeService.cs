#nullable enable
using System;
using QRCoder;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

/// <summary>Generates QR codes as base-64 encoded PNG data URIs for use in img tags.</summary>
public static class QrCodeService
{
    public static string GenerateQrBase64(string content, int pixelsPerModule = 10)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        var bytes = qr.GetGraphic(pixelsPerModule);
        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
    }
}
