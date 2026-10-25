using System;
using QRCoder;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

internal static class TicketQrCode
{
    public static byte[] CreatePng(string ticketNumber, int pixelsPerModule = 20)
    {
        var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(ticketNumber, QRCodeGenerator.ECCLevel.Q);
        var pngQrCode = new PngByteQRCode(qrCodeData);
        return pngQrCode.GetGraphic(pixelsPerModule);
    }

    public static string CreateDataUrl(string ticketNumber)
        => $"data:image/png;base64,{Convert.ToBase64String(CreatePng(ticketNumber))}";
}
