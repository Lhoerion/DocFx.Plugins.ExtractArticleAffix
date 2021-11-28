using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Composition;
using System.Collections.Immutable;
using Microsoft.DocAsCode.Common;
using Microsoft.DocAsCode.Plugins;
using HtmlAgilityPack;
using Newtonsoft.Json;

namespace DocFx.Plugins.ExtractArticleAffix
{
    [Export(nameof(ExtractArticleAffix), typeof(IPostProcessor))]
    // ReSharper disable once UnusedType.Global
    public class ExtractArticleAffix : IPostProcessor
    {
        private bool _disableAffix;

        public ImmutableDictionary<string, object> PrepareMetadata(ImmutableDictionary<string, object> metadata)
        {
            if (metadata.TryGetValue("_disableAffix", out var disableAffix))
            {
                _disableAffix = (bool)disableAffix;
            }

            return metadata;
        }

        public Manifest Process(Manifest manifest, string outputFolder)
        {
            if (outputFolder == null)
            {
                throw new ArgumentException("Base directory can not be null");
            }

            if (_disableAffix)
            {
                return manifest;
            }

            var htmlFiles = (from item in manifest.Files ?? Enumerable.Empty<ManifestItem>()
                from output in item.OutputFiles
                where item.DocumentType != "Toc" && output.Key.Equals(".html", StringComparison.OrdinalIgnoreCase)
                select (output.Value.RelativePath, item)).ToList();
            if (htmlFiles.Count == 0)
            {
                return manifest;
            }

            foreach (var (relativePath, _) in htmlFiles)
            {
                var filePath = Path.Combine(outputFolder, relativePath);
                var html = new HtmlDocument();

                if (!EnvironmentContext.FileAbstractLayer.Exists(filePath)) continue;
                try
                {
                    using var stream = new MemoryStream();
                    using (var fileStream = EnvironmentContext.FileAbstractLayer.OpenRead(filePath))
                    {
                        fileStream.CopyTo(stream);
                        stream.Position = 0;
                    }

                    html.Load(stream, Encoding.UTF8);
                    stream.Position = 0;

                    var sideAffix = html.DocumentNode.SelectSingleNode("//div[@id=\"affix\"]");
                    var test = TraverseArticle(html.DocumentNode);
                    if (test.Count <= 0)
                    {
                        sideAffix.Attributes.Add("class", "empty");
                        sideAffix.Attributes.Add("style", "display: none;");
                        continue;
                    }

                    var test2 = FormList(html.DocumentNode, test, new[] { "nav", "bs-docs-sidenav" });
                    sideAffix.InnerHtml = "";
                    sideAffix.AppendChild(test2);

                    html.Save(stream, Encoding.UTF8);
                    stream.Position = 0;

                    using (var fileStream = EnvironmentContext.FileAbstractLayer.Create(filePath))
                    {
                        stream.CopyTo(fileStream);
                    }

                    Logger.LogDiagnostic($"Successfully saved affix data to {filePath}");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Warning: Can't load content from {filePath}: {ex.Message}");
                }
            }

            return manifest;
        }

        private static bool IsConceptualArticle(HtmlNode htmlNode)
        {
            return htmlNode
                .SelectSingleNode(
                    "//div[contains(concat(\" \", normalize-space(@class), \" \"), \" content-column \")]")
                .Attributes["class"].Value.Split(' ').Contains("Conceptual");
        }

        private static List<Header> TraverseArticle(HtmlNode htmlRoot)
        {
            var headers = htmlRoot.SelectNodes(
                "//article[@id=\"_content\"]/*[self::h1 or self::h2 or self::h3 or self::h4]");
            var stack = new List<Header>
            {
                new Header
                {
                    Type = "H0"
                }
            };

            var curr = stack[0];

            foreach (var header in headers)
            {
                var xref = header.ChildNodes.Count > 1 ? header.ChildNodes.Last() : null;
                var obj = new Header
                {
                    Parent = curr,
                    Type = header.Name.ToUpper(),
                    Id = header.Attributes.Contains("id") ? header.Attributes["id"].Value : "",
                    Name = header.InnerText,
                    Href = xref != null && xref.Attributes.Contains("class") &&
                           xref.Attributes["class"].Value.Contains("xref")
                        ? xref.Attributes["href"].Value
                        : "#" + (header.Attributes.Contains("id") ? header.Attributes["id"].Value : "")
                };

                switch (string.Compare(obj.Type, curr.Type, StringComparison.InvariantCultureIgnoreCase))
                {
                    case 0:
                        obj.Parent = curr.Parent;
                        curr.Parent.Children.Add(curr = obj);
                        break;
                    case -1:
                        var p = curr.Parent;
                        while (string.CompareOrdinal(p.Type, obj.Type) >= 0) p = p.Parent;
                        p.Children.Add(curr = obj);
                        break;
                    case 1:
                        curr.Children.Add(curr = obj);
                        break;
                }
            }

            return stack[0].Children.Count > 0 && !IsConceptualArticle(htmlRoot)
                ? stack[0].Children[0].Children
                : stack[0].Children;
        }

        private static int _level = 1;

        private static HtmlNode FormList(HtmlNode node, List<Header> item, string[] classes)
        {
            return GetList(node, new Header
            {
                Children = item,
            }, string.Join(" ", classes));
        }

        private static HtmlNode GetList(HtmlNode node, Header model, string cls)
        {
            if (model == null || model.Children.Count <= 0) return null;
            var l = model.Children.Count;
            if (l == 0) return null;
            var ulNode = node.OwnerDocument.CreateElement("ul");
            ulNode.Attributes.Add("class",
                new[] { "level" + _level++, cls, model.Id }.Where(el => !string.IsNullOrEmpty(el))
                    .ToDelimitedString(" "));
            for (var i = 0; i < l; i++)
            {
                var item = model.Children[i];
                var href = item.Href;
                var name = item.Name;
                if (string.IsNullOrEmpty(name)) continue;
                var liNode = node.OwnerDocument.CreateElement("li");
                if (!string.IsNullOrEmpty(href))
                {
                    var aNode = node.OwnerDocument.CreateElement("a");
                    aNode.Attributes.Add("href", href);
                    aNode.InnerHtml = name.Trim();
                    liNode.AppendChild(aNode);
                }
                else
                {
                    liNode.InnerHtml = name;
                }

                var lvl = _level;
                var child = GetList(node, item, cls);
                if (child != null) liNode.AppendChild(child);
                _level = lvl;
                ulNode.AppendChild(liNode);
            }

            _level = 1;
            return ulNode;
        }

        public class Header
        {
            public string Type;

            [JsonIgnore] public Header Parent;

            public string Id;

            public string Name;

            public string Href;

            public List<Header> Children = new List<Header>();
        }
    }
}