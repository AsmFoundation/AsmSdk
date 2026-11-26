// og.cs - adds link-preview tags to every page of the documentation site, after DocFX has built it.
//
// What X, Slack, Discord, LinkedIn, WhatsApp, Telegram and iMessage draw for a shared link comes from a
// page's Open Graph and Twitter tags. DocFX's modern template writes none, so a link to these docs showed a
// bare title, or nothing at all. This adds them to each built page, between two marker comments, so a second
// run replaces its own block instead of stacking another:
//   og:title        the page's <title>
//   og:description  the owner's line of 2026-11-26, the same under every title (the page's own
//                   <meta name="description"> stays for search engines)
//   og:image        images/cards/<demo>.jpg for a Wasm Demos page that has one (its own frame, see
//                   docs/cards/card.html), images/cards/asmsdk.png for every other page
//   og:url          the page's public address (a folder's index.html as the folder)
//   twitter:card    summary_large_image, so X draws the image wide
//   theme-color     teal, which Discord uses for the embed's stripe (the owner's pick of 2026-11-26)
// It edits DocFX's output rather than DocFX's page layout on purpose: overriding the modern template's
// layout/_master.tmpl would freeze a copy of it, and every DocFX upgrade would then be silently masked.
//
// Usage (a .NET 10 file-based app; the docs workflow runs it after "docfx build"):
//   dotnet run docs/og.cs -- <site folder> <base URL>
//   dotnet run docs/og.cs -- docs/_site https://asmfoundation.github.io/AsmSdk/
// Exit status: 0 written; 1 a page has no <head> or no <title>, or a card image is missing or unreadable
// (nothing is written then); 2 wrong arguments.
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run docs/og.cs -- <site folder> <base URL>");
    return 2;
}

try
{
    var (pages, demoCards) = Og.Write(Path.GetFullPath(args[0]), args[1].EndsWith('/') ? args[1] : args[1] + "/");
    Console.WriteLine($"og: link-preview tags on {pages} pages ({demoCards} with a demo's own card)");
    return 0;
}
catch (InvalidDataException ex)
{
    // A page DocFX wrote without a head or a title, or a card the tags would point at that is not there:
    // say which, and leave every page as DocFX wrote it.
    Console.Error.WriteLine($"og: {ex.Message}");
    return 1;
}

/// <summary>Writes the link-preview tags into the built pages.</summary>
internal static partial class Og
{
    /// <summary>The line under the title in every preview: the owner's words of 2026-11-26, the same on the
    /// foundation's website (its site/layout.html). Change both together.</summary>
    private const string Description =
        "Software written in assembly providing competitive FOSAS cloud/backend infrastructure with exceptional performance for any platform or container or programming language";

    /// <summary>The card of every page that has none of its own: the repository's social preview.</summary>
    private const string DefaultCard = "images/cards/asmsdk.png";

    /// <summary>What the default card shows, for readers who cannot see it.</summary>
    private const string DefaultAlt = "AsmSdk: assembly projects on MSBuild and NuGet, for x64, ARM64 and WebAssembly.";

    /// <summary>Opens the block this tool writes; with <see cref="End"/>, lets a second run find and replace it.</summary>
    private const string Begin = "<!-- og.cs: link-preview tags (docs/og.cs) -->";

    /// <summary>Closes the block this tool writes.</summary>
    private const string End = "<!-- /og.cs -->";

    /// <summary>The page's title element; group 1 is its text, still HTML-encoded.</summary>
    [GeneratedRegex(@"<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Title();

    /// <summary>A block written by an earlier run, with the line break after it.</summary>
    [GeneratedRegex(@"<!-- og\.cs: link-preview tags \(docs/og\.cs\) -->.*?<!-- /og\.cs -->\r?\n?", RegexOptions.Singleline)]
    private static partial Regex OldBlock();

    /// <summary>A Wasm Demos page, demos/&lt;name&gt;.html; group 1 is the name, which names its card.</summary>
    [GeneratedRegex(@"^demos/([a-z0-9-]+)\.html$")]
    private static partial Regex DemoPage();

    /// <summary>Adds the tags to every HTML page under the site folder.</summary>
    /// <param name="site">The built site (DocFX's output folder).</param>
    /// <param name="baseUrl">The site's public address, ending in "/": crawlers resolve nothing relative, so every
    /// address in the tags is absolute.</param>
    /// <returns>How many pages got tags, and how many of them a demo's own card.</returns>
    /// <exception cref="InvalidDataException">A page has no &lt;/head&gt; or no &lt;title&gt;, or a card is
    /// missing or not a PNG or JPEG. Every page is prepared before the first is written, so this leaves the site
    /// unchanged.</exception>
    /// <remarks>Two kinds of .html file are left alone: fragments without a &lt;head&gt; (DocFX's toc.html files,
    /// which pages load into their navigation) and redirect pages (DocFX's <c>redirect_url</c>), which send a
    /// crawler on at once.</remarks>
    public static (int Pages, int DemoCards) Write(string site, string baseUrl)
    {
        var defaultSize = ImageSize(Path.Combine(site, DefaultCard));
        var updates = new List<(string Path, string Html)>();
        var demoCards = 0;
        foreach (var path in Directory.EnumerateFiles(site, "*.html", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(site, path).Replace('\\', '/');
            var html = File.ReadAllText(path);
            // Not every .html file is a page: DocFX writes each toc.yml as a toc.html fragment that pages load into
            // their navigation, with no <head> of its own. Only a document with a head can carry tags.
            if (html.IndexOf("<head", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (html.Contains("http-equiv=\"refresh\"", StringComparison.OrdinalIgnoreCase)) continue;

            var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (head < 0) throw new InvalidDataException($"{rel} has no </head> to put the tags in.");
            var title = Title().Match(html);
            if (!title.Success) throw new InvalidDataException($"{rel} has no <title>, which the preview's title comes from.");
            var titleText = WebUtility.HtmlDecode(title.Groups[1].Value).Trim();

            // A demo page with a card of its own shows its frame; anything else (including a new demo that has no
            // card yet) shows the repository's card, so a missing demo card never breaks the docs build.
            var (card, size, alt) = (DefaultCard, defaultSize, DefaultAlt);
            var demo = DemoPage().Match(rel);
            if (demo.Success && File.Exists(Path.Combine(site, "images", "cards", demo.Groups[1].Value + ".jpg")))
            {
                card = $"images/cards/{demo.Groups[1].Value}.jpg";
                size = ImageSize(Path.Combine(site, card));
                alt = $"{titleText.Split(" | ")[0]}: a frame of the demo, drawn by hand-written WebAssembly.";
                demoCards++;
            }
            else if (demo.Success && rel != "demos/index.html")
                Console.Error.WriteLine($"og: warning: {rel} has no images/cards/{demo.Groups[1].Value}.jpg; it gets AsmSdk's card (see docs/cards/card.html)");

            // index.html is served as its folder's address, which is how the pages link each other
            var url = baseUrl + (rel == "index.html" ? "" : rel.EndsWith("/index.html", StringComparison.Ordinal) ? rel[..^"index.html".Length] : rel);
            var block = new StringBuilder()
                .AppendLine(Begin)
                .AppendLine("<meta property=\"og:type\" content=\"website\">")
                .AppendLine("<meta property=\"og:site_name\" content=\"AsmSdk\">")
                .AppendLine($"<meta property=\"og:title\" content=\"{Attr(titleText)}\">")
                .AppendLine($"<meta property=\"og:description\" content=\"{Attr(Description)}\">")
                .AppendLine($"<meta property=\"og:url\" content=\"{Attr(url)}\">")
                .AppendLine($"<meta property=\"og:image\" content=\"{Attr(baseUrl + card)}\">")
                .AppendLine($"<meta property=\"og:image:width\" content=\"{size.Width}\">")
                .AppendLine($"<meta property=\"og:image:height\" content=\"{size.Height}\">")
                .AppendLine($"<meta property=\"og:image:alt\" content=\"{Attr(alt)}\">")
                .AppendLine("<meta name=\"twitter:card\" content=\"summary_large_image\">")
                .AppendLine("<meta name=\"theme-color\" content=\"#06706d\">")
                .AppendLine(End)
                .ToString();

            // drop an earlier run's block first, then insert before </head> (its index moves with the removal)
            var clean = OldBlock().Replace(html, "");
            head = clean.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            updates.Add((path, clean.Insert(head, block)));
        }

        // only now, with every page prepared, touch the files
        foreach (var (path, html) in updates)
            File.WriteAllText(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (updates.Count, demoCards);
    }

    /// <summary>Encodes text for a double-quoted HTML attribute.</summary>
    /// <param name="text">The text, already decoded.</param>
    /// <returns>The encoded text.</returns>
    private static string Attr(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Reads a PNG's or a JPEG's pixel size from its header, for og:image:width and og:image:height
    /// (Facebook and LinkedIn lay out a first share from them, before they fetch the image).</summary>
    /// <param name="path">The image file.</param>
    /// <returns>Its width and height in pixels.</returns>
    /// <exception cref="InvalidDataException">The file is missing, or neither a PNG nor a JPEG with a frame
    /// header: the tags would promise a card that crawlers cannot show.</exception>
    private static (int Width, int Height) ImageSize(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"{path} is missing: the pages' og:image would point nowhere.");
        var b = File.ReadAllBytes(path);
        // PNG: the 8-byte signature, then the IHDR chunk, whose data starts with the width and height (big-endian)
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G')
            return (BigEndian(b, 16), BigEndian(b, 20));
        // JPEG: walk the segments to the first start-of-frame marker (C0-CF, except C4 DHT, C8 JPG and CC DAC),
        // whose data holds the precision byte, then the height and the width (big-endian 16-bit)
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                i += 2 + ((b[i + 2] << 8) | b[i + 3]);  // skip the segment: its length counts itself, not the marker
            }
        }
        throw new InvalidDataException($"{path} is not a PNG or a JPEG whose size can be read.");
    }

    /// <summary>Reads a big-endian 32-bit integer.</summary>
    /// <param name="b">The bytes.</param>
    /// <param name="at">The first byte's index.</param>
    /// <returns>The value.</returns>
    private static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
}
