using System.Net;
using System.Text.RegularExpressions;

namespace MiniLeague.Web.Tests;

/// <summary>
/// The preview a shared link gets in WhatsApp, Facebook, X, Slack and the like: the tags in the static page and the
/// image they point at. Those programs read the page without running it, so the tags must be in index.html.
/// </summary>
public class ShareTests
{
    private static readonly string WwwRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot"));

    // <meta property="og:title" content="..." /> and <meta name="twitter:card" content="..." />
    private static Dictionary<string, string> Tags()
    {
        string page = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));
        var tags = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(page, "<meta\\s+(?:property|name)=\"(?<key>[^\"]+)\"\\s+content=\"(?<value>[^\"]*)\"\\s*/>"))
            tags[m.Groups["key"].Value] = WebUtility.HtmlDecode(m.Groups["value"].Value);
        return tags;
    }

    private const string ImagePath = "images/minileague-baseball.webp";

    // The pixel size of a WebP file (lossy, lossless or extended), read from its header.
    private static (int Width, int Height) WebPSize(byte[] b)
    {
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(b, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(b, 8, 4));
        string kind = System.Text.Encoding.ASCII.GetString(b, 12, 4);
        return kind switch
        {
            "VP8 " => ((b[26] | b[27] << 8) & 0x3FFF, (b[28] | b[29] << 8) & 0x3FFF),
            "VP8L" => (1 + (((b[22] & 0x3F) << 8) | b[21]), 1 + (((b[24] & 0x0F) << 10) | (b[23] << 2) | ((b[22] & 0xC0) >> 6))),
            "VP8X" => (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16)),
            _ => throw new InvalidOperationException($"Not a WebP image kind: {kind}"),
        };
    }

    [Fact]
    public void ALinkToTheGame_HasATitleADescriptionATypeAndAnAddress()
    {
        var tags = Tags();

        Assert.Equal("website", tags["og:type"]);
        Assert.Equal("MiniLeague Baseball", tags["og:title"]);
        Assert.Equal("MiniLeague Baseball", tags["og:site_name"]);
        Assert.InRange(tags["og:description"].Length, 40, 300);   // long enough to say something, short enough not to be cut
        Assert.StartsWith("https://", tags["og:url"]);
        Assert.EndsWith("/", tags["og:url"]);
        Assert.Equal("MiniLeague Baseball", tags["twitter:title"]);
        Assert.False(string.IsNullOrWhiteSpace(tags["twitter:description"]));
    }

    [Fact]
    public void TheShareImage_IsTheGameArtwork_AtAnAbsoluteAddress_ForEveryProgram()
    {
        var tags = Tags();

        // Facebook, WhatsApp and the rest ignore a relative address, so it is the full address of the page plus the image.
        Assert.Equal(tags["og:url"] + ImagePath, tags["og:image"]);
        Assert.Equal(tags["og:image"], tags["twitter:image"]);
        Assert.Equal("image/webp", tags["og:image:type"]);
        Assert.False(string.IsNullOrWhiteSpace(tags["og:image:alt"]));
        Assert.False(string.IsNullOrWhiteSpace(tags["twitter:image:alt"]));
    }

    [Fact]
    public void TheImageIsThere_IsAsBigAsTheTagsSay_AndSmallEnoughForWhatsApp()
    {
        var tags = Tags();
        string file = Path.Combine(WwwRoot, ImagePath);

        Assert.True(File.Exists(file), "the image the tags point at is in the site");
        var (w, h) = WebPSize(File.ReadAllBytes(file));
        Assert.Equal(int.Parse(tags["og:image:width"]), w);
        Assert.Equal(int.Parse(tags["og:image:height"]), h);
        Assert.True(w >= 300 && h >= 300, "big enough for a preview");
        Assert.True(new FileInfo(file).Length < 300 * 1024, "WhatsApp shows a preview image only if it is under about 300 KB");
    }

    [Fact]
    public void TheCardOnXMatchesTheShapeOfTheImage()
    {
        var tags = Tags();
        double ratio = double.Parse(tags["og:image:width"]) / double.Parse(tags["og:image:height"]);

        // A wide image gets the big card; a squarish one gets the small card, so that it is not cropped.
        Assert.Equal(ratio >= 1.7 ? "summary_large_image" : "summary", tags["twitter:card"]);
    }

    [Fact]
    public void TheUsualPageTags_AreStillThere()
    {
        var tags = Tags();

        Assert.False(string.IsNullOrWhiteSpace(tags["description"]));
        Assert.Contains("width=device-width", tags["viewport"]);
        Assert.Matches("^#[0-9a-fA-F]{6}$", tags["theme-color"]);
    }
}
