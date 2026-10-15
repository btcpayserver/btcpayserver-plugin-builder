using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using QRCoder;

namespace PluginBuilder.Components.QRCode;

public class QRCode : ViewComponent
{
    public IViewComponentResult Invoke(string data, string alt)
    {
        using var qrCodeData = QRCodeGenerator.GenerateQrCode(data, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(qrCodeData);
        // Keep the white quiet zone even in dark mode so scanners can locate the code.
        var bytes = qrCode.GetGraphic(5, drawQuietZones: true);
        var image = new TagBuilder("img") { TagRenderMode = TagRenderMode.SelfClosing };
        image.AddCssClass("qr-code");
        image.Attributes["src"] = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        image.Attributes["alt"] = alt;
        image.Attributes["width"] = "256";
        image.Attributes["height"] = "256";
        return new HtmlContentViewComponentResult(image);
    }
}
