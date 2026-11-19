// llms.cs - writes the documentation site's files for AI tools, after DocFX has built the site.
//
//   <site>/llms.txt          the llms.txt index (https://llmstxt.org): what AsmSdk is, then every
//                            page in the navigation's order, each linked as Markdown with a summary
//   <site>/llms-full.txt     every page's Markdown in one file, its links made absolute
//   <site>/<page>.html.md    each page's Markdown next to its HTML page, the llms.txt convention
//
// AI assistants and crawlers read Markdown far more reliably than a DocFX page's HTML, which
// wraps every page in navigation, search and theme markup. The pages and their order come from
// the same toc.yml files that build the site's navigation, so nothing here lists pages by hand
// and a new page is picked up by the next build.
//
// Usage (a .NET 10 file-based app; the docs workflow runs it after "docfx build"):
//   dotnet run docs/llms.cs -- <docs folder> <site folder> <base URL>
//   dotnet run docs/llms.cs -- docs docs/_site https://asmfoundation.github.io/AsmSdk/
// Exit status: 0 written; 1 a page or a link target is missing (nothing is written then);
// 2 wrong arguments.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: dotnet run docs/llms.cs -- <docs folder> <site folder> <base URL>");
    return 2;
}

try
{
    Llms.Write(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), args[2].EndsWith('/') ? args[2] : args[2] + "/");
    return 0;
}
catch (InvalidDataException ex)
{
    // A broken toc entry or link: say which, and leave the site without half-written files.
    Console.Error.WriteLine($"llms: {ex.Message}");
    return 1;
}

/// <summary>One page of the site, in navigation order.</summary>
/// <param name="Section">The top navigation entry it belongs to (Docs, Examples, ...).</param>
/// <param name="Source">Its Markdown file, relative to the docs folder, with forward slashes.</param>
/// <param name="Title">Its first heading, or the navigation name when it has none.</param>
/// <param name="Summary">The first sentences of its first paragraph, as plain text.</param>
/// <param name="Markdown">Its Markdown, front matter removed and includes inlined.</param>
internal sealed record Page(string Section, string Source, string Title, string Summary, string Markdown);

/// <summary>Builds and writes llms.txt, llms-full.txt and the per-page Markdown copies.</summary>
internal static partial class Llms
{
    /// <summary>A Markdown link or image target: [text](target) or ![alt](target), without a title part.</summary>
    [GeneratedRegex(@"(!?\[[^\]]*\]\()([^)\s]+)(\))")]
    private static partial Regex LinkTarget();

    /// <summary>A DocFX include line: [!include[name](path)].</summary>
    [GeneratedRegex(@"^\[!include\[[^\]]*\]\(([^)]+)\)\]\s*$", RegexOptions.Multiline)]
    private static partial Regex Include();

    /// <summary>A top-level list item, "- text", "* text" or "1. text"; group 1 is the text.</summary>
    [GeneratedRegex(@"^(?:[-*]|\d+\.)\s+(.*)$")]
    private static partial Regex ListItem();

    /// <summary>An ordered-list item: "1. text".</summary>
    [GeneratedRegex(@"^\d+\.\s")]
    private static partial Regex OrderedItem();

    /// <summary>The first released version heading of CHANGELOG.md: "## [0.2.0] — date".</summary>
    [GeneratedRegex(@"^## \[(\d+\.\d+\.\d+[^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex ReleaseHeading();

    /// <summary>Writes the three kinds of file into the built site.</summary>
    /// <param name="docs">The docs folder (holds docfx.json and toc.yml).</param>
    /// <param name="site">DocFX's output folder (docs/_site).</param>
    /// <param name="baseUrl">The site's public URL, ending in "/".</param>
    /// <exception cref="InvalidDataException">A toc entry names a missing file, or a page links to a missing page.</exception>
    /// <remarks>
    /// Everything is read and checked before the first file is written, so a failure leaves the
    /// site as DocFX built it.
    /// </remarks>
    public static void Write(string docs, string site, string baseUrl)
    {
        if (!Directory.Exists(site))
        {
            throw new InvalidDataException($"{site} does not exist: run docfx build first");
        }

        List<Page> pages = ReadPages(docs);
        var sources = pages.Select(p => p.Source).ToHashSet(StringComparer.Ordinal);
        var copies = pages.ToDictionary(p => p.Source, p => RewriteLinks(p, sources, baseUrl, absolute: false));
        var full = new StringBuilder();
        foreach (Page page in pages)
        {
            full.Append("<!-- ").Append(baseUrl).Append(HtmlPath(page.Source)).Append(" -->\n\n")
                .Append(RewriteLinks(page, sources, baseUrl, absolute: true).TrimEnd()).Append("\n\n");
        }

        string index = Index(docs, pages, baseUrl);
        foreach ((string source, string markdown) in copies)
        {
            WriteText(Path.Combine(site, HtmlPath(source) + ".md"), markdown);
        }

        WriteText(Path.Combine(site, "llms-full.txt"), full.ToString());
        WriteText(Path.Combine(site, "llms.txt"), index);
        Console.WriteLine($"llms: {pages.Count} pages; wrote llms.txt, llms-full.txt and {copies.Count} .html.md copies to {site}");
    }

    /// <summary>The pages of every section, in the order of the top toc.yml and then each section's toc.yml.</summary>
    /// <param name="docs">The docs folder.</param>
    /// <returns>The pages.</returns>
    /// <exception cref="InvalidDataException">An entry names a file or folder that does not exist.</exception>
    private static List<Page> ReadPages(string docs)
    {
        var pages = new List<Page>();
        foreach ((string section, string href) in ReadToc(Path.Combine(docs, "toc.yml")))
        {
            // A folder entry ("guide/") opens that folder's own toc.yml; a file entry is one page.
            IEnumerable<(string Name, string Source)> entries = href.EndsWith('/')
                ? ReadToc(Path.Combine(docs, href, "toc.yml")).Select(e => (e.Name, href + e.Href))
                : [(section, href)];
            foreach ((string name, string source) in entries)
            {
                string path = Path.Combine(docs, source);
                if (!File.Exists(path))
                {
                    throw new InvalidDataException($"toc entry \"{name}\" names {source}, which does not exist");
                }

                string markdown = Inline(path, StripFrontMatter(File.ReadAllText(path)));
                (string? heading, string summary) = HeadingAndSummary(markdown);
                pages.Add(new Page(section, source, heading ?? name, summary, markdown));
            }
        }

        return pages;
    }

    /// <summary>
    /// The name/href pairs of a toc.yml. Only the flat form the site uses is accepted
    /// ("- name: X" then "  href: Y"); nested items fail loudly instead of being skipped.
    /// </summary>
    /// <param name="path">The toc.yml file.</param>
    /// <returns>The entries, in file order.</returns>
    /// <exception cref="InvalidDataException">The file is missing, nests items, or has an entry without both keys.</exception>
    private static List<(string Name, string Href)> ReadToc(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"{path} does not exist");
        }

        var entries = new List<(string, string)>();
        string? name = null;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("- name:", StringComparison.Ordinal))
            {
                name = Unquote(line["- name:".Length..]);
            }
            else if (line.StartsWith("  href:", StringComparison.Ordinal) && name is not null)
            {
                entries.Add((name, Unquote(line["  href:".Length..])));
                name = null;
            }
            else
            {
                throw new InvalidDataException($"{path}: unexpected line \"{line}\" (only flat name/href entries are supported)");
            }
        }

        return entries;
    }

    /// <summary>A YAML scalar without its surrounding quotes.</summary>
    /// <param name="value">The scalar as written.</param>
    /// <returns>The value.</returns>
    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;
    }

    /// <summary>Removes a leading YAML front matter block (DocFX page metadata).</summary>
    /// <param name="markdown">The file's text.</param>
    /// <returns>The text after the block, or the text unchanged when it has none.</returns>
    private static string StripFrontMatter(string markdown)
    {
        markdown = markdown.Replace("\r\n", "\n");
        if (!markdown.StartsWith("---\n", StringComparison.Ordinal))
        {
            return markdown;
        }

        int end = markdown.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        return end < 0 ? markdown : markdown[(end + 5)..].TrimStart('\n');
    }

    /// <summary>Replaces each DocFX include line with the included file's text.</summary>
    /// <param name="page">The page's path (includes are relative to it).</param>
    /// <param name="markdown">The page's text.</param>
    /// <returns>The text with every include inlined.</returns>
    /// <exception cref="InvalidDataException">An included file does not exist.</exception>
    private static string Inline(string page, string markdown) => Include().Replace(markdown, match =>
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, match.Groups[1].Value));
        return File.Exists(path)
            ? StripFrontMatter(File.ReadAllText(path)).TrimEnd()
            : throw new InvalidDataException($"{page} includes {match.Groups[1].Value}, which does not exist");
    });

    /// <summary>The page's first heading, and the first sentences of its first plain paragraph.</summary>
    /// <param name="markdown">The page's Markdown.</param>
    /// <returns>The heading (null when the page has none) and the summary (empty when it has no paragraph).</returns>
    /// <remarks>
    /// A plain paragraph is one that is not a heading, list, table, quote, code block or HTML. A
    /// paragraph that ends in a colon introduces a list ("This page builds three things:"), so the
    /// list's items are added to it, joined with semicolons. The summary keeps whole sentences up
    /// to about 300 characters, so an index line stays readable.
    /// </remarks>
    private static (string? Heading, string Summary) HeadingAndSummary(string markdown)
    {
        string? heading = null;
        var paragraph = new List<string>();
        var items = new List<string>();
        bool fenced = false;
        bool listing = false;
        foreach (string line in markdown.Split('\n'))
        {
            if (listing)
            {
                // Top-level items only; an indented line continues the item above it.
                Match item = ListItem().Match(line);
                if (item.Success)
                {
                    items.Add(item.Groups[1].Value.Trim());
                }
                else if (line.StartsWith("  ", StringComparison.Ordinal) && items.Count > 0)
                {
                    items[^1] += " " + line.Trim();
                }
                else if (line.Trim().Length != 0 || items.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                if (paragraph.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (fenced)
            {
                continue;
            }

            if (heading is null && line.StartsWith("# ", StringComparison.Ordinal))
            {
                heading = line[2..].Trim();
                continue;
            }

            if (line.Trim().Length == 0)
            {
                if (paragraph.Count > 0)
                {
                    if (!paragraph[^1].EndsWith(':'))
                    {
                        break;
                    }

                    listing = true;
                }

                continue;
            }

            // Not a paragraph: headings, lists ("- ", "* ", "1. "), tables, quotes and HTML. A
            // paragraph may still start with "**bold**", which is not a list.
            if (paragraph.Count == 0 && (line.StartsWith('#') || line.StartsWith("- ", StringComparison.Ordinal)
                                         || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith('|')
                                         || line.StartsWith('>') || line.StartsWith('<') || OrderedItem().IsMatch(line)))
            {
                continue;
            }

            paragraph.Add(line.Trim());
        }

        string text = PlainText(string.Join(' ', paragraph));
        if (items.Count > 0)
        {
            // "builds three things:" + "a; b; c." - each item without its own final period.
            text += " " + string.Join("; ", items.Select(i => PlainText(i).TrimEnd('.', ';'))) + ".";
        }

        const int Limit = 300;
        if (text.Length <= Limit)
        {
            return (heading, text);
        }

        // Cut after the last sentence that ends within the limit; without one, at a word.
        int cut = text.LastIndexOf(". ", Limit, StringComparison.Ordinal);
        if (cut > 40)
        {
            return (heading, text[..(cut + 1)]);
        }

        int space = text.LastIndexOf(' ', Limit);
        return (heading, text[..(space > 0 ? space : Limit)] + "…");
    }

    /// <summary>Markdown inline syntax reduced to its text: links to their words, no emphasis or code marks.</summary>
    /// <param name="markdown">A paragraph.</param>
    /// <returns>Plain text.</returns>
    private static string PlainText(string markdown)
    {
        string text = Regex.Replace(markdown, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
        text = text.Replace("**", "").Replace("`", "");
        text = Regex.Replace(text, @"(?<!\w)\*([^*\s][^*]*)\*(?!\w)", "$1");   // *emphasis*
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// The page's Markdown with its relative links rewritten. For the .html.md copies, a link to
    /// another page becomes a link to that page's copy; for llms-full.txt, every relative link
    /// becomes an absolute URL of the site (pages as .html). Links in code blocks are left alone.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="sources">Every page's source, to tell links to pages from links to other files.</param>
    /// <param name="baseUrl">The site's URL.</param>
    /// <param name="absolute">True for llms-full.txt (absolute URLs); false for the copies.</param>
    /// <returns>The rewritten Markdown.</returns>
    /// <exception cref="InvalidDataException">A relative link names a Markdown page that does not exist.</exception>
    private static string RewriteLinks(Page page, HashSet<string> sources, string baseUrl, bool absolute)
    {
        string folder = page.Source.Contains('/') ? page.Source[..(page.Source.LastIndexOf('/') + 1)] : "";
        var lines = page.Markdown.Split('\n');
        bool fenced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            lines[i] = LinkTarget().Replace(lines[i], match =>
            {
                string target = match.Groups[2].Value;
                if (target.StartsWith('#') || target.Contains("://", StringComparison.Ordinal)
                    || target.StartsWith("mailto:", StringComparison.Ordinal) || target.StartsWith('/'))
                {
                    return match.Value;     // an anchor on the same page, or already absolute
                }

                string path = target, anchor = "";
                int hash = target.IndexOf('#');
                if (hash >= 0)
                {
                    path = target[..hash];
                    anchor = target[hash..];
                }

                string resolved = Resolve(folder, path);
                bool isPage = resolved.EndsWith(".md", StringComparison.Ordinal);
                if (isPage && !sources.Contains(resolved))
                {
                    // A page outside the navigation (the landing page): link its HTML, which exists.
                    if (resolved != "index.md")
                    {
                        throw new InvalidDataException($"{page.Source} links to {target}, which is not a page of the site");
                    }

                    return match.Groups[1].Value + baseUrl + anchor + match.Groups[3].Value;
                }

                string rewritten = absolute
                    ? baseUrl + (isPage ? HtmlPath(resolved) : resolved) + anchor
                    : isPage ? path[..^3] + ".html.md" + anchor : target;
                return match.Groups[1].Value + rewritten + match.Groups[3].Value;
            });
        }

        return string.Join('\n', lines);
    }

    /// <summary>Resolves a relative path against a folder of the site ("guide/" + "../toolchain/x.md").</summary>
    /// <param name="folder">The page's folder, relative to the docs folder, ending in "/" (or empty).</param>
    /// <param name="path">The relative path from the link.</param>
    /// <returns>The path relative to the docs folder, with forward slashes.</returns>
    private static string Resolve(string folder, string path)
    {
        var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (string part in path.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part.Length > 0 && part != ".")
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>The HTML page DocFX builds from a Markdown source: guide/ides.md → guide/ides.html.</summary>
    /// <param name="source">The source, relative to the docs folder.</param>
    /// <returns>The page's path in the site.</returns>
    private static string HtmlPath(string source) => source[..^3] + ".html";

    /// <summary>The llms.txt text: the project, how to start, then the pages by section.</summary>
    /// <param name="docs">The docs folder (docfx.json's description; CHANGELOG.md next to it).</param>
    /// <param name="pages">The pages, in order.</param>
    /// <param name="baseUrl">The site's URL.</param>
    /// <returns>The file's text.</returns>
    /// <exception cref="InvalidDataException">docfx.json has no _description, or CHANGELOG.md no released version.</exception>
    /// <remarks>
    /// The version is the first released one in CHANGELOG.md, not the repository's working
    /// version, so the file never advertises a version nuget.org does not have yet.
    /// </remarks>
    private static string Index(string docs, List<Page> pages, string baseUrl)
    {
        using JsonDocument docfx = JsonDocument.Parse(File.ReadAllText(Path.Combine(docs, "docfx.json")));
        string description = docfx.RootElement.GetProperty("build").GetProperty("globalMetadata").TryGetProperty("_description", out JsonElement d)
            ? d.GetString()!
            : throw new InvalidDataException("docfx.json has no build.globalMetadata._description");
        string changelog = File.ReadAllText(Path.Combine(docs, "..", "CHANGELOG.md"));
        Match release = ReleaseHeading().Match(changelog);
        string version = release.Success ? release.Groups[1].Value : throw new InvalidDataException("CHANGELOG.md names no released version");

        var text = new StringBuilder();
        text.Append("# AsmSdk\n\n")
            .Append("> ").Append(description).Append("\n\n")
            .Append("AsmSdk is the MSBuild SDK `AsmFoundation.Sdk` on nuget.org (latest release: ").Append(version).Append(").\n")
            .Append("A project file, `.asmproj`, builds NASM x86-64 assembly into Linux shared libraries, static libraries\n")
            .Append("or executables with `dotnet build`, shares them as NuGet packages, and sits in the same solution as the\n")
            .Append("C# projects that call it. Restore downloads the assembler and linker, so nothing else is installed.\n")
            .Append("The outputs are Linux x86-64 ELF files; the build runs on Windows, Linux and macOS.\n\n")
            .Append("Start a project with a `global.json` that names the SDK, `{ \"msbuild-sdks\": { \"AsmFoundation.Sdk\": \"")
            .Append(version).Append("\" } }`,\nand a project file that begins `<Project Sdk=\"AsmFoundation.Sdk\">`.\n\n")
            .Append("Every page below is linked as Markdown. [llms-full.txt](").Append(baseUrl)
            .Append("llms-full.txt) holds all of them in one file, in this order.\n");

        foreach (IGrouping<string, Page> section in pages.GroupBy(p => p.Section))
        {
            text.Append("\n## ").Append(section.Key).Append("\n\n");
            foreach (Page page in section)
            {
                text.Append("- [").Append(page.Title).Append("](").Append(baseUrl).Append(HtmlPath(page.Source)).Append(".md)");
                if (page.Summary.Length > 0)
                {
                    text.Append(": ").Append(page.Summary);
                }

                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Writes UTF-8 text without a byte-order mark, with LF line ends, creating the folder.</summary>
    /// <param name="path">The file.</param>
    /// <param name="text">Its text.</param>
    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
