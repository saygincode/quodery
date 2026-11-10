using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.FileProviders;

namespace Quodery.Web;

[HtmlTargetElement("lucide-icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed partial class LucideIconTagHelper : TagHelper
{
    private static readonly IFileProvider WebRoot = new ManifestEmbeddedFileProvider(typeof(LucideIconTagHelper).Assembly, "wwwroot");
    private static readonly ConcurrentDictionary<string, Icon> Icons = new(StringComparer.Ordinal);

    [HtmlAttributeName("name")]
    public string Name { get; set; } = "";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var icon = Icons.GetOrAdd(Name, Load);
        output.TagName = "svg";
        output.TagMode = TagMode.StartTagAndEndTag;
        foreach (var (name, value) in icon.Attributes)
            if (!output.Attributes.ContainsName(name))
                output.Attributes.SetAttribute(name, value);
        if (!output.Attributes.ContainsName("aria-hidden"))
            output.Attributes.SetAttribute("aria-hidden", "true");
        output.Content.SetHtmlContent(icon.Content);
    }

    private static Icon Load(string name)
    {
        if (!IconName().IsMatch(name))
            throw new InvalidOperationException($"\"{name}\" is not a valid Lucide icon name.");
        var file = WebRoot.GetFileInfo($"vendor/lucide/{name}.svg");
        if (!file.Exists)
            throw new InvalidOperationException($"The Lucide icon \"{name}\" is missing from wwwroot/vendor/lucide/.");
        using var reader = new StreamReader(file.CreateReadStream());
        var svg = reader.ReadToEnd();

        var start = svg.IndexOf("<svg", StringComparison.Ordinal);
        var startEnd = start < 0 ? -1 : svg.IndexOf('>', start);
        var end = svg.LastIndexOf("</svg>", StringComparison.Ordinal);
        if (start < 0 || startEnd < 0 || end < startEnd)
            throw new InvalidOperationException($"The Lucide icon \"{name}\" is not a single <svg> element.");

        var attributes = RootAttribute().Matches(svg[(start + 4)..startEnd])
            .Select(match => (Name: match.Groups[1].Value, Value: match.Groups[2].Value))
            .Where(attribute => attribute.Name is not ("class" or "xmlns"))
            .ToArray();
        return new Icon(attributes, svg[(startEnd + 1)..end].Trim());
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex IconName();

    [GeneratedRegex("([A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*\"([^\"]*)\"")]
    private static partial Regex RootAttribute();

    private sealed record Icon((string Name, string Value)[] Attributes, string Content);
}
